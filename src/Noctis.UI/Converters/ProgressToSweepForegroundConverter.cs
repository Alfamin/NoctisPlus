using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Noctis.Controls;

namespace Noctis.Converters;

/// <summary>
/// Builds the karaoke sweep as a text <b>Foreground</b> brush from
/// <c>[0]=Progress</c>, <c>[1]=the lyrics foreground brush</c>, and optionally
/// <c>[2]=layout width</c> + <c>[3]=font size</c>: a CONSTANT-width feathered band
/// slides left-to-right with progress; its stops clamp to the word box and carry the
/// alpha the band would have at the clamped position, so the visible edges move at
/// constant velocity and the band straddles token boundaries (progress runs a
/// little past [0..1] on neighbouring words — see LyricsTimeline.DriveWordLayer).
/// Optional <c>[4]=SweepLeadIn</c> / <c>[5]=SweepTrailOut</c> bring the band in from
/// dark / out fully lit next to a rest (<see cref="Noctis.Controls.KaraokeBand.Center"/>).
/// The desktop lyrics surfaces draw with <see cref="Noctis.Controls.KaraokeWordText"/>
/// instead (a new Foreground brush per frame re-measures a TextBlock); this converter
/// serves the mobile page.
///
/// The previous collapsing feather (width = min(F, raw, 1-raw)) kept the stop
/// midpoint linear but parked the solid edge at the start of every token and rushed
/// it at 2× near the end — on multi-second held words that read as the sweep
/// stalling at every letter boundary (worst on CJK, one cell per character).
///
/// Painted as Foreground rather than OpacityMask on purpose: PushOpacityMask makes the
/// compositor render through an intermediate layer, and the sibling held-note glow
/// (an Effect visual) got composited inside that layer — multiplied by the sweep
/// gradient and hard-clipped at the overlay's bounds, which drew a cut-off box around
/// emphasis words. A gradient foreground paints the same wipe with no layer at all.
/// </summary>
public class ProgressToSweepForegroundConverter : IMultiValueConverter
{
    /// <summary>Half-width of the band as a fraction of the token — fallback when
    /// the view doesn't supply layout width + font size.</summary>
    public const double Feather = KaraokeBand.FallbackFeather;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = values.Count > 0
            ? values[0] switch
            {
                double d => d,
                float f => f,
                _ => 0.0,
            }
            : 0.0;
        var fg = values.Count > 1 ? values[1] as IBrush : null;
        var color = (fg as ISolidColorBrush)?.Color ?? Colors.White;

        // The feather is a fixed ~0.6em, not a fraction of the word (see KaraokeBand).
        var feather = Feather;
        if (values.Count > 3 && values[2] is double width && values[3] is double fontSize)
            feather = KaraokeBand.Feather(width, fontSize);

        // Optional [4]=SweepLeadIn, [5]=SweepTrailOut: next to a rest the band enters from
        // dark / leaves fully lit inside the word's own window.
        var leadIn = values.Count > 4 && values[4] is true;
        var trailOut = values.Count > 5 && values[5] is true;
        raw = KaraokeBand.Center(raw, feather, leadIn, trailOut);

        // Band fully before / fully past the token (covers the inert sentinels).
        if (raw <= -feather) return Brushes.Transparent;
        if (raw >= 1 + feather) return fg ?? Brushes.White;

        // The true band edges may hang past the word box; clamp the stops and give
        // the clamped endpoint the alpha the ramp has at that position — a stop
        // pinned at the boundary keeps *brightening* instead of parking the edge.
        var loT = raw - feather;
        var hiT = raw + feather;
        var lo = Math.Max(loT, 0.0);
        var hi = Math.Min(hiT, 1.0);
        double AlphaAt(double x) => KaraokeBand.LitAt(x, loT, hiT);

        // NOTE: this allocates a brush per evaluation, and the lyrics timer rewrites each
        // active word's Progress continuously — so this is a real per-frame allocation
        // during karaoke playback. It is NOT safe to cache a brush on the converter: the
        // converter is declared with x:Key in the mobile LyricsPage, i.e. a single
        // shared instance across every word cell, so a cached brush would be handed to
        // all of them and every word would render the last-written word's offsets.
        // Fixing it properly means moving the gradient onto the word cell itself (bind
        // GradientStop.Offset directly) rather than producing the brush here.
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(WithAlpha(color, AlphaAt(lo)), lo),
                // Same RGB throughout so the feather fades without darkening.
                new GradientStop(WithAlpha(color, AlphaAt(hi)), hi),
            },
        };
    }

    private static Color WithAlpha(Color color, double factor) =>
        Color.FromArgb((byte)Math.Round(color.A * factor), color.R, color.G, color.B);
}
