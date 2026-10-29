using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Converters;
using Noctis.Features.LyricMotion;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>
/// Native-owned Noctis text layers, animated by the original Lyric Motion letter renderer.
/// The template supplies this control directly; no plugin overlay, discovery or reflection.
/// TextBlock keeps Noctis's font resources, shaping, measurement and line-fade styles.
/// </summary>
internal sealed class MotionWordPresenter : Panel
{
    protected override bool BypassFlowDirectionPolicies => true;
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<MotionWordPresenter, string?>(nameof(Text));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextBlock.ForegroundProperty.AddOwner<MotionWordPresenter>();
    public static readonly StyledProperty<double> FontSizeProperty = TextBlock.FontSizeProperty.AddOwner<MotionWordPresenter>();
    public static readonly StyledProperty<Thickness> PaddingProperty = AvaloniaProperty.Register<MotionWordPresenter, Thickness>(nameof(Padding));
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Thickness Padding { get => GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    internal TextBlock BaseText { get; } = new() { FontWeight = FontWeight.Bold, ClipToBounds = false };
    internal TextBlock SweepText { get; } = new() { FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, ClipToBounds = false };
    private LetterLayer? _letters;
    private bool _settling, _seen;
    // Like the plugin: a word whose letters cannot be redrawn is not tried again and keeps Noctis's text.
    private bool _noLetters;
    private static bool _rehearsed;
    private double _clock = double.PositiveInfinity, _mediaMs = double.NaN, _releaseStep, _reach;
    private WordTiming? _word;
    internal WordTiming? Word { get => _word; set { if (!ReferenceEquals(_word, value)) { Release(); _word = value; } } }
    internal bool IsSettling => _settling;
    internal bool IsAtRest => _letters is null;
    internal int LayoutBuilds { get; private set; }

    public MotionWordPresenter()
    {
        ClipToBounds = false;
        BaseText.Classes.Add("word-base");
        Children.Add(BaseText); Children.Add(SweepText);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (BaseText is null) return;
        if (change.Property == TextProperty)
        {
            Release(); BaseText.Text = Text; SweepText.Text = Text?.TrimEnd();
            AutomationProperties.SetName(this, Text?.TrimEnd() ?? "");
        }
        if (change.Property == ForegroundProperty) { Release(); BaseText.Foreground = Foreground; UpdateProgress(); }
        if (change.Property == PaddingProperty) BaseText.Padding = Padding;
        if (change.Property == FontSizeProperty) Release();
    }

    /// <summary>
    /// A word read from the right, laid out as Lyric Motion 1.4.3 does: the word's own box stays
    /// left-to-right (a mirrored box, or one the letter layer inherits a direction from, puts the
    /// redrawn letters on the wrong side); only its text reads from the right, and the bright fill
    /// lies against the right edge, over the word.
    /// </summary>
    internal bool RightToLeft
    {
        get => _rightToLeft;
        set
        {
            if (_rightToLeft == value) return;
            _rightToLeft = value; Release();
            var direction = value ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            BaseText.FlowDirection = SweepText.FlowDirection = direction;
            SweepText.HorizontalAlignment = value ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
        }
    }
    private bool _rightToLeft;

    internal void UpdateProgress()
    {
        // Same Plus gradient, same pixels (PlusSweepForegroundConverter.Compute). Owner #211: the converter built a new
        // brush for every word on every frame (~4.6 KB each, a sung line churned megabytes a second into the garbage
        // collector, whose pauses hitch the motion). The numbers go straight into this word's own brush instead, and
        // nothing is written when they are unchanged. Replacing TextBlock.Foreground per frame would also invalidate
        // text measurement; mutating our own brush redraws without reshaping the text.
        var band = PlusSweepForegroundConverter.Compute(Word?.Progress ?? -2, Foreground, SweepText.Bounds.Width, BaseText.FontSize);
        if (band.Unlit) { SetSweep(Brushes.Transparent); return; }
        if (band.Lit)
        {
            var lit = Foreground ?? Brushes.White;
            if (!(lit is ISolidColorBrush solid && Sweep is ISolidColorBrush current && solid.Color == current.Color && solid.Opacity == current.Opacity))
                SetSweep(lit);
            return;
        }
        if (_sweepBrush is null)
            _sweepBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(band.LoColor, band.Lo), new GradientStop(band.HiColor, band.Hi) },
            };
        else
        {
            var stops = _sweepBrush.GradientStops;
            if (stops[0].Color != band.LoColor) stops[0].Color = band.LoColor;
            if (stops[0].Offset != band.Lo) stops[0].Offset = band.Lo;
            if (stops[1].Color != band.HiColor) stops[1].Color = band.HiColor;
            if (stops[1].Offset != band.Hi) stops[1].Offset = band.Hi;
        }
        SetSweep(_sweepBrush);
    }

    /// <summary>This word's own sweep brush (never shared, so it may be changed in place).</summary>
    private LinearGradientBrush? _sweepBrush;
    /// <summary>The word's own sweep brush, under the letter layer's transparent override while the word is redrawn
    /// (comparing with the override re-set a new brush on every frame of every redrawn word).</summary>
    private IBrush? Sweep => SweepText.GetBaseValue(TextBlock.ForegroundProperty) is { HasValue: true } own ? own.Value : null;
    private void SetSweep(IBrush brush) { if (!ReferenceEquals(Sweep, brush)) SweepText.Foreground = brush; }

    /// <summary>The resting lift of a word already sung, kept and moved rather than made anew each frame.</summary>
    private readonly TranslateTransform _lift = new();
    private void SetLift(double lift)
    {
        if (lift <= 0) { if (RenderTransform is not null) RenderTransform = null; return; }
        if (_lift.Y != -lift) _lift.Y = -lift;
        if (!ReferenceEquals(RenderTransform, _lift)) RenderTransform = _lift;
    }

    internal void Step(double mediaMs, double nowMs, double durationMs, bool last, Tuning tuning,
        double? beforeGrowth = null, double? afterGrowth = null, bool background = false)
    {
        if (Word is not { } word) return;
        var dt = double.IsFinite(_mediaMs) ? Math.Clamp((mediaMs - _mediaMs) / 1000, 0, 0.1) : 0;
        _mediaMs = mediaMs; _settling = false;
        UpdateProgress();
        if (_letters is { IsBroken: true } broken) { broken.Dispose(); _letters = null; _noLetters = true; }
        if (_letters is { } previous && (Math.Abs(previous.FontSize - BaseText.FontSize) > 0.01 || previous.IsStale))
        { previous.Dispose(); _letters = null; }
        if (_letters is null && !_noLetters && word.Progress is > -1.5 and < 1)
        {
            var split = tuning.LetterWave && (tuning.Flow || tuning.Grow(durationMs, last) >= Tuning.LetterGrowFloor);
            try { _letters = LetterLayer.TryCreate(this, BaseText, SweepText, null, tuning.Glow && durationMs >= tuning.HeldMs, split); }
            catch { _letters = null; _noLetters = true; }
            if (_letters is not null) LayoutBuilds++;
        }
        if (_letters is null)
        {
            // A previously sung word keeps the plugin's resting lift; future text remains stock.
            SetLift(word.Progress >= 1 ? Reach(tuning, background) : 0);
            return;
        }
        RenderTransform = null;
        _letters.CancelRelease();
        var seconds = Math.Max(0.001, durationMs / 1000);
        if (word.Progress >= 1) _clock = _seen ? _clock + dt : double.PositiveInfinity;
        else if (word.Progress > 0) { _seen = true; _clock = word.Progress * seconds; }
        else { _seen = false; _clock = 0; }
        _reach = Reach(tuning, background);
        var growth = tuning.Grow(durationMs, last);
        var arrive = word.Progress > 0 ? Curves.Clamp01(_clock / tuning.RiseSec(seconds)) : 0;
        _letters.Step(dt, word.Progress, _reach, growth, beforeGrowth ?? growth, afterGrowth ?? growth,
            tuning.Glow && durationMs >= tuning.HeldMs ? tuning.HeldGlow(durationMs, last) : 0,
            _clock, Tuning.SettleStartSec(seconds), tuning.Flow ? Tuning.SettleSpreadSec(seconds) : 0,
            Tuning.SettleSec(seconds), tuning.Tail, tuning.LetterRise, 1, arrive, seconds, lineActive: true);
    }

    private double Reach(Tuning tuning, bool background)
    {
        var top = TopLevel.GetTopLevel(this);
        var scale = top?.RenderScaling ?? 1;
        if (top is not null && this.TransformToVisual(top) is { } transform && transform.M11 > 0.05)
            scale *= transform.M11;
        // A whole number of screen pixels, the same for every word of the line; background vocals rise less (plugin rule).
        var reach = Math.Max(1, Math.Round(tuning.LiftEm * BaseText.FontSize * scale)) / scale;
        return background ? Math.Max(1, Math.Round(reach * 0.6 * scale)) / scale : reach;
    }

    /// <summary>
    /// The plugin's once-per-session rehearsal: the first redraw of a session is slow (code made
    /// ready on first use), so one waiting word is taken through it unseen before anything is sung.
    /// </summary>
    internal void Rehearse(Tuning tuning)
    {
        if (_rehearsed || _letters is not null || Bounds.Width <= 0) return;
        _rehearsed = true;
        try
        {
            using var letters = LetterLayer.TryCreate(this, BaseText, SweepText, null, tuning.Glow, tuning.LetterWave);
            letters?.Rehearse();
        }
        catch { }
        UpdateProgress();
    }

    internal void BeginHandover(double nowMs)
    {
        _settling = _letters is not null; _releaseStep = nowMs;
        _mediaMs = double.NaN; RenderTransform = null;
    }

    internal void Settle(double nowMs)
    {
        if (!_settling || _letters is null) return;
        if (!double.IsFinite(_releaseStep)) _releaseStep = nowMs;
        var done = _letters.Release(Math.Max(0, nowMs - _releaseStep) / 1000, _reach);
        _releaseStep = nowMs;
        if (done) { _letters.Dispose(); _letters = null; _settling = false; }
    }

    internal void Freeze() { _mediaMs = double.NaN; _releaseStep = double.NaN; }
    internal void ResetPose() { Release(); UpdateProgress(); }
    internal void SetLineActive(bool active) { }
    internal void Release()
    {
        _letters?.Dispose(); _letters = null; _noLetters = false;
        _settling = false; _seen = false; _clock = double.PositiveInfinity; _mediaMs = double.NaN;
        RenderTransform = null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WordPeer(this);
    private sealed class WordPeer(MotionWordPresenter owner) : ControlAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TextBlock);
        protected override string GetNameCore() => owner.Text?.TrimEnd() ?? "";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}
