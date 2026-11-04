using Noctis.Features.TrueShuffle;
using Noctis.Mobile.Services;
using Xunit;

namespace Noctis.Tests;

public class PhoneShuffleTests
{
    private static Song Song(string id, int plays = 0) => new(id, "", id, "Synthetic", plays,
        false, false, false, null, Remote: true, IdentityKey: id);

    [Fact]
    public void OffAndStockShuffleDelegateToTheHost()
    {
        Assert.Null(PhoneShuffle.Order(new[] { Song("a") }, s => s, PhoneShuffleMode.Off));
        Assert.Null(PhoneShuffle.Order(new[] { Song("a") }, s => s, PhoneShuffleMode.NoctisShuffle));
    }

    [Fact]
    public void DuplicateRecordingIdentityFollowsTheDesktopEnginePolicy()
    {
        var order = PhoneShuffle.Order(new[] { Song("a"), Song("a"), Song("b") }, s => s,
            PhoneShuffleMode.EqualChance, current: "unknown", last: "unknown", draw: _ => 0)!;
        Assert.Equal(2, order.Count);
        Assert.Equal(new[] { "a", "b" }, order.Select(s => s.Key).OrderBy(k => k));
    }

    [Fact]
    public void NeverPlayedDoesNotFallBackToPreviouslyPlayedSongs()
    {
        var input = new[] { Song("heard", 1), Song("new") };
        Assert.Equal("new", Assert.Single(PhoneShuffle.Order(input, s => s, PhoneShuffleMode.NeverPlayed)!).Key);
        Assert.Empty(PhoneShuffle.Order(new[] { Song("heard", 1) }, s => s, PhoneShuffleMode.NeverPlayed)!);
    }
}
