namespace FreeMusicFinder;

/// <summary>Only identical cache keys serialize. Entries disappear when their last active/waiting reader leaves.</summary>
internal sealed class CatalogLocks
{
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public int Readers; }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public async Task<IDisposable> EnterAsync(string key, CancellationToken ct)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new();
            entry.Readers++;
        }
        try { await entry.Gate.WaitAsync(ct).ConfigureAwait(false); }
        catch { Release(key, entry, entered: false); throw; }
        return new Lease(() => Release(key, entry, entered: true));
    }
    private void Release(string key, Entry entry, bool entered)
    {
        lock (_entries)
        {
            if (entered) entry.Gate.Release();
            if (--entry.Readers == 0) { _entries.Remove(key); entry.Gate.Dispose(); }
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
