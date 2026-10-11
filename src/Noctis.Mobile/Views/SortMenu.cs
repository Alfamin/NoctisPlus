using Avalonia.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The sort and filter menus a page's toolbar buttons open: built from the page's choices each
/// time, so the check marks always match what the page shows.
/// </summary>
internal static class SortMenu
{
    internal static MenuFlyout Build(ISortable page)
    {
        var flyout = new MenuFlyout();
        foreach (var choice in page.SortChoices)
        {
            var item = new MenuItem { Header = choice.Label, ToggleType = MenuItemToggleType.Radio, GroupName = "sort", IsChecked = choice.IsSelected };
            item.Click += (_, _) => page.ChooseSort(choice);
            flyout.Items.Add(item);
        }
        if (page.HasSortDirection)
        {
            flyout.Items.Add(new Separator());
            foreach (var (label, descending) in new[] { ("Ascending", false), ("Descending", true) })
            {
                var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = "direction", IsChecked = page.IsDescending == descending };
                item.Click += (_, _) => page.ChooseDirection(descending);
                flyout.Items.Add(item);
            }
        }
        return flyout;
    }

    internal static void Show(Control anchor, ISortable page) => Build(page).ShowAt(anchor);
}
