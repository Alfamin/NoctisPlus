using System.Net;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #69: playlists searched by name, ranked by name and saves, Deezer as the backup, and a found
/// playlist previewed then saved without being read twice. Synthetic answers only.</summary>
internal static class PlaylistSearchTests
{
    /// <summary>Spotify's search, counts and token page, Deezer's search; playlist reads go to the import fake.</summary>
    internal sealed class Http : HttpMessageHandler
    {
        public readonly SpotifyPlaylistTests.Http Lists = new();
        public bool SpotifyDown, CountsSlow;
        /// <summary>Serve Spotify's paged playlist query: this many entries (0: answer it like an unknown page).</summary>
        public int PagedTotal;
        public bool ShortPage;
        public int PageCalls;
        public string LastSearch = "", LastDeezer = "";
        public int Unauthorized;
        public int TokenPages, Searches, CountCalls, DeezerSearches, ListReads;
        // name, owner, saves (null: no count), songs, cover width (null: an uploaded cover without a size)
        public (string Name, string Owner, long? Saves, int Songs, int? Width)[] Found =
        [
            ("Workout Hits", "Gym Co", 900_000, 140, 300),
            ("Hype Drake Only", "Nikita", 6_353, 87, 300),
            ("Drake Hype Songs", "Saarujan", 18_335, 106, null),
            ("Drake hype (no count)", "Ollie", null, 52, 300),
        ];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api-partner.spotify.com" && uri.Query.Contains("operationName=fetchPlaylist"))
            {
                PageCalls++;
                if (PagedTotal == 0) return new(HttpStatusCode.BadRequest);
                using var vars = JsonDocument.Parse(Uri.UnescapeDataString(System.Text.RegularExpressions.Regex.Match(uri.Query, "variables=([^&]+)").Groups[1].Value));
                var offset = vars.RootElement.GetProperty("offset").GetInt32();
                var count = Math.Min(100, PagedTotal - offset) - (ShortPage && offset > 0 ? 1 : 0);
                var items = Enumerable.Range(offset, Math.Max(0, count)).Select(i => (object)(i switch
                {
                    4 => new { itemV2 = new { data = new { __typename = "LocalTrack", uri = "spotify:local:Local+Artist::Local+Song:200" } } },
                    7 => new { itemV2 = new { data = new { __typename = "Episode", uri = "spotify:episode:" + SpotifyPlaylistTests.Id(7) } } },
                    _ => new { itemV2 = new { data = new
                    {
                        __typename = "Track", uri = "spotify:track:" + SpotifyPlaylistTests.Id(i), name = "Paged " + i,
                        artists = new { items = new[] { new { profile = new { name = "Artist" } }, new { profile = new { name = "Guest" } } } },
                        trackDuration = new { totalMilliseconds = 180000 }, contentRating = new { label = i % 2 == 0 ? "EXPLICIT" : "NONE" },
                        albumOfTrack = new { name = "Album " + i, coverArt = new { sources = new[] { new { url = "https://i.scdn.co/image/a" + i, width = 300 } } } },
                    } } },
                })).ToArray();
                return Json(new { data = new { playlistV2 = new { name = "Paged list", content = new { totalCount = PagedTotal, items } } } });
            }
            if (uri.Host == "api-partner.spotify.com")
            {
                Searches++;
                LastSearch = Uri.UnescapeDataString(uri.Query);
                if (SpotifyDown) return new(HttpStatusCode.NotFound);
                if (Unauthorized > 0) { Unauthorized--; return new(HttpStatusCode.Unauthorized); }
                Check.True(request.Headers.Authorization?.Parameter == "search-token", "the search carries the embed page's token");
                var items = Found.Select((f, i) => new
                {
                    data = new
                    {
                        __typename = "Playlist", name = f.Name, uri = "spotify:playlist:" + SpotifyPlaylistTests.Id(10 + i),
                        ownerV2 = new { data = new { name = f.Owner } },
                        images = new { items = new[] { new { sources = new object[]
                        {
                            new { url = $"https://mosaic.scdn.co/640/{i}", width = (int?)(f.Width is null ? null : 640) },
                            new { url = $"https://mosaic.scdn.co/300/{i}", width = f.Width },
                        } } } },
                    },
                });
                return Json(new { data = new { searchV2 = new { playlists = new { items } } } });
            }
            if (uri.Host == "spclient.wg.spotify.com" && (uri.AbsolutePath.StartsWith("/popcount/") || uri.AbsolutePath.EndsWith("/metadata")))
            {
                CountCalls++;
                if (CountsSlow) await Task.Delay(TimeSpan.FromSeconds(10), ct);
                var index = int.Parse(uri.Segments[uri.AbsolutePath.StartsWith("/popcount/") ? 4 : 4].TrimEnd('/')) - 10;
                var f = Found[index];
                if (uri.AbsolutePath.StartsWith("/popcount/"))
                    return f.Saves is { } saves ? Json(new { count = saves.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : new(HttpStatusCode.NotFound);
                return Json(new { length = f.Songs });
            }
            if (uri.Host == "api.deezer.com" && uri.AbsolutePath == "/search/playlist")
            {
                DeezerSearches++;
                LastDeezer = uri.Query;
                return Json(new { data = new object[]
                {
                    new { id = 111, title = "drake hype", nb_tracks = 32, picture_medium = "https://cdn-images.dzcdn.net/images/playlist/a/250x250-000000-80-0-0.jpg", user = new { name = "amz" } },
                    new { id = 222, title = "Hype", nb_tracks = 18, picture_medium = "https://example.com/not-a-cover.jpg", user = new { name = "Adele" } },
                } });
            }
            if (uri.AbsoluteUri == "https://open.spotify.com/embed/playlist/37i9dQZF1DXcBWIGoYBM5M")
            {
                TokenPages++;
                var html = "<script id=\"__NEXT_DATA__\">" + JsonSerializer.Serialize(new { props = new { pageProps = new { state = new { settings = new { session = new
                    { accessToken = "search-token", accessTokenExpirationTimestampMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() } } } } } }) + "</script>";
                return new(HttpStatusCode.OK) { Content = new StringContent(html) };
            }
            ListReads++;
            return await Forward(request, ct);
        }

        private Task<HttpResponseMessage> Forward(HttpRequestMessage request, CancellationToken ct)
            => (Task<HttpResponseMessage>)typeof(HttpMessageHandler).GetMethod("SendAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(Lists, [request, ct])!;

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }

    public static async Task RunAsync()
    {
        Check.Section("searching playlists by name (owner #69)");
        Ranking();
        Parsing();
        await Searching();
        await PreviewThenSave();
        await PagedReading();
    }

    private static async Task PagedReading()
    {
        var http = new Http { PagedTotal = 250 };
        using (var catalog = new PlaylistCatalog(http))
        {
            MusicLink.TryParse(SpotifyPlaylistTests.Url(9), out var link);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var manifest = await catalog.ReadAsync(link!, null, _ => { }, _ => { }, default);
            Check.Equal(250, manifest.Entries.Count, "a 250-song playlist is read whole in pages of 100");
            Check.Equal(3, http.PageCalls, "three pages, not one page per song");
            Check.Equal(0, http.ListReads, "no embed page is read when the pages answer");
            Check.True(manifest.CompleteIds && manifest.Entries.Select(e => e.Position).SequenceEqual(Enumerable.Range(1, 250)), "every position, in order");
            Check.Equal("Paged list", manifest.Name, "under the playlist's own name");
            var first = manifest.Entries[0];
            Check.Equal("Paged 0 / Artist, Guest / 180 / True", $"{first.Request?.Title} / {first.Request?.Artist} / {first.Request?.Seconds} / {first.Request?.Explicit}", "each song with its title, artists, length and explicit flag");
            Check.Equal("Album 0 / https://i.scdn.co/image/a0", $"{first.Album} / {first.Artwork}", "and its album and cover, which the embed pages never gave");
            Check.Equal("Local Song / Local Artist", $"{manifest.Entries[4].Request?.Title} / {manifest.Entries[4].Request?.Artist}", "a local file entry keeps the usual reading");
            Check.True(manifest.Entries[7].Request is null && manifest.Entries[7].Issue.StartsWith("UNSUPPORTED_ENTRY"), "an episode is listed, not guessed");
            Check.True(manifest.Entries[249].Request?.Title == "Paged 249", "the last page is read too");
        }

        var broken = new Http { PagedTotal = 250, ShortPage = true };
        using (var catalog = new PlaylistCatalog(broken))
        {
            MusicLink.TryParse(SpotifyPlaylistTests.Url(1), out var link);
            var manifest = await catalog.ReadAsync(link!, null, _ => { }, _ => { }, default);
            Check.True(broken.ListReads > 0 && manifest.Entries.Count == 4 && manifest.Name == "Feeling Shit", "a short page falls back to the embed reading, never a partial list");
        }
        var unknown = new Http { PagedTotal = 0 };
        using (var catalog = new PlaylistCatalog(unknown))
        {
            MusicLink.TryParse(SpotifyPlaylistTests.Url(2), out var link);
            var manifest = await catalog.ReadAsync(link!, null, _ => { }, _ => { }, default);
            Check.Equal(3, manifest.Entries.Count, "Spotify refusing the paged query: the embed reading answers");
        }
    }

    private static void Ranking()
    {
        var found = new List<FoundPlaylist>
        {
            new("u0", "Workout Songs For The Gym", "", "", 149, 338_427),
            new("u1", "Hype Drake Only", "", "", 87, 6_353),
            new("u2", "Drake Hype Songs", "", "", 106, 18_335),
            new("u3", "drake gym pump", "", "", 76, 18_287),
            new("u4", "hype drake", "", "", 52, 221),
        };
        var ranked = PlaylistCatalog.Rank("hype drake", found).Select(f => f.Url).ToList();
        Check.Equal("u2,u1,u4,u0,u3", string.Join(",", ranked), "names with every word come first, most saved first; a huge generic playlist follows them");
        Check.Equal("u2,u1,u4", string.Join(",", PlaylistCatalog.Rank("hype drake songs", found).Take(3).Select(f => f.Url)),
            "\"songs\" describes any playlist: it does not push Drake playlists without it down");
        var unknown = PlaylistCatalog.Rank("drake", [new("a", "Drake A", "", "", 0, null), new("b", "Drake B", "", "", 0, 5_000)]);
        Check.Equal("b", unknown[0].Url, "a playlist with no count is not taken for a popular one");
    }

    private static void Parsing()
    {
        Check.Equal(18335L, PlaylistCatalog.Number("{\"count\":\"18335\",\"truncated\":true}", "count"), "Spotify's save count arrives as a string");
        Check.Equal(106L, PlaylistCatalog.Number("{\"length\":106}", "length"), "its song count as a number");
        Check.Equal(null, PlaylistCatalog.Number("{\"count\":null}", "count"), "a missing count is no count, not zero");
        Check.Equal(null, PlaylistCatalog.Number("not json", "count"), "an odd answer is no count, not a failed search");
        var deezer = PlaylistCatalog.ParseDeezerSearch("{\"data\":[{\"id\":5,\"title\":\"A\\u0007B\",\"nb_tracks\":20000,\"picture_medium\":\"https://evil.example/x.jpg\",\"user\":{\"name\":\"me\"}},{\"id\":-1,\"title\":\"bad\"}]}");
        Check.Equal(1, deezer.Count, "a Deezer result without a real id is left out");
        Check.Equal("AB", deezer[0].Name, "control characters are dropped from names");
        Check.Equal("", deezer[0].Artwork, "a cover from an unknown host is not fetched");
        Check.Equal(10000, deezer[0].Tracks, "an absurd song count is capped");
        Check.Equal("https://www.deezer.com/playlist/5", deezer[0].Url, "Deezer results open as Deezer playlist links");
        Check.True(ArtistCatalog.ArtworkUrl("https://seed-mix-image.spotifycdn.com/v6/img/desc/Workout/en/default") && ArtistCatalog.ArtworkUrl("https://mosaic.scdn.co/300/ab67"),
            "covers of Spotify's own mixes and mosaics are fetched");
        Check.True(!ArtistCatalog.ArtworkUrl("https://seed.spotifycdn.com.evil.example/x.jpg") && !ArtistCatalog.ArtworkUrl("http://seed-mix-image.spotifycdn.com/x"),
            "a look-alike host or plain http is not");
    }

    private static async Task Searching()
    {
        var http = new Http();
        using (var catalog = new PlaylistCatalog(http))
        {
            var (found, source) = await catalog.SearchPlaylistsAsync("hype drake", 24, default);
            Check.Equal("Spotify", source, "Spotify answers first");
            Check.Equal("Drake Hype Songs,Hype Drake Only,Drake hype (no count),Workout Hits", string.Join(",", found.Select(f => f.Name)),
                "ranked by name, then saves (the 900K workout playlist follows the Drake ones)");
            Check.Equal(18335L, found[0].Saves, "each result carries its saves");
            Check.Equal(106, found[0].Tracks, "and its song count");
            Check.Equal(null, found[2].Saves, "a playlist Spotify gives no count for shows none");
            Check.Equal("https://mosaic.scdn.co/300/1", found[1].Artwork, "the 300 px cover for a tile");
            Check.Equal("https://mosaic.scdn.co/640/2", found[0].Artwork, "a cover that comes without a size is still used");
            Check.Equal("https://open.spotify.com/playlist/" + SpotifyPlaylistTests.Id(12), found[0].Url, "results open as Spotify playlist links");
            var counted = http.CountCalls;
            await catalog.SearchPlaylistsAsync("hype drake", 24, default);
            Check.Equal(1, http.TokenPages, "the search token is fetched once and reused");
            Check.Equal(counted, http.CountCalls, "counts are remembered: a repeat search asks Spotify for none");

            http.Unauthorized = 1;
            (found, source) = await catalog.SearchPlaylistsAsync("gym session", 24, default);
            Check.True(source == "Spotify" && found.Count == 4 && http.TokenPages == 2, "an expired token is replaced once and the search goes on");
        }

        var slow = new Http { CountsSlow = true };
        PlaylistCatalog.CountsBudget = TimeSpan.FromMilliseconds(300);
        try
        {
            using var catalog = new PlaylistCatalog(slow);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (found, _) = await catalog.SearchPlaylistsAsync("slow counts", 24, default);
            Check.True(clock.Elapsed < TimeSpan.FromSeconds(3) && found.Count == 4, "slow counts never hold up the results");
        }
        finally { PlaylistCatalog.CountsBudget = TimeSpan.FromSeconds(3); }

        var down = new Http { SpotifyDown = true };
        using (var catalog = new PlaylistCatalog(down))
        {
            var (found, source) = await catalog.SearchPlaylistsAsync("hype drake", 24, default);
            Check.Equal("Deezer", source, "Spotify not answering: Deezer's search stands in");
            Check.Equal("drake hype,Hype", string.Join(",", found.Select(f => f.Name)), "with Deezer's results");
            Check.Equal(32, found[0].Tracks, "and Deezer's song counts");
            Check.Equal(0, down.CountCalls, "no Spotify counts are asked for Deezer results");
        }
        var paged = new Http();
        using (var catalog = new PlaylistCatalog(paged))
        {
            await catalog.SearchPlaylistsAsync("hype drake", 24, default, offset: 24);
            Check.True(paged.LastSearch.Contains("\"offset\":24"), "a later page asks Spotify from that offset (owner #71)");
        }
        var pagedDown = new Http { SpotifyDown = true };
        using (var catalog = new PlaylistCatalog(pagedDown))
        {
            await catalog.SearchPlaylistsAsync("hype drake", 24, default, offset: 48);
            Check.True(pagedDown.LastDeezer.Contains("index=48"), "and Deezer from the same place");
        }
        var nothing = await new PlaylistCatalog(new Http()).SearchPlaylistsAsync("   ", 24, default);
        Check.Equal(0, nothing.Playlists.Count, "an empty query asks nobody");
    }

    private static async Task PreviewThenSave()
    {
        var folder = Check.NewFolder("playlist-preview");
        var library = new SpotifyPlaylistTests.Library();
        var owned = library.Add(folder, "Song 0");
        var http = new Http();
        var sources = new MusicSources(new TransferTests.Source("DeezLoad"), new TransferTests.Source("Music Hunters"), _ => { });
        using var downloads = new Downloads(_ => { }, Path.Combine(folder, "queue.json"), sources.Track);
        using var imports = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), library, downloads, sources.Track, new PlaylistCatalog(http));

        var (manifest, entries) = await imports.PreviewAsync(SpotifyPlaylistTests.Url(1), default);
        Check.Equal(4, entries.Count, "the preview lists every entry, a song listed twice included");
        Check.Equal(owned.Id, entries[0].LocalId, "a song the library has stands as the library's own");
        Check.True(entries.Skip(1).All(e => e.LocalId is null && e.Song is not null), "the others are missing songs with their details");
        Check.Equal(0, library.Saves, "looking creates no playlist");
        Check.Equal(0, imports.Jobs.Count, "and adds nothing to the import list");
        Check.Equal(null, imports.SavedAs(SpotifyPlaylistTests.Url(1)), "it is not saved yet");

        var reads = http.ListReads;
        await imports.PreviewAsync(SpotifyPlaylistTests.Url(1), default);
        Check.Equal(reads, http.ListReads, "opening it again uses the same reading");

        var id = await imports.SaveAsync(SpotifyPlaylistTests.Url(1), default);
        Check.Equal(reads, http.ListReads, "Save does not read the playlist again");
        Check.Equal(1, library.Saves, "Save creates one normal playlist");
        Check.Equal(owned.Id, library.Lists[id].Single(), "holding the songs the library has, in playlist order");
        Check.Equal("Feeling Shit", library.Names[id], "under the playlist's own name");
        Check.Equal(id, imports.SavedAs(SpotifyPlaylistTests.Url(1)), "it now counts as saved");
        var (found, _) = await imports.SearchAsync("drake", 24, default);
        Check.True(found.All(f => f.SavedAs is null), "search results not saved carry no playlist");
        Check.True(manifest.Url == SpotifyPlaylistTests.Url(1), "the preview reads the link it was given");

        try { await imports.PreviewAsync("https://open.spotify.com/album/" + SpotifyPlaylistTests.Id(1), default); Check.True(false, "an album link is not previewed as a playlist"); }
        catch (FormatException) { Check.True(true, "an album link is not previewed as a playlist"); }
    }
}
