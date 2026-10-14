using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Mobile.Services;

namespace Noctis.Mobile.Views;

/// <summary>
/// The playing row's equalizer, the desktop's row indicator (src/Noctis/Controls/EqVisualizer,
/// which the phone cannot reference) redrawn here with its numbers: five bars
/// <see cref="BarWidth"/> wide and <see cref="BarSpacing"/> apart in a <see cref="RowHeight"/>
/// row, rounded by half their width, each a sine between <see cref="BarMin"/> and
/// <see cref="BarMax"/> at its own <see cref="Frequencies"/> and <see cref="Phases"/>; on pause they ease flat (<see cref="FlatHeight"/>) over
/// <see cref="FlattenDuration"/>, cubic ease-out. While audio is heard the bars follow it as the
/// desktop's do: the sway, a bounce on every beat and a tilt by the spectrum (bass left, treble
/// right), <see cref="LiveLevels"/>, eased per bar with a fast attack and a release that falls
/// between beats (<see cref="Smooth"/>). The pulse and bands come from <see cref="Levels"/>,
/// the meters the Android player's PCM tap feeds; with nothing feeding them (tests, the first
/// frames of a track) the sines alone run, the desktop's no-tap motion.
/// One control draws all five bars: a tick repaints, never re-runs layout.
/// Stepped on the frame clock (TopLevel.RequestAnimationFrame, like the lyrics page), not the
/// desktop's 33 ms Render-priority DispatcherTimer: on the Android emulator that timer pinned
/// the UI thread at 100% CPU as soon as a track played, and the app stopped drawing and taking
/// taps (device run, 2026-10-01). Every frame resamples the same time-based curves: a frame
/// that drew nothing new let the frame chain lapse (headless harness), so none is skipped.
/// Paused (once flat), detached or hidden, no frame is asked for; while an ancestor hides the
/// bars a <see cref="HiddenPollInterval"/> one-shot checks back.
/// </summary>
public sealed class PlayingBars : Control
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<PlayingBars, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<PlayingBars, IBrush?>(nameof(Fill));

    /// <summary>The desktop EqVisualizer's template and constants (tests hold the two together).</summary>
    public const int BarCount = 5;
    public const double BarWidth = 1.75, BarSpacing = 1.75, RowHeight = 12;
    public const double FlatHeight = 1.75, BarMin = 2.25, BarMax = 10.0;
    /// <summary>The desktop EqBar's corner radius: half a bar's width.</summary>
    public const double Radius = 0.875;
    public static readonly IReadOnlyList<double> Phases = new[] { 0.0, 1.2, 2.4, 0.8, 1.8 };
    public static readonly IReadOnlyList<double> Frequencies = new[] { 1.6, 2.0, 1.4, 1.8, 1.7 };
    public static readonly TimeSpan HiddenPollInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan FlattenDuration = TimeSpan.FromMilliseconds(420);
    private static readonly Easing FlattenEasing = new CubicEaseOut();

    /// <summary>The desktop EqVisualizer's live mix (tests hold the two together): level =
    /// Rest + Sway·sine + Bounce·beat pulse + Tone·(band − mean band), eased per bar with a
    /// <see cref="LiveAttackMs"/> attack and a <see cref="LiveReleaseMs"/> release.</summary>
    public const double LiveRest = 0.12, LiveSway = 0.28, LiveBounce = 0.60, LiveTone = 0.60;
    public const double LiveAttackMs = 25, LiveReleaseMs = 120;

    private readonly double[] _heights = { FlatHeight, FlatHeight, FlatHeight, FlatHeight, FlatHeight };
    private readonly double[] _flattenFrom = new double[BarCount];
    private readonly float[] _bands = new float[BarCount];
    private readonly float[] _liveTarget = new float[BarCount];
    private readonly float[] _liveShown = new float[BarCount];
    private DateTime _start, _flattenStart, _lastTick;
    private bool _flattening, _running, _frameQueued;
    /// <summary>Bumped by every start and stop, so a queued frame or poll from an older run drops out.</summary>
    private int _generation;

    static PlayingBars()
    {
        AffectsRender<PlayingBars>(FillProperty);
        IsPlayingProperty.Changed.AddClassHandler<PlayingBars>((c, _) => c.OnPlayingChanged());
        // Rows flip IsVisible to show the bars on the player's track only; a hidden one parks.
        IsVisibleProperty.Changed.AddClassHandler<PlayingBars>((c, _) => c.OnVisibleChanged());
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Where the live pulse and bands come from; null runs the sines alone.</summary>
    public RowEqLevels? Levels { get; set; } = RowEqLevels.Shared;

    /// <summary>The current bar heights, left to right (tests read them).</summary>
    internal IReadOnlyList<double> BarHeights => _heights;

    /// <summary>A frame is asked for: playing, or easing flat (tests check it idles).</summary>
    internal bool IsRunning => _running;

    /// <summary>A bar's height <paramref name="t"/> seconds into the motion: the desktop's
    /// sine mapped to <see cref="BarMin"/> … <see cref="BarMax"/>.</summary>
    public static double OscillationHeight(double t, int bar)
    {
        var s = Math.Sin(2 * Math.PI * Frequencies[bar] * t + Phases[bar]);
        return BarMin + (BarMax - BarMin) * (s * 0.5 + 0.5);
    }

    /// <summary>Bar height for a 0..1 level: the range the sines use (EqVisualizer.HeightForLevel).</summary>
    public static double HeightForLevel(double level) => BarMin + (BarMax - BarMin) * Math.Clamp(level, 0, 1);

    /// <summary>
    /// Target 0..1 levels for the five bars <paramref name="t"/> seconds in, from the beat
    /// <paramref name="pulse"/> (0..1) and five bands (0..1, bass to treble): the desktop's
    /// EqVisualizer.LiveLevels.
    /// </summary>
    public static void LiveLevels(double t, double pulse, ReadOnlySpan<float> bands, Span<float> levels)
    {
        double mean = 0;
        for (var i = 0; i < BarCount; i++) mean += bands[i];
        mean /= BarCount;
        for (var i = 0; i < BarCount; i++)
        {
            var osc = Math.Sin(2 * Math.PI * Frequencies[i] * t + Phases[i]) * 0.5 + 0.5;
            var level = LiveRest + LiveSway * osc + LiveBounce * pulse + LiveTone * (bands[i] - mean);
            levels[i] = (float)Math.Clamp(level, 0, 1);
        }
    }

    /// <summary>Eases <paramref name="shown"/> toward <paramref name="target"/> over
    /// <paramref name="dtMs"/>: up with the attack, down with the release time constant (the
    /// desktop's SpectrumVisualizer.Smooth).</summary>
    public static void Smooth(Span<float> shown, ReadOnlySpan<float> target, double dtMs, double attackMs, double releaseMs)
    {
        var up = (float)(1 - Math.Exp(-dtMs / attackMs));
        var down = (float)(1 - Math.Exp(-dtMs / releaseMs));
        for (var i = 0; i < shown.Length; i++)
        {
            var t = target[i];
            var s = shown[i];
            shown[i] = t > s ? s + (t - s) * up : s + (t - s) * down;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(BarCount * BarWidth + (BarCount - 1) * BarSpacing, RowHeight);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Recycled rows re-attach without an IsPlaying change; restart, or the bars come back frozen.
        if (IsPlaying && IsVisible) Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _flattening = false;
        Stop();
    }

    private void OnPlayingChanged()
    {
        if (IsPlaying)
        {
            _flattening = false;
            if (IsVisible) Start();
        }
        else if (IsVisible && VisualRoot != null)
        {
            Array.Copy(_heights, _flattenFrom, BarCount);
            _flattenStart = DateTime.UtcNow;
            _flattening = true;
            Run();
        }
        else Park();
    }

    private void OnVisibleChanged()
    {
        if (IsVisible && IsPlaying)
        {
            _flattening = false;
            Start();
        }
        else if (!IsVisible) Park();
    }

    private void Start()
    {
        if (VisualRoot == null) return;
        _start = DateTime.UtcNow;
        _lastTick = _start;
        Run();
    }

    /// <summary>Flat and still: nothing to ease out where it cannot be seen.</summary>
    private void Park()
    {
        _flattening = false;
        Stop();
        SetAll(FlatHeight);
    }

    private void Stop()
    {
        _running = false;
        _generation++;
    }

    /// <summary>A fresh run of frames (the old one, if any, drops out at its next frame).</summary>
    private void Run()
    {
        var generation = ++_generation;
        _running = true;
        QueueFrame(generation);
    }

    private void QueueFrame(int generation)
    {
        if (_frameQueued || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameQueued = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameQueued = false;
            // A newer run (pause → flatten, play again) that asked while this frame was queued is
            // served by it: re-queueing instead drew nothing this frame, and in the headless
            // harness a frame that draws nothing is the last one (the flatten never ran).
            if (generation == _generation || _running) OnFrame(_generation);
        });
    }

    private void OnFrame(int generation)
    {
        if (VisualRoot == null) { _flattening = false; Stop(); return; }
        if (!IsEffectivelyVisible)
        {
            // An ancestor hides the row: land flat when there is nothing to show, else stop
            // drawing and look again in a while.
            if (_flattening || !IsPlaying) { Park(); return; }
            DispatcherTimer.RunOnce(() => { if (generation == _generation) QueueFrame(generation); }, HiddenPollInterval);
            return;
        }

        var now = DateTime.UtcNow;
        if (_flattening)
        {
            var progress = (now - _flattenStart).TotalMilliseconds / FlattenDuration.TotalMilliseconds;
            if (progress >= 1) { Park(); return; }
            var eased = FlattenEasing.Ease(progress);
            for (var i = 0; i < BarCount; i++) _heights[i] = _flattenFrom[i] + (FlatHeight - _flattenFrom[i]) * eased;
            InvalidateVisual();
            QueueFrame(generation);
            return;
        }

        var dtMs = Math.Clamp((now - _lastTick).TotalMilliseconds, 0, 100);
        _lastTick = now;
        var t = (now - _start).TotalSeconds;
        if (Levels is { } levels && levels.TryRead(levels.NowMs, out var pulse, _bands))
        {
            LiveLevels(t, pulse, _bands, _liveTarget);
            Smooth(_liveShown, _liveTarget, dtMs, LiveAttackMs, LiveReleaseMs);
            for (var i = 0; i < BarCount; i++) _heights[i] = HeightForLevel(_liveShown[i]);
        }
        else
        {
            Array.Clear(_liveShown);
            for (var i = 0; i < BarCount; i++) _heights[i] = OscillationHeight(t, i);
        }
        InvalidateVisual();
        QueueFrame(generation);
    }

    private void SetAll(double height)
    {
        for (var i = 0; i < BarCount; i++) _heights[i] = height;
        InvalidateVisual();
    }

    /// <summary>
    /// Bar <paramref name="bar"/>'s rect at <paramref name="height"/>, as the desktop's laid-out
    /// bars land: widths and offsets on the pixel grid, the height rounded up to it and centred
    /// in the row (EqBar.BarRectIn). A <paramref name="scale"/> of 0 skips the rounding.
    /// </summary>
    internal static Rect BarRect(int bar, double height, double scale)
    {
        double Round(double v) => scale > 0 ? LayoutHelper.RoundLayoutValue(v, scale) : v;
        var width = Round(BarWidth);
        var x = Round(bar * (width + BarSpacing));
        var h = scale > 0 ? LayoutHelper.RoundLayoutValueUp(height, scale) : height;
        var row = scale > 0 ? LayoutHelper.RoundLayoutValueUp(RowHeight, scale) : RowHeight;
        h = Math.Min(h, row);
        return new Rect(x, Round((row - h) / 2), width, h);
    }

    public override void Render(DrawingContext context)
    {
        if (Fill is not { } fill) return;
        var scale = UseLayoutRounding ? LayoutHelper.GetLayoutScale(this) : 0;
        for (var i = 0; i < BarCount; i++)
        {
            var rect = BarRect(i, _heights[i], scale);
            if (rect.Height <= 0) continue;
            context.DrawRectangle(fill, null, rect, Radius, Radius);
        }
    }
}
