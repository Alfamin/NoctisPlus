using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ProgressivePlaybackClockTests
{
    [Fact]
    public void InterruptedStreamKeepsReportingItsLastAudiblePosition_AfterTheSinkRemovesTheDrainedSegment()
    {
        var own = new GaplessTrackSegment(8000, 1, 0, basePositionMs: 162_000);
        own.MarkEndOfStream();
        Assert.Equal(162_000, VlcAudioPlayer.EngineTickTimeMs(null, 0, true, 251_000, own));
        Assert.Equal(162_000, VlcAudioPlayer.EngineTickTimeMs(null, 0, true, 350_000, own));
        Assert.Null(VlcAudioPlayer.EngineTickTimeMs(null, 1, true, 350_000, own));
        own.Abandon();
        Assert.Null(VlcAudioPlayer.EngineTickTimeMs(null, 0, true, 350_000, own));
    }
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
    public void DrainedDownloadSegment_NeverFallsBackToVlcsLaggingClock()
    {
        // Real app, generated 24 s MP3 with a 45 s underrun: once the ring drained at the end the
        // timer read VLC's clock (21 976 ms) and the bar jumped back from 23.9 s before the end grace.
        var segment = new GaplessTrackSegment(8000, 1, 0, basePositionMs: 23_000);
        Assert.Equal(23_000, VlcAudioPlayer.EngineTickTimeMs(segment, 0, progressiveInput: true, vlcTimeMs: 21_976));
        Assert.Equal(0, VlcAudioPlayer.EngineTickTimeMs(segment, 1, progressiveInput: true, vlcTimeMs: 21_976)); // another player's tail
        Assert.Null(VlcAudioPlayer.EngineTickTimeMs(null, 0, progressiveInput: true, vlcTimeMs: 21_976));
        Assert.Equal(21_976, VlcAudioPlayer.EngineTickTimeMs(null, 0, progressiveInput: false, vlcTimeMs: 21_976)); // local files unchanged
    }

    [Fact]
    public void AUserSeekInsideADownloadIsTheFlushRightAfterIt_NotALaterResync()
    {
        // Owner #204: the sound went to the new spot but the bar and lyrics ran on from the old one, because every
        // flush of a download was treated as VLC's resync. Only the flush following a user seek re-bases the clock.
        Assert.True(VlcAudioPlayer.FlushFollowsUserSeek(10_000, 10_400));
        Assert.False(VlcAudioPlayer.FlushFollowsUserSeek(0, 10_400));        // no seek asked
        Assert.False(VlcAudioPlayer.FlushFollowsUserSeek(10_000, 14_000));   // a resync long after
        var segment = new GaplessTrackSegment(8000, 1, null);
        Assert.True(segment.Write(new short[24000]));
        segment.Read(new float[8000], 0, 8000);
        VlcAudioPlayer.FlushEngineInput(segment, 95_000, nonseekableDownload: false);   // the seek's flush
        Assert.Equal(0, segment.BufferedSamples);
        Assert.Equal(95_000, segment.PositionMs);
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
