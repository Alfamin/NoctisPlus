using Avalonia;
using Avalonia.Controls;
using Noctis.Features.LyricMotion;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>Native wrapping, with RTL word order and intact English runs inside an RTL line.</summary>
public sealed class LyricWordWrapPanel : WrapPanel
{
    public static readonly StyledProperty<LyricLine?> LineProperty = AvaloniaProperty.Register<LyricWordWrapPanel, LyricLine?>(nameof(Line));
    public LyricLine? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty || change.Property == MotionWordCell.RightToLeftEnabledProperty
            || change.Property == MotionWordCell.RightToLeftSongProperty) InvalidateArrange();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (!MotionWordCell.GetRightToLeftEnabled(this)
            || !Script.FlowsRightToLeft(Line?.Text, MotionWordCell.GetRightToLeftSong(this))) return result;
        // No per-frame work: only wrapping/resizing or a change of settings reaches here.
        var rows = Children.Where(c => c.IsVisible).GroupBy(c => Math.Round(c.Bounds.Y, 1));
        foreach (var row in rows)
        {
            var cells = row.ToArray();
            var width = cells.Sum(c => c.Bounds.Width);
            var right = ItemsAlignment == WrapPanelItemsAlignment.Center ? (finalSize.Width + width) / 2 : finalSize.Width;
            for (var i = 0; i < cells.Length;)
            {
                if (Direction(cells[i]) != TextDirection.LeftToRight)
                {
                    var box = cells[i].Bounds;
                    right -= box.Width;
                    cells[i].Arrange(new Rect(right, box.Y, box.Width, box.Height));
                    i++;
                    continue;
                }
                var end = i + 1;
                while (end < cells.Length && Direction(cells[end]) is TextDirection.LeftToRight or TextDirection.Neutral) end++;
                var runWidth = 0.0;
                for (var k = i; k < end; k++) runWidth += cells[k].Bounds.Width;
                var x = right - runWidth;
                for (var k = i; k < end; k++)
                {
                    var box = cells[k].Bounds;
                    cells[k].Arrange(new Rect(x, box.Y, box.Width, box.Height));
                    x += box.Width;
                }
                right -= runWidth;
                i = end;
            }
        }
        return result;
    }

    private static TextDirection Direction(Control cell) => Script.Of((cell.DataContext as WordTiming)?.Text);
}
