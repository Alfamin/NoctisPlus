using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Noctis.Features.LyricMotion;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>
/// Word-cell template host. The original template is its fallback child; the native painter
/// takes its place when the feature is enabled. Layout belongs to the lyrics template.
/// </summary>
public sealed class MotionWordCell : Panel
{
    internal static readonly AttachedProperty<LyricMotionScope?> ScopeProperty =
        AvaloniaProperty.RegisterAttached<MotionWordCell, Control, LyricMotionScope?>("Scope", inherits: true);
    public static readonly AttachedProperty<bool> RightToLeftEnabledProperty =
        AvaloniaProperty.RegisterAttached<MotionWordCell, Control, bool>("RightToLeftEnabled", inherits: true);
    public static readonly AttachedProperty<bool> RightToLeftSongProperty =
        AvaloniaProperty.RegisterAttached<MotionWordCell, Control, bool>("RightToLeftSong", inherits: true);
    public static bool GetRightToLeftEnabled(Control control) => control.GetValue(RightToLeftEnabledProperty);
    public static void SetRightToLeftEnabled(Control control, bool value) => control.SetValue(RightToLeftEnabledProperty, value);
    public static bool GetRightToLeftSong(Control control) => control.GetValue(RightToLeftSongProperty);
    public static void SetRightToLeftSong(Control control, bool value) => control.SetValue(RightToLeftSongProperty, value);
    public static readonly StyledProperty<WordTiming?> WordProperty = AvaloniaProperty.Register<MotionWordCell, WordTiming?>(nameof(Word));
    public static readonly StyledProperty<LyricLine?> LineProperty = AvaloniaProperty.Register<MotionWordCell, LyricLine?>(nameof(Line));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextBlock.ForegroundProperty.AddOwner<MotionWordCell>();
    public static readonly StyledProperty<IDataTemplate?> FallbackTemplateProperty = AvaloniaProperty.Register<MotionWordCell, IDataTemplate?>(nameof(FallbackTemplate));
    private readonly Viewbox _nativeBox;
    private Control? _fallback;
    internal MotionWordPresenter Presenter { get; } = new();
    internal WordTiming? BeforeWord { get; set; }
    internal WordTiming? AfterWord { get; set; }
    internal bool IsLastWord { get; set; }
    internal bool AfterIsLast { get; set; }
    internal bool IsBackground { get; set; }
    private LyricMotionScope? _scope;
    private WordTiming? _observedWord;
    private bool _attached, _native;
    public WordTiming? Word { get => GetValue(WordProperty); set => SetValue(WordProperty, value); }
    public LyricLine? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IDataTemplate? FallbackTemplate { get => GetValue(FallbackTemplateProperty); set => SetValue(FallbackTemplateProperty, value); }

    public MotionWordCell()
    {
        ClipToBounds = false;
        _nativeBox = new Viewbox { Child = Presenter, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, ClipToBounds = false };
        Children.Add(_nativeBox);
        UpdateVisibility();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Rebind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _scope?.Unregister(this);
        _scope = null;
        if (_observedWord is not null) _observedWord.PropertyChanged -= OnWordChanged;
        _observedWord = null;
        Presenter.Release();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WordProperty || change.Property == LineProperty || change.Property == ScopeProperty)
        {
            if (_attached) Rebind();
            else UpdateWord();
        }
        else if (change.Property == ForegroundProperty) Presenter.Foreground = Foreground;
        else if (change.Property == FallbackTemplateProperty) UpdateVisibility();
        else if (change.Property == RightToLeftSongProperty || change.Property == RightToLeftEnabledProperty) UpdatePadding();
    }

    /// <summary>
    /// Noctis leaves room after a line's last word (padding on the right of its text). The plugin's
    /// right-to-left rules (Lyric Motion 1.4.3 LayerFlow): for a word read from the right "after" is on
    /// its left; where that is the edge the line sits against (a right-to-left line in a left-to-right
    /// song), and for a left-to-right line in a right-to-left song, the room is dropped.
    /// </summary>
    private void UpdatePadding()
    {
        var final = Word?.Text is { Length: > 0 } text && !char.IsWhiteSpace(text[^1]);
        if (!final) { Presenter.Padding = default; return; }
        var enabled = GetValue(RightToLeftEnabledProperty);
        var song = enabled && GetValue(RightToLeftSongProperty);
        var line = enabled && Script.FlowsRightToLeft(Line?.Text, song);
        if (enabled && Script.Of(Word?.Text) == TextDirection.RightToLeft)
            Presenter.Padding = line && !song ? default : new Thickness(24, 0, 0, 0);
        else
            Presenter.Padding = song && !line ? default : new Thickness(0, 0, 24, 0);
    }

    private void Rebind()
    {
        _scope?.Unregister(this);
        if (_observedWord is not null) _observedWord.PropertyChanged -= OnWordChanged;
        _observedWord = Word;
        if (_observedWord is not null) _observedWord.PropertyChanged += OnWordChanged;
        UpdateWord();
        _scope = GetValue(ScopeProperty);
        _scope?.Register(this);
        if (_scope is null) SetMode(false, false);
    }

    private void UpdateWord()
    {
        Presenter.Word = Word;
        Presenter.Text = Word?.Text ?? "";
        Presenter.Foreground = Foreground;
        UpdatePadding();
        Presenter.ResetPose();
        if (_fallback is not null) _fallback.DataContext = Word;
        UpdateVisibility();
    }

    private void OnWordChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_native && e.PropertyName is nameof(WordTiming.Progress) or nameof(WordTiming.IsPast) or nameof(WordTiming.IsCurrent))
            Presenter.UpdateProgress();
    }

    internal void SetMode(bool native, bool rtl)
    {
        _native = native && Word is not null;
        Presenter.RightToLeft = rtl && Script.Of(Word?.Text) == TextDirection.RightToLeft;
        Presenter.SetLineActive(Line?.IsActive == true);
        UpdatePadding();
        if (!_native) Presenter.ResetPose();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        _nativeBox.IsVisible = _native;
        if (_native && _fallback is not null)
        {
            // Hidden bindings still evaluate on every Progress change. Unload the stock
            // stack entirely so its sweep converter and emphasis transitions do no work.
            _fallback.DataContext = null;
            Children.Remove(_fallback);
            _fallback = null;
        }
        else if (!_native && _fallback is null && Word is not null && FallbackTemplate is { } template)
        {
            _fallback = template.Build(Word);
            if (_fallback is not null)
            {
                _fallback.DataContext = Word;
                Children.Add(_fallback);
            }
        }
    }
}
