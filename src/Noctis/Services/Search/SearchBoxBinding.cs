using System.ComponentModel;
using Noctis.ViewModels;

namespace Noctis.Services.Search;

/// <summary>The shell's box filters the current page. Programmatic page changes must not
/// erase the persistent Search page's query while its outgoing view is still current.</summary>
internal sealed class SearchBoxBinding(TopBarViewModel box, SearchViewModel search, Func<bool> searchIsCurrent) : IDisposable
{
    private int _pageChange;

    internal void Attach() => box.PropertyChanged += Changed;

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_pageChange == 0 && e.PropertyName == nameof(TopBarViewModel.SearchText) && searchIsCurrent())
            search.SetQuery(box.SearchText);
    }

    internal void SetPageText(string text)
    {
        _pageChange++;
        try { box.SearchText = text; }
        finally { _pageChange--; }
    }

    public void Dispose() => box.PropertyChanged -= Changed;
}
