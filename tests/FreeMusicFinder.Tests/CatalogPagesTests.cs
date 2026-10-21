using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>The typed catalog service behind the native artist/album pages (replaced the
/// plugin-era window-scanning bridge, 2026-10-17). Ported from the bridge's identity checks.</summary>
internal static class CatalogPagesTests
{
    private sealed class ScanningLibrary : IPlaylistLibrary
    {
        public Task<IReadOnlyList<LocalSong>> SnapshotAsync() => throw new IOException("LIBRARY_SCANNING: synthetic scan in progress");
        public Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
            => throw new InvalidOperationException("Browsing must not write playlists");
    }

    internal static async Task RunAsync()
    {
        Check.Section("catalog pages: artist identity, remembered choices, namesake visits, album opening");
        var folder = Check.NewFolder("catalog-pages");
        var wrongArtist = new CatalogArtist("deezer:404", "North Star", "Dance");
        ArtistIdentityTests.Seed(folder, "search:NORTH STAR", new[] { wrongArtist, DiscoveryTests.Star });
        ArtistIdentityTests.Seed(folder, "artist:" + wrongArtist.Id, new CatalogProfile(wrongArtist, [], [new("deezer:505", "Late Night Sessions", wrongArtist, "2026-01-01", "album", 10)]));
        ArtistIdentityTests.Seed(folder, "artist:" + DiscoveryTests.Star.Id, new CatalogProfile(DiscoveryTests.Star, [], [DiscoveryTests.Orbit, new("deezer:112", "Second Light", DiscoveryTests.Star, "2025-01-01", "album", 8)]));
        var choicesPath = Path.Combine(folder, "artist-choices.json");
        File.WriteAllText(choicesPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, CatalogArtist> { ["North Star"] = wrongArtist }));

        var http = new DiscoveryTests.Http();
        using var catalog = DiscoveryTests.Catalog(folder, http);
        var library = new SpotifyPlaylistTests.Library();
        using var downloads = new Downloads(_ => { });
        var actions = new DiscoveryActions(library, downloads, _ => throw new Exception("No transfer is allowed in this fixture"), () => folder, () => false);
        using var pages = new CatalogPages(() => catalog, actions, _ => { }, () => true, library: () => null, choicesPath: choicesPath);

        var (artist, _) = await pages.IdentifyAsync("North Star", ["Orbit", "Second Light"], [], CancellationToken.None);
        Check.Equal("deezer:42", artist?.Id, "local album evidence picks the right artist over a wrong saved pin");
        var confirmed = File.ReadAllText(choicesPath);
        Check.True(confirmed.Contains("deezer:42") && !confirmed.Contains("deezer:404"), "the wrong legacy pin is repaired without the user editing anything");

        CatalogArtist? opened = null;
        void OnArtist(CatalogArtist a) => opened = a;
        CatalogPages.ArtistRequested += OnArtist;
        try { pages.OpenArtist(wrongArtist); }
        finally { CatalogPages.ArtistRequested -= OnArtist; }
        Check.Equal(wrongArtist, opened, "opening a same-name search result asks the app for exactly that artist");
        Check.Equal(confirmed, File.ReadAllText(choicesPath), "visiting a namesake never overwrites the library artist's choice");

        pages.Choose("North Star", wrongArtist);
        var calls = http.Calls.Count;
        var (manual, _) = await pages.IdentifyAsync("North Star", ["Orbit", "Second Light"], [], CancellationToken.None);
        Check.Equal(wrongArtist, manual, "a choice the user made by hand is kept");
        Check.Equal(calls, http.Calls.Count, "and needs no network");

        CatalogAlbumOpen? album = null;
        void OnAlbum(CatalogAlbumOpen a) => album = a;
        CatalogPages.AlbumRequested += OnAlbum;
        try { await pages.OpenAlbumAsync(DiscoveryTests.Orbit, CancellationToken.None); }
        finally { CatalogPages.AlbumRequested -= OnAlbum; }
        Check.True(album is { Album.Songs.Count: > 0 } && album.Matches.Count == album.Album.Songs.Count, "opening an online album hands the page its songs and which ones the library has");
        Check.Equal(0, downloads.History.Count, "opening an album never downloads audio");
        var logs = new List<string>();
        var scanningActions = new DiscoveryActions(new ScanningLibrary(), downloads, _ => throw new Exception("No audio"), () => folder, () => false);
        using (var scanningPages = new CatalogPages(() => catalog, scanningActions, logs.Add, () => true, library: () => null))
        {
            album = null;
            CatalogPages.AlbumRequested += OnAlbum;
            try { await scanningPages.OpenAlbumAsync(DiscoveryTests.Orbit, default); }
            finally { CatalogPages.AlbumRequested -= OnAlbum; }
            Check.True(album is { Album.Songs.Count: 3, Matches.Count: 0 }, "a busy library scan cannot hide an already fetched album tracklist");
            Check.True(logs.Any(l => l.Contains("CATALOG_LIBRARY_CHECK_FAILED")), "ownership-check failure is logged instead of silently dropping the album");
        }
        // A cached/capped discography can omit the album while direct album search still has it.
        var searchHttp = new DiscoveryTests.Http { Override = (request, ct) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new { data = new[] { DiscoveryTests.Http.Album(), DiscoveryTests.Http.Album(999, "Unrelated") } })) }) };
        var incompleteFolder = Check.NewFolder("catalog-incomplete-profile");
        ArtistIdentityTests.Seed(incompleteFolder, "artist:" + DiscoveryTests.Star.Id, new CatalogProfile(DiscoveryTests.Star, [], []));
        ArtistIdentityTests.Seed(incompleteFolder, "artist:deezer:404", new CatalogProfile(wrongArtist, [], []));
        using (var incompleteCatalog = DiscoveryTests.Catalog(incompleteFolder, searchHttp))
        using (var incompletePages = new CatalogPages(() => incompleteCatalog, actions, _ => { }, () => true, library: () => null))
        {
            Check.Equal(DiscoveryTests.Orbit.Id, (await incompletePages.FindLibraryReleaseAsync(DiscoveryTests.Star, "Orbit", default))?.Id,
                "an incomplete saved artist profile is repaired by exact album lookup");
            Check.Equal(1, searchHttp.Calls.Count, "the missing release needs one direct album search, not a full discography refresh");
            Check.True(await incompletePages.FindLibraryReleaseAsync(new("deezer:404", "North Star"), "Orbit", default) is null,
                "a same-name foreign artist's album is not accepted by the fallback");
        }
        await PopularityAsync();
    }

    private static async Task PopularityAsync()
    {
        Check.Section("artist popularity: stable IDs, album evidence, offline caches and comparable namesakes");
        var folder = Check.NewFolder("artist-fans");
        var http = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new System.Net.Http.StringContent("{\"data\":[{\"id\":43,\"name\":\"North Star\",\"nb_fan\":40},{\"id\":42,\"name\":\"North Star\",\"nb_fan\":2000000}]}") }) };
        using var catalog = DiscoveryTests.Catalog(folder, http);
        var artists = (await catalog.SearchAsync("North Star", false, default)).Value;
        Check.Equal(2000000L, catalog.FansOf(artists[1]) ?? -1, "nb_fan is retained outside the artist record");
        Check.Equal(new CatalogArtist("deezer:42", "North Star"), artists[1], "popularity does not change CatalogArtist value equality");
        var calls = http.Calls.Count;
        var hits = SearchRanking.Build(FinderQuery.Parse("North Star"), artists, [], [], fansOf: catalog.FansOf);
        Check.Equal("deezer:42", SearchRanking.Artists(hits).First().Id, "the popular exact namesake leads even when Deezer lists the tiny one first");
        Check.Equal(calls, http.Calls.Count, "ranking makes no additional metadata requests");

        // Owner 2026-10-18: "post melone" showed a 1-fan namesake instead of Post Malone.
        var typo = new List<CatalogArtist> { new("deezer:9", "Post Melone"), new("deezer:7", "Post Malone") };
        var typoFans = new Dictionary<string, long> { ["deezer:9"] = 1, ["deezer:7"] = 4_000_000 };
        var typoHits = SearchRanking.Build(FinderQuery.Parse("post melone"), typo, [], [], fansOf: a => typoFans[a.Id]);
        Check.Equal("deezer:7", SearchRanking.Artists(typoHits).First().Id, "a one-letter typo of a hugely more popular artist finds that artist");
        var peers = new Dictionary<string, long> { ["deezer:9"] = 300_000, ["deezer:7"] = 4_000_000 };
        var peerHits = SearchRanking.Build(FinderQuery.Parse("post melone"), typo, [], [], fansOf: a => peers[a.Id]);
        Check.Equal("deezer:9", SearchRanking.Artists(peerHits).First().Id, "a real, comparably popular exact namesake keeps the lead");
        Check.True(SearchRanking.NearName("post melone", "post malone") && !SearchRanking.NearName("drake", "blake shelton") && !SearchRanking.NearName("sza", "sia"), "near names are small typos only, never short or different names");

        using var downloads = new Downloads(_ => { });
        var actions = new DiscoveryActions(new SpotifyPlaylistTests.Library(), downloads, _ => throw new Exception("No audio"), () => folder, () => false);
        foreach (var artist in artists) ArtistIdentityTests.Seed(folder, "artist-songs:" + artist.Id, new List<CatalogSearchSong>());
        using var pages = new CatalogPages(() => catalog, actions, _ => { }, () => true, () => null);
        var selected = await pages.IdentifyAsync("North Star", [], [], default);
        Check.Equal("deezer:42", selected.Artist?.Id, "name-only artist navigation chooses a decisive fan-count winner without prompting");
        Check.Equal(calls, http.Calls.Count, "known popularity does not fetch every artist profile");

        var offlineHttp = new DiscoveryTests.Http { Offline = true };
        using var offline = DiscoveryTests.Catalog(folder, offlineHttp);
        Check.Equal("deezer:42", (await offline.PopularNamesakeAsync("North Star", artists, default))?.Id, "popularity survives restarting offline");
        Check.Equal(0, offlineHttp.Calls.Count, "saved popularity adds no offline network wait");

        // Even the less popular artist wins when the user's actual recordings prove that identity.
        ArtistIdentityTests.Seed(folder, "artist:deezer:43", new CatalogProfile(artists[0], [], [new("deezer:123", "Second Light", artists[0], "2026", "album", 12)]));
        Check.Equal("deezer:43", (await pages.IdentifyAsync("North Star", ["Second Light"], [], default)).Artist?.Id, "library album evidence outranks popularity");
        pages.Choose("North Star", artists[0]);
        Check.Equal("deezer:43", (await pages.IdentifyAsync("North Star", [], [], default)).Artist?.Id, "an explicit choice is never overruled by popularity");

        foreach (var counts in new[] { "1000,1500", "1000,null", "999,0", "1000,\"wrong type\"" })
        {
            var values = counts.Split(',');
            var input = "{\"data\":[{\"id\":43,\"name\":\"North Star\",\"nb_fan\":" + values[0] + "},{\"id\":42,\"name\":\"North Star\",\"nb_fan\":" + values[1] + "}]}";
            var otherHttp = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(input) }) };
            using var other = DiscoveryTests.Catalog(Check.NewFolder("comparable-fans"), otherHttp);
            var matches = (await other.SearchAsync("North Star", false, default)).Value;
            Check.True(await other.PopularNamesakeAsync("North Star", matches, default) is null, "comparable, missing or malformed popularity does not invent an identity: " + counts);
            var requests = otherHttp.Calls.Count;
            await other.PopularNamesakeAsync("North Star", matches, default);
            Check.Equal(requests, otherHttp.Calls.Count, "missing counts are refreshed at most once, not on every artist visit");
        }

        var legacyFolder = Check.NewFolder("legacy-artist-fans");
        ArtistIdentityTests.Seed(legacyFolder, "search:NORTH STAR", artists);
        var legacyHttp = new DiscoveryTests.Http { Override = http.Override };
        using var legacy = DiscoveryTests.Catalog(legacyFolder, legacyHttp);
        Check.Equal("deezer:42", (await legacy.PopularNamesakeAsync("North Star", artists, default))?.Id, "legacy namesake caches gain popularity through one search refresh");
        Check.Equal(1, legacyHttp.Calls.Count, "legacy repair is one search request, not one request per candidate");
    }
}
