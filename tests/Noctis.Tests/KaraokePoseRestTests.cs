using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

[Collection("App.Services global")]
public class KaraokePoseRestTests
{
    private sealed class PoseEffect : IWordEffect
    {
        public double Y = -0.04;
        public bool Throw;
        public WordDrawMode Mode(in WordPoseInput word) => WordDrawMode.Whole;
        public LetterPose Pose(in WordPoseInput word, int letter) => Throw ? throw new InvalidOperationException("effect failed") : new(1, 0, Y, 1);
        public void LineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft) { }
    }

    private static (double Weight, double Y) Raster(KaraokeWordText word)
    {
        using var target = new RenderTargetBitmap(new PixelSize(240, 100));
        target.Render(word);
        using var stream = new MemoryStream(); target.Save(stream); stream.Position = 0;
        using var pixels = SkiaSharp.SKBitmap.Decode(stream);
        double sum = 0, weight = 0;
        for (var y = 0; y < pixels.Height; y++)
            for (var x = 0; x < pixels.Width; x++)
            {
                var p = pixels.GetPixel(x, y); var w = p.Alpha / 255.0;
                sum += w * y; weight += w;
            }
        return (weight, sum / weight);
    }

    [AvaloniaFact]
    public void MovingToStillPose_DoesNotChangeRasterPosition()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var effect = new PoseEffect(); LyricsWordEffect.Set(effect);
        var word = new KaraokeWordText { Text = "had", FontSize = 35, Foreground = Brushes.White, Progress = 2, Padding = new Thickness(20) };
        var window = new Window { Width = 240, Height = 100, Content = word };
        try
        {
            window.Show(); window.UpdateLayout();
            Raster(word);
            effect.Y = -0.042; word.Progress = 3; Raster(word);
            effect.Y = -0.043; word.Progress = 4; var moving = Raster(word);
            word.Progress = 5; var still = Raster(word);
            Assert.True(moving.Weight > 10); Assert.True(still.Weight > 10);
            Assert.InRange(Math.Abs(moving.Y - still.Y), 0, 0.01);
        }
        finally { window.Close(); LyricsWordEffect.Set(null); }
    }

    [AvaloniaFact]
    public void EffectFailureDuringDrawing_PaintsFallbackInsteadOfABlankFrame()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var effect = new PoseEffect(); LyricsWordEffect.Set(effect);
        var word = new KaraokeWordText { Text = "hello", FontSize = 35, Foreground = Brushes.White, Progress = 2, Padding = new Thickness(20) };
        var window = new Window { Width = 240, Height = 100, Content = word };
        try
        {
            window.Show(); window.UpdateLayout(); Assert.True(Raster(word).Weight > 10);
            effect.Throw = true; word.InvalidateVisual();
            Assert.True(Raster(word).Weight > 10, "effect failure left the current frame blank");
        }
        finally { window.Close(); LyricsWordEffect.Set(null); }
    }
}