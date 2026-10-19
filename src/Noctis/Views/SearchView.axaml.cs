using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class SearchView : UserControl
{
    /// <summary>The sidebar's search pill: fixed width, its left edge on the rail's Search button.</summary>
    private const double PillWidth = SidebarView.SearchCapsuleOpenWidth;
    private const double ChipGap = 14;

    // One row of artist portraits / album tiles on the All view: tile + its right margin.
    private const double ArtistTileStride = 162 + 14;
    private const double AlbumTileStride = 168 + 4 + 2 + 12;

    private Button? _railSearch;

    public SearchView()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => PlaceChips();
        Scroller.SizeChanged += (_, e) => UpdateRowCapacity(e.NewSize.Width);
        // The toolbar stays put; once results run under it, the top bar's hairline marks its edge.
        Scroller.ScrollChanged += (_, _) =>
        {
            HeaderRule.IsVisible = Scroller.Offset.Y > 0.5;
            // Owner #71: a screen and a half before the end of the Playlists tab, the next page starts loading by itself (the extent also grows
            // when a page lands, so a page that does not fill the window asks for the one after it).
            if (DataContext is SearchViewModel { CanLoadMorePlaylists: true } vm &&
                Scroller.Offset.Y + Scroller.Viewport.Height >= Scroller.Extent.Height - 1.5 * Scroller.Viewport.Height)
                vm.LoadMorePlaylistsCommand.Execute(null);
        };
    }

    /// <summary>Puts the chips on the pill's line, just past its right end. Measured from the
    /// rail's Search button in layout units, so it holds at any window size and display scale.</summary>
    private void PlaceChips()
    {
        _railSearch ??= TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<SidebarView>().FirstOrDefault()
            ?.FindControl<Button>("SearchButton");
        if (_railSearch is null) return;
        // The row itself moves down to the pill's line (its margin, not a render offset), so
        // the results below always start under it whatever the top bar does.
        if (_railSearch.TranslatePoint(new Point(0, _railSearch.Bounds.Height / 2), this) is { } centre)
        {
            var top = Math.Max(0, centre.Y - Toolbar.Height / 2);
            if (Math.Abs(Toolbar.Margin.Top - top) > 0.5) Toolbar.Margin = new Thickness(28, top, 28, 0);
        }
        if (_railSearch.TranslatePoint(default, Toolbar) is not { } at) return;
        var left = Math.Max(0, at.X + PillWidth + ChipGap);
        if (Math.Abs(Chips.Margin.Left - left) > 0.5) Chips.Margin = new Thickness(left, 0, 0, 0);
    }

    private void UpdateRowCapacity(double width)
    {
        if (DataContext is not SearchViewModel vm) return;
        var usable = width - 56; // the page's side margins
        vm.SetRowCapacity(Math.Max(3, (int)((usable + 14) / ArtistTileStride)), Math.Max(3, (int)((usable + 12) / AlbumTileStride)));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Scroller.Bounds.Width > 0) UpdateRowCapacity(Scroller.Bounds.Width);
    }

    // Playing takes a double click, as in every other song list (the cover's Play is one click).
    private void OnSongRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SearchSongItem item } && DataContext is SearchViewModel vm)
        {
            vm.PlaySongCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnTopSongDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SearchViewModel { TopSong: { } item } vm)
        {
            vm.PlaySongCommand.Execute(item);
            e.Handled = true;
        }
    }
}
