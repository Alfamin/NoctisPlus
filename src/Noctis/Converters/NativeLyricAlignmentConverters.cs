using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Noctis.Features.LyricMotion;
using Noctis.Models;

namespace Noctis.Converters;

/// <summary>
/// Duet anchors take precedence; otherwise, as in Lyric Motion 1.4.3, every line of a right-to-left
/// song sits against the right edge. A right-to-left line in a left-to-right song stays on the left
/// (its words run from the right inside it).
/// </summary>
public sealed class NativeLyricAlignmentConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var voice = values.Count > 0 && values[0] is LyricVoice v ? v : LyricVoice.Default;
        if (voice == LyricVoice.Group) return HorizontalAlignment.Center;
        if (voice == LyricVoice.Voice2) return HorizontalAlignment.Right;
        return values.Count >= 4 && values[2] is true && values[3] is true
            ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
