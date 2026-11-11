using System.Net;
using System.Text.Json;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #283: the phone's For you takes the computer's route: the plays logged to the Last.fm account from anywhere,
/// and the computer's page choices (Remove this page, Clear all pages, Undo).</summary>
internal static class PhoneForYouRouteTests
{
    // Fake fixture, never a real account key.
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static string Recent(int totalPages, params (string Artist, string Title, long Uts)[] rows) => JsonSerializer.Serialize(new
    {
        recenttracks = new Dictionary<string, object>
        {
            ["@attr"] = new { totalPages = totalPages.ToString() },
            ["track"] = rows.Select(r => (object)new Dictionary<string, object>
            {
                ["artist"] = new Dictionary<string, string> { ["#text"] = r.Artist },
                ["name"] = r.Title,
                ["date"] = new { uts = r.Uts.ToString() },
            }).Prepend(new Dictionary<string, object>
            {
                // The song playing now: no date, not a play yet.
                ["artist"] = new Dictionary<string, string> { ["#text"] = "Now Artist" }, ["name"] = "Now playing",
                ["@attr"] = new { nowplaying = "true" },
            }).ToArray(),
        },
    });

    /// <summary>Runs after the UI dispatcher is set up: off it, so an await never waits for a dispatcher nobody runs.</summary>
    public static void Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        Check.Section("phone For you: Last.fm plays from anywhere, the computer's page choices");
        await LoggedPlays();
        Joined();
        await Pages();
    }

    private static async Task LoggedPlays()
    {
        var folder = Check.NewFolder("phone-lastfm-listens"); var now = DateTimeOffset.UtcNow; var user = "listener";
        var asked = new List<string>(); var fail = false;
        var http = new DiscoveryTests.Http { Override = (r, _) =>
        {
            asked.Add(r.RequestUri!.Query);
            if (fail) throw new HttpRequestException("offline");
            var page = r.RequestUri.Query.Contains("page=2") ? 2 : 1;
            var body = r.RequestUri.Query.Contains("from=")
                ? Recent(1, ("Fresh Artist", "Fresh Song", 3_000))
                : page == 1 ? Recent(2, ("Artist A", "Song A", 2_000), ("Artist B", "Song B", 1_900)) : Recent(2, ("Artist C", "Song C", 1_000));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        } };
        using (var listens = new LastFmListens(folder, http, () => user, () => Key, () => now))
        {
            var plays = await listens.PlaysAsync(CancellationToken.None);
            Check.Equal("Song A,Song B,Song C", string.Join(",", plays.Select(p => p.Title)), "a first read takes every page of scrobbles, newest first, the song playing now left out");
            Check.True(plays.All(p => p.Heard && p.Weight == 1.0 && p.At.Kind == DateTimeKind.Utc), "a scrobble is a play heard properly");
            Check.True(asked.All(q => q.Contains("method=user.getrecenttracks") && q.Contains("user=listener")), "asked by the account name only (a public read)");
            Check.Equal(2, asked.Count, "two pages, two requests");
            await listens.PlaysAsync(CancellationToken.None);
            Check.Equal(2, asked.Count, "a fresh read asks Last.fm nothing again");
            now += LastFmListens.Fresh;
            plays = await listens.PlaysAsync(CancellationToken.None);
            Check.True(asked[^1].Contains("from=2001"), "a later read asks only for scrobbles after the newest kept one");
            Check.Equal("Fresh Song,Song A,Song B,Song C", string.Join(",", plays.Select(p => p.Title)), "new scrobbles join the kept ones");
            fail = true; now += LastFmListens.Fresh;
            Check.Equal(4, (await listens.PlaysAsync(CancellationToken.None)).Count, "offline: the kept scrobbles are still used");
        }
        var disk = File.ReadAllText(Path.Combine(folder, "lastfm-listens.json"));
        Check.False(disk.Contains(Key) || disk.Contains("api_key"), "the kept scrobbles hold no key");
        fail = false;
        using (var restarted = new LastFmListens(folder, new DiscoveryTests.Http { Offline = true }, () => user, () => Key, () => now))
            Check.Equal(4, (await restarted.PlaysAsync(CancellationToken.None)).Count, "kept across a restart, offline");
        user = "someone-else";
        using (var other = new LastFmListens(folder, new DiscoveryTests.Http { Offline = true }, () => user, () => Key, () => now))
            Check.Equal(0, (await other.PlaysAsync(CancellationToken.None)).Count, "another account's scrobbles are not this listener's taste");
        user = "";
        using (var none = new LastFmListens(folder, http, () => user, () => Key, () => now))
        {
            var before = asked.Count;
            Check.Equal(0, (await none.PlaysAsync(CancellationToken.None)).Count, "no account connected: nothing");
            Check.Equal(before, asked.Count, "and nothing asked");
        }
    }

    private static void Joined()
    {
        var at = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var local = new List<TastePlay> { new("Artist A", at, 1.0, "Song A", Heard: true), new("Artist B", at.AddHours(-1), -0.7, "Song B") };
        var logged = new List<TastePlay>
        {
            new("Artist A", at.AddSeconds(40), 1.0, "Song A", Heard: true),  // this phone's own play, scrobbled
            new("Artist A", at.AddHours(-5), 1.0, "Song A", Heard: true),    // the same song played on the computer earlier
            new("Artist C", at.AddHours(-2), 1.0, "Song C", Heard: true),    // a song only played on the computer
        };
        var plays = TasteSources.WithLogged(local, logged);
        Check.Equal(4, plays.Count, "a scrobble of this device's own play is that play, not a second one");
        Check.True(plays.Any(p => p.Title == "Song C") && plays.Count(p => p.Title == "Song A") == 2, "plays logged elsewhere join this device's");
        Check.True(ReferenceEquals(local, TasteSources.WithLogged(local, [])), "no logged plays: this device's own, unchanged");
    }

    private static async Task Pages()
    {
        var folder = Check.NewFolder("phone-for-you-pages");
        var library = new SpotifyPlaylistTests.Library();
        var now = DateTimeOffset.UtcNow;
        ForYouCache.Page Page(string batch, int first, int ageDays, int expiresInDays) => new(1, now.AddDays(-ageDays), now.AddDays(expiresInDays),
            new(Enumerable.Range(first, 3).Select(i => new DiscoverSong(new(DiscoveryTests.Song("deezer:" + i, "Song " + i), DiscoveryTests.Orbit), 1, "Because you play North Star")).ToList(), [], "", Batch: batch));
        new ForYouCache(Path.Combine(folder, "for-you-last.json")).Write(Page("page-new", 300, 3, 1));
        new ForYouCache(Path.Combine(folder, "for-you-pages.json")).WriteEarlier([Page("page-mid", 200, 8, -2), Page("page-old", 100, 9, -3)]);
        var http = new DiscoveryTests.Http { Offline = true };
        using var catalog = DiscoveryTests.Catalog(Path.Combine(folder, "catalog"), http);
        using var downloads = new Downloads(_ => { });
        var actions = new DiscoveryActions(library, downloads, _ => throw new Exception("No transfer is allowed in this fixture"), () => folder, () => false);
        using var pages = new CatalogPages(() => catalog, actions, _ => { }, () => true, library: () => null);
        var online = new FinderOnlineSearch(() => catalog, actions, pages, () => true, () => { });
        using var source = new ForYouSource(() => catalog, actions, _ => [], library, null, Path.Combine(folder, "hidden.json"));
        Noctis.Services.Search.IOnlineDiscovery phone = new FinderDiscovery(source, online, library);
        string Kept() => string.Join(",", source.Pages().Select(p => p.Batch));

        var shown = await phone.LoadAsync(false, CancellationToken.None);
        Check.Equal("page-new,page-mid,page-old", string.Join(",", shown.Select(p => p.Id)), "the phone shows the kept pages");
        var receipt = await phone.RemovePageAsync("page-mid");
        Check.Equal("page-new,page-old", Kept(), "Remove this page takes only that page off");
        Check.True(receipt is not null && await phone.UndoAsync(receipt), "Remove this page offers Undo");
        Check.Equal("page-new,page-mid,page-old", Kept(), "Undo brings it back in its own place");
        Check.True(await phone.RemovePageAsync("no-such-page") is null, "a page no longer there: nothing to undo");
        receipt = await phone.ClearPagesAsync();
        Check.Equal("", Kept(), "Clear all pages leaves none");
        Check.True(receipt is not null && await phone.UndoAsync(receipt), "Clear all pages offers Undo");
        Check.Equal("page-new,page-mid,page-old", Kept(), "Undo brings every page back");
        Check.False(await phone.UndoAsync(receipt!), "one Undo per choice");
    }
}
