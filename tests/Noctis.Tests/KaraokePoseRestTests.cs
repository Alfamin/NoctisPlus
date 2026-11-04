using Avalonia.Headless.XUnit;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// #255 step 1: a still posed word keeps one snapped transform while a neighbouring word's
/// Progress crosses the old (-2,3) window (the window no longer picks snapped vs unsnapped).
/// </summary>
public class KaraokePoseRestTests
{
    [AvaloniaFact]
    public void StillPose_KeepsSameSnappedTransform_WhileNeighbourCrossesOldWindow()
    {
        var still = new KaraokeWordText { Text = "had", FontSize = 35, Progress = 1 };
        var neighbour = new KaraokeWordText { Text = "it", FontSize = 35 };
        var pose = new LetterPose(1, 0.0137, -0.0551, 0);
        var first = still.RestOnPixels(pose, 1.5);
        for (double p = -3; p <= 4; p += 0.25)
        {
            neighbour.Progress = p;
            var now = still.RestOnPixels(pose, 1.5);
            Assert.Equal(first, now);
            Assert.False(still.PoseMovingForTest);
        }
        // Offsets land on whole device pixels.
        Assert.Equal(System.Math.Round(first.OffsetXEm * 35 * 1.5), first.OffsetXEm * 35 * 1.5, 9);
        Assert.Equal(System.Math.Round(first.OffsetYEm * 35 * 1.5), first.OffsetYEm * 35 * 1.5, 9);
    }
}
