using Avalonia;
using Avalonia.Controls;

namespace Noctis.Mobile.Views;

/// <summary>
/// Rows of <see cref="Columns"/> equal cells across the width, the last row's cells centred
/// under the full ones (the accent swatches: five over four, no ragged row).
/// </summary>
public sealed class CenteredRowsPanel : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<CenteredRowsPanel, int>(nameof(Columns), 5);

    static CenteredRowsPanel() => AffectsMeasure<CenteredRowsPanel>(ColumnsProperty);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private int Cols => Math.Max(1, Columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var cell = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / Cols;
        double widest = 0, rowHeight = 0;
        foreach (var child in visible)
        {
            child.Measure(new Size(cell, double.PositiveInfinity));
            widest = Math.Max(widest, child.DesiredSize.Width);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        var rows = (visible.Count + Cols - 1) / Cols;
        var width = double.IsInfinity(cell) ? widest * Math.Min(Cols, visible.Count) : availableSize.Width;
        return new Size(width, rows * rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        if (visible.Count == 0) return finalSize;
        var rows = (visible.Count + Cols - 1) / Cols;
        var cell = finalSize.Width / Cols;
        var rowHeight = finalSize.Height / rows;
        for (var i = 0; i < visible.Count; i++)
        {
            var row = i / Cols;
            var inRow = Math.Min(Cols, visible.Count - row * Cols);
            var left = (finalSize.Width - inRow * cell) / 2;
            visible[i].Arrange(new Rect(left + (i % Cols) * cell, row * rowHeight, cell, rowHeight));
        }
        return finalSize;
    }
}
