using Avalonia.Controls;
using Avalonia.Interactivity;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class PlaylistTools : GlassPanel
{
    /// <summary>Styled as the GlassPanel it is (MobileStyles' header-glass).</summary>
    protected override Type StyleKeyOverride => typeof(GlassPanel);

    public PlaylistTools()
    {
        InitializeComponent();
    }

    private void OnSortClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaylistListPageViewModel vm && sender is Control button) SortMenu.Show(button, vm);
    }
}
