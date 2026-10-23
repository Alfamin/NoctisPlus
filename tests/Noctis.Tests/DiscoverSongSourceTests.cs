using System.Net;
using System.Text;
using FreeMusicFinder;
using Noctis.Services;
using SimilarArtist = FreeMusicFinder.SimilarArtist;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Songs like the listener's best-liked recordings: Deezer's artist radio (no key) and, with the listener's own key, Last.fm's
/// track.getSimilar. The provider answers are hand-written in the shape of each provider's documented response (no live calls):
/// what is sent, caching and offline use, the same filters and caps as every other card, the spread of seeds and sources on the
/// page, and a failing source failing alone.
/// </summary>
public class DiscoverSongSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DiscoverEvaluationTests.Now;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noctis-discover-songs-" + Guid.NewGuid().ToString("N"));
    public DiscoverSongSourceTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    /// <summary>Answers by request; null means unreachable. Remembers every request.</summary>
    private sealed class Fixture(Func<Uri, string?> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return answer(request.RequestUri!) is { } body
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") })
                : throw new HttpRequestException("unreachable in this test");
        }
    }

    private static string Track(long id, string title, long artistId, string artist, int rank) =>
        $$"""{"id":{{id}},"readable":true,"title":"{{title}}","duration":201,"rank":{{rank}},"explicit_lyrics":false,"artist":{"id":{{artistId}},"name":"{{artist}}","type":"artist"},"album":{"id":{{id + 9000}},"title":"{{title}} (Single)","cover_medium":"https://cdn-images.dzcdn.net/images/cover/0/250x250-000000-80-0-0.jpg","type":"album"},"type":"track"}""";

    /// <summary>GET /artist/27/radio: a track list like Deezer's (data: track objects with artist and album).</summary>
    private static readonly string Radio = "{\"data\":[" + string.Join(",",
        Track(101, "Lantern Road", 27, "Night Owls", 600_000), Track(102, "Glass Harbour", 31, "Paper Kites Club", 410_000),
        Track(103, "Low Tide", 32, "Juno Ferry", 380_000), Track(101, "Lantern Road", 27, "Night Owls", 600_000)) + "]}";

    private static string Artists(long id, string name) => $$"""{"data":[{"id":{{id}},"name":"{{name}}","nb_fan":12000,"type":"artist"}],"total":1}""";

    /// <summary>track.getSimilar's documented answer: similartracks.track, each with name, match and artist.name.</summary>
    private const string SimilarTracks = """
        {"similartracks":{"track":[
          {"name":"Moonlight","playcount":900,"match":1.0,"artist":{"name":"Night Owls"}},
          {"name":"Quiet Streets","playcount":700,"match":0.42,"artist":{"name":"Juno Ferry"}},
          {"name":"Paper Boats","playcount":800,"match":0.87,"artist":{"name":"Paper Kites Club"}}
        ],"@attr":{"artist":"Night Owls"}}}
        """;

    private static string Deezer(Uri uri) => uri.AbsolutePath switch
    {
        "/search/artist" when uri.Query.Contains("Night%20Owls") => Artists(27, "Night Owls"),
        "/search/artist" when uri.Query.Contains("Paper%20Kites%20Club") => Artists(31, "Paper Kites Club"),
        "/search/artist" when uri.Query.Contains("Juno%20Ferry") => Artists(32, "Juno Ferry"),
        "/artist/27/radio" => Radio,
        "/artist/31/top" => "{\"data\":[" + Track(201, "Paper Boats", 31, "Paper Kites Club", 300_000) + "],\"total\":1}",
        "/artist/32/top" => "{\"data\":[" + Track(202, "Not This One", 32, "Juno Ferry", 300_000) + "],\"total\":1}",
        _ => "{\"data\":[],\"total\":0}",
    };

    [Fact]
    public void LikedRecordings_ComeFromWhatWasHeardAndSaid_NeverFromImportsOrOwningAFile()
    {
        var at = Now.UtcDateTime;
        var plays = new List<TastePlay>();
        for (int i = 0; i < 5; i++) plays.Add(new("Night Owls", at.AddDays(-i - 1), 1.5, "Lantern Road", true));
        for (int i = 0; i < 5; i++) plays.Add(new("Juno Ferry", at.AddDays(-i - 1), -1.5, "Skipped Song", true));
        var favorite = new LocalSong(Guid.NewGuid(), "Paper Kites Club", "Paper Boats", "C:/a.flac", TimeSpan.FromMinutes(3), Favorite: true);
        var imported = new LocalSong(Guid.NewGuid(), "Imported Band", "Imported Song", "C:/b.flac", TimeSpan.FromMinutes(3));
        var justOwned = new LocalSong(Guid.NewGuid(), "Owned Band", "Owned Song", "C:/c.flac", TimeSpan.FromMinutes(3));
        var liked = new List<TasteRecording>();
        Taste.Seeds(plays, [favorite, imported, justOwned], at, playlists: [new(true, [imported.Id])], liked: liked);
        Assert.Equal(["Lantern Road", "Paper Boats"], liked.Select(l => l.Title));
        Assert.Equal("Night Owls", liked[0].Artist);

        var many = Enumerable.Range(0, 30).Select(i => new TastePlay("Night Owls", at.AddDays(-1), 1.5, "Song " + i, true)).ToList();
        var top = new List<TasteRecording>();
        Taste.Seeds(many, [], at, liked: top);
        Assert.Equal(Taste.LikedRecordings, top.Count);
    }

    [Fact]
    public async Task DeezerRadio_SendsOnlyTheArtistId_IsCached_AndServedOfflineWhenStale()
    {
        var folder = Path.Combine(_folder, "catalog");
        var http = new Fixture(Deezer);
        var clock = Now;
        using (var catalog = new ArtistCatalog(folder, http, () => clock, TimeSpan.Zero))
        {
            var radio = (await catalog.RadioAsync(new("deezer:27", "Night Owls"), CancellationToken.None)).Value;
            Assert.Equal(["Lantern Road", "Glass Harbour", "Low Tide"], radio.Select(s => s.Song.Title)); // the repeated track once
            Assert.Equal("Paper Kites Club", radio[1].Song.Artists[0].Name);
            Assert.Equal(410_000, radio[1].Song.Popularity);
            Assert.Equal("Glass Harbour (Single)", radio[1].Album!.Title);
            var request = Assert.Single(http.Requests);
            Assert.Equal(("/artist/27/radio", ""), (request.AbsolutePath, request.Query)); // an ID, no names or titles
            await catalog.RadioAsync(new("deezer:27", "Night Owls"), CancellationToken.None);
            Assert.Single(http.Requests);
            Assert.Empty((await catalog.RadioAsync(new("apple:27", "Night Owls"), CancellationToken.None)).Value);
            Assert.Single(http.Requests);
        }
        clock = Now.AddDays(4); // past the three days it stays fresh, and Deezer cannot be reached
        var down = new Fixture(_ => null);
        using var later = new ArtistCatalog(folder, down, () => clock, TimeSpan.Zero);
        var stale = await later.RadioAsync(new("deezer:27", "Night Owls"), CancellationToken.None);
        Assert.Single(down.Requests);
        Assert.True(stale.Cached);
        Assert.Equal(3, stale.Value.Count);
    }

    [Fact]
    public async Task OfflineMode_RadioSendsNothing()
    {
        var access = new OnlineAccess();
        access.SetOffline(true);
        var http = new Fixture(Deezer);
        using var catalog = new ArtistCatalog(Path.Combine(_folder, "offline"), new OnlineAccessHandler(http, access), () => Now, TimeSpan.Zero);
        await Assert.ThrowsAnyAsync<Exception>(() => catalog.RadioAsync(new("deezer:27", "Night Owls"), CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task LastFmSimilarTracks_SendTheTitleAndArtist_OnlyWithAKey_AndAreCached()
    {
        var http = new Fixture(uri => uri.Host == "ws.audioscrobbler.com" ? SimilarTracks : null);
        var key = "";
        using var lastFm = new LastFmSimilar(Path.Combine(_folder, "lastfm"), http, () => key, () => Now, TimeSpan.Zero);
        Assert.Empty(await lastFm.SimilarTracksAsync("Night Owls", "Moonlight", CancellationToken.None));
        Assert.Empty(http.Requests); // no key, nothing asked

        key = new string('a', 32);
        var tracks = await lastFm.SimilarTracksAsync("Night Owls", "Moonlight", CancellationToken.None);
        Assert.Equal(["Paper Boats", "Quiet Streets"], tracks.Select(t => t.Title)); // most similar first; the song itself left out
        Assert.Equal("Paper Kites Club", tracks[0].Artist);
        var query = Assert.Single(http.Requests).Query;
        Assert.Contains("method=track.getsimilar", query);
        Assert.Contains("artist=Night%20Owls", query);
        Assert.Contains("track=Moonlight", query);
        await lastFm.SimilarTracksAsync("Night Owls", "Moonlight", CancellationToken.None);
        Assert.Single(http.Requests);
        using var reloaded = new LastFmSimilar(Path.Combine(_folder, "lastfm"), http, () => key, () => Now, TimeSpan.Zero);
        Assert.Equal(2, (await reloaded.SimilarTracksAsync("Night Owls", "Moonlight", CancellationToken.None)).Count);
        Assert.Single(http.Requests); // from the saved file

        var unknown = new Fixture(_ => """{"error":6,"message":"Track not found"}""");
        using var none = new LastFmSimilar(Path.Combine(_folder, "lastfm-none"), unknown, () => key, () => Now, TimeSpan.Zero);
        Assert.Empty(await none.SimilarTracksAsync("Night Owls", "Nothing", CancellationToken.None));
    }

    [Fact]
    public async Task SongsLike_WithoutAKey_AreDeezerRadio_AndLastFmSongsComeFirstWithOne()
    {
        var http = new Fixture(uri => uri.Host == "ws.audioscrobbler.com" ? SimilarTracks : Deezer(uri));
        var key = "";
        using var catalog = new ArtistCatalog(Path.Combine(_folder, "both"), http, () => Now, TimeSpan.Zero);
        using var lastFm = new LastFmSimilar(Path.Combine(_folder, "both-lastfm"), http, () => key, () => Now, TimeSpan.Zero);
        var discover = new CatalogForDiscover(() => catalog, _ => [], () => null, () => lastFm);
        var liked = new TasteRecording("Night Owls", "Moonlight", 3);

        var radio = await discover.SongsLikeAsync(liked, CancellationToken.None);
        Assert.Equal(["Lantern Road", "Glass Harbour", "Low Tide"], radio.Select(s => s.Song.Title));
        Assert.DoesNotContain(http.Requests, r => r.Host == "ws.audioscrobbler.com");
        Assert.DoesNotContain(http.Requests, r => r.Query.Contains("Moonlight")); // Deezer never gets the liked song's title

        key = new string('b', 32);
        var both = await discover.SongsLikeAsync(liked, CancellationToken.None);
        // Paper Boats is in its artist's catalog songs; Quiet Streets is not, so it is left out rather than guessed.
        Assert.Equal(["Paper Boats", "Lantern Road", "Glass Harbour", "Low Tide"], both.Select(s => s.Song.Title));
        Assert.Single(http.Requests, r => r.Host == "ws.audioscrobbler.com");
        Assert.DoesNotContain(http.Requests, r => r.Host == "api.deezer.com" && r.Query.Contains("Moonlight"));
    }

    [Fact]
    public async Task SongsLike_OneSourceFailing_LeavesTheOther()
    {
        var key = new string('c', 32);
        var lastFmDown = new Fixture(uri => uri.Host == "ws.audioscrobbler.com" ? null : Deezer(uri));
        using (var catalog = new ArtistCatalog(Path.Combine(_folder, "a"), lastFmDown, () => Now, TimeSpan.Zero))
        using (var lastFm = new LastFmSimilar(Path.Combine(_folder, "a-lastfm"), lastFmDown, () => key, () => Now, TimeSpan.Zero))
            Assert.Equal(3, (await new CatalogForDiscover(() => catalog, _ => [], () => null, () => lastFm).SongsLikeAsync(new("Night Owls", "Moonlight", 3), CancellationToken.None)).Count);

        var radioDown = new Fixture(uri => uri.Host == "ws.audioscrobbler.com" ? SimilarTracks : uri.AbsolutePath.EndsWith("/radio", StringComparison.Ordinal) ? null : Deezer(uri));
        using (var catalog = new ArtistCatalog(Path.Combine(_folder, "b"), radioDown, () => Now, TimeSpan.Zero))
        using (var lastFm = new LastFmSimilar(Path.Combine(_folder, "b-lastfm"), radioDown, () => key, () => Now, TimeSpan.Zero))
            Assert.Equal(["Paper Boats"], (await new CatalogForDiscover(() => catalog, _ => [], () => null, () => lastFm).SongsLikeAsync(new("Night Owls", "Moonlight", 3), CancellationToken.None)).Select(s => s.Song.Title));

        var allDown = new Fixture(_ => null);
        using var nothing = new ArtistCatalog(Path.Combine(_folder, "c"), allDown, () => Now, TimeSpan.Zero);
        await Assert.ThrowsAnyAsync<Exception>(() => new CatalogForDiscover(() => nothing, _ => [], () => null).SongsLikeAsync(new("Night Owls", "Moonlight", 3), CancellationToken.None));
    }

    /// <summary>
    /// The synthetic world, plus a song source made of traps: for each liked recording the recording itself, a song the listener has
    /// heard, a dismissed one, an owned one and a remaster of a song the artist path also finds, then eight songs by artists of its own
    /// ("Echo …"), so a card from this source can be told apart.
    /// </summary>
    private sealed class Traps(DiscoverEvaluationTests.World world) : IDiscoverCatalog
    {
        public static readonly CatalogSong Dismissed = new("deezer:x:dismissed", "Dismissed Song", [new("deezer:x", "Echo Dismissed")], 200, false);
        public static readonly CatalogSong Owned = new("deezer:x:owned", "Owned Song", [new("deezer:y", "Echo Owned")], 200, false);
        public Func<TasteRecording, CancellationToken, Task>? Before;
        /// <summary>Every song id this source gave.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Given { get; } = new();
        public Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct) => world.FindAsync(seed, ct);
        public Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct) => world.FindByNameAsync(name, ct);
        public Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct) => world.SimilarAsync(seed, artist, ct);
        public Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct) => world.TopSongsAsync(artist, ct);
        public async Task<List<CatalogSearchSong>> SongsLikeAsync(TasteRecording liked, CancellationToken ct)
        {
            if (Before is { } before) await before(liked, ct);
            var by = new CatalogArtist("deezer:" + liked.Artist, liked.Artist);
            var songs = new List<CatalogSong>
            {
                new("deezer:itself:" + liked.Title, liked.Title + " (Remastered 2011)", [by], 200, false),
                new("deezer:heard:" + liked.Title, "Rock 0 Song 1", [new("deezer:Rock-0", "Rock 0")], 181, true),
                Dismissed, Owned,
                new("apple:hip6:7", "Hip 6 Song 7 (Remastered)", [new("apple:Hip-6", "Hip 6")], 187, true),
            };
            songs.AddRange(Enumerable.Range(1, 8).Select(i => new CatalogSong($"deezer:echo:{liked.Title}:{i}", $"Echo {liked.Title} {i}", [new($"deezer:echo:{liked.Title}:{i}", $"Echo {liked.Title} {i}")], 200, false)));
            foreach (var song in songs) Given[song.Id] = true;
            return songs.Select(s => new CatalogSearchSong(s, null)).ToList();
        }
    }

    private (List<TastePlay> Plays, DiscoverEvaluationTests.Persona Persona) Steady()
    {
        var persona = DiscoverEvaluationTests.Make(_folder, "steady");
        return (TasteSources.Load(persona.Folder), persona);
    }

    private static Task<DiscoverResult> Build(List<TastePlay> plays, DiscoverEvaluationTests.Persona persona, IDiscoverCatalog catalog, DiscoverHidden hidden, string folder,
        CancellationToken ct = default) => Discover.ForListenerAsync(plays, persona.Library, TasteSources.Playlists(persona.Folder), new DiscoverFeedback(Path.Combine(folder, "feedback.json"), () => Now),
        hidden, catalog, (songs, _) => Task.FromResult(songs.Where(s => s.Id == Traps.Owned.Id).Select(s => s.Id).ToHashSet(StringComparer.Ordinal)), null, Now, ct);

    [Fact]
    public async Task SongsLike_GoThroughTheSameFiltersAndCaps_AsEveryOtherCard()
    {
        var (plays, persona) = Steady();
        var hidden = new DiscoverHidden(Path.Combine(_folder, "hidden.json"));
        hidden.Hide(Traps.Dismissed);
        var page = await Build(plays, persona, new Traps(new()), hidden, _folder);
        Assert.Equal(40, page.Songs.Count);
        var keys = page.Songs.Select(s => DiscoverIdentity.Key(s.Track.Song)).ToList();
        var heard = TasteSources.Familiar(plays);
        Assert.DoesNotContain(keys, heard.Contains); // the liked recordings themselves and every heard song
        Assert.DoesNotContain(page.Songs, s => s.Track.Song.Id is "deezer:x:dismissed" or "deezer:x:owned");
        Assert.Equal(keys.Count, keys.Distinct().Count()); // the remaster is the artist path's song, once
        var echoes = page.Songs.Where(s => s.Track.Song.Title.StartsWith("Echo ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(echoes);
        foreach (var perLiked in echoes.GroupBy(s => s.Track.Song.Title[..s.Track.Song.Title.LastIndexOf(' ')]))
            Assert.True(perLiked.Count() <= Discover.PerLiked, perLiked.Key);
        var shelf = page.Songs.Take(Discover.Shelf).ToList();
        Assert.True(shelf.GroupBy(s => DiscoverIdentity.Artist(s.Track.Song.Artists[0].Name)).Max(g => g.Count()) <= 3);
        Assert.True(shelf.GroupBy(DiscoverPageMetrics.SeedOf).Max(g => g.Count()) <= Discover.PerSeed);
        Assert.All(echoes, s => Assert.StartsWith("Because you play ", s.Because)); // the existing reasons, no new text
    }

    [Fact]
    public async Task TheVisiblePage_TakesSourcesAndSeedsInTurn_SoOneOldInterestCannotFillTheTop()
    {
        // Mostly hip-hop, plus a handful of plays of one rock band two months ago (the owner's 5 Pink Floyd plays).
        var folder = Path.Combine(_folder, "mostly-hip");
        var shuffle = Path.Combine(folder, "plugin-data", "dev.moshi.trueshuffle");
        Directory.CreateDirectory(shuffle);
        var lines = new List<string>();
        for (int d = 30; d > 0; d--) for (int a = 0; a < 5; a++) lines.Add(DiscoverEvaluationTests.Listen($"Hip {a}", $"Hip {a} Song {1 + d % 4}", Now.AddDays(-d).AddMinutes(a), "Finished", 1, 200_000));
        for (int n = 1; n <= 5; n++) lines.Add(DiscoverEvaluationTests.Listen("Rock 0", $"Rock 0 Song {n}", Now.AddDays(-60 - n), "Finished", 1, 200_000));
        File.WriteAllLines(Path.Combine(shuffle, "listens.jsonl"), lines);
        var plays = TasteSources.Load(folder);
        var traps = new Traps(new());
        var page = await Discover.ForListenerAsync(plays, [], [], new DiscoverFeedback(Path.Combine(folder, "feedback.json"), () => Now), new DiscoverHidden(Path.Combine(folder, "hidden.json")),
            traps, (_, _) => Task.FromResult(new HashSet<string>()), null, Now, CancellationToken.None);
        var top = page.Songs.Take(6).ToList();
        Assert.True(top.GroupBy(DiscoverPageMetrics.SeedOf).Max(g => g.Count()) <= 2, string.Join(" | ", top.Select(s => s.Because)));
        Assert.DoesNotContain(top.Take(3), s => DiscoverPageMetrics.SeedOf(s) == "rock 0");
        for (int i = 1; i < Discover.Shelf; i++)
            Assert.NotEqual(DiscoverPageMetrics.SeedOf(page.Songs[i - 1]), DiscoverPageMetrics.SeedOf(page.Songs[i]));
        // The two sources alternate on the visible page while both have songs left.
        var fromSongs = page.Songs.Take(8).Select(s => traps.Given.ContainsKey(s.Track.Song.Id)).ToList();
        for (int i = 1; i < fromSongs.Count; i++) Assert.True(fromSongs[i - 1] != fromSongs[i], string.Join(" | ", page.Songs.Take(8).Select(s => s.Track.Song.Title + " / " + s.Because)));
    }

    [Fact]
    public async Task SongSourceDown_FailsAlone_SlowCancelledOrOffline_IsHandledLikeTheOthers()
    {
        var (plays, persona) = Steady();
        var hidden = new DiscoverHidden(Path.Combine(_folder, "hidden.json"));
        var catalog = new Traps(new()) { Before = (_, _) => throw new HttpRequestException("Deezer radio down") };
        var page = await Build(plays, persona, catalog, hidden, _folder);
        Assert.Equal(40, page.Songs.Count);
        Assert.False(page.Unreachable);
        Assert.Contains("Some artists could not be loaded", page.Notice);
        Assert.DoesNotContain(page.Songs, s => s.Track.Song.Title.StartsWith("Echo ", StringComparison.Ordinal));

        catalog.Before = (_, _) => throw new TaskCanceledException("HttpClient.Timeout elapsed"); // a provider's own timeout
        Assert.Equal(40, (await Build(plays, persona, catalog, hidden, _folder)).Songs.Count);

        var started = new TaskCompletionSource();
        catalog.Before = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var stop = new CancellationTokenSource();
        var build = Build(plays, persona, catalog, hidden, _folder, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build.WaitAsync(TimeSpan.FromSeconds(5)));

        catalog.Before = (_, _) => throw new OfflineModeException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(plays, persona, catalog, hidden, _folder));
    }
}
