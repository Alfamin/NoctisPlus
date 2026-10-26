using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ForYouShelfView : UserControl
{
    private ForYouShelfViewModel? _observed, _listening;
    private ScrollViewer? _scroller;
    private bool _attached, _reportPending;
    /// <summary>How many times the cards on screen were worked out (a test measures that unrelated layout costs nothing).</summary>
    internal int Reports { get; private set; }
    public ForYouShelfView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true; UpdateActivity();
            _scroller = this.FindAncestorOfType<ScrollViewer>();
            if (_scroller is not null) _scroller.ScrollChanged += ViewChanged;
            ViewChanged(this, EventArgs.Empty);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false; UpdateActivity();
            if (_scroller is not null) _scroller.ScrollChanged -= ViewChanged;
            _scroller = null;
        };
        // Only what changes which cards are on screen: the page scrolling or resizing (its offset, extent or viewport), the
        // shelf moving within it (the list's effective viewport), a new page and opening the shelf. Not LayoutUpdated: it fires
        // for every layout pass anywhere in the window, many times a second while music plays (the playback bar, lyrics).
        if (this.FindControl<ItemsControl>("PickList") is { } list)
        {
            list.EffectiveViewportChanged += ViewChanged;
            list.Items.CollectionChanged += ViewChanged;
        }
        DataContextChanged += (_, _) => UpdateActivity();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) { UpdateActivity(); ViewChanged(this, EventArgs.Empty); } };
    }
    // Owner #166: "Don't recommend artist" on a card: the artist itself when the song has one, else one entry per artist.
    private void OnCardMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: ForYouPick pick } menu || DataContext is not ForYouShelfViewModel vm) return;
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "block")) is not { } item) return;
        FillArtists(item, pick.Artists, vm.BlockArtistCommand);
    }

    // Owner #166: the For you heading lists the blocked artists; choosing one recommends them again.
    private void OnHeadingMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || DataContext is not ForYouShelfViewModel vm) return;
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "unblock")) is not { } item) return;
        item.Items.Clear();
        var names = vm.BlockedArtists;
        item.IsVisible = names.Count > 0;
        foreach (var name in names) item.Items.Add(new MenuItem { Header = name, Command = vm.UnblockArtistCommand, CommandParameter = name });
    }

    /// <summary>One name: the item itself runs <paramref name="command"/>; several: one sub-item each.</summary>
    internal static void FillArtists(MenuItem item, IReadOnlyList<string> names, System.Windows.Input.ICommand command)
    {
        item.Items.Clear();
        item.IsVisible = names.Count > 0;
        var single = names.Count == 1;
        item.Command = single ? command : null;
        item.CommandParameter = single ? names[0] : null;
        if (!single) foreach (var name in names) item.Items.Add(new MenuItem { Header = name, Command = command, CommandParameter = name });
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

    // Which cards the listener can actually see: told to the shelf after the change is laid out, at most once per pass.
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
        Reports++;
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
        // Listened to only while on screen, so a shelf model that outlives this view never keeps it alive.
        var listen = _attached ? _observed : null;
        if (ReferenceEquals(listen, _listening)) return;
        if (_listening is not null) _listening.PropertyChanged -= ShelfChanged;
        _listening = listen;
        if (listen is not null) listen.PropertyChanged += ShelfChanged;
    }

    private void ShelfChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ForYouShelfViewModel.IsExpanded)) ViewChanged(sender, EventArgs.Empty);
    }
}
