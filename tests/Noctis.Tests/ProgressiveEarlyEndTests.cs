using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ProgressiveEarlyEndTests
{
    [Fact]
    public void NaturalDecoderEof_WithElevenSecondsStillBuffered_DrainsWithoutReopening()
    {
        var tail = new GaplessTrackSegment(8000, 1, source: 0, basePositionMs: 158_000);
        Assert.True(tail.Write(new short[11 * 8000]));
        tail.MarkEndOfStream();
        Assert.Null(VlcAudioPlayer.EarlyDownloadResumeMs(169_000, tail, 0));
        Assert.Equal(11 * 8000, tail.BufferedSamples);
    }

    [Fact]
    public void TruncatedInput_ResumesAtAudiblePosition_NotAtTheDecodedTail()
    {
        // The reported failure: 36 s heard, 11 s decoded ahead, 169 s expected.
        var shortInput = new GaplessTrackSegment(8000, 1, source: 0, basePositionMs: 36_000);
        Assert.True(shortInput.Write(new short[11 * 8000]));
        shortInput.MarkEndOfStream();
        Assert.Equal(36_000, VlcAudioPlayer.EarlyDownloadResumeMs(169_000, shortInput, 0));
        shortInput.Abandon();
        Assert.Null(VlcAudioPlayer.EarlyDownloadResumeMs(169_000, shortInput, 0));
    }

    [Theory]
    [InlineData(169000, 36000, 36000L)]
    [InlineData(169000, 164000, null)] // Small catalog/encoder differences are not interrupted inputs.
    [InlineData(0, 36000, null)]
    public void NonEnginePlayback_UsesTheNativeClock(long length, long heard, long? resume)
        => Assert.Equal(resume, VlcAudioPlayer.EarlyDownloadResumeMs(length, null, heard));
}
