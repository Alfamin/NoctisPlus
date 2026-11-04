using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Services.Sync;

namespace Noctis.TelegramSync;

/// <summary>
/// The reader's small persisted cursor: the last sync-channel message id it read, and per
/// device the highest batch seq applied plus any seqs seen above a gap. Apply is idempotent by
/// (device, seq), so posts read twice, out of order or after a restart never apply twice.
/// </summary>
public sealed class TgSyncCursor
{
    [JsonPropertyName("lastMessageId")] public long LastMessageId { get; set; }
    /// <summary>Highest seq per device with every seq at or below it applied.</summary>
    [JsonPropertyName("contiguous")] public Dictionary<string, long> Contiguous { get; set; } = new();
    /// <summary>Seqs applied above a gap (a missed post); folded into <see cref="Contiguous"/> once the gap fills.</summary>
    [JsonPropertyName("above")] public Dictionary<string, SortedSet<long>> Above { get; set; } = new();
    [JsonPropertyName("ownSeq")] public long OwnSeq { get; set; }

    public bool Seen(string device, long seq) =>
        (Contiguous.TryGetValue(device, out var c) && seq <= c) || (Above.TryGetValue(device, out var a) && a.Contains(seq));

    public void Mark(string device, long seq)
    {
        var c = Contiguous.GetValueOrDefault(device);
        if (seq <= c) return;
        if (!Above.TryGetValue(device, out var a)) Above[device] = a = new SortedSet<long>();
        a.Add(seq);
        while (a.Remove(c + 1)) c++;
        Contiguous[device] = c;
        if (a.Count == 0) Above.Remove(device);
    }

    /// <summary>After a snapshot: everything it covered counts as applied (gaps before it are not waited on).</summary>
    public void Raise(string device, long seq)
    {
        if (seq > Contiguous.GetValueOrDefault(device)) Contiguous[device] = seq;
        if (Above.TryGetValue(device, out var a)) { a.RemoveWhere(s => s <= seq); if (a.Count == 0) Above.Remove(device); }
    }

    public void Save(string path)
        => TgStateFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, SyncJson.Options));

    public static TgSyncCursor Load(string path)
    {
        using var document = TgStateFile.Read(path);
        if (document is null) return new();
        var root = document.RootElement;
        if (root.TryGetProperty("cursor", out var cursor)) root = cursor;
        return root.Deserialize<TgSyncCursor>(SyncJson.Options) ?? new();
    }
}

/// <summary>
/// One device's end of the sync log: <see cref="Record"/> queues local changes, <see cref="FlushAsync"/>
/// posts them as one batch (paced: at most one post per <see cref="MinPostInterval"/>, honours
/// FLOOD_WAIT), <see cref="PullAsync"/> reads newer posts (100 per history call) and merges them,
/// <see cref="PostSnapshotAsync"/> posts and pins the full state for new devices.
/// </summary>
public sealed class TgSyncLog
{
    public const string BatchFileName = "noctis-sync.json", SnapshotFileName = "noctis-snapshot.json";
    public const string BatchCaption = "noctis-plus sync batch", SnapshotCaption = "noctis-plus snapshot";

    private readonly ITelegramDrive _drive;
    private readonly long _channel;
    private readonly Func<DateTime> _now;
    private readonly string? _cursorPath;
    private readonly List<BatchItem> _pending = new();
    private DateTime _nextPostUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _stateGate = new();
    private SyncBatch? _inFlight;
    private sealed record Checkpoint(TgSyncCursor Cursor, List<SyncItem> Ledger, List<BatchItem> Pending, SyncBatch? InFlight,
        bool Bootstrapped = false, Dictionary<string, PlayCounterState>? EarlyPlays = null);
    // Review M7: plays heard before this device first read the log (its own counters unknown), as increments.
    private readonly Dictionary<string, PlayCounterState> _earlyPlays = new(StringComparer.Ordinal);
    private bool _bootstrapped;

    public TgSyncLog(ITelegramDrive drive, long channel, string device, TgLedger ledger, Func<DateTime>? now = null, string? cursorPath = null)
    {
        if (string.IsNullOrWhiteSpace(device) || device.Contains(SnapshotDeviceSeparator)) throw new ArgumentException("Invalid device id", nameof(device));
        _drive = drive; _channel = channel; Device = device; Ledger = ledger;
        _now = now ?? (() => DateTime.UtcNow);
        _cursorPath = cursorPath;
        Cursor = cursorPath is null ? new TgSyncCursor() : TgSyncCursor.Load(cursorPath);
        if (cursorPath is not null)
        {
            using var saved = TgStateFile.Read(cursorPath);
            if (saved?.RootElement.TryGetProperty("cursor", out _) == true)
            {
                var checkpoint = saved.RootElement.Deserialize<Checkpoint>(SyncJson.Options)!;
                Ledger.Restore(checkpoint.Ledger);
                _pending.AddRange(checkpoint.Pending);
                _inFlight = checkpoint.InFlight;
                // A checkpoint from before M7 that had read the log counts as read.
                _bootstrapped = checkpoint.Bootstrapped || checkpoint.Cursor.LastMessageId > 0;
                foreach (var (song, early) in checkpoint.EarlyPlays ?? []) _earlyPlays[song] = early;
            }
            else if (saved is not null)
            {
                // Old cursors were persisted without their ledger. Replay rather than silently skipping state.
                Cursor.LastMessageId = 0; Cursor.Contiguous.Clear(); Cursor.Above.Clear();
            }
        }
    }

    public string Device { get; }
    public TgLedger Ledger { get; }
    public TgSyncCursor Cursor { get; }
    public TimeSpan MinPostInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int PendingCount { get { lock (_stateGate) return _pending.Count; } }
    public DateTime NextPostUtc => _nextPostUtc;
    /// <summary>History calls made by <see cref="PullAsync"/> (the budget: one per 100 posts).</summary>
    public int HistoryCalls { get; private set; }

    /// <summary>A local change: applied to the ledger now, posted with the next flush.</summary>
    public void Record(string kind, string id, string payload, DateTime? updatedUtc = null)
    {
        lock (_stateGate)
        {
            var now = _now();
            // Posted exactly as stored, so the writer can recognise its own item when the post is read back.
            var stamp = TgLedger.ClampStamp(updatedUtc ?? now, now);
            var old = Ledger.Get(kind, id);
            // This device's edits of one item are sequential: a later one never sorts before its own earlier one.
            if (old?.Device == Device && old.UpdatedUtc >= stamp && old.Payload != payload)
                stamp = old.UpdatedUtc.AddTicks(1);
            var item = new BatchItem(kind, id, payload, stamp);
            Ledger.Apply(item, Device, stamp);
            // Only the newest unposted edit of an item is sent: two edits in one post would collapse onto the same
            // bounded stamp at the readers when this clock runs fast, and then the earlier one would win the tie.
            var posting = _inFlight?.Items.Count ?? 0;
            var older = _pending.FindLastIndex(p => p.Kind == kind && p.Id == id);
            if (older >= posting) _pending.RemoveAt(older);
            _pending.Add(item);
        }
    }

    /// <summary>True once this device has read the log (a pull or the snapshot): its own counters are known.</summary>
    public bool Bootstrapped { get { lock (_stateGate) return _bootstrapped; } }

    /// <summary>
    /// Review M7: one play of <paramref name="songId"/> on this device. Its counter is this device's own
    /// (<c>song@device</c>) and the newest write wins, so writing <c>count = 1</c> before the log was read would
    /// replace a count this device id already had. Until then the play is kept as an increment (saved with the
    /// cursor) and added on top of the counter read from the log.
    /// </summary>
    public void RecordPlay(string songId, DateTime? playedUtc = null)
    {
        var played = playedUtc ?? _now();
        lock (_stateGate)
        {
            if (!_bootstrapped)
            {
                var early = _earlyPlays.GetValueOrDefault(songId);
                _earlyPlays[songId] = new PlayCounterState((early?.Count ?? 0) + 1, Later(early?.LastPlayed, played));
            }
            else WritePlays(songId, 1, played);
        }
        if (!Bootstrapped) Save();
    }

    private void WritePlays(string songId, int plays, DateTime? played)
    {
        var id = TgKinds.PlaysId(songId, Device);
        var own = Ledger.Get(TgKinds.Plays, id) is { } item ? SyncJson.Deserialize<PlayCounterState>(item.Payload) : null;
        Record(TgKinds.Plays, id, SyncJson.Serialize(new PlayCounterState((own?.Count ?? 0) + plays, Later(own?.LastPlayed, played))));
    }

    private static DateTime? Later(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>The log was read: early plays join this device's counters now.</summary>
    private void Bootstrap()
    {
        lock (_stateGate)
        {
            _bootstrapped = true;
            foreach (var (song, early) in _earlyPlays) WritePlays(song, early.Count, early.LastPlayed);
            _earlyPlays.Clear();
        }
    }

    public void RecordJson<T>(string kind, string id, T state, DateTime? updatedUtc = null) =>
        Record(kind, id, SyncJson.Serialize(state), updatedUtc);

    /// <summary>Posts the pending batch if pacing allows. Returns false when nothing was posted (empty, paced, or flood wait).</summary>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (!await _ioGate.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try
        {
            var now = _now();
            SyncBatch batch;
            lock (_stateGate)
            {
                if (_pending.Count == 0 || now < _nextPostUtc) return false;
                // Persist the exact reserved batch. Ambiguous failures retry its immutable payload
                // under the same seq, so there are neither collisions nor permanent gaps.
                batch = _inFlight ??= new SyncBatch(BatchCodec.Version, Device, Cursor.OwnSeq + 1, _pending.ToList());
                Cursor.OwnSeq = Math.Max(Cursor.OwnSeq, batch.Seq);
            }
            Save();
            try
            {
                using var body = new MemoryStream(BatchCodec.Encode(batch));
                await _drive.PostDocumentAsync(_channel, BatchFileName, body, BatchCaption, null, ct).ConfigureAwait(false);
            }
            catch (TelegramFloodWaitException e)
            {
                _nextPostUtc = now + e.RetryAfter;
                // A definite rejection can return the reservation; the pending prefix is unchanged.
                if (Cursor.OwnSeq == batch.Seq) { Cursor.OwnSeq = batch.Seq - 1; _inFlight = null; Save(); }
                return false;
            }
            Cursor.Mark(Device, batch.Seq);
            lock (_stateGate) { _pending.RemoveRange(0, batch.Items.Count); _inFlight = null; }
            _nextPostUtc = now + MinPostInterval;
            Save();
            return true;
        }
        finally { _ioGate.Release(); }
    }

    /// <summary>Own seq never goes below anything this device already posted (reinstall, lost cursor).</summary>
    private void RaiseOwnSeq()
    {
        var high = Cursor.Contiguous.GetValueOrDefault(Device);
        if (Cursor.Above.TryGetValue(Device, out var a) && a.Count > 0) high = Math.Max(high, a.Max);
        if (high > Cursor.OwnSeq) Cursor.OwnSeq = high;
    }

    /// <summary>Reads every post after the cursor and merges it. Returns the number of batches applied.</summary>
    public async Task<int> PullAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await PullCoreAsync(ct).ConfigureAwait(false); }
        finally { _ioGate.Release(); }
    }

    private async Task<int> PullCoreAsync(CancellationToken ct)
    {
        var applied = 0;
        while (true)
        {
            var page = await _drive.ReadHistoryAsync(_channel, Cursor.LastMessageId, 100, ct).ConfigureAwait(false);
            HistoryCalls++;
            if (page.Count == 0) break;
            foreach (var msg in page.OrderBy(m => m.Id))
            {
                if (msg.Id <= Cursor.LastMessageId) continue;
                // The cursor moves only after the post is applied or deliberately skipped; a failed download throws first.
                if (msg.Document is not null && msg.Caption == BatchCaption)
                {
                    var batch = await ReadBatchAsync(msg, ct).ConfigureAwait(false);
                    if (batch is not null && !batch.Snapshot)
                    {
                        if (!Cursor.Seen(batch.Device, batch.Seq))
                        {
                            // Bounded by the server's date of this post: every reader stores the same stamp (M2).
                            foreach (var item in batch.Items) Ledger.Apply(item, batch.Device, msg.DateUtc);
                            Cursor.Mark(batch.Device, batch.Seq);
                            applied++;
                        }
                        // This device's own post read back: take the stamps the others gave it.
                        if (batch.Device == Device)
                            foreach (var item in batch.Items) Ledger.AdoptPosted(item, Device, msg.DateUtc);
                    }
                }
                Cursor.LastMessageId = msg.Id;
            }
            if (page.Count < 100) break;
        }
        RaiseOwnSeq();
        Bootstrap();
        Save();
        return applied;
    }

    /// <summary>A new device (or one offline past the deleted posts): apply the pinned snapshot, then continue after it.</summary>
    public async Task<bool> LoadSnapshotAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadSnapshotCoreAsync(ct).ConfigureAwait(false); }
        finally { _ioGate.Release(); }
    }

    private async Task<bool> LoadSnapshotCoreAsync(CancellationToken ct)
    {
        var pinned = await _drive.GetPinnedAsync(_channel, ct).ConfigureAwait(false);
        if (pinned?.Document is null || pinned.Caption != SnapshotCaption) return false;
        var snap = await ReadBatchAsync(pinned, ct).ConfigureAwait(false);
        if (snap is not { Snapshot: true }) return false;
        foreach (var item in snap.Items)
        {
            var (device, payloadItem) = SplitSnapshotItem(item);
            Ledger.Apply(payloadItem, device, pinned.DateUtc);
        }
        if (snap.UpToMessageId > Cursor.LastMessageId) Cursor.LastMessageId = snap.UpToMessageId;
        foreach (var d in DeviceSeqs(snap)) Cursor.Raise(d.Key, d.Value);
        RaiseOwnSeq();
        Bootstrap();
        Save();
        return true;
    }

    /// <summary>Posts the whole ledger as one document and pins it. Call after a pull so it covers everything read.</summary>
    public async Task<TgMessage> PostSnapshotAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await PostSnapshotCoreAsync(ct).ConfigureAwait(false); }
        finally { _ioGate.Release(); }
    }

    private async Task<TgMessage> PostSnapshotCoreAsync(CancellationToken ct)
    {
        var items = new List<BatchItem>(Ledger.Count + Cursor.Contiguous.Count);
        foreach (var i in Ledger.Everything)
            items.Add(new BatchItem(i.Kind, i.Id + SnapshotDeviceSeparator + i.Device, i.Payload, i.UpdatedUtc));
        var seqs = new Dictionary<string, long>(Cursor.Contiguous);
        foreach (var (device, above) in Cursor.Above)
            if (above.Count > 0) seqs[device] = Math.Max(seqs.GetValueOrDefault(device), above.Max);
        foreach (var (device, seq) in seqs)
            items.Add(new BatchItem(SnapshotSeqKind, device, seq.ToString(System.Globalization.CultureInfo.InvariantCulture), _now()));
        var snap = new SyncBatch(BatchCodec.Version, Device, 0, items, Snapshot: true, UpToMessageId: Cursor.LastMessageId);
        using var body = new MemoryStream(BatchCodec.Encode(snap));
        var msg = await _drive.PostDocumentAsync(_channel, SnapshotFileName, body, SnapshotCaption, null, ct).ConfigureAwait(false);
        await _drive.PinAsync(_channel, msg.Id, ct).ConfigureAwait(false);
        return msg;
    }

    // A snapshot keeps each item's original writer (the tie-break needs it): "<id>\u001f<device>".
    internal const char SnapshotDeviceSeparator = '\u001f';
    internal const string SnapshotSeqKind = "_seq";

    private static (string Device, BatchItem Item) SplitSnapshotItem(BatchItem item)
    {
        var cut = item.Id.LastIndexOf(SnapshotDeviceSeparator);
        if (item.Kind == SnapshotSeqKind || cut < 0) return (string.Empty, item with { Kind = string.Empty });
        return (item.Id[(cut + 1)..], item with { Id = item.Id[..cut] });
    }

    private static IEnumerable<KeyValuePair<string, long>> DeviceSeqs(SyncBatch snap) =>
        snap.Items.Where(i => i.Kind == SnapshotSeqKind && long.TryParse(i.Payload, out _))
                  .Select(i => new KeyValuePair<string, long>(i.Id, long.Parse(i.Payload, System.Globalization.CultureInfo.InvariantCulture)));

    private async Task<SyncBatch?> ReadBatchAsync(TgMessage msg, CancellationToken ct)
    {
        if (msg.Document is not { Size: <= BatchCodec.MaxWireBytes }) return null;
        using var ms = new CappedMemoryStream();
        try
        {
        try { await _drive.DownloadAsync(msg, ms, 0, null, ct).ConfigureAwait(false); }
        catch (TelegramFileReferenceExpiredException)
        {
            var fresh = await _drive.GetMessageAsync(msg.Peer, msg.Id, ct).ConfigureAwait(false);
            if (fresh?.Document is null) return null;
            ms.SetLength(0);
            await _drive.DownloadAsync(fresh, ms, 0, null, ct).ConfigureAwait(false);
        }
        return BatchCodec.Decode(ms.ToArray());
        }
        catch (InvalidDataException) { return null; }
    }

    private void Save()
    {
        if (_cursorPath is null) return;
        byte[] bytes;
        lock (_stateGate)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new Checkpoint(Cursor, Ledger.Everything.ToList(), _pending.ToList(), _inFlight,
                _bootstrapped, new Dictionary<string, PlayCounterState>(_earlyPlays, StringComparer.Ordinal)), SyncJson.Options);
        TgStateFile.Write(_cursorPath, bytes);
    }
}
