using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #285: word timing that arrives while the song plays reaches the main line's word layer.</summary>
public class LyricLineTakeTimingTests
{
    [Fact]
    public void TakingWordTiming_TellsTheViewsOfTheMainLinesWordLayer()
    {
        var shown = new LyricLine { Timestamp = TimeSpan.Zero, Text = "go now" };
        var timed = new LyricLine
        {
            Timestamp = TimeSpan.Zero, Text = "go now",
            Words = [new() { Text = "go ", Start = TimeSpan.Zero, End = TimeSpan.FromMilliseconds(400) },
                     new() { Text = "now", Start = TimeSpan.FromMilliseconds(400), End = TimeSpan.FromMilliseconds(900) }],
            BackgroundWords = [new() { Text = "(yeah)", Start = TimeSpan.Zero, End = TimeSpan.FromMilliseconds(500) }],
        };
        var told = new List<string?>();
        shown.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        shown.TakeTimingFrom(timed);

        Assert.Same(timed.Words, shown.LiveWords);
        Assert.True(shown.ShowLiveWords);
        Assert.Contains(nameof(LyricLine.LiveWords), told);
        Assert.Contains(nameof(LyricLine.ShowLiveWords), told);
        Assert.Contains(nameof(LyricLine.ShowRestingWords), told);
        Assert.Contains(nameof(LyricLine.ShowBackgroundWords), told);
    }
}
