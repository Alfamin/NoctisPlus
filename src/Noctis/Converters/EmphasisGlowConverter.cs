using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Services;

namespace Noctis.Converters;

/// <summary>
/// Held-note glow opacity from <c>[0]=EmphasisMs</c>, <c>[1]=HeldDurationMs</c>,
/// <c>[2]=IsLastInLine</c> and <c>[3]=EmphasisCharCount</c>: the mean of AMLL's per-letter
/// glow bells (<see cref="EmphasisMotion.GlowOpacity"/>), which rise and release one letter
/// after another and peak higher the longer the note is held. 0 at rest (NaN).
/// </summary>
public class EmphasisGlowConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var t = values.Count > 0 && values[0] is double ms ? ms : double.NaN;
        if (double.IsNaN(t)) return 0.0;
        var heldMs = values.Count > 1 && values[1] is double d ? d : 0.0;
        var isLast = values.Count > 2 && values[2] is true;
        var count = values.Count > 3 && values[3] is int n ? n : 1;
        return EmphasisMotion.GlowOpacity(t, count, EmphasisMotion.For(heldMs, isLast));
    }
}
