using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plus owner #201: retain the original desktop renderer and line clock. Upstream leads
/// into lines early and paints them with KaraokeWordText; Plus deliberately does neither.
/// Original upstream context: while the list glides ahead into the next word-timed line
/// (ScrollLineIndex), the focus ramp gives that line full opacity — but its words were
/// still drawn in the inactive style (unsung text at BaseOpacity 1), so the whole line
/// showed solid white until its first word activated it. The incoming line must show
/// its words unsung (Apple: ~40% white) and light them only as the sweep reaches them,
/// on the page, the side panel and the mini player.
/// </summary>
[Collection("MetadataServiceStatics")]
public class LyricsLeadLineViewTests
{
    private readonly ITestOutputHelper _o;
    public LyricsLeadLineViewTests(ITestOutputHelper o) => _o = o;

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

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    // Michael Jackson "Thriller" (owner's file): "door slam" ends 2:01.79, "cold hand"
    // starts 2:03.88. The list leads into "cold hand" from 2:03.451 (2:03.88 − 349 ms
    // glide lead − 80 ms word lookahead); it activates at 2:03.80.
    internal const string Thriller = """
        [01:54.12]<01:54.12>Ooh<01:54.72>
        [01:55.84]<01:55.84>You <01:56.14>hear <01:56.39>the <01:56.52>door <01:57.52>slam <01:58.52>and <01:58.66>realize <01:59.44>there's <01:59.69>nowhere <02:00.17>left <02:00.46>to <02:00.56>run<02:01.79>
        [02:03.88]<02:03.88>You <02:04.16>feel <02:04.49>the <02:04.60>cold <02:05.71>hand <02:06.63>and <02:06.74>wonder <02:07.24>if <02:07.52>you'll <02:07.76>ever <02:08.20>see <02:08.59>the <02:08.70>sun<02:09.62>
        [02:10.00]<02:10.00>You <02:10.32>close <02:10.58>your <02:10.70>eyes <02:12.50>and <02:12.84>hope <02:13.06>that <02:13.36>this <02:13.57>is <02:13.85>just <02:14.18>im<02:14.31>a<02:14.56>gin<02:14.79>at<02:15.03>ion<02:16.36>
        """;

    private static LyricsViewModel MakeLyrics(PlayerViewModel player, FakeLibraryService library)
    {
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(Thriller));
        // As LoadLyrics leaves it for a synced file (the lead's focus ramp checks it).
        typeof(LyricsViewModel).GetField("_hasSyncedLyrics",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, true);
        return vm;
    }

    private static void At(LyricsViewModel vm, double seconds) =>
        typeof(LyricsViewModel).GetMethod("UpdateActiveLine",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { TimeSpan.FromSeconds(seconds) });

    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(4);
        } while (sw.ElapsedMilliseconds < ms);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Walks the clock up to <paramref name="seconds"/> as playback would (every
    /// line in between activates and finishes), then holds there so transitions settle.</summary>
    private static void PlayTo(LyricsViewModel vm, double from, double seconds)
    {
        for (var s = from; s < seconds; s += 0.05) { At(vm, s); PumpFor(1); }
        At(vm, seconds);
        PumpFor(700);
    }

    private static LyricLine LineStarting(LyricsViewModel vm, string text) =>
        vm.LyricLines.First(l => l.Text.StartsWith(text, StringComparison.Ordinal));


    // Owner #201 explicitly rejects the upstream early-scroll/single-painter replacement.
    // Exercise the retained Plus view on all three surfaces, without dropping the checks
    // for seek, unsung progress, active focus or per-word geometry.
    private void CheckSurface(Control view, Window window, LyricsViewModel vm)
    {
        window.Show();
        PumpFor(100);
        var incoming = LineStarting(vm, "You feel");
        var current = LineStarting(vm, "You hear");
        At(vm, 123.5);
        PumpFor(100);
        Assert.Equal(vm.LyricLines.IndexOf(current), vm.ActiveLineIndex);
        Assert.Equal(vm.ActiveLineIndex, vm.ScrollLineIndex);
        Assert.False(incoming.IsActive);
        Assert.False(incoming.IsLeading);
        Assert.True(incoming.LineOpacity < 1);
        Assert.Equal(1, current.LineOpacity);
        Assert.Empty(view.GetVisualDescendants().OfType<KaraokeWordText>());
        var cells = view.GetVisualDescendants().OfType<MotionWordCell>().ToList();
        Assert.NotEmpty(cells);
        Assert.All(cells, c => Assert.NotNull(c.Word));
        var bases = view.GetVisualDescendants().OfType<TextBlock>()
            .Where(c => c.Classes.Contains("word-base") && c.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(bases);
        Assert.All(bases, c => Assert.True(c.Bounds.Width > 0));
        At(vm, 124.2);
        PumpFor(50);
        Assert.True(incoming.IsActive);
        Assert.True(incoming.Words![0].Progress >= 1);
        Assert.All(incoming.Words.Where(w => w.Text.Trim() is "cold" or "sun"), w => Assert.True(w.Progress <= 0));
        At(vm, 120);
        PumpFor(50);
        Assert.Equal(vm.LyricLines.IndexOf(current), vm.ActiveLineIndex);
        Assert.Equal(1, current.LineOpacity);
        Assert.Equal(0, current.BlurRadius);
        Assert.False(incoming.IsActive);
        _o.WriteLine("Plus original word cells, no early scroll, active focus and rewind verified.");
    }

    [AvaloniaFact]
    public void Page_KeepsPlusRendererAndClock()
    {
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        using var vm = MakeLyrics(player, lib);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        try { CheckSurface(view, win, vm); } finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SidePanel_KeepsPlusRendererAndClock()
    {
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        using var vm = MakeLyrics(player, lib);
        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 420, Height = 800, Content = view };
        try { CheckSurface(view, win, vm); } finally { win.Close(); }
    }

    [AvaloniaFact]
    public void MiniPlayer_KeepsPlusRendererAndClock()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        using var vm = MakeLyrics(player, lib);
        var settings = new SettingsViewModel(new TestPersistenceService(), lib, new NoOpPlayHistoryService());
        var mini = new MiniPlayerViewModel(player, vm, settings, lib);
        var win = new MiniPlayerWindow { DataContext = mini, Width = 340, Height = 432 };
        try
        {
            win.Show(); PumpFor(150);
            mini.ToggleLyricsFormCommand.Execute(null); PumpFor(700);
            CheckSurface(win, win, vm);
        }
        finally { win.Close(); }
    }
}
