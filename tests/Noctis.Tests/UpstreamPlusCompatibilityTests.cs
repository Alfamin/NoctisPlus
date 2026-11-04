using Noctis.Features.WordLyrics;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class UpstreamPlusCompatibilityTests
{
    [Fact]
    public void ExplicitTimingMustPersistUnmark_WhenPlayingAndLibraryShareTheTrack()
    {
        var song = new Track { FilePath = "C:/synthetic/song.mp3", NoLyrics = true };
        Assert.True(WordLyricsService.ClearNoLyricsMark(song.FilePath, song, [song]));
        Assert.False(song.NoLyrics);
        Assert.False(WordLyricsService.ClearNoLyricsMark(song.FilePath, song, [song]));
    }

    [Fact]
    public void ExplicitTimingUnmarksTheLibraryCopy_WithoutChangingOtherSongs()
    {
        var song = new Track { FilePath = "C:/synthetic/song.mp3", NoLyrics = true };
        var playing = new Track { FilePath = song.FilePath, NoLyrics = true };
        var other = new Track { FilePath = "C:/synthetic/other.mp3", NoLyrics = true };
        Assert.True(WordLyricsService.ClearNoLyricsMark(song.FilePath, playing, [song, other]));
        Assert.False(song.NoLyrics);
        Assert.False(playing.NoLyrics);
        Assert.True(other.NoLyrics);
    }

    [Fact]
    public void PlusKeepsItsOriginalMotion_WhenUpstreamChangesItsDefault()
    {
        Assert.Equal(650, Noctis.Helpers.PlusLineMotion.DurationMs);
        Assert.Equal(1000, Noctis.Helpers.LineMotion.DurationMs);
        Assert.Equal(0.8, Noctis.Helpers.PlusLineMotion.Zeta);
        Assert.Equal(1, new Noctis.Helpers.PlusLineMotionEase().Ease(1));
    }
}
