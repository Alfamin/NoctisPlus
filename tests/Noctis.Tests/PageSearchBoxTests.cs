using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using FreeMusicFinder;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

public class PageSearchBoxTests
{
    private readonly ITestOutputHelper _output;
    public PageSearchBoxTests(ITestOutputHelper output) => _output = output;

    private static async Task WaitFor(Func<bool> ready)
    {
        var until = Environment.TickCount64 + 5000;
        while (!ready() && Environment.TickCount64 < until)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
        Assert.True(ready());
    }

    private static Window Show(Control view)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static TextBox Input(Control view) => view.GetVisualDescendants().OfType<PageSearchBox>()
        .Single().FindControl<TextBox>("Input")!;

    private static void Type(Control view, string text) => Input(view).SetCurrentValue(TextBox.TextProperty, text);

    private static void Clear(Control view) => view.GetVisualDescendants().OfType<PageSearchBox>().Single()
        .FindControl<Button>("ClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task LibrarySizedSearch_FiltersWithoutWaitingForTheNetwork()
    {
        using var data = new TestPersistenceService();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 2134; i++)
            lib.TrackList.Add(new Track { Title = $"Track {i:D4}", Artist = "Synthetic Singer", Album = "Search measurement" });
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, data, new FakeAnimatedCoverService());
        using var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(data, lib), data);
        var view = new LibrarySongsView { DataContext = vm };
        var window = Show(view);
        try
        {
            vm.IsActive = true;
            await WaitFor(() => vm.FilteredTracks.Count == 2134);
            foreach (var query in new[] { "Track 2133", "Track 1000", "Track 0200" })
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Type(view, query);
                await WaitFor(() => vm.FilteredTracks.Count == 1 && vm.FilteredTracks[0].Title == query);
                _output.WriteLine($"LOCAL_SEARCH_RESPONSE: 2134 tracks, {clock.Elapsed.TotalMilliseconds:0.0} ms (10 ms observation interval)");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LocalEdits_ApplyOnTheNextDispatch_WithoutATypingTimer()
    {
        var page = new LocalPage();
        var box = new PageSearchBox { DataContext = page };
        var window = Show(box);
        try
        {
            var input = box.FindControl<TextBox>("Input")!;
            input.SetCurrentValue(TextBox.TextProperty, "s");
            input.SetCurrentValue(TextBox.TextProperty, "song");
            // Drain queued input/filter work without waiting for wall-clock time.
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["song"], page.Applied);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Songs_SearchesOnlyThisLibrary_AndClearRestoresTheList()
    {
        using var data = new TestPersistenceService();
        var lib = new FakeLibraryService();
        var wanted = new Track { Title = "Halo", Artist = "Beyoncé", Album = "I Am", FilePath = TestPaths.Primary("search", "halo.mp3") };
        lib.TrackList.AddRange([wanted, new Track { Title = "First Light", Artist = "Synthetic Singer", FilePath = TestPaths.Primary("search", "light.mp3") }]);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, data, new FakeAnimatedCoverService());
        using var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(data, lib), data);
        var view = new LibrarySongsView { DataContext = vm };
        var window = Show(view);
        try
        {
            vm.IsActive = true;
            await WaitFor(() => vm.FilteredTracks.Count == 2);
            Type(view, "beyonce");
            await WaitFor(() => vm.FilteredTracks.Count == 1);
            Assert.Same(wanted, vm.FilteredTracks[0]);
            Assert.Equal("beyonce", vm.SearchText);
            Clear(view);
            await WaitFor(() => vm.FilteredTracks.Count == 2);
            Assert.Equal("", vm.SearchText);
            // A programmatic navigation reset still updates the input after using Clear.
            vm.ApplyFilter("light");
            await WaitFor(() => Input(view).Text == "light" && vm.FilteredTracks.Count == 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Playlist_SearchKeepsMissingDuplicatesAndSourceOrder()
    {
        using var data = new TestPersistenceService();
        var lib = new FakeLibraryService();
        var owned = new Track { Title = "Owned", Artist = "Singer" }; lib.TrackList.Add(owned);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, data, new FakeAnimatedCoverService());
        using var vm = new PlaylistViewModel(new Playlist { Name = "Mixed", TrackIds = [owned.Id] }, player, lib, data, new SidebarViewModel(data, lib));
        var view = new PlaylistView { DataContext = vm };
        var window = Show(view);
        try
        {
            await WaitFor(() => vm.Tracks.Count == 1);
            var song = new CatalogSong("spotify:missing", "Missing Night", [new("", "Guest")], 180, true);
            vm.ShowImportedEntries([new(1, song, null, ""), new(2, null, owned.Id, ""), new(3, song, null, "")]);
            Type(view, "missing");
            await WaitFor(() => vm.Rows.Count == 2 && vm.SearchText == "missing");
            Assert.Equal([1, 3], vm.Rows.Cast<ArtistCatalogSong>().Select(row => row.Rank));
            Clear(view);
            await WaitFor(() => vm.Rows.Count == 3);
            Assert.Same(owned, vm.Rows[1]);
            Type(view, "not in this playlist");
            await WaitFor(() => vm.ShowNoResults);
            Assert.Empty(vm.Rows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Favorites_SearchDoesNotIncludeUnlikedLibrarySongs()
    {
        using var data = new TestPersistenceService();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange([new Track { Title = "Liked Night", IsFavorite = true },
            new Track { Title = "Liked Morning", IsFavorite = true }, new Track { Title = "Unliked Night" }]);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, data, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(data, lib, new History());
        using var vm = new FavoritesViewModel(player, lib, data, new SidebarViewModel(data, lib), settings);
        var view = new FavoritesView { DataContext = vm };
        var window = Show(view);
        try
        {
            vm.Refresh();
            Type(view, "night");
            await WaitFor(() => vm.SearchText == "night" && vm.FavoriteItems.Count == 1);
            Assert.Equal("Liked Night", vm.FavoriteItems[0].Title);
            Clear(view);
            await WaitFor(() => vm.FavoriteItems.Count == 2);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PendingEdit_CannotFilterTheNextPage_AndLatestEditWins()
    {
        var first = new LocalPage(); var next = new LocalPage();
        var box = new PageSearchBox { DataContext = first };
        var window = Show(box);
        try
        {
            box.FindControl<TextBox>("Input")!.SetCurrentValue(TextBox.TextProperty, "old query");
            box.DataContext = next;
            await Task.Delay(200); Dispatcher.UIThread.RunJobs();
            Assert.Empty(first.Applied); Assert.Empty(next.Applied);
            box.FindControl<TextBox>("Input")!.SetCurrentValue(TextBox.TextProperty, "n");
            box.FindControl<TextBox>("Input")!.SetCurrentValue(TextBox.TextProperty, "new");
            await WaitFor(() => next.Applied.Count > 0);
            Assert.Equal(["new"], next.Applied);
        }
        finally { window.Close(); }
    }

    private sealed class LocalPage : ObservableObject, ILocalSearchable
    {
        private string _search = "";
        public string SearchText => _search;
        public List<string> Applied { get; } = [];
        public void ApplyFilter(string query) { Applied.Add(query); SetProperty(ref _search, query, nameof(SearchText)); }
    }

    private sealed class History : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => [];
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
