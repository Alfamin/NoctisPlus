using System.Collections.Concurrent;
using System.Diagnostics;

namespace FreeMusicFinder;

/// <summary>For you's optional sound lane: rank cached vectors immediately, warm the latest candidate pool off-thread.
/// A page is never held for a cold model/network and never rearranged after it was shown. The next refresh reuses the work.
/// Taste and candidate previews use the SAME domain. Local-only taste stays out until its domain gap has been validated.</summary>
internal sealed class SonicService(SonicCache cache, ISonicEncoder encoder, SonicPreviews previews,
    Func<TasteRecording, CancellationToken, Task<CatalogSong?>> resolve, Func<bool> busy, Action<string> log) : IDiscoverSonic, IDisposable
{
    private sealed record Work(List<CatalogSong> Candidates, List<TasteRecording> Liked, List<TastePlay> Plays);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, CatalogSong> _resolved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _failed = new(StringComparer.Ordinal);
    private Work? _pending;
    private bool _working, _disposed;
    private volatile bool _enabled;
    private CancellationTokenSource? _pass;
    internal double Blend { get; set; } = 0.2;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            lock (_sync)
            {
                _enabled = value;
                if (!value)
                {
                    _pending = null;
                    try { _pass?.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }
    public bool Ready => encoder.Ready;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, double>> RankAsync(IReadOnlyList<CatalogSong> candidates,
        IReadOnlyList<TasteRecording> liked, IReadOnlyList<TastePlay> plays, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_enabled || _disposed || !encoder.Ready || liked.Count < 2) return Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
        // A second page replaces waiting work; it does not grow an unbounded analysis queue.
        lock (_sync)
        {
            _pending = new(candidates.Take(120).ToList(), liked.Take(20).ToList(), plays.TakeLast(4000).ToList());
            if (!_working) { _working = true; Completion = Task.Run(WarmAsync); }
        }
        return Task.Run<IReadOnlyDictionary<string, double>>(() =>
        {
            var vectors = new Dictionary<string, SonicVector>(StringComparer.Ordinal);
            foreach (var song in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var key = DiscoverIdentity.Key(song);
                if (cache.Read(key, song.Id) is { } v) vectors[key] = v;
            }
            var taste = Examples(liked, plays);
            var result = SonicRanking.Multipliers(candidates, vectors, taste, Blend);
            log($"sonic rank: {vectors.Count}/{candidates.Count} vectors, {taste.Count} taste, {result.Count} scores, blend {Blend:0.00}");
            return result;
        }, ct);
    }

    private List<SonicExample> Examples(IReadOnlyList<TasteRecording> liked, IReadOnlyList<TastePlay> plays)
    {
        var examples = new List<SonicExample>();
        foreach (var like in liked)
        {
            var key = DiscoverIdentity.Key(like.Title, like.Artist);
            if (_resolved.TryGetValue(key, out var song) && cache.Read(DiscoverIdentity.Key(song), song.Id) is { } v) examples.Add(new(key, v, like.Weight));
        }
        // Weak negatives only from real early-skip evidence, capped by recording. Never use unexposed cards as negatives.
        foreach (var group in plays.Where(p => p.Weight < 0).GroupBy(p => DiscoverIdentity.Key(p.Title, p.Artist)).Take(20))
            if (_resolved.TryGetValue(group.Key, out var song) && cache.Read(DiscoverIdentity.Key(song), song.Id) is { } v)
                examples.Add(new(group.Key, v, Math.Max(-1, group.Sum(p => p.Weight))));
        return examples;
    }

    private async Task WarmAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Work? work;
                lock (_sync) { work = _pending; _pending = null; if (work is null) return; }
                if (!_enabled || !encoder.Ready || Noctis.Services.OnlineAccess.Shared.IsOffline) continue;
                var watch = Stopwatch.StartNew();
                int done = 0, errors = 0;
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_sync) _pass = pass;
                pass.CancelAfter(TimeSpan.FromMinutes(3));
                var token = pass.Token;
                var taste = work.Liked.Concat(work.Plays.Where(p => p.Weight < 0).GroupBy(p => DiscoverIdentity.Key(p.Title, p.Artist))
                    .Take(20).Select(g => new TasteRecording(g.First().Artist, g.First().Title, -1))).ToList();
                foreach (var like in taste)
                {
                    if (!_enabled) break;
                    await BudgetAsync(token).ConfigureAwait(false);
                    var key = DiscoverIdentity.Key(like.Title, like.Artist);
                    if (key.Length == 0 || _resolved.ContainsKey(key) || _failed.TryGetValue(key, out var until) && until > DateTime.UtcNow) continue;
                    try
                    {
                        if (await resolve(like, token).ConfigureAwait(false) is { } song) _resolved[key] = song;
                        else _failed[key] = DateTime.UtcNow.AddHours(12);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { _failed[key] = DateTime.UtcNow.AddMinutes(1); log("sonic taste lookup deferred: " + ex.GetType().Name); }
                }
                // Bounded pipelining: up to four network requests, one encoder. Existing Search has its own HTTP lane.
                var tasteSongs = taste.Select(t => _resolved.GetValueOrDefault(DiscoverIdentity.Key(t.Title, t.Artist))).OfType<CatalogSong>().DistinctBy(s => s.Id).ToList();
                async ValueTask Embed(CatalogSong song, CancellationToken stop)
                {
                    if (!_enabled || !encoder.Ready) return;
                    await BudgetAsync(stop).ConfigureAwait(false);
                    var key = DiscoverIdentity.Key(song);
                    if (cache.Read(key, song.Id) is not null || _failed.TryGetValue(song.Id, out var until) && until > DateTime.UtcNow) return;
                    try
                    {
                        var audio = await previews.FetchAsync(song, stop).ConfigureAwait(false);
                        if (audio is null) { _failed[song.Id] = DateTime.UtcNow.AddHours(12); return; }
                        await BudgetAsync(stop).ConfigureAwait(false);
                        if (!_enabled) return;
                        if (await encoder.EncodeAsync(audio, stop).ConfigureAwait(false) is { } vector) { cache.Write(key, song.Id, vector); Interlocked.Increment(ref done); }
                        else _failed[song.Id] = DateTime.UtcNow.AddHours(12);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { _failed[song.Id] = DateTime.UtcNow.AddMinutes(1); Interlocked.Increment(ref errors); }
                }
                var parallel = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 };
                await Parallel.ForEachAsync(tasteSongs, parallel, Embed).ConfigureAwait(false);
                // No usable taste means no reason to fetch a hundred candidates. Keep the metadata page instead.
                if (Examples(work.Liked, work.Plays).Count(t => t.Weight > 0) < 2) { log("sonic warm: insufficient preview taste; keeping normal picks"); continue; }
                await Parallel.ForEachAsync(work.Candidates.DistinctBy(s => s.Id), parallel, Embed).ConfigureAwait(false);
                log($"sonic warm: {done} new vectors, {errors} deferred, {watch.Elapsed.TotalSeconds:0.0} s");
                if (_resolved.Count > 256) _resolved.Clear();
                if (_failed.Count > 1000) _failed.Clear();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log("sonic warming deferred: " + ex.GetType().Name); }
        finally
        {
            lock (_sync)
            {
                _working = false;
                _pass = null;
                if (_pending is not null && !_disposed) { _working = true; Completion = Task.Run(WarmAsync); }
            }
        }
    }

    private async Task BudgetAsync(CancellationToken ct)
    {
        while (busy()) await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        Noctis.Services.OnlineAccess.Shared.ThrowIfOffline();
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _pending = null; }
        _lifetime.Cancel();
        _ = Completion.ContinueWith(_ => { previews.Dispose(); encoder.Dispose(); _lifetime.Dispose(); }, TaskScheduler.Default);
    }
}
