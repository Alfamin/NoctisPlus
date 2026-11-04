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
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SyncJson.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static TgSyncCursor Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<TgSyncCursor>(File.ReadAllText(path), SyncJson.Options) ?? new() : new(); }
        catch (JsonException) { return new(); }
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
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public TgSyncLog(ITelegramDrive drive, long channel, string device, TgLedger ledger, Func<DateTime>? now = null, string? cursorPath = null)
    {
        _drive = drive; _channel = channel; Device = device; Ledger = ledger;
        _now = now ?? (() => DateTime.UtcNow);
        _cursorPath = cursorPath;
        Cursor = cursorPath is null ? new TgSyncCursor() : TgSyncCursor.Load(cursorPath);
    }

    public string Device { get; }
    public TgLedger Ledger { get; }
    public TgSyncCursor Cursor { get; }
    public TimeSpan MinPostInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int PendingCount => _pending.Count;
    public DateTime NextPostUtc => _nextPostUtc;
    /// <summary>History calls made by <see cref="PullAsync"/> (the budget: one per 100 posts).</summary>
    public int HistoryCalls { get; private set; }

    /// <summary>A local change: applied to the ledger now, posted with the next flush.</summary>
    public void Record(string kind, string id, string payload, DateTime? updatedUtc = null)
    {
        var item = new BatchItem(kind, id, payload, updatedUtc ?? _now());
        Ledger.Apply(item, Device);
        _pending.Add(item);
    }

    public void RecordJson<T>(string kind, string id, T state, DateTime? updatedUtc = null) =>
        Record(kind, id, SyncJson.Serialize(state), updatedUtc);

    /// <summary>Posts the pending batch if pacing allows. Returns false when nothing was posted (empty, paced, or flood wait).</summary>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (_pending.Count == 0) return false;
        if (!await _flushGate.WaitAsync(0, ct).ConfigureAwait(false)) return false; // one flush at a time
        try
        {
            var now = _now();
            if (_pending.Count == 0 || now < _nextPostUtc) return false;
            // Reserve the seq before posting: a crash or an ambiguous failure never reuses it.
            var batch = new SyncBatch(BatchCodec.Version, Device, Cursor.OwnSeq + 1, _pending.ToList());
            Cursor.OwnSeq = batch.Seq;
            Save();
            try
            {
                using var body = new MemoryStream(BatchCodec.Encode(batch));
                await _drive.PostDocumentAsync(_channel, BatchFileName, body, BatchCaption, null, ct).ConfigureAwait(false);
            }
            catch (TelegramFloodWaitException e)
            {
                _nextPostUtc = now + e.RetryAfter;
                // FLOOD_WAIT is a definite rejection (nothing landed), so the seq is free again: no gap for readers.
                if (Cursor.OwnSeq == batch.Seq) { Cursor.OwnSeq = batch.Seq - 1; Save(); }
                return false;
            }
            Cursor.Mark(Device, batch.Seq);
            _pending.RemoveRange(0, batch.Items.Count); // keep items recorded while the post was in flight
            _nextPostUtc = now + MinPostInterval;
            Save();
            return true;
        }
        finally { _flushGate.Release(); }
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
                    if (batch is not null && !batch.Snapshot && !Cursor.Seen(batch.Device, batch.Seq))
                    {
                        foreach (var item in batch.Items) Ledger.Apply(item, batch.Device);
                        Cursor.Mark(batch.Device, batch.Seq);
                        applied++;
                    }
                }
                Cursor.LastMessageId = msg.Id;
            }
            if (page.Count < 100) break;
        }
        RaiseOwnSeq();
        Save();
        return applied;
    }

    /// <summary>A new device (or one offline past the deleted posts): apply the pinned snapshot, then continue after it.</summary>
    public async Task<bool> LoadSnapshotAsync(CancellationToken ct = default)
    {
        var pinned = await _drive.GetPinnedAsync(_channel, ct).ConfigureAwait(false);
        if (pinned?.Document is null || pinned.Caption != SnapshotCaption) return false;
        var snap = await ReadBatchAsync(pinned, ct).ConfigureAwait(false);
        if (snap is not { Snapshot: true }) return false;
        foreach (var item in snap.Items)
        {
            var (device, payloadItem) = SplitSnapshotItem(item);
            Ledger.Apply(payloadItem, device);
        }
        if (snap.UpToMessageId > Cursor.LastMessageId) Cursor.LastMessageId = snap.UpToMessageId;
        foreach (var d in DeviceSeqs(snap)) Cursor.Raise(d.Key, d.Value);
        RaiseOwnSeq();
        Save();
        return true;
    }

    /// <summary>Posts the whole ledger as one document and pins it. Call after a pull so it covers everything read.</summary>
    public async Task<TgMessage> PostSnapshotAsync(CancellationToken ct = default)
    {
        var items = new List<BatchItem>(Ledger.Count + Cursor.Contiguous.Count);
        foreach (var i in Ledger.Everything)
            items.Add(new BatchItem(i.Kind, i.Id + SnapshotDeviceSeparator + i.Device, i.Payload, i.UpdatedUtc));
        foreach (var (device, seq) in Cursor.Contiguous)
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
        using var ms = new MemoryStream();
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

    private void Save() { if (_cursorPath is not null) Cursor.Save(_cursorPath); }
}
