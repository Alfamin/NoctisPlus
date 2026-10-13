using Avalonia.Controls;
using Avalonia.Interactivity;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class PlaylistTools : UserControl
{
    public PlaylistTools()
    {
        InitializeComponent();
    }

    private void OnSortClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaylistListPageViewModel vm && sender is Control button) SortMenu.Show(button, vm);
    }
}
