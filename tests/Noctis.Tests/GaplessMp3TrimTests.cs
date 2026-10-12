using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// GitHub #127: an MP3 decoded through the avformat demuxer ends in its encoder padding.
// The engine cuts it when the input ends, so the next track follows the last real sample;
// without the cut every boundary carried 4-50 ms of near-silence.
public class GaplessMp3TrimTests
{
    private static short[] Block(short value, int samples) => Enumerable.Repeat(value, samples).ToArray();

    [Fact]
    public void TailTrim_CutsThePaddingAtEndOfStream_SoTheSpliceIsSampleAdjacent()
    {
        var provider = new GaplessSpliceProvider(8000, 2);
        var a = new GaplessTrackSegment(8000, 2, source: null) { TailTrimFrames = 30 };
        var b = new GaplessTrackSegment(8000, 2, source: null);
        provider.Enqueue(a);
        provider.Enqueue(b);

        Assert.True(a.Write(Block(16384, 200)));     // 100 frames of track
        Assert.True(a.Write(Block(0, 60)));          // 30 frames of padding
        a.MarkEndOfStream();
        Assert.Equal(200, a.BufferedSamples);
        Assert.True(b.Write(Block(-16384, 200)));
        b.MarkEndOfStream();

        var buffer = new float[400];
        Assert.Equal(400, provider.Read(buffer, 0, 400));
        Assert.All(buffer.Take(200), s => Assert.True(s > 0.4f, $"A sample was {s}"));
        Assert.All(buffer.Skip(200), s => Assert.True(s < -0.4f, $"B sample was {s}"));
    }

    [Fact]
    public void TailTrim_AppliesOnce()
    {
        var seg = new GaplessTrackSegment(8000, 2, source: null) { TailTrimFrames = 10 };
        Assert.True(seg.Write(Block(1000, 100)));
        seg.MarkEndOfStream();
        seg.MarkEndOfStream(); // drain callback and EndReached both mark it
        Assert.Equal(80, seg.BufferedSamples);
    }

    [Fact]
    public void TailTrim_NeverCutsWhatTheRendererAlreadyTook()
    {
        var seg = new GaplessTrackSegment(8000, 1, source: null) { TailTrimFrames = 50 };
        Assert.True(seg.Write(Block(1000, 100)));
        var buffer = new float[80];
        Assert.Equal(80, seg.Read(buffer, 0, 80));
        seg.MarkEndOfStream();
        Assert.Equal(0, seg.BufferedSamples);
        Assert.True(seg.IsFinished);
    }

    [Fact]
    public void TailTrim_KeepsTheRingConsistentAcrossTheWrap()
    {
        // 2 s ring at 1 kHz mono: write past the end so the write index has wrapped.
        var seg = new GaplessTrackSegment(1000, 1, source: null, capacitySeconds: 2) { TailTrimFrames = 3 };
        Assert.True(seg.Write(Block(1, 1500)));
        var buffer = new float[1500];
        Assert.Equal(1500, seg.Read(buffer, 0, 1500));
        var tail = Enumerable.Range(0, 1000).Select(i => (short)(i < 997 ? 2 : 9)).ToArray();
        Assert.True(seg.Write(tail));
        seg.MarkEndOfStream();
        Assert.Equal(997, seg.BufferedSamples);
        var rest = new float[1000];
        Assert.Equal(997, seg.Read(rest, 0, 1000));
        Assert.All(rest.Take(997), s => Assert.Equal(2 / 32768f, s));
    }

    [Fact]
    public void Abandoned_SegmentIsNotTrimmed()
    {
        var seg = new GaplessTrackSegment(8000, 1, source: null) { TailTrimFrames = 10 };
        Assert.True(seg.Write(Block(1000, 100)));
        seg.Abandon();
        seg.MarkEndOfStream();
        Assert.Equal(0, seg.BufferedSamples);
        Assert.True(seg.IsFinished);
    }

    [Theory]
    [InlineData(true, true, 0, true)]      // avformat forced, LAME tag, from the start: native demuxer
    [InlineData(true, true, -1, true)]     // -1 = no start position
    [InlineData(true, true, 2500, false)]  // opened at a position: avformat (exact seek)
    [InlineData(true, false, 0, false)]    // no LAME tag: keep the avformat seek fix
    [InlineData(false, true, 0, false)]    // system libvlc: native demuxer already
    public void NativeMp3Demux_OnlyForTaggedFilesFromTheStart(bool avformatForced, bool tagged, long startMs, bool expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.UseNativeMp3Demux(avformatForced, tagged, startMs));
    }
}
