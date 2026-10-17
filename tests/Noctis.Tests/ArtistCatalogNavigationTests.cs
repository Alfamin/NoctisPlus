using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class ArtistCatalogNavigationTests
{
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
        Assert.Equal(rows, page.CatalogSongs);
        Assert.Same(owned, page.CatalogSongs[^1].Song.Local);
        Assert.True(page.CatalogSongs[0].Song.CanStartDownload);
        Assert.False(page.ShowNoPopular);
        Assert.False(page.ShowNoAllSongs);
        page.ApplyFilter("Song 12");
        Assert.Equal("Song 12", Assert.Single(page.CatalogSongs).Song.Title);
        page.ApplyFilter("");
        Assert.Equal(13, page.CatalogSongs.Count);
    }
}
