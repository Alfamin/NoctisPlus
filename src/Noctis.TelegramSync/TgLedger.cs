using System.Globalization;
using System.Text;
using Noctis.Services.Sync;

namespace Noctis.TelegramSync;

/// <summary>
/// The device's view of the merged log: newest known state of every (kind, id). Same conflict
/// rule as <see cref="SyncStore.Upsert"/>: last-writer-wins on the stamp, ties broken by device
/// id (ordinal), a stored stamp past the clock by more than <see cref="SyncStore.MaxClockSkew"/>
/// loses to any write. Incoming stamps are clamped like <c>LibrarySyncService.ClampStamp</c>
/// (that method is internal to Noctis.Core, so the two-line rule is repeated here on the same
/// public constants). In memory so a 10,000-song merge stays far under budget; the caller
/// persists it (stage 2 writes it through to the desktop's SyncStore).
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

    /// <summary>Apply one item written by <paramref name="device"/>. True when the ledger changed.</summary>
    public bool Apply(BatchItem item, string device)
    {
        lock (_gate) return ApplyCore(item, device);
    }

    private bool ApplyCore(BatchItem item, string device)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Payload)) return false;
        var now = _now();
        var stamp = ClampStamp(item.UpdatedUtc, now);
        device ??= string.Empty;
        var key = (item.Kind, item.Id);
        if (_items.TryGetValue(key, out var stored))
        {
            var stale = stored.UpdatedUtc > now + SyncStore.MaxClockSkew;
            var newer = stale || stamp > stored.UpdatedUtc
                        || (stamp == stored.UpdatedUtc && string.CompareOrdinal(device, stored.Device) > 0 && item.Payload != stored.Payload);
            if (!newer) return false;
        }
        _items[key] = new SyncItem(item.Kind, item.Id, item.Payload, stamp, device, ++_seq);
        return true;
    }

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
