using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #91: "3 waiting" with nothing running looked stuck; held songs say when they try again.</summary>
public class WordTimingNextTryTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NothingHeld_SaysNothingMore()
    {
        Assert.Equal("", WordLyricsViewModel.NextTry(null, Now));
        Assert.Equal("", WordLyricsViewModel.NextTry(Now.AddSeconds(-5), Now));
    }

    [Fact]
    public void HeldSongs_SayWhenTheyTryAgain()
    {
        Assert.Equal(" · next try in 5 min", WordLyricsViewModel.NextTry(Now.AddMinutes(4.2), Now));
        Assert.Equal(" · next try in 1 h 32 min", WordLyricsViewModel.NextTry(Now.AddMinutes(92.5), Now));
    }
}
