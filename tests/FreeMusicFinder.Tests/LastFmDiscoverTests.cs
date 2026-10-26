using System.Net;
using System.Text.Json;

namespace FreeMusicFinder.Tests;

internal static class LastFmDiscoverTests
{
    // Fake fixture, never a real account key.
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static string Artists(params (string Name, object Score)[] rows) => JsonSerializer.Serialize(new
    { similarartists = new { artist = rows.Select(r => new { name = r.Name, match = r.Score }).ToArray() } });

    public static async Task RunAsync()
    {
        Check.Section("Last.fm: safe credentials, bounded caches, fallback and speed");
        await CacheAndParsing(); await Failures(); await Lifetime(); await ProtocolAndPacing(); await CatalogSongs(); await ParallelSeeds(); await SavedPage(); await RedirectedCaches();
    }

    private static async Task CacheAndParsing()
    {
        var folder = Check.NewFolder("lastfm-cache"); var now = DateTimeOffset.UtcNow; string? agent = null;
        var http = new DiscoveryTests.Http { Override = (r, _) =>
        {
            agent = r.Headers.UserAgent.ToString();
            return Task.FromResult(Json(Artists(("New Artist", "0.75"), ("Other", 2), ("New Artist", 0.5), ("Juice WRLD", 1), ("Invalid", "NaN"))));
        } };
        using (var last = new LastFmSimilar(folder, http, () => Key, () => now, TimeSpan.Zero))
        {
            var list = await last.SimilarAsync("Juice WRLD", CancellationToken.None);
            Check.True(list.Count == 2 && list[0].Name == "Other" && list[0].Score == 1 && list[1].Score == .75, "Last.fm parses string/numeric matches, bounds scores, removes duplicates/self/NaN");
            Check.True(agent?.Contains("NoctisFreeMusicFinder") == true && http.Calls.Single().Host == "ws.audioscrobbler.com", "Last.fm uses injected HTTP and the app User-Agent");
            await last.SimilarAsync("Juice WRLD", CancellationToken.None);
            Check.Equal(1, http.Calls.Count, "fresh artist relations need no second request");
        }
        using (var again = new LastFmSimilar(folder, new DiscoveryTests.Http { Offline = true }, () => Key, () => now))
            Check.Equal(2, (await again.SimilarAsync("Juice WRLD", CancellationToken.None)).Count, "the Last.fm cache survives restart and works offline");
        var disk = File.ReadAllText(Path.Combine(folder, "lastfm.json"));
        Check.False(disk.Contains(Key) || disk.Contains("api_key") || disk.Contains("audioscrobbler.com/"), "relation caches contain no key or credential URL");
        var names = LastFmSimilar.Filter(new[] { "Drake & Future", "Future & Juice WRLD", "Juice WRLD archive", "Sade", "Earth, Wind & Fire", "Rico Ace" }
            .Select(n => new SimilarArtist(n, 1)), "Juice WRLD", ["Drake", "Future"]);
        Check.True(names.Select(n => n.Name).SequenceEqual(["Sade", "Earth, Wind & Fire", "Rico Ace"]), "known collabs and seed archives are removed, unrelated bands stay");
        var invalid = new DiscoveryTests.Http();
        using var disabled = new LastFmSimilar(Check.NewFolder("lastfm-invalid"), invalid, () => "not a key");
        Check.True(!disabled.Enabled && (await disabled.SimilarAsync("Name", CancellationToken.None)).Count == 0 && invalid.Calls.Count == 0, "absent/malformed keys never make a credential request");
        Check.False(LastFmSimilar.ValidKey(Key + "\nextra"), "key validation rejects extra lines");
    }

    private static async Task Failures()
    {
        var now = DateTimeOffset.UtcNow; string key = Key; int code = 10;
        var http = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(Json(code == 0 ? Artists(("Fresh", 1)) : "{\"error\":" + code + ",\"message\":\"" + Key + " provider echo\"}")) };
        using var last = new LastFmSimilar(Check.NewFolder("lastfm-errors"), http, () => key, () => now, TimeSpan.Zero);
        async Task<string> Error(string name)
        { try { await last.SimilarAsync(name, CancellationToken.None); return ""; } catch (Exception ex) { return ex.ToString(); } }
        var error = await Error("A");
        Check.True(error.Contains("rejected") && !error.Contains(Key) && !last.Notice.Contains(Key), "invalid-key notices and exception chains never echo credentials/provider messages");
        await Error("B"); Check.Equal(1, http.Calls.Count, "a rejected key is not retried for every seed");
        key = "abcdef0123456789abcdef0123456789"; code = 0;
        Check.True((await last.SimilarAsync("C", CancellationToken.None)).Count == 1 && last.Notice.Length == 0, "changing the key recovers without restarting");
        code = 29; Check.True((await Error("D")).Contains("rate limited"), "error 29 starts a cooldown");
        int before = http.Calls.Count; await Error("E"); Check.Equal(before, http.Calls.Count, "remaining seeds respect the cooldown");
        now = now.AddMinutes(2); code = 0;
        Check.Equal(1, (await last.SimilarAsync("E", CancellationToken.None)).Count, "Last.fm recovers after the cooldown");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
        try { await last.SimilarAsync("C", cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check.True(stopped, "caller cancellation stays cancellation on cache hits");
        var leak = new DiscoveryTests.Http { Override = (_, _) => throw new HttpRequestException("https://host/?api_key=" + Key) };
        using var network = new LastFmSimilar(Check.NewFolder("lastfm-network"), leak, () => Key, pace: TimeSpan.Zero);
        string message = ""; try { await network.SimilarAsync("A", CancellationToken.None); } catch (Exception ex) { message = ex.ToString(); }
        Check.True(message.Contains("VPN/proxy") && !message.Contains(Key), "underlying HTTP failures are sanitized before reaching logs/UI");
        var fallbackHttp = new DiscoveryTests.Http { Override = (r, _) => Task.FromResult(Json(r.RequestUri!.Host == "musicbrainz.org"
            ? "{\"artists\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Seed\"}]}"
            : "[{\"artist_mbid\":\"22222222-2222-2222-2222-222222222222\",\"name\":\"Fallback\",\"score\":100}]")) };
        using var lb = new SimilarArtists(Check.NewFolder("lastfm-fallback"), fallbackHttp, musicBrainzPace: TimeSpan.Zero);
        var adapter = new CatalogForDiscover(() => throw new Exception("Successful ListenBrainz must not ask Apple"), _ => [], () => lb, () => network);
        var related = await adapter.SimilarAsync(new("Seed", 1, 0), null, CancellationToken.None);
        Check.True(related.Single().Name == "Fallback" && adapter.Notice.Contains("VPN/proxy"), "Last.fm failure falls back and retains a useful notice");
        var noKey = new CatalogForDiscover(() => throw new Exception("Not needed"), _ => [], () => lb);
        Check.Equal("Fallback", (await noKey.SimilarAsync(new("Seed", 1, 0), null, CancellationToken.None)).Single().Name, "ListenBrainz still works without a Last.fm key");
    }

    private static async Task Lifetime()
    {
        var now = DateTimeOffset.UtcNow; int calls = 0; bool empty = true, store = true;
        var http = new DiscoveryTests.Http { Override = (_, _) =>
        {
            calls++; var r = Json(empty ? "{\"error\":6,\"message\":\"not found\"}" : Artists(("Found", .9)));
            if (!empty) r.Headers.CacheControl = new() { MaxAge = TimeSpan.FromSeconds(30), NoStore = !store };
            return Task.FromResult(r);
        } };
        var folder = Check.NewFolder("lastfm-lifetime");
        using var last = new LastFmSimilar(folder, http, () => Key, () => now, TimeSpan.Zero);
        await last.SimilarAsync("Missing", CancellationToken.None); await last.SimilarAsync("Missing", CancellationToken.None);
        Check.Equal(1, calls, "not-found error 6 is cached as an empty answer");
        now = now.AddDays(8); await last.SimilarAsync("Missing", CancellationToken.None);
        Check.Equal(2, calls, "missing artists are retried after the negative cache expires");
        empty = false; await last.SimilarAsync("Present", CancellationToken.None); now = now.AddSeconds(31); await last.SimilarAsync("Present", CancellationToken.None);
        Check.Equal(4, calls, "Cache-Control shortens the relation cache lifetime");
        store = false; await last.SimilarAsync("Private", CancellationToken.None);
        Check.False(File.ReadAllText(Path.Combine(folder, "lastfm.json")).Contains("PRIVATE"), "no-store responses never enter the persistent cache");
        Check.True(last.DataExpires <= now, "no-store relations also prevent persisting the derived page");
    }

    private static async Task CatalogSongs()
    {
        bool tie = false, emptyDeezer = false, regional = false;
        var http = new DiscoveryTests.Http { Override = (r, _) =>
        {
            if (r.RequestUri!.Host == "api.deezer.com") return Task.FromResult(emptyDeezer ? Json("{\"data\":[]}") : new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(Json(JsonSerializer.Serialize(new { results = new[]
            {
                Song(1, 101, "Other", "Wrong spelling"), Song(2, 102, "Rising Star", "One"), Song(2, 103, "Rising Star", "Two"),
                Song(3, 104, "Rising Star", "Namesake"), Song(tie ? 3 : 2, 105, "Rising Star", "Three"),
            }.Concat(regional ? Enumerable.Range(0, 6).Select(i => Song(3, 200 + i, "Rising Star", "Namesake")) : []).ToArray() })));
        } };
        using var catalog = DiscoveryTests.Catalog(Check.NewFolder("discover-name-songs"), http);
        var found = (await catalog.SongsByArtistNameAsync("Rising Star", CancellationToken.None)).Value;
        Check.True(found.Artist?.Id == "apple:2" && found.Songs.Count == 3 && found.Songs.All(s => s.Song.Artists[0].Id == "apple:2"), "one-request artist songs keep exact names and the dominant catalog ID");
        Check.True(http.Calls.Count(r => r.Host == "itunes.apple.com") == 1 && http.Calls.Last().Query.Contains("attribute=artistTerm"), "related artists use one Apple song search rather than search plus lookup");
        int calls = http.Calls.Count; await catalog.SongsByArtistNameAsync("Rising Star", CancellationToken.None);
        Check.Equal(calls, http.Calls.Count, "related artist songs are cached");
        tie = true;
        using var ambiguous = DiscoveryTests.Catalog(Check.NewFolder("discover-name-tie"), http);
        Check.True((await ambiguous.SongsByArtistNameAsync("Rising Star", CancellationToken.None)).Value.Artist is null, "equally represented namesakes are skipped instead of guessed");
        tie = false; emptyDeezer = true;
        using var missing = DiscoveryTests.Catalog(Check.NewFolder("discover-deezer-empty"), http);
        Check.True((await missing.SongsByArtistNameAsync("Rising Star", CancellationToken.None)).Value.Artist?.Id == "apple:2", "empty Deezer results fall back to Apple just like blocked connections");
        regional = true;
        using var editions = DiscoveryTests.Catalog(Check.NewFolder("discover-namesake-editions"), http);
        Check.True((await editions.SongsByArtistNameAsync("Rising Star", CancellationToken.None)).Value.Artist?.Id == "apple:2", "regional reissues of one namesake recording cannot outvote three distinct recordings");
    }

    private static async Task ProtocolAndPacing()
    {
        var now = DateTimeOffset.UtcNow; int attempts = 0;
        var limited = new DiscoveryTests.Http { Override = (_, _) =>
        {
            attempts++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5)); return Task.FromResult(response);
        } };
        using (var last = new LastFmSimilar(Check.NewFolder("lastfm-429"), limited, () => Key, () => now, TimeSpan.Zero))
        {
            for (int i = 0; i < 2; i++) try { await last.SimilarAsync("Seed" + i, CancellationToken.None); } catch (InvalidOperationException) { }
            Check.True(attempts == 1 && last.Notice.Contains("rate limited"), "HTTP 429 respects Retry-After across seeds");
            now = now.AddMinutes(2);
            try { await last.SimilarAsync("Next", CancellationToken.None); } catch (InvalidOperationException) { }
            Check.Equal(1, attempts, "a five-minute Retry-After is not shortened to the default one minute");
        }
        var malformedFolder = Check.NewFolder("lastfm-malformed");
        var malformed = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(Json("{\"similarartists\":\"broken\"}")) };
        using (var last = new LastFmSimilar(malformedFolder, malformed, () => Key, pace: TimeSpan.Zero))
        {
            for (int i = 0; i < 2; i++) try { await last.SimilarAsync("Seed", CancellationToken.None); } catch (InvalidOperationException) { }
            Check.True(malformed.Calls.Count == 2 && !File.Exists(Path.Combine(malformedFolder, "lastfm.json")), "malformed service responses are not cached as artist-not-found");
        }
        var huge = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(Json(new string('x', 1024 * 1024 + 1))) };
        using (var last = new LastFmSimilar(Check.NewFolder("lastfm-size"), huge, () => Key))
        {
            bool rejected = false;
            try { await last.SimilarAsync("Seed", CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            Check.True(rejected, "Last.fm responses over one MiB are rejected before parsing");
        }
        int active = 0, max = 0; var starts = new List<long>();
        var paced = new DiscoveryTests.Http { Override = async (_, ct) =>
        {
            lock (starts) { starts.Add(System.Diagnostics.Stopwatch.GetTimestamp()); max = Math.Max(max, ++active); }
            try { await Task.Delay(150, ct); return Json(Artists(("Related", .9))); }
            finally { lock (starts) active--; }
        } };
        using (var last = new LastFmSimilar(Check.NewFolder("lastfm-pacing"), paced, () => Key, pace: TimeSpan.FromMilliseconds(40)))
        {
            await Task.WhenAll(Enumerable.Range(0, 6).Select(i => last.SimilarAsync("Seed" + i, CancellationToken.None)));
            Check.True(max is > 1 and <= 3 && starts.Count == 6, "Last.fm HTTP requests have at most three in flight");
            Check.True(starts.Zip(starts.Skip(1)).All(p => System.Diagnostics.Stopwatch.GetElapsedTime(p.First, p.Second) >= TimeSpan.FromMilliseconds(20)), "request starts share a pacing gate across concurrent seeds");
        }
    }
    private static object Song(int artist, int id, string name, string title) => new
    { kind = "song", wrapperType = "track", artistId = artist, artistName = name, trackId = id, trackName = title, trackTimeMillis = 180000, trackExplicitness = "explicit", collectionId = 1000 + id, collectionName = "Album", artworkUrl100 = "" };

    private sealed class DelayedCatalog : IDiscoverCatalog
    {
        public int Active, Max, Done, PrematureIdentity;
        public Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct)
        { if (Done < 9) PrematureIdentity++; return Task.FromResult<CatalogArtist?>(new("apple:" + (int.Parse(seed.Name[4..]) + 1), seed.Name)); }
        public Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct) => Task.FromResult<CatalogArtist?>(null);
        public Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct) => Task.FromResult(new List<CatalogSearchSong> { new(new(artist.Id + "0", "Song", [artist], 180, true), null) });
        public async Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Active); Max = Math.Max(Max, n);
            try { await Task.Delay(30, ct); Interlocked.Increment(ref Done); return []; }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private static async Task ParallelSeeds()
    {
        var catalog = new DelayedCatalog();
        await Discover.BuildAsync(Enumerable.Range(0, 20).Select(i => new TasteSeed("Seed" + i, 1, 1)).ToList(), catalog,
            (_, _) => Task.FromResult(new HashSet<string>()), new DiscoverHidden(Path.Combine(Check.NewFolder("discover-parallel"), "hidden.json")), null, CancellationToken.None);
        Check.True(catalog.Max == Discover.ArtistLookups && catalog.Done == Discover.SeedArtistsUsed, $"seed relations run {Discover.ArtistLookups} at a time with bounded fanout");
        Check.Equal(0, catalog.PrematureIdentity, "phase one does not identify artists before requesting relations");
    }

    internal static DiscoverResult Page(string id = "apple:101") => new([new(new(new(id, "Saved song", [new("apple:1", "Saved Artist")], 180, true), null), 1, "Because you play Seed")], [], "");
    private static async Task SavedPage()
    {
        var folder = Check.NewFolder("discover-saved"); var now = DateTimeOffset.UtcNow;
        var cache = new ForYouCache(Path.Combine(folder, "for-you-last.json")); cache.Write(new(1, now.AddHours(-13), now.AddDays(1), Page()));
        using var source = new ForYouSource(() => throw new Exception("Offline"), null, _ => [], new SpotifyPlaylistTests.Library(), null, Path.Combine(folder, "hidden.json"), now: () => now);
        Check.Equal("Saved song", (await source.SavedAsync(CancellationToken.None))?.Songs.Single().Track.Song.Title, "a saved page loads before any request, even when stale/offline");
        source.Hidden.Hide(Page().Songs[0].Track.Song);
        Check.Equal(0, (await source.SavedAsync(CancellationToken.None))?.Songs.Count, "saved pages reapply dismissals");
        now = now.AddDays(2); Check.True(await source.SavedAsync(CancellationToken.None) is null, "expired saved pages cannot be presented forever");
        File.WriteAllText(Path.Combine(folder, "for-you-last.json"), "{\"Version\":1,\"Result\":null}");
        Check.True(cache.Read(now) is null, "damaged saved pages do not block new discovery");
        cache.Write(new(1, now.AddHours(1), now.AddDays(1), Page())); Check.True(cache.Read(now) is null, "future-dated saved pages are rejected");
    }

    private static async Task RedirectedCaches()
    {
        var root = Check.NewFolder("discover-redirected"); var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
        var link = Path.Combine(root, "link");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { Console.WriteLine("  skip  cache link checks: this test account cannot create symbolic links"); return; }
        var now = DateTimeOffset.UtcNow;
        var original = new ForYouCache(Path.Combine(target, "for-you-last.json")); original.Write(new(1, now, now.AddDays(1), Page()));
        var redirected = new ForYouCache(Path.Combine(link, "for-you-last.json"));
        Check.True(redirected.Read(now) is null, "a saved page inside a redirected ancestor is not read");
        redirected.Write(new(1, now, now.AddDays(1), Page("apple:999")));
        Check.Equal("apple:101", original.Read(now)?.Result.Songs.Single().Track.Song.Id, "a redirected ancestor cannot overwrite the target's saved page");
        using var last = new LastFmSimilar(link, new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(Json(Artists(("Related", 1)))) }, () => Key);
        Check.Equal(1, (await last.SimilarAsync("Seed", CancellationToken.None)).Count, "unsafe cache paths do not prevent in-memory recommendations");
        Check.False(File.Exists(Path.Combine(target, "lastfm.json")), "Last.fm refuses cache writes through a redirected ancestor");
    }
}
