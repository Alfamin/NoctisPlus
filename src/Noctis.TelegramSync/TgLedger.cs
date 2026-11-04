using System.Globalization;
using System.Text;
using Noctis.Services.Sync;

namespace Noctis.TelegramSync;

/// <summary>
/// The device's view of the merged log: newest known state of every (kind, id). Same conflict
/// rule as <see cref="SyncStore.Upsert"/>: last-writer-wins on the stamp, ties broken by device
/// id (ordinal). Review M2: stamps are bounded by a time every device agrees on, never by the
/// reader's own clock: a remote item may be at most <see cref="SyncStore.MaxClockSkew"/> past the
/// Telegram server's date of the message that carried it (<see cref="Apply(BatchItem,string,DateTime)"/>),
/// so two devices that read the same post at different times store the same stamp. A local edit is
/// bounded by the writer's clock until its own post is read back (<see cref="AdoptPosted"/>).
/// In memory so a 10,000-song merge stays far under budget; the caller persists it.
/// </summary>
public sealed class TgLedger
{
    private readonly Dictionary<(string Kind, string Id), SyncItem> _items = new();
    // This device's own edits whose post it has not read back yet: their stamp may still be lowered (AdoptPosted).
    private readonly HashSet<(string Kind, string Id)> _provisional = new();
    // The best remote item that lost ONLY to such an edit. Readers compare it with the bounded stamp; so must we,
    // once ours is adopted. Not part of the state (Fingerprint); kept with the ledger so a restart cannot lose it.
    private readonly Dictionary<(string Kind, string Id), SyncItem> _shadow = new();
    private readonly Func<DateTime> _now;
    private long _seq;
    private readonly object _gate = new();

    public TgLedger(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>UTC within [<see cref="LibrarySyncService.OldestStamp"/>, now + <see cref="SyncStore.MaxClockSkew"/>].</summary>
    public static DateTime ClampStamp(DateTime value, DateTime now)
    {
        value = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        var max = now + SyncStore.MaxClockSkew;
        return value < LibrarySyncService.OldestStamp ? LibrarySyncService.OldestStamp : value > max ? max : value;
    }

    /// <summary>A local edit by <paramref name="device"/>, bounded by this device's clock. True when the ledger changed.</summary>
    public bool Apply(BatchItem item, string device)
    {
        lock (_gate) return ApplyCore(item, device, _now());
    }

    /// <summary>This device's own edit, provisional until its post is read back (<see cref="AdoptPosted"/>).</summary>
    public bool ApplyOwn(BatchItem item, string device, DateTime bound)
    {
        lock (_gate)
        {
            if (!ApplyCore(item, device, bound, own: true)) return false;
            _provisional.Add((item.Kind, item.Id));
            return true;
        }
    }

    /// <summary>An item read from a post: bounded by <paramref name="postedUtc"/>, the server's date of that
    /// message (the same for every reader). True when the ledger changed.</summary>
    public bool Apply(BatchItem item, string device, DateTime postedUtc)
    {
        lock (_gate) return ApplyCore(item, device, postedUtc);
    }

    /// <summary>The stamp every device stores for <paramref name="stamp"/> carried by a post dated <paramref name="postedUtc"/>.</summary>
    public static DateTime Canonical(DateTime stamp, DateTime postedUtc) => ClampStamp(stamp, ToUtc(postedUtc));

    private bool ApplyCore(BatchItem item, string device, DateTime bound, bool own = false)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Payload)) return false;
        var stamp = Canonical(item.UpdatedUtc, bound);
        device ??= string.Empty;
        var key = (item.Kind, item.Id);
        // Deterministic: depends only on the two items, never on when or where this runs.
        if (_items.TryGetValue(key, out var stored) && !Wins(stamp, device, item.Payload, stored))
        {
            // Lost to our own not-yet-bounded edit: keep it to compare once ours has its final stamp.
            if (!own && _provisional.Contains(key) && stored.Device != device
                && (!_shadow.TryGetValue(key, out var best) || Wins(stamp, device, item.Payload, best)))
                _shadow[key] = new SyncItem(item.Kind, item.Id, item.Payload, stamp, device, 0);
            return false;
        }
        var candidate = new SyncItem(item.Kind, item.Id, item.Payload, stamp, device, ++_seq);
        // A remote item beat our raw stamp, so it beats our bounded one too; the best of it and the held item wins.
        // (A newer own edit keeps both the provisional mark and the held item for its own read-back.)
        if (!own && stored is not null && _provisional.Remove(key) && _shadow.Remove(key, out var kept)
            && Wins(kept.UpdatedUtc, kept.Device, kept.Payload, candidate))
            candidate = kept with { Seq = candidate.Seq };
        _items[key] = candidate;
        return true;
    }

    private static bool Wins(DateTime stamp, string device, string payload, SyncItem stored)
        => stamp > stored.UpdatedUtc
           || (stamp == stored.UpdatedUtc && string.CompareOrdinal(device, stored.Device) > 0 && payload != stored.Payload);

    /// <summary>
    /// The writer reads its own post back: an item it still holds exactly as recorded (same device, payload and
    /// raw stamp) takes the stamp every reader gave it. Only the first copy of a post (by message order) can
    /// match, as only the first copy is applied by the others. A later edit of the same item is left alone.
    /// </summary>
    public bool AdoptPosted(BatchItem item, string device, DateTime postedUtc)
    {
        lock (_gate)
        {
            if (item is null || !_items.TryGetValue((item.Kind, item.Id), out var stored)) return false;
            var key = (item.Kind, item.Id);
            var raw = ClampStamp(item.UpdatedUtc, DateTime.MaxValue - SyncStore.MaxClockSkew);
            if (!_provisional.Contains(key) || stored.Device != device || stored.Payload != item.Payload || stored.UpdatedUtc != raw) return false;
            var adopted = stored with { UpdatedUtc = Canonical(item.UpdatedUtc, postedUtc), Seq = ++_seq };
            _provisional.Remove(key);
            // The same comparison every reader made: a remote item ours only seemed to beat now wins.
            if (_shadow.Remove(key, out var kept) && Wins(kept.UpdatedUtc, kept.Device, kept.Payload, adopted))
                adopted = kept with { Seq = adopted.Seq };
            var changed = adopted != stored;
            _items[key] = adopted;
            return changed;
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public SyncItem? Get(string kind, string id) { lock (_gate) return _items.GetValueOrDefault((kind, id)); }

    public IEnumerable<SyncItem> All(string kind) { lock (_gate) return _items.Values.Where(i => i.Kind == kind).ToArray(); }

    public IEnumerable<SyncItem> Everything { get { lock (_gate) return _items.Values.ToArray(); } }

    /// <summary>Own edits not read back yet and the remote items held against them (saved with the ledger).</summary>
    internal (List<string> Provisional, List<SyncItem> Shadowed) Pending()
    {
        lock (_gate) return (_provisional.Select(k => k.Kind + SnapshotKeySeparator + k.Id).ToList(), _shadow.Values.ToList());
    }

    internal void RestorePending(IEnumerable<string>? provisional, IEnumerable<SyncItem>? shadowed)
    {
        lock (_gate)
        {
            foreach (var key in provisional ?? [])
                if (key.IndexOf(SnapshotKeySeparator) is var cut and > 0) _provisional.Add((key[..cut], key[(cut + 1)..]));
            foreach (var item in shadowed ?? []) _shadow[(item.Kind, item.Id)] = item;
        }
    }

    private const char SnapshotKeySeparator = '\u001f';

    internal void Restore(IEnumerable<SyncItem> items)
    {
        lock (_gate)
            foreach (var item in items)
            {
                _items[(item.Kind, item.Id)] = item;
                _seq = Math.Max(_seq, item.Seq);
            }
    }

    /// <summary>A stable fingerprint of the merged state (two converged devices have the same one).</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var i in Everything.OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal))
            sb.Append(i.Kind).Append('|').Append(i.Id).Append('|').Append(i.Payload).Append('|')
              .Append(i.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|').Append(i.Device).Append('\n');
        return sb.ToString();
    }
}
