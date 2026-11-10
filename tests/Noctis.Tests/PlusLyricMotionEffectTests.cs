using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

public sealed class PlusLyricMotionEffectTests
{
    private readonly PlusLyricMotionEffect _effect = new();

    [Fact]
    public void SplitsOnlySupportedPageWords()
    {
        var latin = new WordPoseInput(0, 500, 0, false, false, false, 5, 30, WordSurface.Page);
        var rtl = latin with { IsRightToLeft = true };
        var longWord = latin with { LetterCount = 15 };
        var quick = latin with { DurationMs = 100 };
        var mini = latin with { Surface = WordSurface.MiniPlayer };

        Assert.Equal(WordDrawMode.Letters, _effect.Mode(latin));
        Assert.Equal(WordDrawMode.Whole, _effect.Mode(rtl));
        Assert.Equal(WordDrawMode.Whole, _effect.Mode(longWord));
        Assert.Equal(WordDrawMode.Whole, _effect.Mode(quick));
        Assert.Equal(WordDrawMode.Native, _effect.Mode(mini));
    }

    [Fact]
    public void LettersRiseInReadingOrderAndReturnToRest()
    {
        var word = new WordPoseInput(0.2, 500, 0, false, false, false, 4, 30, WordSurface.Page);
        var first = _effect.Pose(word, 0);
        var second = _effect.Pose(word, 1);
        Assert.True(first.OffsetYEm < 0);
        Assert.Equal(0, second.OffsetYEm);

        var later = word with { Progress = 0.6 };
        Assert.True(_effect.Pose(later, 1).OffsetYEm < 0);

        var future = _effect.Pose(word with { Progress = -2 }, 0);
        var finished = _effect.Pose(word with { Progress = 3 }, 0);
        Assert.Equal(1, future.Scale);
        Assert.Equal(0, future.OffsetXEm);
        Assert.Equal(0, future.OffsetYEm);
        Assert.Equal(1, finished.Scale);
        Assert.Equal(0, finished.OffsetXEm);
        Assert.Equal(0, finished.OffsetYEm);
    }

    [Fact]
    public void PoseIsDeterministicAndAllocatesNothingPerFrame()
    {
        var word = new WordPoseInput(0.63, 930, 0.42, true, false, false, 8, 32, WordSurface.Page);
        var expected = _effect.Pose(word, 3);
        for (var i = 0; i < 100; i++) _ = _effect.Pose(word, i % 8);

        var before = GC.GetAllocatedBytesForCurrentThread();
        double checksum = 0;
        for (var i = 0; i < 10_000; i++)
        {
            checksum += _effect.Mode(word) == WordDrawMode.Letters ? 1 : 0;
            var pose = _effect.Pose(word, i % 8);
            checksum += pose.Scale + pose.OffsetXEm + pose.OffsetYEm + pose.Glow;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, _effect.Pose(word, 3));
        Assert.True(double.IsFinite(checksum));
        Assert.Equal(0, allocated);
    }
}
