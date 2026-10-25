using System.Net;
using System.Text;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #155: a song found from part of its lyrics. Synthetic answers only.</summary>
internal static class LyricSearchTests
{
    private const string NewHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    /// <summary>Spotify's token page, song search (stale until the web player's current query is read) and web player.</summary>
    internal sealed class Http : HttpMessageHandler
    {
        public bool StaleFallback = true, NoPlayerHash;
        public int Searches, StaleAnswers, PlayerReads;
        public readonly List<string> Hashes = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.AbsoluteUri.StartsWith("https://open.spotify.com/embed/playlist/", StringComparison.Ordinal))
                return Text("<script id=\"__NEXT_DATA__\">" + JsonSerializer.Serialize(new { props = new { pageProps = new { state = new { settings = new { session = new
                    { accessToken = "lyric-token", accessTokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() } } } } } }) + "</script>");
            if (uri.AbsoluteUri == "https://open.spotify.com/search")
            { Interlocked.Increment(ref PlayerReads); return Text("<script src=\"https://open.spotifycdn.com/cdn/build/web-player/web-player.f954508a.js\"></script>"); }
            if (uri.AbsoluteUri == "https://open.spotifycdn.com/cdn/build/web-player/web-player.f954508a.js")
                return Text("x={1328:\"xpui-pip-mini\",4406:\"xpui-routes-search\",4563:\"xpui-routes-show\"};y={4406:\"a0a5ee13\"};z={9:\"1\",4406:\"2aa27db9\"}");
            if (uri.AbsoluteUri == "https://open.spotifycdn.com/cdn/build/web-player/xpui-routes-search.a0a5ee13.js") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (uri.AbsoluteUri == "https://open.spotifycdn.com/cdn/build/web-player/xpui-routes-search.2aa27db9.js")
                return Text(NoPlayerHash ? "nothing here" : "a(\"searchTracks\",\"query\",\"" + NewHash + "\",null);");
            if (uri.Host == "api-partner.spotify.com" && uri.Query.Contains("operationName=searchTracks"))
            {
                Interlocked.Increment(ref Searches);
                var hash = System.Text.RegularExpressions.Regex.Match(Uri.UnescapeDataString(uri.Query), "\"sha256Hash\":\"([0-9a-f]{64})\"").Groups[1].Value;
                lock (Hashes) Hashes.Add(hash);
                if (StaleFallback && hash == PlaylistCatalog.TracksHashFallback)
                { Interlocked.Increment(ref StaleAnswers); return Text("{\"errors\":[{\"message\":\"PersistedQueryNotFound\"}]}"); }
                return Text(JsonSerializer.Serialize(Tracks()));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static Task<HttpResponseMessage> Text(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
    }

    internal static object Tracks() => new
    {
        data = new { searchV2 = new { tracksV2 = new { items = new object[]
        {
            new { matchedFields = new[] { "LYRICS" }, item = new { data = new
            {
                __typename = "Track", id = "0wwPcA6wtMf6HUMpIRdeP7", name = "Hotline Bling",
                artists = new { items = new[] { new { profile = new { name = "Drake" } } } },
                duration = new { totalMilliseconds = 267066 }, contentRating = new { label = "EXPLICIT" },
                albumOfTrack = new { name = "Views", coverArt = new { sources = new[] { new { url = "https://i.scdn.co/image/views" } } } },
            } } },
            new { matchedFields = Array.Empty<string>(), item = new { data = new
            {
                __typename = "Track", id = "1", name = "You Used to Call Me on My Cellphone",
                artists = new { items = new[] { new { profile = new { name = "Yezhi" } } } },
                duration = new { totalMilliseconds = 120000 }, contentRating = new { label = "NONE" },
                albumOfTrack = new { name = "Covers", coverArt = new { sources = Array.Empty<object>() } },
            } } },
            new { item = new { data = new { __typename = "Episode", id = "2", name = "A podcast" } } },
        } } } },
    };

    internal static async Task RunAsync()
    {
        Check.Section("lyric search: words, title or lyric, catalog copy, Spotify's song search and its query refresh");

        Check.True(LyricSearch.LooksLikeLyric("you used to call me") && !LyricSearch.LooksLikeLyric("hotline bling drake"), "four words or more may be lyrics; fewer are a name");
        Check.True(LyricSearch.Words("Don't stop—BELIEVIN'! (yeah)").SequenceEqual(new[] { "dont", "stop", "believin", "yeah" }),
            "words are folded: case, apostrophes and punctuation gone; a dash parts words");
        Check.True(LyricSearch.Near("doin", "doing") && LyricSearch.Near("nothing", "nothin") && !LyricSearch.Near("do", "dog"), "doin and doing are the same word; short words are not stretched");
        Check.True(LyricSearch.NamesSong("hotline bling by drake", "Hotline Bling", ["Drake"]) && LyricSearch.NamesSong("Hotline Bling Drake song lyrics", "Hotline Bling", ["Drake"]),
            "a query of the title and artist (with words like by, song, lyrics) names the song");
        Check.False(LyricSearch.NamesSong("hotline bling by drake now tonight", "Hotline Bling", ["Drake"]), "two words beyond the name: not the name");
        Check.False(LyricSearch.NamesSong("you used to call me on my cell phone", "Hotline Bling", ["Drake"]), "a line of lyrics does not name the song");
        Check.False(LyricSearch.NamesSong("started from the bottom now we here", "Started From the Bottom", ["Drake"]), "a lyric that begins with the title is still a lyric");

        Song Catalog(string id, string title, string artist, double seconds, int popularity) => new(id, title, artist, seconds, popularity);
        var hit = new SpotifyTrackHit("sp", "Hotline Bling", ["Drake"], "Views", 267, true, "", true);
        Check.True(LyricSearch.NamesAlbum("without warning 21 savage", new SpotifyTrackHit("gk", "Ghostface Killers (feat. Travis Scott)",
            ["21 Savage", "Offset", "Metro Boomin"], "Without Warning", 269, true, "", true)), "an album and its artist name the album, not lyrics");
        Check.False(LyricSearch.NamesAlbum("you used to call me on my cell phone", hit), "a line of lyrics does not name the album");
        var copies = new[]
        {
            Catalog("cover", "Hotline Bling", "William Singe", 200, 600),
            Catalog("long", "Hotline Bling", "Drake", 290, 990),
            Catalog("drake", "Hotline Bling", "Drake", 267.5, 900),
            Catalog("drake-low", "Hotline Bling", "Drake", 266, 100),
        }.Select(s => s.Search()).ToList();
        Check.Equal("drake", LyricSearch.Match(hit, copies)?.Song.Id, "the catalog copy is Drake's own, the right length, most popular; never the cover");
        Check.True(LyricSearch.Match(hit with { Artists = ["Somebody Else"] }, copies) is null, "no copy by a fitting artist: no answer");
        var drake = copies[2];
        var catalogFirst = new List<FinderHit> { new("Songs", "drake", "Hotline Bling", "Drake", 100, Track: drake) };
        Check.False(FinderOnlineSearch.FromLyrics(FinderQuery.Parse("hotline bling by drake"), drake),
            "a title search gets no lyrics label, even when Spotify's answer is that song");
        Check.True(FinderOnlineSearch.Leads(FinderQuery.Parse("you used to call me on my cell phone"), catalogFirst, drake)
            && FinderOnlineSearch.FromLyrics(FinderQuery.Parse("you used to call me on my cell phone"), drake),
            "a line of lyrics leads with its song, labelled, also when the catalog found that song too");
        var cover = copies[0];
        var coverFirst = new List<FinderHit> { new("Songs", "cover", "Hotline Bling", "William Singe", 100, Track: cover) };
        Check.False(FinderOnlineSearch.Leads(FinderQuery.Parse("hotline bling william singe"), coverFirst, drake),
            "the words name the catalog's first song and it is not a far-less-played copy: the catalog's ranking stands");
        var karaoke = Catalog("karaoke", "Started From The Bottom Now We Here (Originally Performed By Drake)", "Karaoke Hits", 180, 80).Search();
        var bottom = Catalog("bottom", "Started From the Bottom", "Drake", 174, 900_000).Search();
        var karaokeFirst = new List<FinderHit> { new("Songs", "karaoke", karaoke.Song.Title, "Karaoke Hits", 110, Track: karaoke) };
        var started = FinderQuery.Parse("started from the bottom now we here");
        Check.True(FinderOnlineSearch.Leads(started, karaokeFirst, bottom) && FinderOnlineSearch.FromLyrics(started, bottom),
            "a karaoke copy whose title is the typed line gives way to Drake's song, played far more (owner: popularity wins)");

        var parsed = PlaylistCatalog.ParseTracks(JsonSerializer.Serialize(Tracks()));
        Check.Equal(2, parsed.Count, "songs are read; a podcast in the results is not");
        Check.True(parsed[0] is { Title: "Hotline Bling", Album: "Views", Explicit: true, Lyrics: true, Seconds: > 267 and < 268 } && parsed[0].Artists.SequenceEqual(new[] { "Drake" })
            && parsed[0].Artwork == "https://i.scdn.co/image/views", "a song keeps its title, artist, album, length, cover and Spotify's lyrics flag");
        Check.False(parsed[1].Lyrics, "a song matched only by its name is not flagged as lyrics");
        Check.True(PlaylistCatalog.StaleQuery("{\"errors\":[{\"message\":\"PersistedQueryNotFound\"}]}") && !PlaylistCatalog.StaleQuery("{\"data\":{}}"), "a retired query is recognised");
        Check.Equal(NewHash, PlaylistCatalog.TracksHashIn("x(\"searchTracks\",\"query\",\"" + NewHash + "\",null)"), "the current song-search query is read from the web player's code");

        var http = new Http();
        using (var catalog = new PlaylistCatalog(http))
        {
            var found = await catalog.SearchTracksAsync("you use to call me on my cellphone late nite", 5, default);
            Check.Equal("Hotline Bling", found.FirstOrDefault()?.Title, "a retired query is replaced by the web player's current one and the search answers");
            Check.True(http.StaleAnswers == 1 && http.Hashes.Last() == NewHash, "the search was retried once with the current query");
            await catalog.SearchTracksAsync("is this the real life is this just fantasy", 5, default);
            Check.True(http.PlayerReads == 1 && http.Hashes.Last() == NewHash, "later searches keep the current query: the web player is read once");
        }
        var broken = new Http { NoPlayerHash = true };
        using (var catalog = new PlaylistCatalog(broken))
        {
            var failed = false;
            try { await catalog.SearchTracksAsync("you used to call me on my cell phone", 5, default); }
            catch (InvalidDataException) { failed = true; }
            Check.True(failed, "no current query to be found: the lyric search fails cleanly (the normal search does not depend on it)");
        }
    }

    private sealed record Song(string Id, string Title, string Artist, double Seconds, int Popularity)
    {
        public CatalogSearchSong Search() => new(new CatalogSong(Id, Title, [new CatalogArtist("a-" + Artist, Artist)], Seconds, true, Popularity: Popularity), null);
    }
}
