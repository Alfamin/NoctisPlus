// Original Lyric Motion 1.4.3 fill reader (MIT).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace Noctis.Features.LyricMotion;
internal sealed class FillReader
{
    private IBrush? _source;
    private double _signature = double.NaN;
    private IBrush? _result;

    /// <summary>True when the last brush read was a sweep that had to be turned round.</summary>
    public bool IsTurned { get; private set; }

    public IBrush? Read(TextBlock sweep, bool fromRight, out bool changed)
    {
        var source = sweep.GetBaseValue(TextBlock.ForegroundProperty) is { HasValue: true } value ? value.Value : null;

        double signature = 0;
        var gradient = source as ILinearGradientBrush;
        if (gradient is not null)
        {
            signature = gradient.GradientStops.Count;
            foreach (var stop in gradient.GradientStops)
                signature = signature * 31 + stop.Offset * 1000 + stop.Color.A;
        }
        else if (source is ISolidColorBrush solid) signature = solid.Color.ToUInt32() + solid.Opacity;

        changed = !ReferenceEquals(source, _source) || signature != _signature;
        if (!changed) return _result;
        _source = source;
        _signature = signature;

        IsTurned = fromRight && gradient is not null;
        if (gradient is null) return _result = source;

        // Always a brush of our own: a new one each time the fill has moved on, so
        // whatever shows it is redrawn the same way in both directions.
        var turned = new LinearGradientBrush
        {
            StartPoint = fromRight ? Turned(gradient.StartPoint) : gradient.StartPoint,
            EndPoint = fromRight ? Turned(gradient.EndPoint) : gradient.EndPoint,
            SpreadMethod = gradient.SpreadMethod,
            Opacity = gradient.Opacity,
        };
        foreach (var stop in gradient.GradientStops)
            turned.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
        return _result = turned;
    }

    private static RelativePoint Turned(RelativePoint point) => point.Unit == RelativeUnit.Relative
        ? new RelativePoint(1 - point.Point.X, point.Point.Y, RelativeUnit.Relative)
        : point;
}

