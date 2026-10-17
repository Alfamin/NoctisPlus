using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>The typed catalog service behind the native artist/album pages (replaced the
/// plugin-era window-scanning bridge, 2026-10-17). Ported from the bridge's identity checks.</summary>
internal static class CatalogPagesTests
{
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
    }
}
