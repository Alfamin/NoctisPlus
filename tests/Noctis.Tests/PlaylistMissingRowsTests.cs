using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner #36: an imported playlist with songs the library lacks lists them in the playlist itself (after the owned
/// rows, numbered by playlist position, Play + Download), instead of looking empty.
/// </summary>
public class PlaylistMissingRowsTests
{
    [AvaloniaFact]
    public async Task ImportedPlaylist_ShowsItsMissingSongsAsRows_AfterTheOwnedOnes()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var owned = new Track
        {
            Id = Guid.NewGuid(), Title = "Owned Song", Artist = "Synthetic Singer", Album = "Night", Duration = TimeSpan.FromMinutes(3),
            FilePath = TestPaths.Primary("missing-rows", $"{Guid.NewGuid():N}.mp3"),
        };
        var lib = new FakeLibraryService(); lib.TrackList.Add(owned);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist { Name = "Imported", TrackIds = [owned.Id] };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        var view = new PlaylistView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        try
        {
            window.Show();
            var until = Environment.TickCount64 + 5000;
            while (vm.Tracks.Count == 0 && Environment.TickCount64 < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            vm.CatalogRows.Add(PlaylistMissingHeader.Instance);
            vm.CatalogRows.Add(new ArtistCatalogSong(2, new SearchSongItem(new OnlineSong("spotify:a", "Missing One", "Synthetic Singer, Guest", "", 201, true)), "spotify:a"));
            vm.CatalogRows.Add(new ArtistCatalogSong(4, new SearchSongItem(new OnlineSong("spotify:b", "Missing Two", "Synthetic Singer", "", 187, false)), "spotify:b"));
            for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

            var items = view.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(4, items.Count);
            Assert.IsType<Track>(items[0].DataContext);
            Assert.IsType<PlaylistMissingHeader>(items[1].DataContext);
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Not in your library", texts);
            Assert.Contains("Synthetic Singer, Guest", texts);
            Assert.Equal("3 tracks", vm.TrackCountText); // the missing songs are this playlist's songs too
        }
        finally { window.Close(); vm.Dispose(); }
    }
}
