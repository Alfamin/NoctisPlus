using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Track = Noctis.Models.Track;

namespace Noctis.Tests;

/// <summary>
/// A sweep of the app's surfaces for the "type name read aloud" class: a Button, row, tab or
/// menu item whose content is a control (not text) and that has no AutomationProperties.Name
/// reads its content's ToString() — UIA on the lyrics page (10-13) read the artist link as
/// "Noctis.Controls.MarqueeTextBlock" and another button as "Avalonia.Controls.StackPanel",
/// Edit Info read its sections as "Avalonia.Controls.ScrollViewer". Every surface below is
/// mounted with sample data and every element is asked for its automation name: none may be a
/// CLR type name, and no input with a visible label may be nameless.
/// </summary>
public class AccessibleNamesSweepTests
{
    public AccessibleNamesSweepTests() => AccessibleNames.Install();

    // ── the sweep ──

    private static HashSet<string>? _typeNames;

    /// <summary>Every loaded type's full name ("Noctis.Models.Track", "System.Object").</summary>
    private static bool IsTypeName(string name)
    {
        _typeNames ??= AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>().ToArray(); } })
            .Select(t => t.FullName).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return _typeNames.Contains(name);
    }

    private static bool IsTypeNameJunk(string? name)
        => !string.IsNullOrEmpty(name)
           && (name.StartsWith("Avalonia.", StringComparison.Ordinal)
               || name.StartsWith("Noctis.", StringComparison.Ordinal)
               || name.StartsWith("System.", StringComparison.Ordinal)
               || IsTypeName(name));

    private static string? NameOf(Control control)
        => ControlAutomationPeer.CreatePeerForElement(control).GetName();

    private static bool IsInput(Control c)
        => c is Button or MenuItem or TabItem or ListBoxItem or TreeViewItem or Slider or TextBox or ComboBox
            or LottieToggle;

    private static bool HiddenFromAutomation(Control c)
        => c.GetSelfAndLogicalAncestors().OfType<Control>().Concat(c.GetVisualAncestors().OfType<Control>())
            .Any(a => AutomationProperties.GetAccessibilityView(a) == AccessibilityView.Raw);

    private static string? TextOf(Control c) => c switch
    {
        TextBlock t when !string.IsNullOrWhiteSpace(t.Text) => t.Text,
        MarqueeTextBlock m when !string.IsNullOrWhiteSpace(m.Text) => m.Text,
        _ => null,
    };

    /// <summary>The text a sighted user reads as this input's label: its own text content,
    /// its tooltip, its watermark, or a text label just before it in its row.</summary>
    private static string? VisibleLabel(Control c)
    {
        if (ToolTip.GetTip(c) is string tip && !string.IsNullOrWhiteSpace(tip)) return "tip:" + tip;
        if (c is TextBox box && !string.IsNullOrWhiteSpace(box.Watermark)) return "watermark:" + box.Watermark;
        if (c is ContentControl or Avalonia.Controls.Primitives.HeaderedItemsControl or Avalonia.Controls.Primitives.HeaderedContentControl)
        {
            var own = c.GetLogicalDescendants().OfType<Control>().Select(TextOf).FirstOrDefault(t => t != null);
            if (own != null) return "text:" + own;
        }
        // A "Label  [control]" row: a text sibling before the control (or before the branch holding it).
        Control branch = c;
        for (var depth = 0; depth < 3 && branch.Parent is Panel panel; depth++, branch = panel)
        {
            foreach (var sibling in panel.Children)
            {
                if (sibling == branch) break;
                if (!sibling.IsVisible || sibling.GetSelfAndLogicalDescendants().OfType<Control>().Any(IsInput)) continue;
                var label = sibling.GetSelfAndLogicalDescendants().OfType<Control>().Select(TextOf).FirstOrDefault(t => t != null);
                if (label != null) return "label:" + label;
            }
        }
        return null;
    }

    private static string Describe(string surface, Control control, string? name, string? label = null)
    {
        var path = string.Join(" < ", control.GetLogicalAncestors().OfType<Control>()
            .Where(a => !string.IsNullOrEmpty(a.Name)).Take(2).Select(a => a.Name));
        var tp = control.TemplatedParent is { } parent ? $" tparent={parent.GetType().Name}" : "";
        return $"{surface}: {control.GetType().Name}#{control.Name} [{string.Join(' ', control.Classes)}]{tp} "
               + $"name='{name}'" + (label != null ? $" ({label})" : "") + $" in {path}";
    }

    /// <summary>Visual and logical descendants (the logical tree holds hidden flyout/popup content).</summary>
    private static IEnumerable<Control> All(Control root)
        => root.GetSelfAndVisualDescendants().OfType<Control>()
            .Concat(root.GetSelfAndLogicalDescendants().OfType<Control>())
            .Distinct();

    /// <summary>The menu items of a flyout or context menu, nested ones included.</summary>
    private static IEnumerable<Control> MenuTree(IEnumerable<object?> items)
    {
        foreach (var item in items.OfType<Control>())
        {
            foreach (var c in All(item)) yield return c;
            if (item is MenuItem mi)
                foreach (var c in MenuTree(mi.Items)) yield return c;
        }
    }

    private static List<string> Offenders(string surface, IEnumerable<Control> controls, bool includeHidden = false)
    {
        var found = new List<string>();
        foreach (var c in controls.Distinct())
        {
            // Not shown, not in the automation tree (a peer lists only visible children).
            if ((!includeHidden && !c.IsEffectivelyVisible) || HiddenFromAutomation(c)) continue;
            // A slider's own track halves and thumb (every Slider in the app, not its page's).
            if (c.TemplatedParent is Slider) continue;
            var name = NameOf(c);
            if (IsTypeNameJunk(name))
            {
                found.Add(Describe(surface, c, name));
                continue;
            }
            if (!IsInput(c) || !string.IsNullOrWhiteSpace(name)) continue;
            // A plain combo box's template keeps a hidden PART_EditableTextBox; never shown, never read.
            if (c.TemplatedParent is not null && !c.IsVisible) continue;
            if (VisibleLabel(c) is { } label)
                found.Add(Describe(surface, c, name, label));
        }
        return found;
    }

    private static void AssertNone(List<string> offenders)
        => Assert.True(offenders.Count == 0, $"{offenders.Count} offenders:\n" + string.Join('\n', offenders.Distinct()));

    // ── mounting ──

    private static void EnsureAppStyles()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });
    }

    private static Window Show(Control content, double width = 1400, double height = 900)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Pump(window);
        return window;
    }

    private static void Pump(TopLevel window)
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Track Song(string title, string artist = "Nine Inch Nails", string album = "Pretty Hate Machine",
        Guid? albumId = null, int plays = 0) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        AlbumId = albumId ?? Track.ComputeAlbumId(artist, album),
        FilePath = TestPaths.Primary("a11y-sweep", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(4),
        Genre = "Industrial",
        Year = 1989,
        PlayCount = plays,
        IsExplicit = true,
    };

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private const string SyncedLrc =
        "[00:01.00]Head like a hole\n" +
        "[00:03.50]Black as your soul\n" +
        "[00:06.00]I'd rather die\n" +
        "[00:09.00]Than give you control\n";

    /// <summary>A playing song with a sidecar .lrc, loaded into the lyrics view model.</summary>
    private static async Task<(LyricsViewModel Vm, PlayerViewModel Player)> SyncedLyrics(string artist = "Nine Inch Nails")
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-a11y-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "song.lrc"), SyncedLrc);
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), lib);
        player.Duration = TimeSpan.FromSeconds(10);
        var track = Song("Head Like A Hole", artist);
        track.FilePath = Path.Combine(dir, "song.mp3");
        player.CurrentTrack = track;
        vm.SetLyricsSurfaceVisible(true);
        vm.EnsureLyricsForCurrentTrack();
        await PumpUntil(() => vm.IsSynced && vm.LyricLines.Count > 0);
        Assert.True(vm.IsSynced, "the sidecar .lrc never loaded");
        return (vm, player);
    }

    // ── surfaces ──

    /// <summary>Why the sweep skips what isn't shown: a peer lists only its visible children,
    /// so a hidden control never reaches a screen reader (the states that show it are swept).</summary>
    [AvaloniaFact]
    public void HiddenControls_AreNotInTheAutomationTree()
    {
        var shown = new Button { Content = "Shown" };
        var hidden = new Button { Content = "Hidden", IsVisible = false };
        var panel = new StackPanel { Children = { shown, hidden } };
        var window = Show(panel);
        try
        {
            var names = ControlAutomationPeer.CreatePeerForElement(panel).GetChildren().Select(p => p.GetName()).ToList();
            Assert.Equal(new[] { "Shown" }, names);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LyricsPage_SyncedSong()
    {
        EnsureAppStyles();
        var found = new List<string>();
        foreach (var artist in new[] { "Nine Inch Nails", "Trent Reznor, Atticus Ross" })
        {
            var (vm, _) = await SyncedLyrics(artist);
            var view = new LyricsView { DataContext = vm };
            var window = Show(view, 1400, 900);
            try
            {
                await PumpUntil(() => view.GetVisualDescendants().OfType<Button>().Count() > 10, 1500);
                Pump(window);
                found.AddRange(Offenders($"Lyrics page ({artist})", All(view)));

                var lines = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("lyric-line-btn"))
                    .Select(NameOf).ToList();
                Assert.Contains("Black as your soul", lines);
                Assert.Equal("Pretty Hate Machine", NameOf(view.FindControl<Button>("AlbumLinkButton")!));
                if (!vm.HasMultipleArtists)
                    Assert.Equal(artist, NameOf(view.FindControl<Button>("ArtistLinkButton")!));
            }
            finally
            {
                window.Close();
            }
        }
        AssertNone(found);
    }

    [AvaloniaFact]
    public async Task LyricsSidePanel_SyncedSong()
    {
        EnsureAppStyles();
        var (vm, _) = await SyncedLyrics();
        var view = new LyricsPanelView { DataContext = vm };
        var window = Show(view, 420, 900);
        try
        {
            await PumpUntil(() => view.GetVisualDescendants().OfType<Button>().Count() > 3, 1500);
            Pump(window);
            AssertNone(Offenders("Lyrics panel", All(view)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PlaybackBar_AndItsMenus()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = Song("Head Like A Hole");
        player.Duration = TimeSpan.FromMinutes(4);
        var found = new List<string>();
        foreach (var compact in new[] { false, true })
        {
            var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = compact };
            var window = Show(bar, 1400, 200);
            try
            {
                var controls = All(bar).ToList();
                foreach (var b in controls.OfType<Button>().Where(b => b.Flyout is MenuFlyout).ToList())
                    controls.AddRange(MenuTree(((MenuFlyout)b.Flyout!).Items));
                found.AddRange(Offenders("Playback bar", controls));
            }
            finally
            {
                window.Close();
            }
        }
        AssertNone(found);
    }

    private static MiniPlayerViewModel MiniPlayerVm()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new FakeHistoryLog());
        player.CurrentTrack = Song("Head Like A Hole");
        player.Duration = TimeSpan.FromMinutes(4);
        player.UpNext.Add(Song("Terrible Lie"));
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    [AvaloniaFact]
    public async Task MiniPlayer_EveryDesign()
    {
        EnsureAppStyles();
        var found = new List<string>();
        foreach (var style in Enum.GetValues<MiniPlayerStyle>())
        {
            var vm = MiniPlayerVm();
            vm.SetDesignCommand.Execute(style.ToString());
            var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
            win.Show();
            await PumpUntil(() => false, 150);
            try
            {
                found.AddRange(Offenders($"Mini player ({style})", All(win)));
                foreach (var seek in win.GetVisualDescendants().OfType<Slider>().Where(x => x.Classes.Contains("mini-seek") && x.IsEffectivelyVisible))
                    Assert.Equal(Loc.T("PlaybackBar.Seek"), NameOf(seek));
            }
            finally
            {
                win.Close();
            }
        }
        AssertNone(found);
    }

    [AvaloniaFact]
    public async Task LibraryPages_SongsAlbumsArtistsPlaylistsFoldersFavorites()
    {
        EnsureAppStyles();
        var albumId = Guid.NewGuid();
        var tracks = new List<Track> { Song("Head Like A Hole", albumId: albumId, plays: 4), Song("Terrible Lie", albumId: albumId, plays: 2) };
        tracks[0].IsFavorite = true;
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var album = new Album
        {
            Id = albumId, Name = "Pretty Hate Machine", Artist = "Nine Inch Nails", Year = 1989,
            TrackCount = tracks.Count, Tracks = tracks,
        };
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        sidebar.Playlists.Add(new Playlist { Name = "Night Drive", TrackIds = tracks.Select(t => t.Id).ToList() });
        var settings = new SettingsViewModel(persistence, lib, new FakeHistoryLog());
        var found = new List<string>();

        async Task Sweep(string surface, Control view, Func<bool>? ready = null)
        {
            var window = Show(view);
            try
            {
                if (ready != null)
                {
                    await PumpUntil(ready, 2000);
                    Assert.True(ready(), $"{surface}: its rows never realized");
                }
                Pump(window);
                found.AddRange(Offenders(surface, All(view)));
            }
            finally
            {
                window.Close();
            }
        }

        var songs = new LibrarySongsViewModel(lib, player, sidebar, persistence);
        var songsView = new LibrarySongsView { DataContext = songs };
        songs.IsActive = true;
        await Sweep("Songs", songsView, () => songsView.GetVisualDescendants().OfType<ListBoxItem>().Any());

        var albums = new LibraryAlbumsViewModel(lib, player, sidebar, settings);
        albums.FilteredAlbumRows.Add(new AlbumRow { Albums = new List<Album> { album } });
        var albumsView = new LibraryAlbumsView { DataContext = albums };
        await Sweep("Albums", albumsView, () => albumsView.GetVisualDescendants().OfType<Button>().Count() > 5);

        var artists = new LibraryArtistsViewModel(lib);
        var row = new ArtistRow();
        row.Artists.Add(new Artist { Id = Guid.NewGuid(), Name = "Nine Inch Nails", IsFavorite = true });
        artists.ArtistRows.ReplaceAll(new[] { row });
        var artistsView = new LibraryArtistsView { DataContext = artists };
        string? tileName = null;
        await Sweep("Artists", artistsView, () =>
        {
            var tile = artistsView.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("artist-tile"));
            tileName = tile is null ? null : NameOf(tile);
            return tile != null;
        });
        Assert.Equal("Nine Inch Nails", tileName);

        var playlists = new LibraryPlaylistsViewModel(sidebar, player, lib, persistence);
        var playlistsView = new LibraryPlaylistsView { DataContext = playlists };
        await Sweep("Playlists", playlistsView, () => playlistsView.GetVisualDescendants().OfType<Button>().Count() > 3);

        var folders = new LibraryFoldersViewModel(lib, player, persistence, sidebar);
        await Sweep("Folders", new LibraryFoldersView { DataContext = folders });

        var favorites = new FavoritesViewModel(player, lib, persistence, sidebar, settings);
        await Sweep("Favorites", new FavoritesView { DataContext = favorites });

        AssertNone(found);
    }

    [AvaloniaFact]
    public void Statistics_BothTabs()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var a = Song("Head Like A Hole");
        var b = Song("Terrible Lie");
        lib.TrackList.AddRange(new[] { a, b });
        var now = DateTime.UtcNow;
        var events = Enumerable.Range(0, 20)
            .Select(i => StatisticsNumbersTests.Play((i % 2 == 0 ? a : b).Id, (i % 2 == 0 ? a : b).Title, "Nine Inch Nails", now.AddHours(-i)))
            .ToArray();
        var vm = new StatisticsViewModel(lib, new StatisticsNumbersTests.History(events));
        var view = new StatisticsView { DataContext = vm };
        var window = Show(view);
        var found = new List<string>();
        try
        {
            StatisticsNumbersTests.Refresh(vm);
            Pump(window);
            found.AddRange(Offenders("Stats (overview)", All(view)));
            vm.IsHistoryTabSelected = true;
            Pump(window);
            found.AddRange(Offenders("Stats (history)", All(view)));
        }
        finally
        {
            window.Close();
        }
        AssertNone(found);
    }

    [AvaloniaFact]
    public async Task Wrap()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var log = new FakeHistoryLog();
        var year = DateTime.Now.Year;
        for (var i = 0; i < 6; i++)
        {
            var t = Song($"Song {i}", $"Artist {i}", $"Album {i}");
            lib.TrackList.Add(t);
            for (var p = 0; p < 8 - i; p++)
                log.Seed(t, new DateTime(year, 1, 1 + p, 9 + i, 0, 0, DateTimeKind.Local).ToUniversalTime());
        }
        var archive = Path.Combine(Path.GetTempPath(), "noctis-a11y-wrap-" + Guid.NewGuid().ToString("N") + ".json");
        var vm = new WrapViewModel(log, lib, new WrapArchiveService(archive), _ => null);
        var win = new WrapDialog(vm) { Width = 1300, Height = 900 };
        win.Show();
        try
        {
            await PumpUntil(() => vm.HasData && !vm.IsLoading, 5000);
            Pump(win);
            AssertNone(Offenders("Wrap", All(win)));
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void LyricsStudio_ChooseSongs()
    {
        EnsureAppStyles();
        var root = Path.Combine(Path.GetTempPath(), "noctis-a11y-studio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracks = new[] { Song("Head Like A Hole"), Song("Terrible Lie") };
        var vm = new LyricsStudioViewModel(tracks, new LyricsStudioModelDownloadTests.ModelOnlyEngine(new WhisperModelManager(root)),
            new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = true };
        var window = Show(panel, 1200, 850);
        try
        {
            AssertNone(Offenders("Lyrics Studio", All(panel)));
            Assert.Contains("Terrible Lie, Nine Inch Nails", panel.GetVisualDescendants().OfType<ListBoxItem>().Select(NameOf));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LrcEditor_WithSyncedLines()
    {
        EnsureAppStyles();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LrcEditorViewModel(Song("Head Like A Hole"), player, new StubMetadata(), SyncedLrc, null);
        var dialog = new LrcEditorDialog(vm) { Width = 1100, Height = 820 };
        dialog.Show();
        try
        {
            Pump(dialog);
            AssertNone(Offenders("LRC editor", All(dialog)));
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void CommonDialogs()
    {
        EnsureAppStyles();
        var found = new List<string>();
        var dialogs = new (string Name, Window Dialog)[]
        {
            ("Confirmation", new ConfirmationDialog("Remove 3 songs from the library?")),
            ("Text prompt", new TextPromptDialog("Rename", "Night Drive", "Name", "Save")),
            ("Remove from library", new RemoveFromLibraryDialog(3)),
            ("Create playlist", new CreatePlaylistDialog { DataContext = new CreatePlaylistDialogViewModel() }),
            ("Add to playlist", new AddToPlaylistDialog { DataContext = new AddToPlaylistDialogViewModel(AddToPlaylistDialogTests.Lists(3), 2) }),
            ("Create smart playlist", new CreateSmartPlaylistDialog()),
            ("Edit playlist", new EditPlaylistDialog()),
            ("Badge name", new BadgeNameDialog()),
        };
        foreach (var (name, dialog) in dialogs)
        {
            dialog.Width = 1000;
            dialog.Height = 760;
            dialog.Show();
            try
            {
                Pump(dialog);
                found.AddRange(Offenders(name, All(dialog)));
            }
            finally
            {
                dialog.Close();
            }
        }
        AssertNone(found);
    }

    [AvaloniaFact]
    public void MainWindow_ShellSidebarAndTopBar()
    {
        EnsureAppStyles();
        var window = new MainWindow();
        try
        {
            // The island and the sidebar bind their view models (their names are bindings);
            // they are swept below and in PlaybackBar_AndItsMenus with one.
            // The queue's now-playing card (shown with a current track) binds that track.
            window.FindControl<StackPanel>("QueueNowPlaying")!.GetLogicalDescendants().OfType<Border>().First()
                .DataContext = Song("Down In It");
            var found = Offenders("Main window", All(window).Where(c => c.FindLogicalAncestorOfType<PlaybackBarView>() == null
                                                                      && c.FindLogicalAncestorOfType<SidebarView>() == null));
            var sidebarVm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService()) { TopBar = new TopBarViewModel() };
            sidebarVm.SidebarRows.Add(new PlaylistNavItem { Label = "Road Trip", PlaylistId = Guid.NewGuid() });
            var sidebar = new SidebarView { DataContext = sidebarVm };
            var shown = Show(sidebar, 300, 900);
            try { found.AddRange(Offenders("Sidebar", All(sidebar))); }
            finally { shown.Close(); }
            AssertNone(found);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DetailPages_AlbumArtistPlaylistQueueHome()
    {
        EnsureAppStyles();
        var albumId = Guid.NewGuid();
        var tracks = new List<Track> { Song("Head Like A Hole", albumId: albumId, plays: 12), Song("Terrible Lie", albumId: albumId, plays: 9) };
        var album = new Album
        {
            Id = albumId, Name = "Pretty Hate Machine", Artist = "Nine Inch Nails", Year = 1989,
            TrackCount = tracks.Count, TotalDuration = TimeSpan.FromMinutes(8), Tracks = tracks,
        };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(tracks[0]);
        player.UpNext.Add(tracks[1]);
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist { Name = "Night Drive", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(playlist);
        var found = new List<string>();

        async Task Sweep(string surface, Control view, Func<bool>? ready = null)
        {
            var window = Show(view);
            try
            {
                if (ready != null)
                {
                    await PumpUntil(ready, 2000);
                    Assert.True(ready(), $"{surface}: its rows never realized");
                }
                Pump(window);
                found.AddRange(Offenders(surface, All(view)));
            }
            finally
            {
                window.Close();
            }
        }

        var albumView = new AlbumDetailView { DataContext = new AlbumDetailViewModel(album, player, persistence, lib, sidebar, null!) };
        await Sweep("Album page", albumView, () => albumView.GetVisualDescendants().OfType<ListBoxItem>().Count() >= 2);

        var artistVm = new ArtistDetailViewModel("Nine Inch Nails", lib, player);
        var artistView = new ArtistDetailView { DataContext = artistVm };
        await Sweep("Artist page", artistView, () => artistView.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("song-row")) >= 2);

        var playlistVm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        var playlistView = new PlaylistView { DataContext = playlistVm };
        await Sweep("Playlist page", playlistView, () => playlistView.GetVisualDescendants().OfType<ListBoxItem>().Count() >= 2);

        var queueView = new QueueView { DataContext = new QueueViewModel(player) };
        await Sweep("Queue page", queueView, () => queueView.GetVisualDescendants().OfType<ListBoxItem>().Any());

        var home = new HomeViewModel(player, lib, sidebar);
        await home.RefreshAsync();
        var homeView = new HomeView { DataContext = home };
        await Sweep("Home", homeView, () => homeView.GetVisualDescendants().OfType<Button>().Count() > 5);

        AssertNone(found);
    }
}
