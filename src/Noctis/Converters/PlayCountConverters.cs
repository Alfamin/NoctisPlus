using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>The word after a play count is singular at one: rows read "1 plays".</summary>
public static class PlayCountConverters
{
    public static readonly IValueConverter IsOne = new FuncValueConverter<int, bool>(n => n == 1);
    public static readonly IValueConverter IsNotOne = new FuncValueConverter<int, bool>(n => n != 1);
}
