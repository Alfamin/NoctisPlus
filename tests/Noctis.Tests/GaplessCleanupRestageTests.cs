using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// The deferred cleanup of a swapped-out player runs a second after the swap. A track
// shorter than the view model's prepare lead plus that second has its successor staged
// on the same player before the cleanup runs, and stopping the player then killed the
// staged track (GitHub #127 harness: the third track of an album of 3-4 s tracks went
// silent half a second in on the gapless engine). The cleanup now leaves a re-staged
// player alone.
public class GaplessCleanupRestageTests
{
    [Theory]
    // Re-staged after the cleanup was queued: the standby now holds the next track.
    [InlineData(true, false, true, 4, 5, true)]
    // The re-staged track was already swapped in: the player is the active one.
    [InlineData(false, true, false, 4, 5, true)]
    // Nothing staged since: the ordinary retire of the outgoing player.
    [InlineData(true, false, false, 4, 4, false)]
    [InlineData(true, false, true, 4, 4, false)]
    // A prepare ran, but on the other player, and this one is neither standby nor active.
    [InlineData(false, false, true, 4, 5, false)]
    public void DeferredCleanup_LeavesARestagedPlayerAlone(
        bool isStandby, bool isActive, bool standbyPrepared, long genAtQueue, long genNow, bool expected)
    {
        Assert.Equal(expected,
            VlcAudioPlayer.CleanupTargetRestaged(isStandby, isActive, standbyPrepared, genAtQueue, genNow));
    }
}
