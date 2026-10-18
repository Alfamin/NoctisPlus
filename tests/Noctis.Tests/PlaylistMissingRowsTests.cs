using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Noctis.Views;
using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner #36: native owned/unowned rows share source order, including duplicates and unavailable metadata.
/// </summary>
public class PlaylistMissingRowsTests
{
    [AvaloniaFact]
    public async Task ImportedPlaylist_InterleavesMissingAndOwned_AndUpgradesInPlace()
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
            CatalogSong missing = new("spotify:a", "Missing One", [new("", "Synthetic Singer"), new("", "Guest")], 201, true);
            ImportedPlaylistEntry[] entries = [new(1, missing, null, ""), new(2, null, owned.Id, ""), new(3, missing, null, ""), new(4, null, null, "Unavailable")];
            vm.ShowImportedEntries(entries);
            for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

            var items = view.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(4, items.Count);
            Assert.Equal(1, Assert.IsType<ArtistCatalogSong>(items[0].DataContext).Rank);
            Assert.Same(owned, items[1].DataContext);
            Assert.Equal(3, Assert.IsType<ArtistCatalogSong>(items[2].DataContext).Rank);
            Assert.IsType<PlaylistUnavailableRow>(items[3].DataContext);
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.DoesNotContain("Not in your library", texts);
            Assert.Contains("Unavailable track", texts);
            Assert.Contains("Synthetic Singer, Guest", texts);
            Assert.Equal("4 tracks", vm.TrackCountText);
            Assert.False(vm.ShowEmptyManual);
            Assert.False(vm.CanReorder); // A linked partial import cannot reorder only its local subset.

            var landed = new Track { Id = Guid.NewGuid(), Title = missing.Title, Artist = "Synthetic Singer", Duration = TimeSpan.FromSeconds(201) };
            vm.Tracks.ReplaceAll([landed, owned, landed]);
            vm.ShowImportedEntries([entries[0] with { LocalId = landed.Id }, entries[1], entries[2] with { LocalId = landed.Id }, entries[3]]);
            for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Assert.Equal(new object[] { landed, owned, landed }, vm.Rows.Take(3));
            Assert.Equal("4 tracks", vm.TrackCountText);
            Assert.Empty(vm.CatalogRows.OfType<ArtistCatalogSong>());
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task EntirelyUnownedPlaylist_IsNotEmpty_AndSearchIncludesMissingTitles()
    {
        var lib = new FakeLibraryService();
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        using var vm = new PlaylistViewModel(new Playlist { Name = "Empty local library" }, player, lib, persistence, sidebar);
        await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        vm.ShowImportedEntries([new(1, new("spotify:a", "One", [new("", "Singer")], 180, null), null, ""),
            new(2, new("spotify:b", "Two", [new("", "Guest")], null, null), null, "")]);
        Assert.Equal(2, vm.Rows.Count);
        Assert.False(vm.ShowEmptyManual);
        vm.ApplyFilter("Two");
        await WaitFor(() => vm.Rows.Count == 1);
        Assert.Equal("Two", Assert.IsType<ArtistCatalogSong>(vm.Rows[0]).Song.Title);
        vm.ApplyFilter("no matching song");
        await WaitFor(() => vm.ShowNoResults);
        Assert.Empty(vm.Rows);
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        var until = Environment.TickCount64 + 5000;
        while (!ready() && Environment.TickCount64 < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        Assert.True(ready());
    }
}
