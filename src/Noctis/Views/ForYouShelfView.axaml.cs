using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ForYouShelfView : UserControl
{
    private ForYouShelfViewModel? _observed;
    private ScrollViewer? _scroller;
    private bool _attached, _reportPending;
    public ForYouShelfView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true; UpdateActivity();
            _scroller = this.FindAncestorOfType<ScrollViewer>();
            if (_scroller is not null) _scroller.ScrollChanged += ViewChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false; UpdateActivity();
            if (_scroller is not null) _scroller.ScrollChanged -= ViewChanged;
            _scroller = null;
        };
        LayoutUpdated += ViewChanged;
        DataContextChanged += (_, _) => UpdateActivity();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateActivity(); };
    }
    // The cover's dots circle opens the tile's own menu (the right-click one), as on Home.
    private void OnMoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control more && more.FindAncestorOfType<Button>(includeSelf: false) is { ContextMenu: { } menu } tile)
        {
            menu.PlacementTarget = more;
            menu.Open(tile);
        }
    }

    // Which cards the listener can actually see: told to the shelf after scrolling or a new layout, at most once per pass.
    private void ViewChanged(object? sender, EventArgs e)
    {
        if (_reportPending || !_attached) return;
        _reportPending = true;
        Dispatcher.UIThread.Post(ReportInView, DispatcherPriority.Background);
    }

    private void ReportInView()
    {
        _reportPending = false;
        if (!_attached || !IsEffectivelyVisible || _observed is not { } vm || this.FindControl<ItemsControl>("PickList") is not { } list) return;
        Visual? viewport = _scroller ?? (Visual?)TopLevel.GetTopLevel(this);
        if (viewport is null) return;
        vm.InView(InView(list, viewport).Select(i => list.Items[i]).OfType<ForYouPick>());
    }

    /// <summary>The indices of the items at least half inside <paramref name="viewport"/>.</summary>
    internal static List<int> InView(ItemsControl list, Visual viewport)
    {
        var result = new List<int>();
        var size = viewport.Bounds.Size;
        for (int i = 0; i < list.ItemCount; i++)
        {
            if (list.ContainerFromIndex(i) is not Control item || !item.IsEffectivelyVisible || item.TranslatePoint(default, viewport) is not { } at) continue;
            var b = item.Bounds.Size;
            double w = Math.Min(at.X + b.Width, size.Width) - Math.Max(at.X, 0), h = Math.Min(at.Y + b.Height, size.Height) - Math.Max(at.Y, 0);
            if (w > 0 && h > 0 && w * h >= 0.5 * b.Width * b.Height) result.Add(i);
        }
        return result;
    }

    private void UpdateActivity()
    {
        if (!ReferenceEquals(_observed, DataContext)) _observed?.SetActive(false);
        _observed = DataContext as ForYouShelfViewModel;
        _observed?.SetActive(_attached && IsVisible);
    }
}
