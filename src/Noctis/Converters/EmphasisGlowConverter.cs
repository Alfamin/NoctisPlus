using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Noctis.Services;

namespace Noctis.Converters;

/// <summary>
/// Held-note glow opacity from <c>[0]=EmphasisMs</c>, <c>[1]=HeldDurationMs</c>,
/// <c>[2]=IsLastInLine</c> and <c>[3]=EmphasisCharCount</c>: the mean of AMLL's per-letter
/// glow bells (<see cref="EmphasisMotion.GlowOpacity"/>), which rise and release one letter
/// after another and peak higher the longer the note is held, times <see cref="Gain"/>.
/// 0 at rest (NaN). The glow layer only draws the sung letters, so this is how bright the
/// halo around them is.
/// </summary>
public class EmphasisGlowConverter : IMultiValueConverter
{
    /// <summary>
    /// AMLL's strength is a text-shadow level, not a layer opacity. Fitted on an Apple
    /// Music capture: late in "goodbye" (a 1.7 s last word, strength 0.13) the halo adds
    /// ~5% white right at the glyph edge and fades out ~0.25 em away. With the
    /// <see cref="EmphasisGlowBlurConverter"/> blur a headless render of the same moment
    /// matches that within ~10% at this gain.
    /// </summary>
    public const double Gain = 1.35;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var t = values.Count > 0 && values[0] is double ms ? ms : double.NaN;
        if (double.IsNaN(t)) return 0.0;
        var heldMs = values.Count > 1 && values[1] is double d ? d : 0.0;
        var isLast = values.Count > 2 && values[2] is true;
        var count = values.Count > 3 && values[3] is int n ? n : 1;
        return Opacity(t, heldMs, isLast, count);
    }

    /// <summary>Glow layer opacity (0..1) at <paramref name="tMs"/> into the held word.</summary>
    public static double Opacity(double tMs, double heldMs, bool isLast, int count)
    {
        if (double.IsNaN(tMs)) return 0;
        var glow = EmphasisMotion.GlowOpacity(tMs, count, EmphasisMotion.For(heldMs, isLast));
        return Math.Clamp(glow * Gain, 0, 1);
    }
}

/// <summary>
/// The held-note glow's blur for the glow layer's font size: a fixed fraction of the em, so
/// the halo keeps Apple's ~0.25 em reach on the lyrics page, the side panel, the mini player
/// and the smaller background-vocal rows alike (a fixed blur(6) was a tight 0.1 em rim at
/// the page size and a wide smear on the small rows).
/// </summary>
public class EmphasisGlowBlurConverter : IValueConverter
{
    /// <summary>Blur radius per em of font size.</summary>
    public const double RadiusEm = 0.45;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var size = value is double s && s > 0 ? s : 20.0;
        return new BlurEffect { Radius = Radius(size) };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Blur radius (DIPs) for a glow drawn at <paramref name="fontSize"/>.</summary>
    public static double Radius(double fontSize) => Math.Round(fontSize * RadiusEm, 1);
}
