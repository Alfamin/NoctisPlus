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
/// Owner 10-18 (Thriller): while the list glides ahead into the next word-timed line
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

    private static List<KaraokeWordText> SweepWords(Visual root, LyricLine line) =>
        root.GetVisualDescendants().OfType<KaraokeWordText>()
            .Where(k => k.Classes.Contains("word-sweep") && line.Words!.Any(w => ReferenceEquals(w, k.DataContext)))
            .ToList();

    /// <summary>Unsung look: ghost base (Apple's ~41% white), no word lit.</summary>
    private static void AssertUnsung(LyricLine line, List<KaraokeWordText> words, string where)
    {
        Assert.Equal(line.Words!.Count, words.Count);
        AssertWordsUnsung(words, where);
    }

    private static void AssertWordsUnsung(IEnumerable<KaraokeWordText> words, string where)
    {
        foreach (var k in words)
        {
            Assert.True(k.BaseOpacity < 0.5, $"{where}: '{k.Text}' unsung base {k.BaseOpacity:F2} (1 = drawn fully white)");
            Assert.True(k.Progress <= 0, $"{where}: '{k.Text}' progress {k.Progress} (lit before its sweep)");
        }
    }

    private static void AssertLeadState(LyricsViewModel vm, LyricLine incoming)
    {
        var index = vm.LyricLines.IndexOf(incoming);
        Assert.Equal(index - 1, vm.ActiveLineIndex);   // highlight still on "door slam"
        Assert.Equal(index, vm.ScrollLineIndex);       // the list leads into "cold hand"
        Assert.Equal(1.0, incoming.LineOpacity);       // focus ramp moved with the lead
        Assert.False(incoming.IsActive);
    }

    [AvaloniaFact]
    public void Page_IncomingLine_WhileTheListLeads_ShowsItsWordsUnsung()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = MakeLyrics(player, library);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        try
        {
            win.Show();
            PumpFor(100);
            var incoming = LineStarting(vm, "You feel");
            PlayTo(vm, 115.0, 123.5);
            AssertLeadState(vm, incoming);
            AssertUnsung(incoming, SweepWords(view, incoming), "page lead 2:03.5");

            // Activated, first word sweeping: "cold" onwards still unsung.
            PlayTo(vm, 123.5, 124.2);
            Assert.True(incoming.IsActive);
            var words = SweepWords(view, incoming);
            var you = words.First(k => ReferenceEquals(k.DataContext, incoming.Words![0]));
            Assert.True(you.Progress >= 1, $"'You' should be swept at 2:04.2, progress {you.Progress}");
            var rest = words.Where(k => k.Text!.Trim() is "cold" or "sun").ToList();
            Assert.Equal(2, rest.Count);
            AssertWordsUnsung(rest, "page 2:04.2");
        }
        finally { win.Close(); }
    }

    /// <summary>A line sung earlier keeps its words fully swept after it finishes; a seek
    /// back into its lead must not bring it in fully lit.</summary>
    [AvaloniaFact]
    public void Page_SeekBackIntoTheLead_OfALineSungBefore_ShowsItUnsung()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = MakeLyrics(player, library);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        try
        {
            win.Show();
            PumpFor(100);
            var incoming = LineStarting(vm, "You feel");
            PlayTo(vm, 115.0, 131.0);                 // "cold hand" sung and finished
            Assert.False(incoming.IsActive);

            At(vm, 123.5);                            // seek back into its lead
            PumpFor(700);
            AssertLeadState(vm, incoming);
            AssertUnsung(incoming, SweepWords(view, incoming), "page seek-back lead");
        }
        finally { win.Close(); }
    }

    /// <summary>
    /// Real Skia only (NOCTIS_TEST_SKIA=1): measured word brightness on the page, as the
    /// fraction of white over the backdrop at the brightest glyph pixels. The incoming
    /// "cold hand" words stay at the unsung level (~0.4) through the lead and after the
    /// activation until swept; before the fix they measured fully white in the lead.
    /// </summary>
    [AvaloniaFact]
    public void Page_IncomingLine_RendersAtTheUnsungLevel_ThroughTheLead()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = MakeLyrics(player, library);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Background = new SolidColorBrush(Color.Parse("#22303C")), Content = view };
        try
        {
            win.Show();
            PumpFor(200);
            var incoming = LineStarting(vm, "You feel");
            double Bright(string text)
            {
                var k = SweepWords(view, incoming).First(w => w.Text!.Trim() == text);
                var o = k.TranslatePoint(new Point(0, 0), win)!.Value;
                using var bmp = win.CaptureRenderedFrame()!;
                using var ms = new MemoryStream();
                bmp.Save(ms);
                ms.Position = 0;
                using var sk = SkiaSharp.SKBitmap.Decode(ms);
                var bg = sk.GetPixel(4, sk.Height - 4).Red;
                var reds = new List<int>();
                for (var y = Math.Max(0, (int)o.Y); y < Math.Min(sk.Height, (int)(o.Y + k.Bounds.Height)); y++)
                    for (var x = Math.Max(0, (int)o.X); x < Math.Min(sk.Width, (int)(o.X + k.Bounds.Width)); x++)
                        reds.Add(sk.GetPixel(x, y).Red);
                reds.Sort();
                var peak = reds[(int)(reds.Count * 0.98)];
                return (peak - bg) / (255.0 - bg);
            }

            var from = 115.0;
            var rows = new List<string>();
            foreach (var t in new[] { 122.5, 123.5, 123.85, 124.2 })
            {
                PlayTo(vm, from, t);
                from = t;
                var (cold, sun, you) = (Bright("cold"), Bright("sun"), Bright("You"));
                rows.Add($"{t:F2}s active {vm.ActiveLineIndex} scroll {vm.ScrollLineIndex}: You {you:F2} cold {cold:F2} sun {sun:F2}");
                _o.WriteLine(rows[^1]);
                if (t >= 123.5)
                {
                    Assert.True(cold < 0.55, rows[^1]);
                    Assert.True(sun < 0.55, rows[^1]);
                }
                if (t >= 124.2) Assert.True(you > 0.85, rows[^1]);
            }
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SidePanel_IncomingLine_WhileTheListLeads_ShowsItsWordsUnsung()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = MakeLyrics(player, library);
        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 420, Height = 800, Content = view };
        try
        {
            win.Show();
            PumpFor(100);
            var incoming = LineStarting(vm, "You feel");
            PlayTo(vm, 115.0, 123.5);
            AssertLeadState(vm, incoming);
            AssertUnsung(incoming, SweepWords(view, incoming), "panel lead 2:03.5");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void MiniPlayer_IncomingLine_WhileTheListLeads_ShowsItsWordsUnsung()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = MakeLyrics(player, library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var mini = new MiniPlayerViewModel(player, lyrics, settings, library);
        var win = new MiniPlayerWindow { DataContext = mini, Width = 340, Height = 432 };
        try
        {
            win.Show();
            PumpFor(150);
            mini.ToggleLyricsFormCommand.Execute(null);
            PumpFor(700);
            var incoming = LineStarting(lyrics, "You feel");
            PlayTo(lyrics, 115.0, 123.5);
            AssertLeadState(lyrics, incoming);
            AssertUnsung(incoming, SweepWords(win, incoming), "mini lead 2:03.5");
        }
        finally { win.Close(); }
    }
}
