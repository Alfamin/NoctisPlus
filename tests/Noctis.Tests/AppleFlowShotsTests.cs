using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Eyeball probe for the Apple Music word flow (owner 10-18): renders the lyrics page at
/// chosen song times of a word-timed .lrc and saves the active line as PNGs, for a
/// side-by-side with stills of an Apple Music capture. Explicit only: runs when
/// NOCTIS_TEST_SKIA=1, NOCTIS_APPLEFLOW_LRC names the .lrc and NOCTIS_APPLEFLOW_DIR the
/// output folder (NOCTIS_APPLEFLOW_PREFIX optional); otherwise a no-op.
/// </summary>
public class AppleFlowShotsTests
{
    private readonly ITestOutputHelper _o;
    public AppleFlowShotsTests(ITestOutputHelper o) => _o = o;

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

    private static readonly TimeSpan Lookahead = TimeSpan.FromMilliseconds(80);

    // Song times (s) of the Apple stills: mid "Where you and I had", mid "goodbye", mid "follow".
    private static readonly (string Name, double Seconds)[] Shots =
    {
        ("where-you-and-i-had", 17.30),
        ("goodbye", 18.60),
        ("goodbye-late", 19.45),
        ("follow", 33.00),
    };

    [AvaloniaFact]
    public void SaveShots()
    {
        var lrc = Environment.GetEnvironmentVariable("NOCTIS_APPLEFLOW_LRC");
        var dir = Environment.GetEnvironmentVariable("NOCTIS_APPLEFLOW_DIR");
        var prefix = Environment.GetEnvironmentVariable("NOCTIS_APPLEFLOW_PREFIX") ?? "noctis";
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(lrc) || string.IsNullOrEmpty(dir) || !File.Exists(lrc)) return;
        Directory.CreateDirectory(dir);

        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(File.ReadAllText(lrc)));
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Background = new SolidColorBrush(Color.Parse("#22303C")) };

        void PumpFor(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(2);
            } while (sw.ElapsedMilliseconds < ms);
            Dispatcher.UIThread.RunJobs();
        }

        try
        {
            win.Show();
            win.Content = view;
            PumpFor(300);
            foreach (var (name, seconds) in Shots)
            {
                var t = new LyricsTimeline(vm.LyricLines, Lookahead, Lookahead);
                // Walk up to the moment at 30 fps so lines that finished settle as in the app.
                for (var s = seconds - 3; s < seconds; s += 1 / 30.0)
                    t.Update(TimeSpan.FromSeconds(s) - Lookahead);
                t.Update(TimeSpan.FromSeconds(seconds) - Lookahead);
                PumpFor(700); // let the line glide and opacity transitions land

                var btn = view.GetVisualDescendants().OfType<Button>()
                    .First(b => b.Classes.Contains("lyric-line-btn") && b.Classes.Contains("active"));
                var origin = btn.TranslatePoint(new Point(0, 0), win)!.Value;
                using var bmp = win.CaptureRenderedFrame()!;
                var x = Math.Max(0, (int)origin.X - 20);
                var y = Math.Max(0, (int)origin.Y - 30);
                var rect = new PixelRect(x, y,
                    (int)Math.Min(btn.Bounds.Width + 60, 1280 - x),
                    (int)Math.Min(btn.Bounds.Height + 60, 800 - y));
                CropSave(bmp, rect, Path.Combine(dir, $"{prefix}-{name}.png"));
                var em = btn.GetVisualDescendants().OfType<Noctis.Controls.KaraokeWordText>().FirstOrDefault()?.FontSize;
                _o.WriteLine($"{name} @ {seconds:F2}s: line '{t.ActiveLine?.Text}' rect {rect} em {em}");
            }
        }
        finally
        {
            win.Close();
        }
    }

    private static void CropSave(Avalonia.Media.Imaging.Bitmap bmp, PixelRect rect, string path)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        using var src = SkiaSharp.SKBitmap.Decode(ms);
        using var dst = new SkiaSharp.SKBitmap(rect.Width, rect.Height);
        src.ExtractSubset(dst, new SkiaSharp.SKRectI(rect.X, rect.Y, rect.Right, rect.Bottom));
        using var img = SkiaSharp.SKImage.FromBitmap(dst);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
