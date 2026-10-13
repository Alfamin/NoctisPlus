using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Settings' pages inside the sheet: the top level and the pushed page (Music Folders,
/// Appearance, Lyrics) share one area. A pushed page slides in from the right while the top
/// level drifts a little left and fades, and Back reverses it (TransformOperationsTransition:
/// an Animation cannot drive RenderTransform here). The header's hairline shows once the page
/// showing has scrolled under it.
/// </summary>
public partial class SettingsPage : UserControl
{
    private static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(380);

    private SettingsPageViewModel? _vm;
    private int _moves;

    public SettingsPage()
    {
        InitializeComponent();
        PaneHost.SizeChanged += (_, _) => PlacePanes(animate: false);
        MainScroll.ScrollChanged += (_, _) => UpdateHairline();
        SubScroll.ScrollChanged += (_, _) => UpdateHairline();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as SettingsPageViewModel;
        if (_vm != null) _vm.PropertyChanged += OnViewModelChanged;
        PlacePanes(animate: false);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsPageViewModel.Pane)) return;
        if (_vm?.IsSubPane == true) SubScroll.Offset = default;   // a page always opens at its top
        PlacePanes(animate: true);
    }

    /// <summary>Puts both panes where the current page wants them, sliding when asked. The pane
    /// leaving is hidden once its slide ends, so only the page showing is in the tree's reach
    /// (touch, the screen reader).</summary>
    private void PlacePanes(bool animate)
    {
        var sub = _vm?.IsSubPane == true;
        var width = PaneHost.Bounds.Width > 0 ? PaneHost.Bounds.Width : 412;

        MainScroll.Transitions = animate ? SlideTransitions() : null;
        SubScroll.Transitions = animate ? SlideTransitions() : null;
        MainScroll.IsVisible = true;
        SubScroll.IsVisible = true;
        MainScroll.IsHitTestVisible = !sub;
        SubScroll.IsHitTestVisible = sub;
        MainScroll.RenderTransform = TransformOperations.Parse(sub ? $"translateX({-width * 0.3:0.#}px)" : "translateX(0px)");
        SubScroll.RenderTransform = TransformOperations.Parse(sub ? "translateX(0px)" : $"translateX({width:0.#}px)");
        MainScroll.Opacity = sub ? 0 : 1;
        UpdateHairline();

        var move = ++_moves;
        if (animate) _ = SettleAfterSlideAsync(move);
        else Settle();
    }

    private async Task SettleAfterSlideAsync(int move)
    {
        await Task.Delay(Slide + TimeSpan.FromMilliseconds(40)).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (move == _moves) Settle();   // a later move owns the panes now
        });
    }

    private void Settle()
    {
        var sub = _vm?.IsSubPane == true;
        MainScroll.IsVisible = !sub;
        SubScroll.IsVisible = sub;
    }

    private static Transitions SlideTransitions() => new()
    {
        new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Slide, Easing = new GlideEase() },
        new DoubleTransition { Property = OpacityProperty, Duration = Slide, Easing = new GlideEase() },
    };

    private void UpdateHairline()
    {
        var showing = _vm?.IsSubPane == true ? SubScroll : MainScroll;
        HeaderHairline.Opacity = showing.Offset.Y > 0.5 ? 1 : 0;
    }
}
