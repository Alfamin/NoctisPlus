using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services.Search;
using Xunit;

namespace Noctis.Tests;

public class MobileCatalogArtistTests
{
    [AvaloniaFact]
    public async Task SearchKeepsProviderRanking_ArtistScopeDoesNotSearchAgain_AndOpensNativePageWithoutAudio()
    {
        var source = new MobileCatalogAlbumTests.Source();
        var artist = new OnlineArtist("deezer:42", "Artist");
        var answer = new TaskCompletionSource<OnlineArtistPage?>(); source.ArtistMetadata = answer.Task;
        source.SearchAnswer = new("Artist", [new("deezer:1", "One", "Artist")], [artist], [source.Release], true) { Top = ("artist", artist.Id) };
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        var search = rig.Shell.Search; search.Query = "Artist"; await search.Online!.SearchWork;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(search.Online.ArtistSectionOrder < search.Online.SongSectionOrder);
        search.Scope = SearchScope.Artists;
        Assert.Single(search.Online.Artists); Assert.Empty(search.Online.Songs); Assert.Empty(search.Online.Albums);
        Assert.Equal(1, source.Searches);
        search.Online.OpenArtistCommand.Execute(search.Online.Artists[0]);
        var page = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        Assert.True(page.IsCatalogLoading);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            MobileFixtures.Land(window); Assert.NotNull(MobileFixtures.Find<ArtistPage>(view));
            answer.SetResult(new(artist, [source.Release], [new("deezer:1", "One", "Artist")]));
            await page.CatalogLoad; window.UpdateLayout();
            Assert.False(page.IsCatalogLoading); Assert.Single(page.Albums); Assert.Single(page.TopSongs);
            Assert.Empty(rig.Library.TrackList); Assert.Empty(rig.Library.Albums); Assert.Empty(source.Saves); Assert.Equal(0, source.Prepares);
            rig.Shell.OpenAlbumCommand.Execute(page.Albums[0].Album);
            var album = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage); await album.CatalogLoad;
            Assert.Equal(1, source.Reads); album.OnClosed();
        }
        finally { window.Close(); page.OnClosed(); }
    }

    [AvaloniaFact]
    public async Task AllSongsIncludesUnownedBeyondTopTwenty_AndRemoteMenusCannotSavePhantomPlaylistIds()
    {
        var owned = MobileFixtures.Song("Owned", artist: "Artist");
        var source = new MobileCatalogAlbumTests.Source();
        var songs = Enumerable.Range(1, 23).Select(i => new OnlineSong($"deezer:{i}", $"Song {i}", "Artist", LocalTrackId: i == 1 ? owned.Id : null)).ToArray();
        source.ArtistMetadata = Task.FromResult<OnlineArtistPage?>(new(new("deezer:42", "Artist"), [source.Release], songs));
        using var rig = MobileFixtures.MakeRig(tracks: [owned], onlineSearch: source);
        var page = new ArtistPageViewModel(rig.Shell, "Artist"); await page.CatalogLoad;
        try
        {
            Assert.Equal(23, page.Songs.Count); Assert.Equal(20, page.TopSongs.Count); Assert.Same(owned, page.Songs[0]);
            page.OpenAllSongsCommand.Execute(null);
            var all = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage); Assert.Equal(23, all.Songs.Count);
            rig.Shell.OpenTrackSheetCommand.Execute(all.Songs[1]);
            var menu = Assert.IsType<OptionsMenuViewModel>(rig.Shell.OptionsMenu);
            Assert.Equal(new[] { "Play", "Download now", "Add to downloads" }, menu.Items.Select(i => i.Label));
            menu.Items[2].PickCommand.Execute(null);
            Assert.False(Assert.Single(source.Saves).Now); Assert.Equal("deezer:2", source.Saves[0].Songs[0].Id);
            Assert.Single(rig.Library.TrackList); Assert.Empty(rig.Library.Albums);
            all.OnClosed();
        }
        finally { page.OnClosed(); }
    }

    [AvaloniaFact]
    public async Task SignedOutCatalogList_ShowsDownloadIndicator_AndDoesNotPrepareAnUnownedSong()
    {
        var source = new MobileCatalogAlbumTests.Source { SignedIn = false };
        source.ArtistMetadata = Task.FromResult<OnlineArtistPage?>(new(new("deezer:42", "Artist"), [], [new("deezer:1", "One", "Artist")]));
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        var page = new ArtistPageViewModel(rig.Shell, "Artist"); await page.CatalogLoad;
        page.OpenAllSongsCommand.Execute(null);
        var list = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            MobileFixtures.Land(window);
            var row = MobileFixtures.Find<SongRow>(view);
            Assert.True(MobileFixtures.Named<PathIcon>(row, "RemoteIndicator").IsVisible);
            list.PlayCommand.Execute(list.Songs[0]); list.PlayAllCommand.Execute(null); list.ShuffleAllCommand.Execute(null);
            Assert.Null(rig.Shell.Player.CurrentTrack); Assert.Equal(0, source.Prepares); Assert.Empty(source.Saves);
            Assert.False(new ArtistCard(ArtistCardKind.Featured, new Noctis.Models.Album()).HasSongsText);
        }
        finally { window.Close(); list.OnClosed(); page.OnClosed(); }
    }

    [AvaloniaFact]
    public async Task ArtistClosedBeforeMetadataArrives_DoesNotPublishLateRows_AndFailuresKeepLocalSongs()
    {
        var source = new MobileCatalogAlbumTests.Source(); var answer = new TaskCompletionSource<OnlineArtistPage?>(); source.ArtistMetadata = answer.Task;
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        var page = new ArtistPageViewModel(rig.Shell, "Artist"); page.OnClosed();
        answer.SetResult(new(new("deezer:42", "Artist"), [source.Release], [new("deezer:1", "One", "Artist")]));
        await page.CatalogLoad; Assert.Empty(page.Albums); Assert.Empty(page.TopSongs);
        source.ArtistMetadata = Task.FromException<OnlineArtistPage?>(new IOException("Network unavailable"));
        var local = MobileFixtures.Song("Owned", artist: "Artist"); rig.Library.TrackList.Add(local);
        var failed = new ArtistPageViewModel(rig.Shell, "Artist"); await failed.CatalogLoad;
        Assert.True(failed.HasCatalogError); Assert.Contains(local, failed.Songs); failed.OnClosed();
    }
}
