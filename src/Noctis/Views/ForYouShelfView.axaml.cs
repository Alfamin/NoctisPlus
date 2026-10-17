using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ForYouShelfView : UserControl
{
    private ForYouShelfViewModel? _observed;
    private bool _attached;
    public ForYouShelfView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { _attached = true; UpdateActivity(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; UpdateActivity(); };
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

    private void UpdateActivity()
    {
        if (!ReferenceEquals(_observed, DataContext)) _observed?.SetActive(false);
        _observed = DataContext as ForYouShelfViewModel;
        _observed?.SetActive(_attached && IsVisible);
    }
}
