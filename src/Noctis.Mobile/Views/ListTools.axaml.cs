using Avalonia.Controls;
using Avalonia.Interactivity;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class ListTools : GlassPanel
{
    /// <summary>Styled as the GlassPanel it is (MobileStyles' header-glass).</summary>
    protected override Type StyleKeyOverride => typeof(GlassPanel);

    public ListTools()
    {
        InitializeComponent();
    }

    private void OnSortClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SongListPageViewModel vm && sender is Control button) SortMenu.Show(button, vm);
    }

    /// <summary>The filter menu: favourites, and (with an account) the desktop songs kept offline.</summary>
    private void OnFilterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SongListPageViewModel vm || sender is not Control button) return;
        var flyout = new MenuFlyout();
        var favourites = new MenuItem { Header = "Favorites", ToggleType = MenuItemToggleType.CheckBox, IsChecked = vm.OnlyFavourites };
        favourites.Click += (_, _) => vm.OnlyFavourites = !vm.OnlyFavourites;
        flyout.Items.Add(favourites);
        if (vm.CanFilterDownloaded)
        {
            var downloaded = new MenuItem { Header = "Downloaded", ToggleType = MenuItemToggleType.CheckBox, IsChecked = vm.OnlyDownloaded };
            downloaded.Click += (_, _) => vm.OnlyDownloaded = !vm.OnlyDownloaded;
            flyout.Items.Add(downloaded);
        }
        if (vm.IsFiltered)
        {
            flyout.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Show All" };
            clear.Click += (_, _) => { vm.OnlyFavourites = false; vm.OnlyDownloaded = false; };
            flyout.Items.Add(clear);
        }
        flyout.ShowAt(button);
    }
}
