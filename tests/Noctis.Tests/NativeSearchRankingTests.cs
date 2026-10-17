using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class NativeSearchRankingTests
{
    [Fact]
    public void TopResult_DoesNotOverridePopularRecordingWithLiteralArtistName()
    {
        var song = new OnlineSong("song:kanye", "Runaway", "Kanye West", Score: 96);
        var artist = new OnlineArtist("artist:obscure", "Runaway", Score: 91);
        var row = new SearchSongItem(song);
        var result = new OnlineSearchResult("runaway", [song], [artist], [], true) { Top = ("song", song.Id) };
        Assert.Same(row, SearchViewModel.ChooseTop("runaway", [row], [new(artist)], [], result));
    }

    [Fact]
    public void TopResult_StillUsesCanonicalArtistIntent_AndOwnedRecordingWinsATie()
    {
        var song = new OnlineSong("song:drake", "God's Plan", "Drake", Score: 92);
        var artist = new OnlineArtist("artist:drake", "Drake", Score: 96);
        var tile = new SearchArtistItem(artist);
        var result = new OnlineSearchResult("Drake", [song], [artist], [], true) { Top = ("artist", artist.Id) };
        Assert.Same(tile, SearchViewModel.ChooseTop("Drake", [new(song)], [tile], [], result));
        var owned = new SearchSongItem(new Track { Title = "Runaway", Artist = "Kanye West" });
        Assert.Same(owned, SearchViewModel.ChooseTop("runaway", [owned], [tile], [], result));
    }
}
