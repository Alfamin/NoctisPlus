using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

[Collection("App.Services global")]
public sealed class PlusLyricMotionRasterTests
{
    private static double InkY(RenderTargetBitmap target, int minY, int maxY)
    {
        using var stream = new MemoryStream();
        target.Save(stream);
        stream.Position = 0;
        using var pixels = SkiaSharp.SKBitmap.Decode(stream);
        double sum = 0, weight = 0;
        for (var y = minY; y < maxY; y++)
            for (var x = 0; x < pixels.Width; x++)
            {
                var alpha = pixels.GetPixel(x, y).Alpha / 255.0;
                sum += alpha * y;
                weight += alpha;
            }
        return weight == 0 ? double.NaN : sum / weight;
    }

    [AvaloniaFact]
    public void ActiveWordMotionDoesNotHopAndStillNeighborNeverMoves()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var effect = new PlusLyricMotionEffect();
        LyricsWordEffect.Set(effect);
        var still = new KaraokeWordText
        {
            Text = "steady",
            FontSize = 44,
            Foreground = Brushes.White,
            Progress = -2,
            Padding = new Thickness(8),
        };
        var moving = new KaraokeWordText
        {
            Text = "smooth",
            FontSize = 44,
            Foreground = Brushes.White,
            HeldDurationMs = 500,
            Padding = new Thickness(8),
        };
        var content = new StackPanel { Spacing = 8, Children = { still, moving } };
        var window = new Window { Width = 480, Height = 150, Content = content };
        using var target = new RenderTargetBitmap(new PixelSize(480, 150));
        try
        {
            window.Show();
            window.UpdateLayout();
            double? priorStill = null, priorMoving = null;
            var stillStep = 0.0;
            var movingStep = 0.0;
            for (var frame = 0; frame <= 60; frame++)
            {
                var progress = frame / 30.0; // 16.7 ms per frame over the 500 ms note and release.
                moving.Progress = progress;
                moving.Lift = Math.Clamp(progress, 0, 1);
                target.Render(window);
                var stillY = InkY(target, 0, 70);
                var movingY = InkY(target, 70, 150);
                Assert.True(double.IsFinite(stillY) && double.IsFinite(movingY));
                if (priorStill is { } sy) stillStep = Math.Max(stillStep, Math.Abs(stillY - sy));
                if (priorMoving is { } my) movingStep = Math.Max(movingStep, Math.Abs(movingY - my));
                priorStill = stillY;
                priorMoving = movingY;
            }

            Assert.InRange(stillStep, 0, 0.01);
            Assert.InRange(movingStep, 0, 0.35);
        }
        finally
        {
            window.Close();
            LyricsWordEffect.Set(null);
        }
    }
}
