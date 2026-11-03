using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services.LyricsVideo;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner #243: the lyrics Reel. Frames are pure functions of time (so they can be drawn in parallel), the page sits
/// in the Reel's safe area, the sung line sweeps, and the intro opens out of black. Explicit only: NOCTIS_VIDEO_SHOTS
/// names a folder to save sample frames (and, with NOCTIS_FFMPEG, a whole encoded Reel) for a look by eye.
/// </summary>
public class LyricsVideoRendererTests
{
    private readonly ITestOutputHelper _o;
    public LyricsVideoRendererTests(ITestOutputHelper o) => _o = o;

    private static readonly string[] Words =
    [
        "I kept the lights on for you tonight", "every street still knows your name", "and the city hums our song",
        "hold on I am almost home", "under all this neon rain", "we were never far apart",
        "say it one more time", "the radio is playing low", "I can hear you in the static",
        "don't let go", "stay with me until the morning", "we were never far apart",
    ];

    internal static List<LyricLine> SongLines(double lineSeconds = 3.2, double start = 4)
    {
        var lines = new List<LyricLine>();
        for (var i = 0; i < Words.Length; i++)
        {
            var at = start + i * lineSeconds;
            var tokens = Words[i].Split(' ');
            var per = (lineSeconds * 0.8) / tokens.Length;
            var words = new List<WordTiming>();
            for (var w = 0; w < tokens.Length; w++)
            {
                var s = at + w * per;
                // The last word of line 4 is held: it swells and glows.
                var held = i == 3 && w == tokens.Length - 1;
                words.Add(new WordTiming
                {
                    Text = tokens[w] + (w + 1 < tokens.Length ? " " : ""),
                    Start = TimeSpan.FromSeconds(s),
                    End = TimeSpan.FromSeconds(s + (held ? 1.8 : per)),
                });
            }
            var end = TimeSpan.FromSeconds(at + lineSeconds * 0.8 + (i == 3 ? 1.8 : 0));
            lines.Add(new LyricLine { Timestamp = TimeSpan.FromSeconds(at), Text = Words[i], EndTimestamp = end, Words = words });
        }
        return lines;
    }

    internal static string MakeCover()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics-video-cover.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var surface = SKSurface.Create(new SKImageInfo(1000, 1000));
        var c = surface.Canvas;
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1000, 1000),
                [new SKColor(0x7a, 0x3f, 0xd1), new SKColor(0xe0, 0x52, 0x6b), new SKColor(0xf2, 0xa6, 0x5a)], SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, 1000, 1000, paint);
        }
        using (var sun = new SKPaint { IsAntialias = true, Color = new SKColor(255, 240, 220) })
            c.DrawCircle(680, 340, 150, sun);
        using (var band = new SKPaint { Color = new SKColor(0x2a, 0x1b, 0x4a) })
            c.DrawRect(0, 760, 1000, 240, band);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    internal static LyricsVideoSpec Spec(bool intro = true, bool outro = true) => new()
    {
        Title = "Neon Rain",
        Artist = "Sample Artist",
        IsExplicit = true,
        CoverPath = MakeCover(),
        Lines = LyricsVideoLine.From(SongLines()),
        FirstLine = 2,
        LastLine = 6,
        Intro = intro,
        Outro = outro,
        Bpm = 96,
    };

    private static byte[] Frame(LyricsVideoRenderer renderer, int index)
    {
        return renderer.RenderPixels(index, _ => 0.2).ToArray();
    }

    private static double Luma(byte[] bgra, SKRectI area)
    {
        double sum = 0;
        var n = 0;
        for (var y = area.Top; y < area.Bottom; y += 3)
            for (var x = area.Left; x < area.Right; x += 3)
            {
                var i = (y * LyricsVideoSpec.Width + x) * 4;
                sum += 0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2];
                n++;
            }
        return sum / n;
    }

    [AvaloniaFact]
    public void AFrame_IsTheSameWhicheverOrderFramesAreDrawnIn()
    {
        var spec = Spec();
        using var scene = new LyricsVideoScene(spec);
        using var a = new LyricsVideoRenderer(scene);
        using var b = new LyricsVideoRenderer(scene);
        var mid = spec.FrameCount / 2;
        var first = Frame(a, mid);
        Frame(b, 3);
        Frame(b, spec.FrameCount - 2);
        var again = Frame(b, mid);
        Assert.Equal(first, again);
    }

    [AvaloniaFact]
    public void TheIntro_OpensOutOfBlack_AndThePageSitsInTheReelsSafeArea()
    {
        var spec = Spec();
        using var scene = new LyricsVideoScene(spec);
        using var renderer = new LyricsVideoRenderer(scene);
        var page = scene.Geometry.Page;
        Assert.True(page.Top >= 250 && page.Bottom <= LyricsVideoSpec.Height - 350,
            $"page {page} outside the Reels safe area");

        var opening = Frame(renderer, 0);
        var settled = Frame(renderer, (int)(LyricsVideoSpec.Fps * 2.5));
        var panel = new SKRectI((int)page.Left, (int)page.Top, (int)page.Right, (int)page.Bottom);
        Assert.True(Luma(opening, panel) < 12, $"first frame not dark: {Luma(opening, panel):0.0}");
        Assert.True(Luma(settled, panel) > Luma(opening, panel) + 10, "the page never arrived");
    }

    [AvaloniaFact]
    public void TheSungLine_IsBrighterBehindTheSweepThanAheadOfIt()
    {
        var spec = Spec(intro: false);
        using var scene = new LyricsVideoScene(spec);
        using var renderer = new LyricsVideoRenderer(scene);
        // Halfway through line 3's words: its first words are sung, its last ones not yet.
        var line = scene.Lines[3];
        var mid = line.Line.Start + 1.2;
        var index = (int)((mid - spec.ClipStart) * LyricsVideoSpec.Fps);
        var frame = Frame(renderer, index);
        var g = scene.Geometry;
        var at = scene.Motion.ScrollAt(mid);
        var top = g.Page.Top + g.Lyrics.Top + g.Lyrics.Height * g.AnchorRatio + line.Top - (float)at;
        var first = line.Words[0];
        var last = line.Words[^1];
        int Px(float x) => (int)(g.Page.Left + g.Lyrics.Left + x);
        var sung = new SKRectI(Px(first.X), (int)top + 4, Px(first.X + first.GlyphWidth), (int)(top + g.LyricSize));
        var unsung = new SKRectI(Px(last.X), (int)top + 4, Px(last.X + last.GlyphWidth), (int)(top + g.LyricSize));
        Assert.True(Luma(frame, sung) > Luma(frame, unsung) + 8, $"sung {Luma(frame, sung):0} vs unsung {Luma(frame, unsung):0}");
    }

    [AvaloniaTheory]
    [InlineData(LyricsVideoLayout.CompactHeader)]
    [InlineData(LyricsVideoLayout.FullPage)]
    public void FullFrameLayouts_KeepTheHeaderAndLyricsInsideTheSafeArea(LyricsVideoLayout layout)
    {
        using var scene = new LyricsVideoScene(Spec() with { Layout = layout });
        var g = scene.Geometry;
        Assert.False(g.IsPanel);
        Assert.InRange(g.Cover.Top, 250, 1570);
        Assert.True(g.Cover.Bottom < g.Lyrics.Top);
        Assert.True(g.Lyrics.Bottom <= 1570 && g.Lyrics.Left >= 60 && g.Lyrics.Right <= 1020);
        var titleLeft = g.Title.X - (g.TitleCentered ? g.TitleWidth / 2 : 0);
        Assert.True(titleLeft >= 60 && titleLeft + g.TitleWidth <= 1020);
        using var renderer = new LyricsVideoRenderer(scene);
        Assert.True(Luma(Frame(renderer, 90), new SKRectI(0, 0, 1080, 1920)) > 5);
    }

    [AvaloniaFact]
    public void LayoutChoice_UpdatesTheSpecAndRetainsTheReflectionChoice()
    {
        using var vm = new Noctis.ViewModels.LyricsVideoViewModel(LyricsVideoLine.From(SongLines()), 0,
            "Title", "Artist", false, null, "audio.flac", "ffmpeg.exe", 0);
        vm.Reflection = 78;
        vm.LayoutIndex = 1;
        Assert.False(vm.ShowReflection);
        Assert.Equal(LyricsVideoLayout.CompactHeader, vm.Spec()!.Layout);
        vm.LayoutIndex = 0;
        Assert.True(vm.ShowReflection);
        Assert.Equal(78, vm.Reflection);
    }

    [AvaloniaFact]
    public void Shots_SaveBuiltLayouts()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_VIDEO_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var layout in Enum.GetValues<LyricsVideoLayout>())
        {
            var spec = Spec() with { Layout = layout };
            using var scene = new LyricsVideoScene(spec);
            using var renderer = new LyricsVideoRenderer(scene);
            foreach (var seconds in new[] { 0.4, 1.3, 3.0, 8.0, spec.Duration - 1.1 })
            {
                using var frame = renderer.Snapshot(spec.ClipStart + seconds, 0.3, (int)(seconds * 30));
                using var data = frame.Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(Path.Combine(dir, $"{layout}-{seconds:00.0}.png"), data.ToArray());
            }
        }
    }

    [AvaloniaFact]
    public async Task Shots_SaveSampleFramesAndAReel()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_VIDEO_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var spec = Spec();
        var watch = Stopwatch.StartNew();
        using var scene = new LyricsVideoScene(spec);
        _o.WriteLine($"scene {watch.ElapsedMilliseconds} ms, {spec.FrameCount} frames");
        using var renderer = new LyricsVideoRenderer(scene);
        foreach (var seconds in new[] { 0.4, 1.0, 1.6, 2.4, 4.0, 5.3, 8.3, 10.6, 11.6, spec.Duration - 2.0, spec.Duration - 1.0 })
        {
            var index = (int)(seconds * LyricsVideoSpec.Fps);
            watch.Restart();
            using var image = renderer.Snapshot(spec.ClipStart + seconds, 0.3, index);
            var ms = watch.ElapsedMilliseconds;
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"frame-{seconds:00.0}s.png"), data.ToArray());
            _o.WriteLine($"{seconds:0.0}s drawn in {ms} ms");
        }
        var buffer = new byte[LyricsVideoRenderer.FrameBytes];
        watch.Restart();
        for (var i = 0; i < 60; i++) renderer.RenderFrame(i + 150, _ => 0.3, buffer);
        _o.WriteLine($"60 frames on one thread: {watch.ElapsedMilliseconds} ms");

        var ffmpeg = Environment.GetEnvironmentVariable("NOCTIS_FFMPEG");
        if (string.IsNullOrEmpty(ffmpeg)) return;
        var audio = Path.Combine(dir, "beat.wav");
        var make = Process.Start(new ProcessStartInfo(ffmpeg,
            $"-y -hide_banner -loglevel error -f lavfi -i \"aevalsrc=0.6*sin(2*PI*55*t)*exp(-12*mod(t\\,0.625))+0.08*sin(2*PI*440*t):s=44100:d=60\" \"{audio}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        await make.WaitForExitAsync();
        watch.Restart();
        var output = Path.Combine(dir, "reel.mp4");
        var failure = await LyricsVideoExporter.ExportAsync(spec, audio, ffmpeg, output, null, CancellationToken.None);
        _o.WriteLine($"export: {failure ?? "ok"} in {watch.Elapsed.TotalSeconds:0.0} s for {spec.Duration:0.0} s of video");
        Assert.Null(failure);
    }
}
