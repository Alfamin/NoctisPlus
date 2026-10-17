using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeMusicFinder;

namespace Noctis.Tests;

public class ArtistCatalogNavigationTests
{
    [AvaloniaFact]
    public void OnlineReleases_AreShownOnThePage_ButNeverPlayedOrAddedToTheLibrary()
    {
        using var page = Page();
        var artist = new CatalogArtist("deezer:2", "Avenoir");
        var profile = new CatalogProfile(artist, [], [
            new("deezer:10", "NOIRE", artist, "2025-05-01", "album", 9),
            new("deezer:11", "Mirage", artist, "2026-02-01", "single", 1),
            new("deezer:12", "Guest Spot", new("deezer:3", "Someone"), "2024-01-01", "album", 10),
        ]);
        using var catalog = new ArtistCatalog(Path.Combine(Path.GetTempPath(), "noctis-tests-catalog-" + Guid.NewGuid().ToString("N")));
        page.ApplyCatalogProfile(profile, catalog);

        Assert.Equal(2, page.Releases.Count);
        Assert.All(page.Releases, a => Assert.True(a.IsOnlineOnly));
        Assert.Equal("Mirage", page.LatestRelease?.Name);                       // the newest leads the hero
        Assert.Equal(ReleaseType.Single, page.Releases.Single(a => a.Name == "Mirage").ReleaseType);
        Assert.Contains(page.AppearsOn, a => a.Name == "Guest Spot");           // another artist's release
        Assert.Empty(page.GetAllTracks());                                        // play/shuffle stay local-only

        var tile = page.Releases.First(a => a.Name == "NOIRE");
        page.ApplyCatalogProfile(profile, catalog);                               // a refresh keeps the same tiles (covers stay)
        Assert.Same(tile, page.Releases.First(a => a.Name == "NOIRE"));
    }

    private static ArtistDetailViewModel Page()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        return new ArtistDetailViewModel("Avenoir", library, player);
    }

    [Fact]
    public void SimilarArtistWithoutLocalSongs_StillNavigates()
    {
        using var page = Page();
        string? opened = null;
        page.ArtistOpened += (_, name) => opened = name;
        page.OpenSimilarArtistCommand.Execute(new SimilarArtistRow { Name = "Chase Shakur", IsInLibrary = false });
        Assert.Equal("Chase Shakur", opened);
    }

    [Fact]
    public void SimilarRows_RetainStableProviderIdentity()
    {
        var row = Assert.Single(ArtistDetailViewModel.BuildSimilarRows(
            [new() { Name = "Chase Shakur", DeezerId = 123 }], [], null));
        Assert.Equal("deezer:123", row.CatalogId);
        Assert.False(row.IsInLibrary);
    }

    [Fact]
    public void SeeAll_ContainsOnlineSongsInProviderOrder_AndKeepsOwnedPlaybackRows()
    {
        using var page = Page();
        var owned = new Track { Id = Guid.NewGuid(), Title = "Owned song", Artist = "Avenoir" };
        var rows = Enumerable.Range(1, 12).Select(i => new ArtistCatalogSong(i,
            new SearchSongItem(new OnlineSong("apple:" + i, "Song " + i, "Avenoir")))).ToList();
        rows.Add(new(13, new SearchSongItem(owned)));
        page.SetCatalogRows(rows);
        Assert.Equal(5, page.CatalogPopular.Count);
        page.SelectTabCommand.Execute("songs");
        Assert.Equal(13, page.CatalogSongs.Count);
        Assert.Equal(13, page.DisplaySongCount);
        Assert.Equal(rows, page.CatalogSongs);
        Assert.Same(owned, page.CatalogSongs[^1].Song.Local);
        Assert.True(page.CatalogSongs[0].Song.CanStartDownload);
        Assert.False(page.ShowNoPopular);
        Assert.False(page.ShowNoAllSongs);
        page.ApplyFilter("Song 12");
        Assert.Equal("Song 12", Assert.Single(page.CatalogSongs).Song.Title);
        page.ApplyFilter("");
        Assert.Equal(13, page.CatalogSongs.Count);
        page.ApplyFilter("No matching title at all");
        Assert.True(page.ShowNoAllSongs);
        Assert.True(page.ShowNoPopular);
        page.ClearCatalogSongs();
        Assert.False(page.HasCatalogSongs);
        Assert.Empty(page.CatalogSongs);
        Assert.Empty(page.CatalogPopular);
    }
}
