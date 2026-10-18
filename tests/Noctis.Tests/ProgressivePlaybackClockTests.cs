using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ProgressivePlaybackClockTests
{
    [Fact]
    public void DecoderResyncDuringDownloadPreservesUnplayedPcm_AndTheAudibleClockContinues()
    {
        var segment = new GaplessTrackSegment(8000, 1, null);
        Assert.True(segment.Write(new short[24000]));
        Assert.Equal(16000, segment.Read(new float[16000], 0, 16000)); Assert.Equal(2000, segment.PositionMs);
        VlcAudioPlayer.FlushEngineInput(segment, 0, nonseekableDownload: true, discardFrames: 50);
        Assert.Equal(8000, segment.BufferedSamples); Assert.Equal(2000, segment.PositionMs);
        Assert.Equal(8000, segment.Read(new float[8000], 0, 8000)); Assert.Equal(3000, segment.PositionMs);
        // A second underrun/refill must keep accumulating instead of looping the clock.
        VlcAudioPlayer.FlushEngineInput(segment, 0, nonseekableDownload: true);
        Assert.True(segment.Write(new short[8000])); segment.Read(new float[8000], 0, 8000);
        Assert.Equal(4000, segment.PositionMs);
    }

    [Fact]
    public void OrdinaryLocalSeekStillFlushesTheRingAndUsesItsRequestedPosition()
    {
        var segment = new GaplessTrackSegment(8000, 1, null);
        Assert.True(segment.Write(new short[8000]));
        VlcAudioPlayer.FlushEngineInput(segment, 12000, nonseekableDownload: false);
        Assert.Equal(0, segment.BufferedSamples); Assert.Equal(12000, segment.PositionMs);
    }
}
