using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Noctis.Mobile.Views;

/// <summary>
/// A glyph drawn at its own 24-unit design grid, scaled to the control's size. PathIcon instead
/// stretches each glyph's bounds to fill its box, so Noctis.UI's icons — all on the 24 grid but
/// each cropped differently (the moon 9.9 wide, the folder 20, sync 22.5) — came out at
/// different visual sizes and offsets side by side. Here one dp of box is always 1/24 of the
/// grid, so every icon in a column has the same size and the same optical centre.
/// </summary>
public sealed class GridIcon : Control
{
    /// <summary>The design grid every phone and Noctis.UI glyph is drawn on.</summary>
    public const double GridSize = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<GridIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<GridIcon>();

    static GridIcon()
    {
        AffectsRender<GridIcon>(DataProperty, ForegroundProperty);
        AffectsMeasure<GridIcon>(DataProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsNaN(Width) ? GridSize : Width,
        double.IsNaN(Height) ? GridSize : Height);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush) return;
        // Uniform: a non-square box keeps the grid square and centres it.
        var scale = Math.Min(Bounds.Width, Bounds.Height) / GridSize;
        var dx = (Bounds.Width - GridSize * scale) / 2;
        var dy = (Bounds.Height - GridSize * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(brush, null, data);
    }
}
