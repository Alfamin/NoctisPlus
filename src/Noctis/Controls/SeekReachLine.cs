using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// Owner #204, mini player: while a song streams, how far a seek can reach — the played line's
/// colour, faint, from the start to <see cref="Fraction"/>. Purely a visual over the mini seek
/// Slider (not hit-testable). Draws nothing for a song on disk (1) or one that cannot seek (0).
///
/// Same mapping as <see cref="WaveformSeekBar"/>: the thumb centre travels
/// <see cref="TrackInset"/>..width−TrackInset, and the mini player's line runs edge to edge.
/// </summary>
public sealed class SeekReachLine : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<SeekReachLine, double>(nameof(Fraction), 1);

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<SeekReachLine, IBrush?>(nameof(Brush), Brushes.White);

    public static readonly StyledProperty<double> TrackInsetProperty =
        AvaloniaProperty.Register<SeekReachLine, double>(nameof(TrackInset), 6);

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<SeekReachLine, double>(nameof(LineThickness), 4);

    public double Fraction { get => GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double TrackInset { get => GetValue(TrackInsetProperty); set => SetValue(TrackInsetProperty, value); }
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    static SeekReachLine()
    {
        AffectsRender<SeekReachLine>(FractionProperty, BrushProperty, TrackInsetProperty, LineThicknessProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<SeekReachLine>(false);
    }

    /// <summary>The drawn line's width for a control <paramref name="width"/> wide; 0 = nothing drawn.</summary>
    public static double ReachWidth(double fraction, double width, double inset)
    {
        if (!(fraction > 0 && fraction < 1) || width <= 0) return 0;
        var span = Math.Max(0, width - 2 * inset);
        return Math.Clamp(inset + span * fraction, 0, width);
    }

    public override void Render(DrawingContext context)
    {
        var width = ReachWidth(Fraction, Bounds.Width, TrackInset);
        if (width <= 0 || Brush is not { } brush) return;
        var thickness = Math.Min(LineThickness, Bounds.Height);
        var top = (Bounds.Height - thickness) / 2;
        context.DrawRectangle(brush, null, new RoundedRect(new Rect(0, top, width, thickness), thickness / 2));
    }
}
