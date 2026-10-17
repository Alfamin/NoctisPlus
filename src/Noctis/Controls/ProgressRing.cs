using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// A thin circular progress indicator (Search: a song downloading in place of its download
/// button). The arc eases to each new value instead of jumping; with
/// <see cref="IsIndeterminate"/> a short arc turns slowly (waiting in the queue).
/// </summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 2);

    /// <summary>The drawn fraction; follows <see cref="Value"/> through a short transition.</summary>
    private static readonly StyledProperty<double> ShownProperty =
        AvaloniaProperty.Register<ProgressRing, double>("Shown");

    /// <summary>Rotation of the indeterminate arc, 0..1 turns.</summary>
    private static readonly StyledProperty<double> SpinProperty =
        AvaloniaProperty.Register<ProgressRing, double>("Spin");

    private readonly Animation _spin = new()
    {
        Duration = TimeSpan.FromSeconds(1.1),
        IterationCount = new IterationCount(1),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Avalonia.Styling.Setter(SpinProperty, 0.0) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Avalonia.Styling.Setter(SpinProperty, 1.0) } },
        },
    };
    private CancellationTokenSource? _spinning;
    internal Task SpinTask { get; private set; } = Task.CompletedTask;

    static ProgressRing()
    {
        AffectsRender<ProgressRing>(ShownProperty, SpinProperty, ForegroundProperty, ThicknessProperty, IsIndeterminateProperty);
    }

    public ProgressRing()
    {
        Transitions = new Transitions { new DoubleTransition { Property = ShownProperty, Duration = TimeSpan.FromMilliseconds(220) } };
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty) SetValue(ShownProperty, Math.Clamp(Value, 0, 1));
        else if (change.Property == IsIndeterminateProperty || change.Property == IsVisibleProperty || change.Property.Name == nameof(IsEffectivelyVisible)) UpdateSpin();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateSpin();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _spinning?.Cancel();
        _spinning?.Dispose();
        _spinning = null;
    }

    private void UpdateSpin()
    {
        var want = IsIndeterminate && IsEffectivelyVisible && VisualRoot is not null;
        if (want == _spinning is not null) return;
        if (!want) { _spinning?.Cancel(); _spinning?.Dispose(); _spinning = null; return; }
        _spinning = new CancellationTokenSource();
        SpinTask = SpinAsync(_spinning.Token);
    }

    private async Task SpinAsync(CancellationToken ct)
    {
        try
        {
            // Avalonia 12 rejects infinite RunAsync animations. A cancellable sequence of
            // finite turns uses its public clock API and leaves no unobserved faulted task.
            while (!ct.IsCancellationRequested) await _spin.RunAsync(this, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var brush = Foreground ?? Brushes.White;
        var t = Thickness;
        var radius = (size - t) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        // The track: the same brush, faint, so the ring reads on any row fill.
        using (context.PushOpacity(0.22))
            context.DrawEllipse(null, new Pen(brush, t), center, radius, radius);
        double start, sweep;
        if (IsIndeterminate) { start = GetValue(SpinProperty) * 360; sweep = 90; }
        else { start = 0; sweep = Math.Clamp(GetValue(ShownProperty), 0, 1) * 360; }
        if (sweep <= 0.5) return;
        var pen = new Pen(brush, t, lineCap: PenLineCap.Round);
        if (sweep >= 359.5) { context.DrawEllipse(null, pen, center, radius, radius); return; }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var a0 = (start - 90) * Math.PI / 180;
            var a1 = (start + sweep - 90) * Math.PI / 180;
            g.BeginFigure(new Point(center.X + radius * Math.Cos(a0), center.Y + radius * Math.Sin(a0)), false);
            g.ArcTo(new Point(center.X + radius * Math.Cos(a1), center.Y + radius * Math.Sin(a1)), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
