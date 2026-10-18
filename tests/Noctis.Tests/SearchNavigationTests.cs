using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class SearchNavigationTests
{
    [AvaloniaFact]
    public void ArtistRoundTrip_KeepsQueryResultsAndSelectedChip()
    {
        using var data = new TestPersistenceService();
        var library = new FakeLibraryService();
        ((List<Track>)library.Tracks).Add(new() { Title = "ATM", Artist = "J. Cole" });
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, data, new FakeAnimatedCoverService());
        using var search = new SearchViewModel(library, player, _ => { }, _ => { });
        var box = new TopBarViewModel();
        var current = true;
        using var binding = new SearchBoxBinding(box, search, () => current);
        binding.Attach();
        box.SearchText = "j cole";
        search.Filter = "Artists";
        var result = Assert.Single(search.Songs);

        // Detail routes clear the shared box before their CurrentView changes.
        binding.SetPageText("");
        current = false;
        Assert.Equal("j cole", search.QueryText);
        binding.SetPageText("artist-page filter");
        // Back restores its history entry while the detail is still current.
        binding.SetPageText("j cole");
        current = true;
        search.SetQuery(box.SearchText);

        Assert.Equal("j cole", box.SearchText);
        Assert.Equal("j cole", search.QueryText);
        Assert.Same(result, Assert.Single(search.Songs));
        Assert.Equal("Artists", search.Filter);
    }

    [AvaloniaFact]
    public void SectionRoundTripWithANonemptyFilter_DoesNotEraseSearch_AndUserClearStillWorks()
    {
        using var data = new TestPersistenceService();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, data, new FakeAnimatedCoverService());
        using var search = new SearchViewModel(library, player, _ => { }, _ => { });
        var box = new TopBarViewModel();
        var current = true;
        using var binding = new SearchBoxBinding(box, search, () => current);
        binding.Attach();
        box.SearchText = "j cole";
        current = false;
        binding.SetPageText("");
        box.SearchText = "drake"; // a local Artists-page filter
        current = true; // Navigate sets CurrentView first
        binding.SetPageText(search.QueryText);
        MainWindowViewModel.ClearStaleSearch(search);
        Assert.Equal("j cole", box.SearchText);
        Assert.Equal("j cole", search.QueryText);

        box.ClearSearchCommand.Execute(null); // the user's Clear is not a page change
        Assert.Equal("", search.QueryText);
        Assert.Empty(search.Songs);
    }
}
