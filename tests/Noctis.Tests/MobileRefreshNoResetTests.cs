using System.Collections.Specialized;
using System.Linq;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A library refresh that changes nothing an open page shows must not Reset its list: every
/// Reset tears down and re-realises every row or tile on screen (covers reload, the playing
/// row's bars restart), and the phone refreshes on each heart and on each of a scan's partial
/// publishes. A heart also no longer re-sorts the whole library (the title sort of every song,
/// culture-aware, ran on the UI thread per tap).
/// </summary>
public class MobileRefreshNoResetTests
{
    private sealed class ResetCounter
    {
        public int Resets;
        public ResetCounter(INotifyCollectionChanged list) =>
            list.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) Resets++; };
    }

    [Fact]
    public void FavouriteToggle_UpdatesTheCount_WithoutResettingOpenLists()
    {
        var a = MobileFixtures.Song("Alpha");
        var b = MobileFixtures.Song("Beta");
        var album = MobileFixtures.MakeAlbum("First", "Band", a, b);
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, new[] { album });
        // Both pages stay on the stack (a popped page stops listening), so both refresh.
        rig.Shell.OpenSongsCommand.Execute(null);
        var songs = (SongListPageViewModel)rig.Shell.CurrentPage!;
        rig.Shell.OpenAlbumsCommand.Execute(null);
        var grid = (AlbumGridPageViewModel)rig.Shell.CurrentPage!;
        rig.Shell.Search.Query = "a";
        var songResets = new ResetCounter(songs.Songs);
        var gridResets = new ResetCounter(grid.Rows);
        var librarySongResets = new ResetCounter(rig.Shell.Library.Songs);
        var searchResets = new ResetCounter(rig.Shell.Search.Songs);

        a.IsFavorite = true;
        rig.Library.NotifyFavoritesChanged();

        Assert.Equal(1, rig.Shell.Library.FavoriteCount);
        Assert.Equal(0, songResets.Resets);
        Assert.Equal(0, gridResets.Resets);
        Assert.Equal(0, librarySongResets.Resets);   // no re-sort of the whole library for a heart
        Assert.Equal(0, searchResets.Resets);
        Assert.Equal(new[] { "Alpha", "Beta" }, songs.Songs.Select(t => t.Title));
    }

    [Fact]
    public void LibraryUpdated_ResetsOpenLists_OnlyWhenTheirRowsChanged()
    {
        var a = MobileFixtures.Song("Alpha");
        var b = MobileFixtures.Song("Beta");
        var album = MobileFixtures.MakeAlbum("First", "Band", a, b);
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, new[] { album });
        rig.Shell.OpenSongsCommand.Execute(null);
        var songs = (SongListPageViewModel)rig.Shell.CurrentPage!;
        rig.Shell.OpenAlbumsCommand.Execute(null);   // over Songs, which keeps listening
        var grid = (AlbumGridPageViewModel)rig.Shell.CurrentPage!;
        var songResets = new ResetCounter(songs.Songs);
        var gridResets = new ResetCounter(grid.Rows);

        rig.Library.RaiseLibraryUpdated();   // a scan's partial publish with nothing new
        Assert.Equal(0, songResets.Resets);
        Assert.Equal(0, gridResets.Resets);

        var c = MobileFixtures.Song("Gamma");
        rig.Library.TrackList.Add(c);
        ((System.Collections.Generic.List<Album>)rig.Library.Albums).Add(MobileFixtures.MakeAlbum("Second", "Band", c));
        rig.Library.RaiseLibraryUpdated();
        Assert.Equal(1, songResets.Resets);
        Assert.Equal(1, gridResets.Resets);
        Assert.Equal(3, songs.Songs.Count);
        Assert.Equal(2, grid.AlbumCount);
    }

    [Fact]
    public void Search_AKeystrokeThatNarrowsNothing_DoesNotResetTheResults()
    {
        // Alpha by its title, Beta through its album "Alps" (a song matches on album too).
        var a = MobileFixtures.Song("Alpha");
        var b = MobileFixtures.Song("Beta");
        var zeta = MobileFixtures.MakeAlbum("Zeta", "Band", a);
        var alps = MobileFixtures.MakeAlbum("Alps", "Band", b);
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, new[] { zeta, alps });
        var search = rig.Shell.Search;
        search.Query = "al";
        Assert.Equal(new[] { "Alpha", "Beta" }, search.Songs.Select(t => t.Title));
        Assert.Equal(new[] { "Alps" }, search.Albums.Select(x => x.Name));
        var songResets = new ResetCounter(search.Songs);
        var albumResets = new ResetCounter(search.Albums);

        search.Query = "alp";   // the same two songs, the same album
        Assert.Equal(0, songResets.Resets);
        Assert.Equal(0, albumResets.Resets);

        search.Query = "alph";  // Alps (and Beta with it) drop out
        Assert.Equal(new[] { "Alpha" }, search.Songs.Select(t => t.Title));
        Assert.Equal(1, songResets.Resets);
        Assert.Equal(1, albumResets.Resets);
    }
}
