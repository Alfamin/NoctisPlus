using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #133: with Liquid Glass on, the Home section headings read crisply through the
/// player island while the covers beside them were frosted. The window is translucent then
/// (the page surfaces paint at ~35% so the OS blur shows through), so the frost of a bare
/// page area came out translucent, and the frost was drawn OVER the page: the un-blurred
/// page stayed visible through it. Opaque content (covers) was hidden by its own opaque
/// frost. The frost now replaces what it blurs.
/// </summary>
public class GlassFrostReplacesBeneathTests
{
    private const int W = 200, H = 120;
    private static readonly SKRect Panel = new(40, 30, 160, 90);

    /// <summary>The Liquid Glass page: a 35% surface with opaque white 2px strokes on it (text).</summary>
    private static SKSurface TranslucentPageWithText()
    {
        var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(new SKColor(0x25, 0x25, 0x25, 0x5A));
        using var white = new SKPaint { Color = SKColors.White };
        for (var x = 0; x < W; x += 16) c.DrawRect(new SKRect(x, 0, x + 2, H), white);
        return surface;
    }

    private static SKPath PanelPath()
    {
        var path = new SKPath();
        path.AddRoundRect(new SKRoundRect(Panel, 20));
        return path;
    }

    private static SKBitmap Pixels(SKSurface surface)
    {
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    /// <summary>The frost alone: the same blur of the same pixels, drawn onto nothing.</summary>
    private static SKBitmap FrostAlone(SKSurface page, float sigma)
    {
        using var source = GlassBlur.Snapshot(page, GlassBlur.SourceRect(Panel, sigma), out var at);
        using var empty = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        empty.Canvas.Clear(SKColors.Transparent);
        using var path = PanelPath();
        GlassBlur.DrawBlurred(empty.Canvas, source!, at, path, GlassBlur.ClampSigma(sigma), 1f);
        return Pixels(empty);
    }

    /// <summary>Largest per-channel difference over the panel's interior, clear of the
    /// anti-aliased rounded edge.</summary>
    private static int MaxDiff(SKBitmap a, SKBitmap b)
    {
        var max = 0;
        for (var y = (int)Panel.Top + 6; y < Panel.Bottom - 6; y++)
            for (var x = (int)Panel.Left + 20; x < Panel.Right - 20; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                max = Math.Max(max, new[]
                {
                    Math.Abs(p.Red - q.Red), Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue), Math.Abs(p.Alpha - q.Alpha),
                }.Max());
            }
        return max;
    }

    [Fact]
    public void Frost_OnATranslucentPage_IsTheFrostAlone_NoCrispTextShowsThrough()
    {
        using var page = TranslucentPageWithText();
        using var expected = FrostAlone(page, 8);
        using var path = PanelPath();
        Assert.True(GlassBlur.Draw(page.Canvas, page, Panel, path, 8));
        using var actual = Pixels(page);
        // Drawn over the page instead, the white strokes stayed at ~(1 - frost alpha) of
        // their strength: well over 100 levels off on every stroke.
        var diff = MaxDiff(actual, expected);
        Assert.True(diff <= 2, $"max diff {diff}");
    }

    private static double Premul(SKColor c) => c.Green * c.Alpha / 255.0;

    [Fact]
    public void Frost_FadingIn_IsALerpFromThePageToTheFrost()
    {
        using var page = TranslucentPageWithText();
        using var before = Pixels(page);
        using var frost = FrostAlone(page, 8);
        using var path = PanelPath();
        Assert.True(GlassBlur.Draw(page.Canvas, page, Panel, path, 8, alpha: 0.5f));
        using var actual = Pixels(page);
        var worst = 0.0;
        string? dbg = null;
        for (var y = (int)Panel.Top + 6; y < Panel.Bottom - 6; y++)
            for (var x = (int)Panel.Left + 20; x < Panel.Right - 20; x++)
            {
                var o = before.GetPixel(x, y);
                var f = frost.GetPixel(x, y);
                var a = actual.GetPixel(x, y);
                worst = Math.Max(worst, Math.Abs(a.Alpha - (o.Alpha + f.Alpha) / 2.0));
                // GetPixel is unpremultiplied; the lerp holds on premultiplied values.
                worst = Math.Max(worst, Math.Abs(Premul(a) - (Premul(o) + Premul(f)) / 2.0));
                if (worst > 3 && dbg == null) dbg = $"({x},{y}) page {o} frost {f} drawn {a}";
            }
        Assert.True(worst <= 3, $"worst {worst} {dbg}");
    }

    [Fact]
    public void Frost_OnAnOpaquePage_IsUnchanged()
    {
        // The covers' case, which always looked right: an opaque page frosts to the frost alone.
        using var page = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        page.Canvas.Clear(SKColors.Black);
        using (var white = new SKPaint { Color = SKColors.White })
            for (var x = 0; x < W; x += 16) page.Canvas.DrawRect(new SKRect(x, 0, x + 8, H), white);
        using var expected = FrostAlone(page, 8);
        using var path = PanelPath();
        Assert.True(GlassBlur.Draw(page.Canvas, page, Panel, path, 8));
        using var actual = Pixels(page);
        Assert.True(MaxDiff(actual, expected) <= 1);
    }

    [Fact]
    public void LensFrost_OnATranslucentPage_HidesTheCrispTextToo()
    {
        using var page = TranslucentPageWithText();
        using var expected = FrostAlone(page, 8);
        using var path = PanelPath();
        // A lens that bends nothing (only a no-op saturation turns it on): it draws the frost.
        var frame = new GlassLensFrame(Panel, new GlassCornerRadii(20, 20, 20, 20), 0, 0, 0, 1, 1.0001f, 0, 1);
        Assert.True(GlassBlur.Draw(page.Canvas, page, Panel, path, 8, lens: frame));
        using var actual = Pixels(page);
        var diff = MaxDiff(actual, expected);
        Assert.True(diff <= 4, $"max diff {diff}");
    }

    /// <summary>The island is above the page in Z (the report also read as the heading drawn
    /// over the island): both live in AppContentLayer and the island comes later with a higher
    /// ZIndex than the page's root, so nothing on a page is composited after it.</summary>
    [AvaloniaFact]
    public void Island_IsAboveEveryPage_InZ()
    {
        var window = new MainWindow();
        try
        {
            var layer = window.FindControl<Panel>("AppContentLayer")!;
            var island = layer.Children.OfType<PlaybackBarView>().Single();
            var pages = window.GetLogicalDescendants().OfType<ContentControl>().Single(c => c.Classes.Contains("page-host"));
            var pageRoot = layer.Children.Single(c => c == pages || c.IsLogicalAncestorOf(pages));
            Assert.True(island.ZIndex > pageRoot.ZIndex);
            Assert.True(layer.Children.IndexOf(island) > layer.Children.IndexOf(pageRoot));
        }
        finally { window.Close(); }
    }

    private static async Task Flush(int settleMs = 150)
    {
        var until = DateTime.UtcNow.AddMilliseconds(settleMs);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        } while (DateTime.UtcNow < until);
    }

    /// <summary>Sum of |dx| of green between horizontal neighbours in <paramref name="r"/>:
    /// crisp text scores high, frosted text low.</summary>
    private static long Edges(Bitmap frame, PixelRect r)
    {
        var stride = frame.PixelSize.Width * 4;
        var buf = new byte[stride * frame.PixelSize.Height];
        var h = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(frame.PixelSize), h.AddrOfPinnedObject(), buf.Length, stride); }
        finally { h.Free(); }
        long sum = 0;
        for (var y = r.Y; y < r.Bottom; y++)
            for (var x = r.X + 1; x < r.Right; x++)
                sum += Math.Abs(buf[y * stride + x * 4 + 1] - buf[y * stride + (x - 1) * 4 + 1]);
        return sum;
    }

    /// <summary>
    /// The report's scene, rendered: a 22px bold heading on a translucent page under a glass
    /// island, on a full frame and after a repaint of the heading alone (a partial frame).
    /// Real Skia only (NOCTIS_TEST_SKIA=1).
    /// </summary>
    [AvaloniaFact]
    public async Task Heading_UnderTheIsland_IsFrosted_OnFullAndPartialFrames()
    {
        if (!HeadlessTestApp.RealRendering) Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var heading = new TextBlock
        {
            Text = "Last Played", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            Margin = new Thickness(40, 0, 0, 0),
        };
        var content = new StackPanel { Margin = new Thickness(0, 210, 0, 0), Children = { heading, new Border { Height = 300, Background = Brushes.Teal } } };
        var glass = new GlassPanel
        {
            UseAppGlass = false, IsGlassActive = true, Height = 50, Width = 320, CornerRadius = new CornerRadius(25),
            Background = new SolidColorBrush(Colors.Black, 0.2), BlurRadius = 14, ZIndex = 30,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40),
        };
        // The Liquid Glass page: translucent, as ApplyLiquidGlass paints it.
        var root = new Panel
        {
            Background = new SolidColorBrush(Color.FromArgb(0xBA, 0x25, 0x25, 0x25)),
            Children = { new ScrollViewer { Content = content }, glass },
        };
        var window = new Window
        {
            Width = 400, Height = 300, Content = root, Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
        };
        window.Show();
        try
        {
            await Flush();
            var under = new PixelRect(60, 216, 200, 28);
            var frosted = Edges(window.CaptureRenderedFrame()!, under);
            heading.Foreground = Brushes.Yellow;
            await Flush();
            var partial = Edges(window.CaptureRenderedFrame()!, under);
            glass.IsGlassActive = false;
            await Flush();
            var crisp = Edges(window.CaptureRenderedFrame()!, under);
            // Measured: crisp ~52000; frost drawn over the page ~8800 (the heading readable);
            // frost replacing the page ~1200.
            Assert.True(frosted * 20 < crisp, $"full frame {frosted} vs crisp {crisp}");
            Assert.True(partial * 20 < crisp, $"partial frame {partial} vs crisp {crisp}");
        }
        finally { window.Close(); }
    }
}
