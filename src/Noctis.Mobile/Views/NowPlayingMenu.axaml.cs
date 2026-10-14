using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Now Playing's ⋯ menu (<see cref="ShellViewModel.NowPlayingMenu"/>): the glass card grows out of
/// <see cref="Anchor"/> (the ⋯ button) as the desktop's right-click track menu does (Noctis.UI
/// MenuOpenAnimation's Pop, the same numbers): scale 0.9 → 1 from the button with the soft
/// decelerate, a quick fade, the rows settling in one after another; closing is that motion
/// reversed, quicker. The card sits with its bottom-right corner on the button, over the page.
/// Like the desktop's glass menus it fades the GlassPanel's own <see cref="GlassPanel.Fade"/> and
/// the rows' Opacity, never an ancestor's Opacity, which the GPU backend does not apply to the
/// frost. Only RenderTransform and those two move per frame.
/// </summary>
public partial class NowPlayingMenu : UserControl
{
    private const double PopScale = 0.9;
    private const double PopDurationMs = 280;
    private const double PopFadeMs = 160;
    private const double PopRowDurationMs = 240;
    private const double PopRowStaggerMs = 16;
    private const double PopRowOffsetY = 6;
    private const int PopStaggeredRows = 10;
    internal const double PopCloseMs = 150;
    private const double PopCloseScale = 0.94;
    // Soft decelerate (easeOutQuint-like) to open, accelerate out to close: the desktop's curves.
    private static readonly Avalonia.Animation.Easings.Easing PopEase = new CubicBezierEase(0.22, 1, 0.36, 1);
    private static readonly Avalonia.Animation.Easings.Easing PopCloseEase = new CubicBezierEase(0.4, 0, 1, 1);

    private ShellViewModel? _shell;
    // Bumped by every open and close: a deferred step of an older run sees it changed and stops.
    private int _run;

    public NowPlayingMenu()
    {
        InitializeComponent();
    }

    /// <summary>The ⋯ button the card grows out of and sits on.</summary>
    public Control? Anchor { get; set; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell != null) _shell.PropertyChanged += OnShellChanged;
        Sync();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.NowPlayingMenu)) Sync();
    }

    private void Sync()
    {
        if (_shell?.NowPlayingMenu is { } menu) Open(menu);
        else Close();
    }

    private void OnCatcherPressed(object? sender, PointerPressedEventArgs e)
    {
        _shell?.CloseSheet();
        e.Handled = true;
    }

    private void Open(ContextSheetViewModel menu)
    {
        var run = ++_run;
        MenuContent.Content = menu;
        Body.Offset = default;
        IsVisible = true;
        IsHitTestVisible = true;
        Place();
        // Built and placed now, not on the next layout pass: a control enables its transitions
        // when it joins the visual tree, and the card only joins it the first time the menu is
        // laid out, so the first open after launch snapped to its end (headless, 10-13).
        UpdateLayout();

        // Start values with no transitions (they snap), the targets a frame later with them.
        Card.Transitions = null;
        Glass.Transitions = null;
        Body.Transitions = null;
        Glass.Fade = 0;
        Body.Opacity = 0;
        Card.RenderTransform = TransformOperations.Parse(Scale(PopScale));

        Dispatcher.UIThread.Post(() =>
        {
            if (run != _run) return;
            Card.RenderTransformOrigin = Origin();
            var rows = Rows();
            foreach (var row in rows)
            {
                row.Transitions = null;
                row.Opacity = 0;
                row.RenderTransform = TransformOperations.Parse($"translateY({PopRowOffsetY}px)");
            }

            var fade = TimeSpan.FromMilliseconds(PopFadeMs);
            Glass.Transitions = new Transitions { new DoubleTransition { Property = GlassPanel.FadeProperty, Duration = fade, Easing = new CubicEaseOut() } };
            Body.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = fade, Easing = new CubicEaseOut() } };
            Card.Transitions = new Transitions
            {
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(PopDurationMs), Easing = PopEase },
            };
            Glass.Fade = 1;
            Body.Opacity = 1;
            Card.RenderTransform = TransformOperations.Parse(Scale(1));

            Dispatcher.UIThread.Post(() =>
            {
                if (run != _run) return;
                for (var i = 0; i < rows.Count; i++)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Min(i, PopStaggeredRows) * PopRowStaggerMs);
                    var duration = TimeSpan.FromMilliseconds(PopRowDurationMs);
                    rows[i].Transitions = new Transitions
                    {
                        new DoubleTransition { Property = OpacityProperty, Duration = duration, Delay = delay, Easing = new CubicEaseOut() },
                        new TransformOperationsTransition { Property = RenderTransformProperty, Duration = duration, Delay = delay, Easing = PopEase },
                    };
                    rows[i].Opacity = 1;
                    rows[i].RenderTransform = TransformOperations.Parse("translateY(0px)");
                }
                // Once every row has landed, hand the rows back to their styles.
                DispatcherTimer.RunOnce(() =>
                {
                    foreach (var row in rows)
                    {
                        row.Transitions = null;
                        row.ClearValue(OpacityProperty);
                        row.ClearValue(RenderTransformProperty);
                    }
                }, TimeSpan.FromMilliseconds(PopRowDurationMs + PopStaggeredRows * PopRowStaggerMs + 60));
            }, DispatcherPriority.Render);
        }, DispatcherPriority.Render);
    }

    /// <summary>The open motion reversed: inert at once (a fading card must never take a tap),
    /// then shrink toward the button while fading, and hide once it is gone.</summary>
    private void Close()
    {
        if (!IsVisible) return;
        var run = ++_run;
        IsHitTestVisible = false;
        var duration = TimeSpan.FromMilliseconds(PopCloseMs);
        Glass.Transitions = new Transitions { new DoubleTransition { Property = GlassPanel.FadeProperty, Duration = duration, Easing = PopCloseEase } };
        Body.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = PopCloseEase } };
        Card.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = duration, Easing = PopCloseEase },
        };
        Glass.Fade = 0;
        Body.Opacity = 0;
        Card.RenderTransform = TransformOperations.Parse(Scale(PopCloseScale));

        DispatcherTimer.RunOnce(() =>
        {
            if (run != _run) return;
            IsVisible = false;
            MenuContent.Content = null;
            Card.Transitions = null;
            Glass.Transitions = null;
            Body.Transitions = null;
        }, duration);
    }

    /// <summary>
    /// The card's bottom-right corner on the ⋯ button's, as Apple's: it opens over the title
    /// and the cover, toward the free space above. In a short window (landscape) it may not
    /// fit: it then stops below the status bar and its rows scroll.
    /// </summary>
    private void Place()
    {
        // Measured against the page, which fills the same box: this control has not been laid
        // out while it was hidden.
        var host = this.GetVisualParent();
        var size = host?.Bounds.Size ?? default;
        if (Anchor is not { } anchor || host == null || anchor.TranslatePoint(default, host) is not { } at || size.Height <= 0)
        {
            Card.Margin = new Thickness(0, 0, 16, 16);
            return;
        }
        var right = Math.Max(12, size.Width - (at.X + anchor.Bounds.Width));
        var bottom = Math.Max(12, size.Height - (at.Y + anchor.Bounds.Height));
        Card.Margin = new Thickness(0, 0, right, bottom);
        Card.MaxHeight = Math.Max(120, size.Height - bottom - (_shell?.SafeArea.Top ?? 0) - 12);
    }

    /// <summary>The ⋯ button's centre inside the card: the point it grows from and shrinks to.</summary>
    private RelativePoint Origin()
    {
        var card = Card.Bounds;
        if (Anchor is not { } anchor || anchor.TranslatePoint(default, this) is not { } at || card.Width <= 0)
            return new RelativePoint(1, 1, RelativeUnit.Relative);
        var x = at.X + anchor.Bounds.Width / 2 - card.X;
        var y = at.Y + anchor.Bounds.Height / 2 - card.Y;
        return new RelativePoint(Math.Clamp(x, 0, card.Width), Math.Clamp(y, 0, card.Height), RelativeUnit.Absolute);
    }

    /// <summary>The card's visible rows (the tile row, each action), top to bottom.</summary>
    private List<Control> Rows() =>
        MenuContent.Presenter?.Child is StackPanel rows
            ? rows.Children.Where(c => c.IsVisible && c is not Border).ToList()
            : new List<Control>();

    private static string Scale(double scale) =>
        $"scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
}
