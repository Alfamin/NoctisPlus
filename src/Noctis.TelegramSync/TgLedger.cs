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

    /// <summary>An item read from a post: bounded by <paramref name="postedUtc"/>, the server's date of that
    /// message (the same for every reader). True when the ledger changed.</summary>
    public bool Apply(BatchItem item, string device, DateTime postedUtc)
    {
        lock (_gate) return ApplyCore(item, device, postedUtc);
    }

    /// <summary>The stamp every device stores for <paramref name="stamp"/> carried by a post dated <paramref name="postedUtc"/>.</summary>
    public static DateTime Canonical(DateTime stamp, DateTime postedUtc) => ClampStamp(stamp, ToUtc(postedUtc));

    private bool ApplyCore(BatchItem item, string device, DateTime bound)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Payload)) return false;
        var stamp = Canonical(item.UpdatedUtc, bound);
        device ??= string.Empty;
        var key = (item.Kind, item.Id);
        // Deterministic: depends only on the two items, never on when or where this runs.
        if (_items.TryGetValue(key, out var stored) && !Wins(stamp, device, item.Payload, stored)) return false;
        _items[key] = new SyncItem(item.Kind, item.Id, item.Payload, stamp, device, ++_seq);
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
            var raw = ClampStamp(item.UpdatedUtc, DateTime.MaxValue - SyncStore.MaxClockSkew);
            if (stored.Device != device || stored.Payload != item.Payload || stored.UpdatedUtc != raw) return false;
            var canonical = Canonical(item.UpdatedUtc, postedUtc);
            if (canonical == stored.UpdatedUtc) return false;
            _items[(item.Kind, item.Id)] = stored with { UpdatedUtc = canonical, Seq = ++_seq };
            return true;
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
