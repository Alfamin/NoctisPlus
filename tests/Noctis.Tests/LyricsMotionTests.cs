using System.Text.RegularExpressions;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A lyric line change used to be two motions on two clocks: the line's own scale
/// transition (a 450ms fast-opening spring, started the instant it went active) and
/// the scroll glide (650–1050ms smootherstep, started a tick later, opening at zero
/// velocity). The line popped to full size before the list had visibly moved. Both
/// now share <see cref="LineMotion"/>; these pin the curve and the XAML side of it.
/// The same XAML pins guard the "lyrics lift while sung" report: the active line and
/// regular words no longer translate — only held-note emphasis words float.
/// </summary>
public class LyricsMotionTests
{
    private static readonly string[] LyricSurfaces =
    {
        Path.Combine("src", "Noctis", "Views", "LyricsView.axaml"),
        Path.Combine("src", "Noctis", "Views", "LyricsPanelView.axaml"),
        Path.Combine("src", "Noctis", "Views", "MiniPlayerWindow.axaml"),
    };

    /// <summary>
    /// Apple Music (iOS, 120 fps capture) starts each line below the active one exactly
    /// 50 ms after the line above it, on the same curve. Pinned so both lyric surfaces
    /// share the measured value; the half-travel moments of neighbouring cascade lines
    /// must then sit about one stagger apart (Apple: 50 ms) instead of the old 35.
    /// </summary>
    [Fact]
    public void Cascade_StaggersLinesByTheMeasuredAppleDelay()
    {
        Assert.Equal(50, LineMotion.CascadeDelayPerLineMs);

        const double travelPx = 131;
        const double hardStepPx = 16;
        double HalfTimeOfLine(int k)
        {
            for (var ms = 0; ms < 1500; ms++)
            {
                var baseEase = LineMotion.Ease(ms / (double)LineMotion.DurationMs);
                var prev = 0.0;
                for (var i = 1; i <= k; i++)
                {
                    var own = LineMotion.Ease((ms - i * LineMotion.CascadeDelayPerLineMs) / LineMotion.DurationMs);
                    prev = LineMotion.CascadeStep(travelPx * (baseEase - own), prev, hardStepPx);
                }
                if (travelPx * baseEase - prev >= travelPx / 2) return ms;
            }
            return double.NaN;
        }

        for (var k = 0; k < 3; k++)
        {
            var gap = HalfTimeOfLine(k + 1) - HalfTimeOfLine(k);
            Assert.InRange(gap, 40, 60);
        }
    }

    [Fact]
    public void Ease_StartsAtZero_EndsAtOne_MonotoneUntilArrival()
    {
        Assert.Equal(0, LineMotion.Ease(0));
        Assert.Equal(1, LineMotion.Ease(1));
        var prev = 0.0;
        for (var i = 1; i <= 400; i++)
        {
            var v = LineMotion.Ease(i / 400.0);
            if (v >= 0.99) break;
            Assert.True(v >= prev, $"not monotone at {i / 400.0} before arrival");
            prev = v;
        }
        // The end of the duration lands within half a pixel of a 131px line change,
        // so clamping to exactly 1 there is invisible.
        Assert.InRange(LineMotion.Ease(0.999), 1 - 0.5 / 131, 1 + 0.5 / 131);
    }

    /// <summary>
    /// Matches the Apple Music line change measured from a 120 fps capture (iOS, "Die
    /// With A Smile", line change at video 6.044 s): 5/50/90/99% of the travel at
    /// 48/181/348/448 ms and a ~2% overshoot (peak 1.020) that settles back slowly.
    /// </summary>
    [Fact]
    public void Ease_MatchesTheMeasuredAppleMusicCurve()
    {
        double Reach(double fraction)
        {
            for (var ms = 0; ms <= LineMotion.DurationMs; ms++)
                if (LineMotion.Ease(ms / (double)LineMotion.DurationMs) >= fraction) return ms;
            return double.NaN;
        }
        Assert.InRange(Reach(0.05), 48 - 15, 48 + 15);
        Assert.InRange(Reach(0.50), 181 - 15, 181 + 15);
        Assert.InRange(Reach(0.90), 348 - 15, 348 + 15);
        Assert.InRange(Reach(0.99), 448 - 15, 448 + 15);

        var peak = Enumerable.Range(0, LineMotion.DurationMs + 1)
            .Max(ms => LineMotion.Ease(ms / (double)LineMotion.DurationMs));
        Assert.InRange(peak, 1.015, 1.025);
        Assert.Equal(0.9, LineMotion.Ease(LineMotion.NinetyTravelMs / LineMotion.DurationMs), 0.001);
    }

    [Fact]
    public void Ease_AcceleratesAtOnce_ButDoesNotPop()
    {
        // Smootherstep spent the first ~25% of the glide nearly motionless (6% at p=0.15);
        // the spring is well under way by then, without a step at p→0.
        Assert.True(LineMotion.Ease(0.15) > 0.2, $"too slow to open: {LineMotion.Ease(0.15):F3}");
        Assert.True(LineMotion.Ease(0.02) < 0.02, $"opens with a step: {LineMotion.Ease(0.02):F3}");
    }

    [Fact]
    public void HalfTravel_IsWhereTheCurveCrossesOneHalf()
    {
        Assert.Equal(0.5, LineMotion.Ease(LineMotion.HalfTravelMs / LineMotion.DurationMs), 0.001);
        Assert.InRange(LineMotion.HalfTravelMs, 100, 250);
    }

    [Fact]
    public void EveryLyricSurface_UsesTheSharedCurveAndDurationForLineChanges()
    {
        var expectedDuration = $"Duration=\"0:0:{LineMotion.DurationMs / 1000.0:0.##}\"";
        foreach (var rel in LyricSurfaces)
        {
            var xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), rel));
            var lineStyle = LineButtonStyle(xaml, rel);
            Assert.Contains("helpers:LineMotionEase", lineStyle);
            Assert.Contains(expectedDuration, lineStyle);
            Assert.DoesNotContain("SpringEase", xaml);
        }
    }

    [Fact]
    public void ActiveLinesAndRegularWords_DoNotTranslate_OnlyEmphasisWordsFloat()
    {
        foreach (var rel in LyricSurfaces)
        {
            var xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), rel));
            foreach (Match m in Regex.Matches(xaml, @"translate\(0px,-\d+(?:\.\d+)?px\)"))
            {
                var selector = OwningSelector(xaml, m.Index);
                Assert.True(selector.Contains("emphasis") && selector.Contains("current"),
                    $"{rel}: lift '{m.Value}' under selector '{selector}' — only word-cell.emphasis.current may float");
            }
        }
    }

    /// <summary>The resting/active line-button style block for that surface.</summary>
    private static string LineButtonStyle(string xaml, string rel)
    {
        var selector = rel.Contains("Panel") ? "Button.panel-lyric-line-btn\""
            : rel.Contains("MiniPlayer") ? "Button.mini-lyric-line\""
            : "Button.lyric-line-btn\"";
        var start = xaml.IndexOf("<Style Selector=\"" + selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{rel}: line button style not found");
        var end = xaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        return xaml.Substring(start, end - start);
    }

    private static string OwningSelector(string xaml, int index)
    {
        var start = xaml.LastIndexOf("<Style Selector=\"", index, StringComparison.Ordinal);
        if (start < 0) return "";
        start += "<Style Selector=\"".Length;
        var end = xaml.IndexOf('"', start);
        return xaml.Substring(start, end - start);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
