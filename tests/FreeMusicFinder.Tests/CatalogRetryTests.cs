using System.Net;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Search's Retry and a VPN switched on while Noctis runs (owner test, 2026-10-17): a
/// refusal without Retry-After (often a block on the way, not a limit) must not keep the catalog
/// paused for 30 minutes once the user retries; a wait the service asked for is kept.</summary>
internal static class CatalogRetryTests
{
    internal static async Task RunAsync()
    {
        Check.Section("catalog retry: self-imposed waits end on Retry, Retry-After waits are kept");
        var folder = Check.NewFolder("catalog-retry");
        var http = new DiscoveryTests.Http();
        using var catalog = DiscoveryTests.Catalog(folder, http);

        http.Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        Check.True(await Fails(() => catalog.SearchAsync("North Star", false, CancellationToken.None)), "a refused search reports the refusal");
        var calls = http.Calls.Count;
        Check.True(await Fails(() => catalog.SearchAsync("North Star", true, CancellationToken.None)), "right after a refusal the catalog waits");
        Check.Equal(calls, http.Calls.Count, "while waiting nothing is sent");

        http.Override = null;
        catalog.ForgetGuessedWaits();
        var found = await catalog.SearchAsync("North Star", true, CancellationToken.None);
        Check.True(found.Value.Count > 0, "Retry ends a wait the service did not ask for, and the search goes through");

        http.Limited = true; // 429 with Retry-After: 9 minutes
        Check.True(await Fails(() => catalog.SearchAsync("Guest", true, CancellationToken.None)), "a rate limit is reported");
        http.Limited = false;
        catalog.ForgetGuessedWaits();
        calls = http.Calls.Count;
        Check.True(await Fails(() => catalog.SearchAsync("Guest", true, CancellationToken.None)), "Retry keeps a wait the service asked for (Retry-After)");
        Check.Equal(calls, http.Calls.Count, "and sends nothing during it");

        Check.Section("catalog fallback: an Apple-only answer is reused briefly, then Deezer is asked again");
        var clock = DateTimeOffset.UtcNow;
        var fallbackHttp = new DiscoveryTests.Http { AppleOnly = true };
        using (var fallbackCatalog = DiscoveryTests.Catalog(Check.NewFolder("catalog-fallback"), fallbackHttp, () => clock))
        {
            var apple = await fallbackCatalog.SearchAsync("North Star", false, CancellationToken.None);
            Check.True(apple.Value.Count > 0 && apple.Value.All(a => a.Id.StartsWith("apple:")), "with Deezer unreachable the search falls back to Apple");
            fallbackHttp.AppleOnly = false;
            clock = clock.AddMinutes(5);
            var reused = await fallbackCatalog.SearchAsync("North Star", false, CancellationToken.None);
            Check.True(reused.Cached && reused.Value.All(a => a.Id.StartsWith("apple:")), "within ten minutes the fallback answer is reused");
            clock = clock.AddMinutes(10);
            var deezer = await fallbackCatalog.SearchAsync("North Star", false, CancellationToken.None);
            Check.True(deezer.Value.Any(a => a.Id.StartsWith("deezer:")), "after ten minutes Deezer is asked again (pictures come back), not a day later");
        }

        Check.Section("metadata route: a changed way out means fresh connections");
        ProxyChoice? computer = null;
        using var route = new MetadataRoute("", null, (_, _) => computer);
        var direct = route.KeyFor("api.deezer.com");
        computer = new ProxyChoice(ProxyKind.Http, "127.0.0.1", 10808);
        var proxied = route.KeyFor("api.deezer.com");
        Check.True(direct != proxied, "switching the computer's proxy on changes the route key (old connections are not reused)");
        Check.Equal(proxied, route.KeyFor("api.deezer.com"), "an unchanged route keeps its key (connections stay pooled)");
    }

    private static async Task<bool> Fails(Func<Task> run)
    {
        try { await run(); return false; }
        catch (Exception ex) when (ex is SourcesWaitingException or HttpRequestException or InvalidDataException) { return true; }
    }
}
