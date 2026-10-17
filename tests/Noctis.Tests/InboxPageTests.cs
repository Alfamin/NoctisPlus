using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Inbox for new music, app side: the Settings switch (off moves everything waiting into the
/// library), the sidebar entry and its count, and the Inbox page (it lists the waiting songs,
/// plays them, and the player keeps an Inbox song when the library republishes).
/// </summary>
public class InboxPageTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Track Song(string title, bool inbox, int trackNumber = 1, DateTime? added = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Tester",
        AlbumArtist = "Tester",
        Album = inbox ? "Downloads" : "Library Album",
        AlbumId = Track.ComputeAlbumId("Tester", inbox ? "Downloads" : "Library Album"),
        TrackNumber = trackNumber,
        FilePath = Path.Combine("C:", "Music", title + ".flac"),
        Duration = TimeSpan.FromMinutes(3),
        DateAdded = added ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        IsInInbox = inbox,
    };

    /// <summary>A loaded library holding two library songs and two Inbox songs.</summary>
    private async Task<(LibraryService Library, PersistenceService Persistence)> LibraryAsync(bool inboxOn = true)
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { InboxEnabled = inboxOn });
        await persistence.SaveLibraryAsync(new List<Track>
        {
            Song("Known A", inbox: false), Song("Known B", inbox: false, 2),
            Song("New 1", inbox: true), Song("New 2", inbox: true, 2),
        });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new AuditTrailService(persistence));
        await library.LoadAsync();
        await library.BackgroundInit;
        return (library, persistence);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Assert.True(done());
    }

    [AvaloniaFact]
    public async Task TurningTheSwitchOff_MovesEverythingWaitingIntoTheLibrary()
    {
        var (library, persistence) = await LibraryAsync();
        Assert.Equal(2, library.InboxTracks.Count);
        var vm = new SettingsViewModel(persistence, library, new NoOpPlayHistoryService());
        await vm.LoadAsync();
        Assert.True(vm.InboxEnabled);

        vm.InboxEnabled = false;

        await WaitUntil(() => library.InboxTracks.Count == 0);
        Assert.Equal(4, library.Tracks.Count);
        Assert.False((await persistence.LoadSettingsAsync()).InboxEnabled);
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "library.json"));
        Assert.DoesNotContain("\"isInInbox\":true", json);
    }

    [AvaloniaFact]
    public async Task TurningTheSwitchOn_SavesIt_AndReleasesNothing()
    {
        var (library, persistence) = await LibraryAsync(inboxOn: false);
        // Loaded with the switch off: the marks were already released.
        Assert.Empty(library.InboxTracks);
        var vm = new SettingsViewModel(persistence, library, new NoOpPlayHistoryService());
        await vm.LoadAsync();

        vm.InboxEnabled = true;

        var saved = false;
        for (var i = 0; i < 200 && !saved; i++)
        {
            await Task.Delay(25);
            saved = (await persistence.LoadSettingsAsync()).InboxEnabled;
        }
        Assert.True(saved);
        Assert.Empty(library.InboxTracks);
        Assert.Equal(4, library.Tracks.Count);
    }

    [AvaloniaFact]
    public async Task SidebarEntry_ShowsWhileOnOrSongsWait_WithTheirCount()
    {
        var (library, _) = await LibraryAsync();
        var sidebar = new SidebarViewModel(new TestPersistenceService(), library);

        sidebar.SetInboxEnabled(false);
        var item = Assert.Single(sidebar.NavItems, i => i.Key == "inbox");
        Assert.Equal(2, item.Count);
        Assert.True(item.HasCount);
        Assert.True(item.ShowBadge);
        // Under Folders, above Playlists.
        var keys = sidebar.NavItems.Select(i => i.Key).ToList();
        Assert.Equal(keys.IndexOf("folders") + 1, keys.IndexOf("inbox"));

        await library.AcceptInboxTracksAsync(library.InboxTracks.Select(t => t.Id).ToList());
        sidebar.RefreshInbox();
        Assert.DoesNotContain(sidebar.NavItems, i => i.Key == "inbox");
        Assert.False(sidebar.IsInboxSectionVisible);

        sidebar.SetInboxEnabled(true);
        item = Assert.Single(sidebar.NavItems, i => i.Key == "inbox");
        Assert.Equal(0, item.Count);
        Assert.False(item.ShowBadge);
        Assert.Equal("Nav.Inbox", SidebarViewModel.LabelKey("inbox"));
    }

    [AvaloniaFact]
    public async Task Page_ListsTheWaitingSongs_AndPlaysThemThroughTheQueue()
    {
        var (library, _) = await LibraryAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(new TestPersistenceService(), library);
        var vm = new InboxViewModel(library, player, sidebar);

        vm.Refresh();
        Assert.Equal(new[] { "New 1", "New 2" }, vm.Tracks.Select(t => t.Title));
        Assert.Equal(2, vm.WaitingCount);
        Assert.False(vm.IsEmpty);
        Assert.StartsWith("2 songs", vm.SummaryText);

        vm.PlayTrackCommand.Execute(vm.Tracks[1]);
        Assert.Equal("New 2", player.CurrentTrack?.Title);

        // Adding the other one republishes the library; the playing Inbox song must not be
        // taken for a deleted one (the player drops tracks GetTrackById cannot find).
        vm.IsActive = true;
        await vm.AcceptAsync(new[] { vm.Tracks[0] });
        await WaitUntil(() => vm.Tracks.Count == 1);
        Assert.Equal("New 2", player.CurrentTrack?.Title);
        Assert.Equal(1, vm.WaitingCount);
        Assert.Equal(3, library.Tracks.Count);
    }

    [AvaloniaFact]
    public async Task Page_SearchFilters_ButIsNotEmpty()
    {
        var (library, _) = await LibraryAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new InboxViewModel(library, player, new SidebarViewModel(new TestPersistenceService(), library));

        vm.ApplyFilter("New 2");

        Assert.Equal("New 2", Assert.Single(vm.Tracks).Title);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.IsSearchCleared);
        vm.ApplyFilter(string.Empty);
        Assert.Equal(2, vm.Tracks.Count);
    }

    [Fact]
    public void Order_NewestArrivalsFirst_ThenAlbumOrder()
    {
        var older = Song("Old", inbox: true, 1, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = Song("Two", inbox: true, 2, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
        var first = Song("One", inbox: true, 1, new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));

        var ordered = InboxViewModel.Order(new[] { older, second, first });

        Assert.Equal(new[] { "One", "Two", "Old" }, ordered.Select(t => t.Title));
    }
}
