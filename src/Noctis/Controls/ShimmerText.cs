using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// A loading line that says "working" without a spinner's worth of text (owner 10-18): the lyrics
/// sweep's soft bright band slides left to right across the text, on a loop, while it is visible.
/// Painted as a gradient Foreground (no opacity-mask layer), like ProgressToSweepForegroundConverter.
/// Ticks only while attached and visible, from the window's frame clock.
/// </summary>
public class ShimmerText : TextBlock
{
    /// <summary>One sweep across the text, then a short rest.</summary>
    private const double SweepSeconds = 1.3, RestSeconds = 0.45, Band = 0.28;

    private TopLevel? _top;
    private bool _running;
    private TimeSpan? _start;
    private IBrush? _base;

    /// <summary>Sweep only while this is true (bind it to the loading flag; a failure line stays still).</summary>
    public static readonly StyledProperty<bool> IsShimmeringProperty =
        AvaloniaProperty.Register<ShimmerText, bool>(nameof(IsShimmering), true);

    public bool IsShimmering
    {
        get => GetValue(IsShimmeringProperty);
        set => SetValue(IsShimmeringProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _running = false; _top = null;
        Restore();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == IsShimmeringProperty)
        {
            if (IsEffectivelyVisible && IsShimmering) Start();
            else { _running = false; Restore(); }
        }
    }

    private void Start()
    {
        if (_running || _top is null || !IsEffectivelyVisible || !IsShimmering) return;
        _running = true; _start = null;
        _base = Foreground;
        _top.RequestAnimationFrame(Tick);
    }

    private void Restore()
    {
        // Back to the inherited/themed colour (the sweep only ever set a current value).
        if (_base is not null) ClearValue(ForegroundProperty);
        _base = null;
    }

    private void Tick(TimeSpan now)
    {
        if (!_running || _top is null) return;
        _start ??= now;
        var t = (now - _start.Value).TotalSeconds % (SweepSeconds + RestSeconds);
        var color = (_base as ISolidColorBrush)?.Color ?? Colors.White;
        var dim = Color.FromArgb((byte)(color.A * 0.55), color.R, color.G, color.B);
        // Band centre runs from just before the text to just past it, then rests off the end.
        var x = t >= SweepSeconds ? 1 + Band : -Band + (1 + 2 * Band) * Ease(t / SweepSeconds);
        SetCurrentValue(ForegroundProperty, new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(dim, Math.Clamp(x - Band, 0, 1)),
                new GradientStop(color, Math.Clamp(x, 0, 1)),
                new GradientStop(dim, Math.Clamp(x + Band, 0, 1)),
            },
        });
        _top.RequestAnimationFrame(Tick);
    }

    private static double Ease(double p) => p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2;
}
