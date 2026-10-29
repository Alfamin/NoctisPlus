using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Real Skia only (NOCTIS_TEST_SKIA=1): Skia rounds an axis-aligned glyph baseline to a
/// whole pixel, so the ~2 px word float rose in two 1 px hops. While it moves the word is
/// drawn unsnapped (quarter-pixel steps); at rest it sits on the pixel grid.
/// </summary>
public class KaraokeLiftRenderTests
{
    private readonly ITestOutputHelper _o;
    public KaraokeLiftRenderTests(ITestOutputHelper o) => _o = o;

    [AvaloniaFact]
    public void Lift_MovesInSubpixelSteps_AndRestsOnTheGrid()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var word = new KaraokeWordText { Text = "had", FontSize = 35, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Progress = 1, BaseOpacity = 1 };
        var win = new Window { Width = 200, Height = 120, Background = Brushes.Black, Content = new Border { Padding = new Thickness(40), Child = word } };
        try
        {
            win.Show();
            var ys = new List<double>();
            for (int i = 0; i <= 10; i++)
            {
                word.Lift = i / 10.0;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                using var bmp = win.CaptureRenderedFrame()!;
                using var ms = new MemoryStream();
                bmp.Save(ms);
                ms.Position = 0;
                using var sk = SkiaSharp.SKBitmap.Decode(ms);
                double sum = 0, wsum = 0;
                for (int y = 0; y < sk.Height; y++)
                    for (int x = 0; x < sk.Width; x++)
                    {
                        var c = sk.GetPixel(x, y);
                        double l = c.Red;
                        sum += l * y;
                        wsum += l;
                    }
                ys.Add(sum / wsum);
            }
            _o.WriteLine(string.Join(" ", ys.Select(y => y.ToString("F3"))));
            var steps = ys.Zip(ys.Skip(1), (a, b) => a - b).ToList();
            _o.WriteLine("steps " + string.Join(" ", steps.Select(d => d.ToString("F3"))));
            // em 35 -> 0.055 em = 1.925 px, rounded to 2 device px at rest.
            Assert.Equal(2.0, ys[0] - ys[^1], precision: 2);
            Assert.All(steps, d => Assert.InRange(d, -0.05, 0.5));
        }
        finally
        {
            win.Close();
        }
    }
}
