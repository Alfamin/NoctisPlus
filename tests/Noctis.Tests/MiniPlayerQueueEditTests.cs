using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (vari), mini player: the Queue drawer's rows can be dragged to reorder and removed
/// (row menu, Delete) like the main Queue panel's, a row drag never moves the window, a card
/// held at the drawer's edge scrolls the list, and the drawer has Reshuffle while Shuffle is on.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerQueueEditTests
{
    private readonly ITestOutputHelper _out;
    public MiniPlayerQueueEditTests(ITestOutputHelper output) => _out = output;

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default) => Task.FromResult(new List<LrcLibResult>());
    }
    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
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
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }
    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SearchIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
    }

    private static async Task PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    private static Track T(string title) => new() { Title = title, Artist = "Artist " + title, FilePath = $@"C:\{title}.flac" };

    private static List<Track> Tracks(int n) => Enumerable.Range(0, n).Select(i => T($"Song {i}")).ToList();

    private sealed record Rig(MiniPlayerWindow Win, MiniPlayerViewModel Vm, PlayerViewModel Player, FakeAudioPlayer Audio)
    {
        public ItemsControl List => Win.FindControl<ItemsControl>("QueueList")!;
        public Border Sheet => Win.FindControl<Border>("DrawerSheet")!;
    }

    private static MiniPlayerViewModel CreateVm(out PlayerViewModel player, out FakeAudioPlayer audio)
    {
        EnsureAppResources();
        var library = new FakeLibraryService();
        audio = new FakeAudioPlayer();
        player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    private static Rig Open(IList<Track> queue, int ended, bool keepPlayed, string? design = null,
        double width = 340, double height = 432)
    {
        var vm = CreateVm(out var player, out var audio);
        player.KeepPlayedInQueue = keepPlayed;
        player.ReplaceQueueAndPlay(queue, 0);
        for (var i = 0; i < ended; i++)
        {
            audio.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
        }
        if (design != null) vm.SetDesignCommand.Execute(design);
        var win = new MiniPlayerWindow { DataContext = vm, Width = width, Height = height };
        win.Show();
        return new Rig(win, vm, player, audio);
    }

    private static async Task OpenQueue(Rig rig, double sheetHeight = 200)
    {
        rig.Vm.ToggleQueueDrawerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        rig.Sheet.Height = sheetHeight; // headless windows do not grow; the sheet's height comes from the resize
        rig.Win.UpdateLayout();
        await PumpFor(500);
        // The open slide lands on its own target height: set ours again once it has.
        rig.Sheet.Height = sheetHeight;
        rig.Win.UpdateLayout();
        await PumpFor(50);
    }

    /// <summary>The row Border of QueuePreview[index] (realized first).</summary>
    private static Border RowOf(Rig rig, int previewIndex)
    {
        rig.List.ScrollIntoView(previewIndex);
        rig.Win.UpdateLayout();
        var container = rig.List.ContainerFromIndex(previewIndex)!;
        return container.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("mini-queue-row"));
    }

    private static List<Track> UpNextRows(Rig rig) =>
        rig.Vm.QueuePreview.OfType<MiniQueueRow>().Where(r => !r.IsPlayed).Select(r => r.Track).ToList();

    private static MiniQueueRow PreviewRow(Rig rig, int index) => Assert.IsType<MiniQueueRow>(rig.Vm.QueuePreview[index]);

    // ── Remove ──

    [AvaloniaFact]
    public async Task RemoveFromQueue_UpNextRow_LeavesThePlayersQueue_ByItsOwnRow()
    {
        var x = T("X"); var a = T("A"); var b = T("B");
        var rig = Open(new List<Track> { x, a, b, a }, ended: 0, keepPlayed: false);
        try
        {
            await OpenQueue(rig);
            // The SECOND a (row 2): IndexOf would have removed the first.
            rig.Vm.RemoveFromQueueCommand.Execute(PreviewRow(rig, 2));
            Assert.Equal(new[] { a, b }, rig.Player.UpNext);
            Assert.Equal(new[] { a, b }, UpNextRows(rig)); // the drawer rebuilt at once
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task RemoveFromQueue_PlayedRow_LeavesThePlayedSongs()
    {
        var tracks = Tracks(6);
        var rig = Open(tracks, ended: 3, keepPlayed: true); // played 0 1 2 | 3 | 4 5
        try
        {
            await OpenQueue(rig);
            var row = PreviewRow(rig, 2); // [Played] s0 s1 …
            Assert.True(row.IsPlayed);
            Assert.Same(tracks[1], row.Track);

            rig.Vm.RemoveFromQueueCommand.Execute(row);

            Assert.Equal(new[] { tracks[0], tracks[2] }, rig.Player.PlayedTracks);
            Assert.Equal(new[] { tracks[4], tracks[5] }, rig.Player.UpNext);
            Assert.Same(tracks[3], rig.Player.CurrentTrack);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task DeleteKey_OnAQueueRow_RemovesIt_AndTheRowMenuOffersRemove()
    {
        var tracks = Tracks(5);
        var rig = Open(tracks, ended: 0, keepPlayed: false);
        try
        {
            await OpenQueue(rig);
            var row = RowOf(rig, 1); // Song 2
            Assert.True(row.Focusable);
            Assert.Equal(tracks[2].ToString(), ControlAutomationPeer.CreatePeerForElement(row).GetName());
            // A click (press + release, no drag) focuses the row; Delete then removes it.
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var at = new Point(row.Bounds.Width / 2, row.Bounds.Height / 2);
            row.RaiseEvent(new PointerPressedEventArgs(row, pointer, row, at, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            row.RaiseEvent(new PointerReleasedEventArgs(row, pointer, row, at, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Dispatcher.UIThread.RunJobs();
            Assert.True(row.IsFocused, "a clicked row takes focus");
            Assert.Equal(tracks.Skip(1), rig.Player.UpNext); // a click moves nothing
            row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete, Source = row });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { tracks[1], tracks[3], tracks[4] }, rig.Player.UpNext);

            // Right-click menu: one "Remove from Queue" item with the app's line icon.
            var menu = Assert.IsType<ContextMenu>(RowOf(rig, 0).ContextMenu);
            var item = Assert.Single(menu.Items.OfType<MenuItem>());
            Assert.Equal(Loc.T("MiniPlayer.RemoveFromQueue"), item.Header);
            item.DataContext = PreviewRow(rig, 0);
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { tracks[3], tracks[4] }, rig.Player.UpNext);
        }
        finally { rig.Win.Close(); }
    }

    // ── Reorder ──

    [AvaloniaFact]
    public async Task MoveQueueRow_ReordersThePlayersUpNext()
    {
        var tracks = Tracks(6); // 0 plays, 1..5 up next
        var rig = Open(tracks, ended: 0, keepPlayed: false);
        try
        {
            await OpenQueue(rig);
            Assert.True(rig.Vm.MoveQueueRow(PreviewRow(rig, 0), 3));
            Assert.Equal(new[] { tracks[2], tracks[3], tracks[4], tracks[1], tracks[5] }, rig.Player.UpNext);
            Assert.Equal(rig.Player.UpNext, UpNextRows(rig));
            Assert.False(rig.Vm.MoveQueueRow(PreviewRow(rig, 2), 2), "same slot: no move");
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraggingARow_ReordersTheQueue_AndNeverArmsTheWindowDrag(bool keepPlayed)
    {
        var tracks = Tracks(8);
        var rig = Open(tracks, ended: 2, keepPlayed: keepPlayed, height: 840); // played 0 1 | 2 | 3..7
        try
        {
            await OpenQueue(rig, sheetHeight: 480); // every row on screen: no edge scroll
            rig.Win.FindControl<ScrollViewer>("QueueScroller")!.Offset = default;
            rig.Win.UpdateLayout();
            var start = rig.Vm.QueueUpNextStart;
            var row = RowOf(rig, start); // Song 3, the first up next
            Assert.Same(tracks[3], ((MiniQueueRow)row.DataContext!).Track);
            Assert.False(rig.Win.ShouldBeginWindowDrag(row), "a queue row would also drag the window");

            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var at = new Point(row.Bounds.Width / 2, row.Bounds.Height / 2);
            var pitch = rig.List.ContainerFromIndex(start)!.Bounds.Height;
            var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            var held = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);

            row.RaiseEvent(new PointerPressedEventArgs(row, pointer, row, at, 0, pressed, KeyModifiers.None));
            Assert.False(rig.Win.IsDragArmed, "the press armed the window drag");
            for (var step = 1; step <= 8; step++)
            {
                row.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, row, pointer, row,
                    at + new Vector(0, pitch * 2 * step / 8.0), 0, held, KeyModifiers.None));
                await PumpFor(16);
            }
            Assert.True(rig.Win.FindControl<Border>("MiniQueueDragPreview")!.IsVisible, "the row lifted onto the card");
            row.RaiseEvent(new PointerReleasedEventArgs(row, pointer, row, at + new Vector(0, pitch * 2), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            await PumpFor(500);

            // Song 3 went down two slots; the played songs and the playing one did not move.
            Assert.Equal(new[] { tracks[4], tracks[5], tracks[3], tracks[6], tracks[7] }, rig.Player.UpNext);
            Assert.Equal(rig.Player.UpNext, UpNextRows(rig));
            Assert.Equal(new[] { tracks[0], tracks[1] }, rig.Player.PlayedTracks);
            Assert.Same(tracks[2], rig.Player.CurrentTrack);
            Assert.False(rig.Win.IsDragArmed);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task DraggingAnUpNextRowUp_StopsAtTheTopOfUpNext_NotAmongThePlayedSongs()
    {
        var tracks = Tracks(8);
        var rig = Open(tracks, ended: 2, keepPlayed: true, height: 840);
        try
        {
            await OpenQueue(rig, sheetHeight: 480);
            rig.Win.FindControl<ScrollViewer>("QueueScroller")!.Offset = default;
            rig.Win.UpdateLayout();
            var start = rig.Vm.QueueUpNextStart;
            var row = RowOf(rig, start + 2); // Song 5
            var pitch = rig.List.ContainerFromIndex(start + 2)!.Bounds.Height;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var at = new Point(row.Bounds.Width / 2, row.Bounds.Height / 2);
            var held = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
            row.RaiseEvent(new PointerPressedEventArgs(row, pointer, row, at, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            for (var step = 1; step <= 10; step++)
            {
                row.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, row, pointer, row,
                    at - new Vector(0, pitch * 6 * step / 10.0), 0, held, KeyModifiers.None));
                await PumpFor(16);
            }
            row.RaiseEvent(new PointerReleasedEventArgs(row, pointer, row, at - new Vector(0, pitch * 6), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            await PumpFor(500);

            Assert.Equal(new[] { tracks[5], tracks[3], tracks[4], tracks[6], tracks[7] }, rig.Player.UpNext);
            Assert.Equal(new[] { tracks[0], tracks[1] }, rig.Player.PlayedTracks);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task HoldingTheCardAtTheDrawersBottomEdge_ScrollsTheList_AndDropsPastTheRowsThatWereOnScreen()
    {
        // The drawer shows three or four rows: without the edge scroll a row could only move
        // among the ones already on screen.
        var tracks = Tracks(30);
        var rig = Open(tracks, ended: 0, keepPlayed: false);
        try
        {
            await OpenQueue(rig);
            var scroller = rig.Win.FindControl<ScrollViewer>("QueueScroller")!;
            Assert.Equal(0, scroller.Offset.Y);
            var lastOnScreen = rig.List.GetRealizedContainers().Max(c => rig.List.IndexFromContainer(c));
            var row = RowOf(rig, 0); // Song 1
            // Window coordinates, as real input has them: the row itself scrolls away.
            var win = rig.Win;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), win)!.Value;
            var edgeY = scroller.TranslatePoint(new Point(0, scroller.Bounds.Height - 6), win)!.Value.Y;
            var held = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
            row.RaiseEvent(new PointerPressedEventArgs(row, pointer, win, at, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            // Down to the bottom edge of the list, then hold there.
            for (var step = 1; step <= 6; step++)
            {
                // Raised on the scroller: the drag captures the pointer there.
                scroller.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, scroller, pointer, win,
                    new Point(at.X, at.Y + (edgeY - at.Y) * step / 6.0), 0, held, KeyModifiers.None));
                await PumpFor(16);
            }
            await PumpFor(700);
            Assert.True(scroller.Offset.Y > 40, $"offset {scroller.Offset.Y}: the list did not scroll");
            scroller.RaiseEvent(new PointerReleasedEventArgs(scroller, pointer, win, new Point(at.X, edgeY), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            await PumpFor(500);

            var landed = rig.Player.UpNext.IndexOf(tracks[1]);
            _out.WriteLine($"lastOnScreen={lastOnScreen} landed={landed} offset={scroller.Offset.Y}");
            Assert.True(landed > lastOnScreen, $"landed at {landed}, the first screen ended at {lastOnScreen}");
            Assert.Equal(29, rig.Player.UpNext.Count);
            Assert.Equal(tracks.Skip(1).Select(t => t.Id).Order(), rig.Player.UpNext.Select(t => t.Id).Order());
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task EdgeScrollDrag_OfASongQueuedTwice_MovesTheCopyThatWasDragged()
    {
        // Up Next: A, B, A, … — the drag lifts the SECOND A. The edge scroll recycles its row's
        // container, and the source was then looked up by song, which found the first A.
        var a = T("A"); var b = T("B");
        var queue = new List<Track> { T("Now"), a, b, a };
        queue.AddRange(Tracks(27));
        var rig = Open(queue, ended: 0, keepPlayed: false);
        try
        {
            await OpenQueue(rig);
            var scroller = rig.Win.FindControl<ScrollViewer>("QueueScroller")!;
            Assert.Equal(0, scroller.Offset.Y);
            var lastOnScreen = rig.List.GetRealizedContainers().Max(c => rig.List.IndexFromContainer(c));
            var row = RowOf(rig, 2); // the second A
            Assert.Same(a, ((MiniQueueRow)row.DataContext!).Track);
            Assert.Equal(2, ((MiniQueueRow)row.DataContext!).Index);
            var win = rig.Win;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), win)!.Value;
            var edgeY = scroller.TranslatePoint(new Point(0, scroller.Bounds.Height - 6), win)!.Value.Y;
            var held = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
            row.RaiseEvent(new PointerPressedEventArgs(row, pointer, win, at, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            for (var step = 1; step <= 6; step++)
            {
                scroller.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, scroller, pointer, win,
                    new Point(at.X, at.Y + (edgeY - at.Y) * step / 6.0), 0, held, KeyModifiers.None));
                await PumpFor(16);
            }
            await PumpFor(700);
            Assert.True(scroller.Offset.Y > 40, $"offset {scroller.Offset.Y}: the list did not scroll");
            scroller.RaiseEvent(new PointerReleasedEventArgs(scroller, pointer, win, new Point(at.X, edgeY), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            await PumpFor(500);

            var upNext = rig.Player.UpNext.ToList();
            _out.WriteLine(string.Join(", ", upNext.Take(8).Select(t => t.Title)) + $" … lastA={upNext.LastIndexOf(a)}");
            Assert.Equal(30, upNext.Count);
            // The first A and B stayed where they were; the dragged A went down past the first screen.
            Assert.Same(a, upNext[0]);
            Assert.Same(b, upNext[1]);
            Assert.True(upNext.LastIndexOf(a) > lastOnScreen, $"the dragged A landed at {upNext.LastIndexOf(a)}");
        }
        finally { rig.Win.Close(); }
    }

    // ── Reshuffle ──

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueDrawer_Reshuffle_ShowsWithShuffleOn_AndReshufflesUpNext(bool keepPlayed)
    {
        AccessibleNames.Install();
        var tracks = Tracks(12);
        var rig = Open(tracks, ended: 1, keepPlayed: keepPlayed);
        try
        {
            await OpenQueue(rig);
            Button? Visible() => rig.Sheet.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Command == rig.Player.ReshuffleCommand && b.IsEffectivelyVisible);
            Assert.Null(Visible());

            rig.Player.ToggleShuffleCommand.Execute(null);
            await PumpFor(200);
            var button = Visible();
            Assert.NotNull(button);
            Assert.Equal(Loc.T("MiniPlayer.Reshuffle"), ControlAutomationPeer.CreatePeerForElement(button!).GetName());

            var before = rig.Player.UpNext.ToList();
            button!.Command!.Execute(null);
            await PumpFor(100);
            Assert.NotEqual(before, rig.Player.UpNext);
            Assert.Equal(before.Count, rig.Player.UpNext.Count);
            Assert.Equal(rig.Player.UpNext, UpNextRows(rig));
        }
        finally { rig.Win.Close(); }
    }

    /// <summary>
    /// Real-Skia renders for eyeballing: the Queue drawer with Reshuffle, and a row mid-drag.
    /// Explicit only: runs when NOCTIS_TEST_SKIA=1 and NOCTIS_QUEUE_MINI_SHOTS_DIR names the folder.
    /// </summary>
    [AvaloniaFact]
    public async Task Shots_QueueDrawer_ReshuffleAndDrag()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_QUEUE_MINI_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        // The app's styles carry the accent brushes the drag card paints with.
        AddToPlaylistDialogTests.EnsureAppStyles();
        var names = new[] { "Midnight City", "Dreams", "Blue Monday", "Heroes", "Teardrop", "Nightcall", "Strobe", "Intro" };
        List<Track> Named() => names.Select(n => new Track { Title = n, Artist = "Various", FilePath = $@"C:\{n}.flac" }).ToList();

        foreach (var design in new[] { "Classic", "Pill" })
        {
            var rig = Open(Named(), ended: 0, keepPlayed: false, design: design == "Classic" ? null : design);
            try
            {
                rig.Win.Background = new SolidColorBrush(Color.Parse("#3A3F47"));
                rig.Player.ToggleShuffleCommand.Execute(null);
                await PumpFor(400);
                await OpenQueue(rig);
                rig.Win.Height = rig.Win.Bounds.Height + 200;
                await PumpFor(500);
                var path = Path.Combine(dir, $"queue-drawer-{design.ToLowerInvariant()}-reshuffle.png");
                rig.Win.CaptureRenderedFrame()!.Save(path);
                _out.WriteLine(path);

                // Mid-drag: the first row lifted a row and a half down, clear of the edge scroll.
                var row = RowOf(rig, 0);
                var scroller = rig.Win.FindControl<ScrollViewer>("QueueScroller")!;
                var win = rig.Win;
                var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), win)!.Value;
                var held = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
                row.RaiseEvent(new PointerPressedEventArgs(row, pointer, win, at, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
                for (var step = 1; step <= 6; step++)
                {
                    scroller.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, scroller, pointer, win,
                        at + new Vector(0, row.Bounds.Height * 1.5 * step / 6.0), 0, held, KeyModifiers.None));
                    await PumpFor(30);
                }
                await PumpFor(300);
                path = Path.Combine(dir, $"queue-drawer-{design.ToLowerInvariant()}-drag.png");
                rig.Win.CaptureRenderedFrame()!.Save(path);
                _out.WriteLine(path);
                scroller.RaiseEvent(new PointerReleasedEventArgs(scroller, pointer, win, at, 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                await PumpFor(400);
            }
            finally { rig.Win.Close(); }
        }
    }
}
