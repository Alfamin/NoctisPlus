using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #126: "Newest Albums First" under the Album Artist sort was lost when the
/// back button returned to the Albums grid. The back-restore path rebuilds the grid
/// synchronously (ApplyFilterImmediate) and dropped the toggle, so each artist's
/// albums went back to oldest first.
/// </summary>
public class AlbumSortBackNavigationTests
{
    private sealed class NoOpPlayHistoryService : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Album Alb(string name, int year) =>
        new() { Id = Guid.NewGuid(), Name = name, Artist = "Artist", Year = year, Tracks = new List<Track>() };

    private static LibraryAlbumsViewModel AlbumsVm(params Album[] libraryAlbums)
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(libraryAlbums);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService()));
    }

    private static List<string> GridOrder(LibraryAlbumsViewModel vm) =>
        vm.FilteredAlbumRows.OfType<AlbumRow>().SelectMany(r => r.Albums).Select(a => a.Name).ToList();

    [AvaloniaFact]
    public void BackToAlbums_KeepsNewestAlbumsFirst()
    {
        var vm = AlbumsVm(Alb("Old", 2001), Alb("New", 2020));
        vm.Refresh();
        for (var i = 0; i < 200 && vm.FilteredAlbumRows.Count == 0; i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        vm.SetAlbumSortCommand.Execute("albumartist");
        vm.SetAlbumSortCommand.Execute("newestfirst");
        Assert.True(vm.AlbumSortNewestFirst);

        // MainWindowViewModel's back-restore for the Albums grid.
        vm.ClearArtistFilter();
        vm.ApplyFilterImmediate(string.Empty);

        Assert.Equal(new[] { "New", "Old" }, GridOrder(vm));
    }
}
