using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Mobile.ViewModels;

/// <summary>One line of a page's sort menu: its label, the key the page sorts by, and whether it is the one in use.</summary>
public sealed partial class SortChoice : ObservableObject
{
    public SortChoice(string label, object key)
    {
        Label = label;
        Key = key;
    }

    public string Label { get; }
    public object Key { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>A page with a sort menu (Search, a song list); the view builds the menu from this.</summary>
public interface ISortable
{
    IReadOnlyList<SortChoice> SortChoices { get; }

    void ChooseSort(SortChoice choice);

    /// <summary>Whether the menu also offers Ascending / Descending.</summary>
    bool HasSortDirection { get; }

    bool IsDescending { get; }

    void ChooseDirection(bool descending);
}
