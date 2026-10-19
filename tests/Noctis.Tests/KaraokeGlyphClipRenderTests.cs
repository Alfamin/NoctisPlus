using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-18: in the active line the first letter of a word ("w" of "way", "why?") was
/// sliced vertically at the word's left edge. A TextBlock clips to its layout box by
/// default (Avalonia 12) and KaraokeWordText draws its held-note swell and float past
/// that box, so the ink was cut at the advance origin.
/// </summary>
public class KaraokeGlyphClipRenderTests
{
    private readonly ITestOutputHelper _o;
    public KaraokeGlyphClipRenderTests(ITestOutputHelper o) => _o = o;

    [AvaloniaFact]
    public void KaraokeWordText_DoesNotClipToItsLayoutBox()
    {
        Assert.False(new KaraokeWordText().ClipToBounds);
        Assert.True(new TextBlock().ClipToBounds); // the default the word must not inherit
    }

    [AvaloniaFact]
    public void HeldNoteSwell_InkLeftOfTheWordOrigin_IsDrawn()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var font = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");
        var lead = new KaraokeWordText { Text = "Why, ", FontFamily = font, FontSize = 60, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Progress = 5 };
        // Mid-hold: the "w" swells about its own centre, so its left stroke reaches past x = 0.
        var word = new KaraokeWordText
        {
            Text = "why?", FontFamily = font, FontSize = 60, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            BaseOpacity = 0.4, Progress = 0.6, IsEmphasis = true, IsLastInLine = true,
            HeldDurationMs = 2900, EmphasisMs = 1800, Lift = 0.8,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { lead, word } };
        var win = new Window { Width = 520, Height = 180, Background = Brushes.Black, Content = new Border { Padding = new Thickness(40), Child = row } };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var bmp = win.CaptureRenderedFrame()!;
            using var ms = new MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            using var sk = SkiaSharp.SKBitmap.Decode(ms);

            // The space of "Why, " sits left of the "w": any ink there is the w's own.
            var x0 = (int)Math.Round(word.TranslatePoint(new Point(0, 0), win)!.Value.X);
            int Ink(int x)
            {
                int ink = 0;
                for (int y = 0; y < sk.Height; y++) ink += sk.GetPixel(x, y).Red;
                return ink;
            }
            var left = Enumerable.Range(x0 - 4, 4).Sum(Ink);
            var first = Ink(x0);
            _o.WriteLine($"x0={x0} ink left of origin={left} first column={first}");
            Assert.True(first > 1000, $"the swollen w reaches its origin column ({first})");
            Assert.True(left > first / 2, $"the w's left stroke is cut at the word origin: {left} ink left of it, {first} on it");
        }
        finally
        {
            win.Close();
        }
    }
}
