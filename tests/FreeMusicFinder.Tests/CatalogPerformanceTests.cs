using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class CatalogPerformanceTests
{
    public static async Task RunAsync()
    {
        Check.Section("catalog performance: cached 200-release artist, no network");
        var folder = Check.NewFolder("catalog-performance");
        var releases = Enumerable.Range(0, 200).Select(i => DiscoveryTests.Orbit with { Id = "deezer:" + (1000 + i), Title = "Release " + i, Explicit = true }).ToList();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("artist:" + DiscoveryTests.Star.Id))).ToLowerInvariant() + ".json";
        File.WriteAllText(Path.Combine(folder, key), JsonSerializer.Serialize(new { Version = 1, SavedAt = DateTimeOffset.UtcNow, Value = new CatalogProfile(DiscoveryTests.Star, [], releases) }));
        var http = new DiscoveryTests.Http { Offline = true }; using var catalog = DiscoveryTests.Catalog(folder, http);
        var first = await catalog.ArtistAsync(DiscoveryTests.Star, false, default);
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 30; i++) await catalog.ArtistAsync(DiscoveryTests.Star, false, default);
        Console.WriteLine($"  PERF  200-release cached artist: {elapsed.Elapsed.TotalMilliseconds / 30:F3} ms average, 30 repeats");
        Check.Equal(200, first.Value.Releases.Count, "large cached artist retains every distinct release");
        Check.Equal(0, http.Calls.Count, "repeated large artist opens use no network");
        Check.True(ReferenceEquals(first.Value, (await catalog.ArtistAsync(DiscoveryTests.Star, false, default)).Value), "repeated artist opens reuse the parsed, consolidated profile");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.Offline = false; http.Override = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(); };
        using var blockedStop = new CancellationTokenSource();
        var blocked = catalog.AlbumAsync(DiscoveryTests.Orbit, true, blockedStop.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var readyStop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var ready = await catalog.ArtistAsync(DiscoveryTests.Star, false, readyStop.Token);
        Check.True(ready.Cached && !blocked.IsCompleted, "cached artist opens immediately while unrelated network request is stalled");
        Check.True((await catalog.SavedArtistAsync(DiscoveryTests.Star, readyStop.Token))?.Value.Releases.Count == 200, "cache-only artist preview does not wait for the network gate");
        blockedStop.Cancel(); await Check.ThrowsAsync<OperationCanceledException>(async () => await blocked, "stalled request cancels without invalidating the usable cached artist");

        var appleHttp = new DiscoveryTests.Http { AppleOnly = true };
        using var apple = DiscoveryTests.Catalog(Check.NewFolder("catalog-fast-apple"), appleHttp);
        var cold = await apple.ArtistAsync(new("apple:75", "North Star"), false, default);
        Check.True(cold.Value.Releases.Count > 0 && appleHttp.Calls.Count == 1, "cold Apple artist needs one release lookup, with no unused popular-song request");
        var queuedHttp = new DiscoveryTests.Http(); var enteredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queuedHttp.Override = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/artist/42") { enteredFirst.TrySetResult(); await releaseFirst.Task.WaitAsync(ct); }
            queuedHttp.Override = null;
            using var forward = new HttpClient(queuedHttp, disposeHandler: false); return await forward.GetAsync(request.RequestUri, ct);
        };
        using var queued = DiscoveryTests.Catalog(Check.NewFolder("catalog-fast-single-flight"), queuedHttp);
        var a = queued.ArtistAsync(DiscoveryTests.Star, false, default); await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var b = queued.ArtistAsync(DiscoveryTests.Star, false, default); releaseFirst.TrySetResult(); await Task.WhenAll(a, b);
        Check.Equal(1, queuedHttp.Calls.Count(u => u.AbsolutePath == "/artist/42/albums" && u.Query.Contains("index=1")), "concurrent opens share one artist refresh and all pages, without duplicate API work");
        Check.True(!queuedHttp.Calls.Any(u => u.AbsolutePath.EndsWith("/top")), "Deezer skips unused popular-song lookup as well");
        await IndependentRequests();
    }

    private static async Task IndependentRequests()
    {
        using var payload = new DiscoveryTests.Http();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new DiscoveryTests.Http { Override = async (r, ct) =>
        {
            if (r.RequestUri!.AbsolutePath == "/album/111") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            using var client = new HttpClient(payload, disposeHandler: false); return await client.GetAsync(r.RequestUri, ct);
        } };
        using var catalog = DiscoveryTests.Catalog(Check.NewFolder("catalog-independent"), http);
        using var stop = new CancellationTokenSource(); var blocked = catalog.AlbumAsync(DiscoveryTests.Orbit, false, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var cold = await catalog.ArtistAsync(DiscoveryTests.Star, false, deadline.Token);
            Check.True(cold.Value.Releases.Count > 0 && !blocked.IsCompleted, "an uncached artist opens while an unrelated album request is stalled");
            Console.WriteLine($"  PERF  cold artist alongside stalled request: {elapsed.Elapsed.TotalMilliseconds:F1} ms (synthetic HTTP)");
        }
        catch (OperationCanceledException) { Check.True(false, "uncached artist was blocked behind unrelated metadata"); }
        finally { stop.Cancel(); try { await blocked; } catch (OperationCanceledException) { } }

        int active = 0, max = 0; var sync = new object();
        var concurrent = new DiscoveryTests.Http { Override = async (r, ct) =>
        {
            lock (sync) { max = Math.Max(max, ++active); }
            try { await Task.Delay(30, ct); using var client = new HttpClient(payload, disposeHandler: false); return await client.GetAsync(r.RequestUri, ct); }
            finally { lock (sync) active--; }
        } };
        using var bounded = DiscoveryTests.Catalog(Check.NewFolder("catalog-bounded"), concurrent);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => bounded.SearchSongsAsync("query" + i, CancellationToken.None)));
        Check.True(max is > 1 and <= 3, "independent catalog queries run concurrently with at most three metadata responses in flight");
        var starts = new List<long>(); using var applePayload = new DiscoveryTests.Http { AppleOnly = true };
        var paced = new DiscoveryTests.Http { Override = async (r, ct) =>
        {
            if (r.RequestUri!.Host == "itunes.apple.com") lock (starts) starts.Add(Stopwatch.GetTimestamp());
            using var client = new HttpClient(applePayload, disposeHandler: false); return await client.GetAsync(r.RequestUri, ct);
        } };
        using var apple = new ArtistCatalog(Check.NewFolder("catalog-concurrent-apple-pace"), paced, applePace: TimeSpan.FromMilliseconds(40));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => apple.SearchSongsAsync("Paced" + i, CancellationToken.None)));
        Check.True(starts.Count == 4 && starts.Zip(starts.Skip(1)).All(p => Stopwatch.GetElapsedTime(p.First, p.Second) >= TimeSpan.FromMilliseconds(20)), "concurrent catalog requests still respect Apple's shared start pacing");

        var locks = new CatalogLocks(); using var leader = await locks.EnterAsync("same", CancellationToken.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Check.ThrowsAsync<OperationCanceledException>(async () => { using var waiting = await locks.EnterAsync("same", cancel.Token); }, "a cancelled waiter does not acquire or poison the same-key request gate");
        using var unrelated = await locks.EnterAsync("other", CancellationToken.None);
        leader.Dispose(); using var recovered = await locks.EnterAsync("same", CancellationToken.None);
        Check.True(true, "the per-key gate remains reusable after cancellation and independent requests");
    }
}
