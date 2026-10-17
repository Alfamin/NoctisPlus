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

public class DownloadsPageTests
{
    private static Track Song(string name = "Song", string artist = "Artist", int seconds = 180) => new()
    {
        Title = name, Artist = artist, Album = "Album", Duration = TimeSpan.FromSeconds(seconds),
        FilePath = Path.Combine(Path.GetTempPath(), "noctis-download-fixture", Guid.NewGuid() + ".flac"),
    };
    private static DownloadEntry Saved(Track track, string? path = null) => new(new object(), track.Title, track.Artist,
        OnlineDownloadState.Saved, 1, "Downloaded", false, false, path, track.Duration.TotalSeconds);

    [AvaloniaFact]
    public void ScanMakesSavedRowActionableInPlace_AndRemovalNeverRemovesLibrarySong()
    {
        var library = new FakeLibraryService(); var song = Song(); var queue = new Queue(Saved(song, song.FilePath));
        using var vm = new DownloadsViewModel(library); vm.Attach(queue);
        var row = Assert.Single(vm.Finished);
        Assert.True(row.IsAddingToLibrary);
        Assert.Equal("Downloaded · not in library yet", row.DisplayDetail);
        library.TrackList.Add(song); library.RaiseLibraryUpdated(); Dispatcher.UIThread.RunJobs();
        Assert.Same(row, Assert.Single(vm.Finished)); Assert.Same(song, row.Local); Assert.True(row.IsActionable);
        vm.RemoveCommand.Execute(row); Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Finished); Assert.Same(song, Assert.Single(library.Tracks));
        Assert.Equal(0, library.SaveCount);
    }

    [AvaloniaFact]
    public void ExactPathWinsOverDuplicateMetadata_AndUnknownPathWaitsForItsOwnScan()
    {
        var a = Song(); var b = Song(); var library = new FakeLibraryService(); library.TrackList.AddRange([a, b]);
        var queue = new Queue(Saved(b, b.FilePath.ToUpperInvariant()), Saved(a, Song().FilePath), Saved(a, "relative.flac"));
        using var vm = new DownloadsViewModel(library); vm.Attach(queue);
        Assert.Same(b, vm.Finished[0].Local); Assert.Null(vm.Finished[1].Local);
        Assert.Null(vm.Finished[2].Local); // a malformed path is not permission to guess by metadata
    }

    [AvaloniaFact]
    public void LegacyHistoryUsesOnlyUnambiguousRecording_WithVersionAndDurationChecks()
    {
        var song = Song(); var wrongArtist = Song(artist: "Namesake"); var remix = Song("Song (Remix)");
        var longVersion = Song(seconds: 360); var library = new FakeLibraryService();
        library.TrackList.AddRange([wrongArtist, remix, longVersion, song]);
        using var vm = new DownloadsViewModel(library); vm.Attach(new Queue(Saved(song)));
        Assert.Same(song, Assert.Single(vm.Finished).Local);
        library.TrackList.Add(Song()); library.RaiseLibraryUpdated(); Dispatcher.UIThread.RunJobs();
        Assert.Null(Assert.Single(vm.Finished).Local); // two candidates: never guess
    }

    [AvaloniaFact]
    public void LibraryReplacesTrackInstance_AndDeletedTrackStopsBeingActionable()
    {
        var song = Song(); var library = new FakeLibraryService(); library.TrackList.Add(song);
        using var vm = new DownloadsViewModel(library); vm.Attach(new Queue(Saved(song, song.FilePath)));
        var replacement = Song(); replacement.FilePath = song.FilePath;
        library.TrackList[0] = replacement; library.RaiseLibraryUpdated(); Dispatcher.UIThread.RunJobs();
        Assert.Same(replacement, Assert.Single(vm.Finished).Local);
        library.TrackList.Clear(); library.RaiseLibraryUpdated(); Dispatcher.UIThread.RunJobs();
        Assert.False(Assert.Single(vm.Finished).IsActionable);
    }

    [AvaloniaFact]
    public void ProgressKeepsRowsStable_AndQueuedSongsCannotPlay()
    {
        var song = Song(); var library = new FakeLibraryService(); library.TrackList.Add(song);
        var queued = Saved(song, song.FilePath) with { State = OnlineDownloadState.Downloading };
        var queue = new Queue(queued); using var vm = new DownloadsViewModel(library); vm.Attach(queue);
        var row = Assert.Single(vm.Pending); Assert.Null(row.Local);
        queue.Entries[0] = queued with { Progress = 0.7 }; queue.Raise(); Dispatcher.UIThread.RunJobs();
        Assert.Same(row, Assert.Single(vm.Pending)); Assert.Equal(0.7, row.Progress);
        queue.Entries[0] = queued with { State = OnlineDownloadState.Saved }; queue.Raise(); Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Pending); Assert.Same(row, Assert.Single(vm.Finished)); Assert.Same(song, row.Local);
    }

    [AvaloniaFact]
    public void NativeRowsHaveMenus_RemoveOnlyTheChosenHistoryEntry_AndKeepFile()
    {
        var root = Directory.CreateTempSubdirectory("Noctis-download-actions-").FullName;
        try
        {
            var song = Song(); song.FilePath = Path.Combine(root, "Song.flac"); File.WriteAllText(song.FilePath, "test marker");
            var library = new FakeLibraryService(); library.TrackList.Add(song); var persistence = new TestPersistenceService();
            var audio = new FakeAudioPlayer(); var player = new PlayerViewModel(audio, library, persistence, new FakeAnimatedCoverService());
            var sidebar = new SidebarViewModel(persistence, library);
            using var actions = new LibrarySongsViewModel(library, player, sidebar, persistence);
            Track? album = null; string? artist = null;
            using var vm = new DownloadsViewModel(library, player, actions, t => album = t, a => artist = a);
            var queue = new Queue(Saved(song, song.FilePath), Saved(song, song.FilePath)); vm.Attach(queue);
            var view = new DownloadsView { DataContext = vm }; var window = new Window { Width = 1100, Height = 650, Content = view };
            window.Show(); window.UpdateLayout();
            try
            {
                var row = vm.Finished[1]; var button = view.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == row && b.Classes.Contains("song-row"));
                Assert.True(view.OpenTrackMenu(button, PlacementMode.Pointer));
                var menu = Assert.IsType<ContextMenu>(button.ContextMenu);
                var remove = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Remove from this list"));
                Assert.NotNull(remove.Command); remove.Command.Execute(remove.CommandParameter); Dispatcher.UIThread.RunJobs();
                Assert.Single(vm.Finished); Assert.Same(queue.Entries[0].Handle, vm.Finished[0].Entry!.Handle);
                Assert.True(File.Exists(song.FilePath)); Assert.Single(library.Tracks);
                vm.ViewAlbumCommand.Execute(song); vm.ViewArtistCommand.Execute(song.Artist);
                Assert.Same(song, album); Assert.Equal(song.Artist, artist);
                vm.PlayTrackCommand.Execute(song); Assert.Contains(song.FilePath, audio.PlayedPaths);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void DisposalDiscardsPostedRefreshes_AndStartupWithoutEventCatchesUp()
    {
        var library = new FakeLibraryService(); var song = Song(); var queue = new Queue(Saved(song, song.FilePath));
        var vm = new DownloadsViewModel(library); vm.Attach(queue);
        library.TrackList.Add(song); vm.Refresh(); Assert.True(Assert.Single(vm.Finished).IsActionable);
        queue.Raise(); library.RaiseLibraryUpdated(); vm.Dispose(); Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Finished); Assert.Equal(0, queue.ListenerCount);
    }

    private sealed class Queue(params DownloadEntry[] entries) : IDownloadQueue
    {
        public List<DownloadEntry> Entries { get; } = [.. entries];
        private EventHandler? _changed;
        public event EventHandler? Changed { add => _changed += value; remove => _changed -= value; }
        public int ListenerCount => _changed?.GetInvocationList().Length ?? 0;
        public void Raise() => _changed?.Invoke(this, EventArgs.Empty);
        public IReadOnlyList<DownloadEntry> Snapshot() => Entries.ToArray();
        public bool IsPaused => false;
        public void Remove(DownloadEntry entry) { Entries.RemoveAll(e => ReferenceEquals(e.Handle, entry.Handle)); Raise(); }
        public void SetPaused(bool paused) { }
        public void RetryFailed() { }
        public void StartHeld() { }
        public void Clear() { Entries.Clear(); Raise(); }
        public void DownloadNext(DownloadEntry entry) { }
    }
}
