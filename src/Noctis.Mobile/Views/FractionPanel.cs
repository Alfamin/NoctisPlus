using Avalonia;
using Avalonia.Controls;

namespace Noctis.Mobile.Views;

/// <summary>
/// Lays its children out over the left <see cref="Fraction"/> of its width (the mini player's
/// elapsed fill): a re-arrange per position tick, no transform for a view model to own.
/// </summary>
public sealed class FractionPanel : Panel
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<FractionPanel, double>(nameof(Fraction));

    static FractionPanel() => AffectsArrange<FractionPanel>(FractionProperty);

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width * Math.Clamp(double.IsNaN(Fraction) ? 0 : Fraction, 0, 1);
        foreach (var child in Children)
            child.Arrange(new Rect(0, 0, width, finalSize.Height));
        return finalSize;
    }
}
