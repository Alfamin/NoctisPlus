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
    }

    /// <summary>Puts the chips on the pill's line, just past its right end. Measured from the
    /// rail's Search button in layout units, so it holds at any window size and display scale.</summary>
    private void PlaceChips()
    {
        if (Chips.Parent is not Visual row) return;
        _railSearch ??= TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<SidebarView>().FirstOrDefault()
            ?.FindControl<Button>("SearchButton");
        if (_railSearch is null || _railSearch.TranslatePoint(default, row) is not { } at) return;
        var left = Math.Max(0, at.X + PillWidth + ChipGap);
        var dy = at.Y + _railSearch.Bounds.Height / 2 - row.Bounds.Height / 2;
        if (Math.Abs(Chips.Margin.Left - left) > 0.5) Chips.Margin = new Thickness(left, 0, 0, 0);
        if (Chips.RenderTransform is not TranslateTransform { } t || Math.Abs(t.Y - dy) > 0.5)
            Chips.RenderTransform = new TranslateTransform(0, dy);
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
        if (sender is Control { DataContext: SearchSongItem { IsLocal: true } item } && DataContext is SearchViewModel vm)
        {
            vm.PlaySongCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnTopSongDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SearchViewModel { TopSong: { IsLocal: true } item } vm)
        {
            vm.PlaySongCommand.Execute(item);
            e.Handled = true;
        }
    }
}
