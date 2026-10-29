using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// The lyrics page side of the held-word fix: the swept word is a
/// <see cref="KaraokeWordText"/> whose per-frame Progress no longer re-measures the
/// layout, keeps the old cell geometry, and — under NOCTIS_TEST_SKIA=1 with
/// NOCTIS_ELRC_SHOTS=&lt;prefix&gt; and NOCTIS_ELRC_SHOTS_DIR — saves 30 fps frames
/// across a held word for eyeballing.
/// </summary>
public class ElrcHeldWordViewTests
{
    private readonly ITestOutputHelper _o;
    public ElrcHeldWordViewTests(ITestOutputHelper o) => _o = o;

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

    private static LyricsViewModel MakeViewModel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static readonly TimeSpan Lookahead = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// The sweep used to be a TextBlock whose Foreground was a new gradient brush every
    /// frame; a TextBlock re-measures on a Foreground change, so every frame of a sweep ran
    /// a layout pass (measured: 30 of 30 frames). Now a Progress change only re-renders.
    /// </summary>
    [AvaloniaFact]
    public void SweepFrames_DoNotRunLayout()
    {
        var feature = new Noctis.Features.LyricMotion.LyricMotionFeature();
        feature.Initialize(new NativeLyricMotionTests.SettingsHost(new NativeLyricMotionTests.Values()));
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(ElrcHeldWordTests.Sample));
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800 };
        try
        {
            win.Show();
            win.Content = view;
            Pump();
            var t = new LyricsTimeline(vm.LyricLines, Lookahead, Lookahead) { WordMotion = false };
            t.Update(TimeSpan.FromSeconds(46.0));
            Pump();

            var told = vm.LyricLines[1].Words![^1];
            // Measure Plus's actual native painter, with its original gradient and text metrics.
            var cell = view.GetVisualDescendants().OfType<MotionWordCell>().First(k => ReferenceEquals(k.Word, told));
            Assert.Single(cell.Children);
            var sweep = cell.Presenter.SweepText;
            Assert.True(told.IsEmphasis);
            var before = ((LinearGradientBrush)sweep.Foreground!).GradientStops.Select(s => s.Offset).ToArray();

            int layouts = 0, invalidated = 0;
            win.LayoutUpdated += (_, _) => layouts++;
            for (int i = 1; i <= 30; i++)
            {
                t.Update(TimeSpan.FromSeconds(46.0 + i / 60.0));
                if (!sweep.IsMeasureValid) invalidated++;
                Pump();
            }
            Assert.NotEqual(before, ((LinearGradientBrush)sweep.Foreground!).GradientStops.Select(s => s.Offset).ToArray());
            Assert.Equal(0, invalidated);
            Assert.Equal(0, layouts);
        }
        finally
        {
            win.Close();
            feature.Shutdown();
        }
    }

    /// <summary>The word cell keeps the size the old base TextBlock gave it, so line wrap and
    /// duet alignment do not move.</summary>
    [AvaloniaTheory]
    [InlineData("told", 40.0)]
    [InlineData("story ", 40.0)]
    [InlineData("合", 28.0)]
    public void WordCell_MeasuresLikeTheTextBlockItReplaced(string text, double fontSize)
    {
        var padding = new Thickness(0, 0, text.EndsWith(' ') ? 0 : 24, 0);
        var plain = new TextBlock { Text = text, FontSize = fontSize, FontWeight = FontWeight.Bold, Padding = padding };
        var word = new KaraokeWordText { Text = text, FontSize = fontSize, FontWeight = FontWeight.Bold, Padding = padding, IsEmphasis = true };
        plain.Measure(Size.Infinity);
        word.Measure(Size.Infinity);
        Assert.Equal(plain.DesiredSize, word.DesiredSize);
    }

    [AvaloniaFact]
    public void HeldWord_SaveFrames()
    {
        var prefix = Environment.GetEnvironmentVariable("NOCTIS_ELRC_SHOTS");
        var dir = Environment.GetEnvironmentVariable("NOCTIS_ELRC_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(ElrcHeldWordTests.Sample));
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Background = new SolidColorBrush(Color.Parse("#202028")) };
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
            var t = new LyricsTimeline(vm.LyricLines, Lookahead, Lookahead);
            int frame = 0;
            for (double s = 44.90; s <= 48.30; s += 1 / 30.0, frame++)
            {
                t.Update(TimeSpan.FromSeconds(s));
                PumpFor(33);
                var btn = view.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("lyric-line-btn")).ElementAt(1);
                var origin = btn.TranslatePoint(new Point(0, 0), win)!.Value;
                using var bmp = win.CaptureRenderedFrame()!;
                var rect = new PixelRect((int)origin.X, Math.Max(0, (int)origin.Y - 20),
                    (int)Math.Min(btn.Bounds.Width + 40, 1280 - origin.X), (int)btn.Bounds.Height + 40);
                CropSave(bmp, rect, Path.Combine(dir, $"{prefix}-{frame:000}-t{s:00.000}.png"));
            }
            _o.WriteLine($"saved {frame} frames to {dir}");
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
