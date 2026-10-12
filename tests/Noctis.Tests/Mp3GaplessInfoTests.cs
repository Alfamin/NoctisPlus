using System;
using System.IO;
using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// GitHub #127: LibVLC 3 under the forced avformat demuxer leaves an MP3's encoder padding
// on the end of every track (and zeros its first ~27 ms), so a seamless album gapped at
// every boundary. The LAME tag in the first frame says how much is padding; these pin
// the parse of it (frame layouts, tag variants, ID3 skip) and the trim it yields.
public class Mp3GaplessInfoTests
{
    // First frame of an MP3: header, zeroed side info, Xing/Info header with the given
    // flags, then the LAME extension carrying delay and padding as two 12-bit numbers.
    private static byte[] FirstFrame(string tag = "Info", int version = 3, bool mono = false, int rateIndex = 0,
        uint flags = 0x0F, int delay = 576, int padding = 1502, string? encoder = "LAME3.100")
    {
        var ms = new MemoryStream();
        ms.WriteByte(0xFF);
        ms.WriteByte((byte)(0xE0 | (version << 3) | (1 << 1) | 1)); // Layer III, no CRC
        ms.WriteByte((byte)((9 << 4) | (rateIndex << 2)));
        ms.WriteByte((byte)(mono ? 0xC0 : 0x40));
        var sideInfo = version == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        ms.Write(new byte[sideInfo]);
        ms.Write(Encoding.ASCII.GetBytes(tag));
        ms.Write(new[] { (byte)(flags >> 24), (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags });
        if ((flags & 0x1) != 0) ms.Write(new byte[4]);
        if ((flags & 0x2) != 0) ms.Write(new byte[4]);
        if ((flags & 0x4) != 0) ms.Write(new byte[100]);
        if ((flags & 0x8) != 0) ms.Write(new byte[4]);
        if (encoder != null)
        {
            ms.Write(Encoding.ASCII.GetBytes(encoder.PadRight(9).Substring(0, 9)));
            ms.Write(new byte[12]);
            ms.WriteByte((byte)(delay >> 4));
            ms.WriteByte((byte)(((delay & 0xF) << 4) | (padding >> 8)));
            ms.WriteByte((byte)(padding & 0xFF));
        }
        ms.Write(new byte[64]);
        return ms.ToArray();
    }

    [Fact]
    public void CbrInfoTag_ReadsDelayAndPadding()
    {
        Assert.True(Mp3GaplessInfo.TryParse(FirstFrame(), out var info));
        Assert.Equal(new Mp3GaplessInfo(44100, 576, 1502, Vbr: false), info);
    }

    [Fact]
    public void VbrXingTag_IsMarkedVbr()
    {
        Assert.True(Mp3GaplessInfo.TryParse(FirstFrame(tag: "Xing", padding: 719), out var info));
        Assert.True(info.Vbr);
        Assert.Equal(719, info.EncoderPadding);
    }

    [Theory]
    [InlineData(3, false, 1, 48000)] // MPEG-1 stereo, 32-byte side info
    [InlineData(3, true, 0, 44100)]  // MPEG-1 mono, 17
    [InlineData(2, false, 0, 22050)] // MPEG-2 stereo, 17
    [InlineData(2, true, 1, 24000)]  // MPEG-2 mono, 9
    [InlineData(0, false, 0, 11025)] // MPEG-2.5
    public void TagOffset_FollowsVersionAndChannelMode(int version, bool mono, int rateIndex, int rate)
    {
        Assert.True(Mp3GaplessInfo.TryParse(FirstFrame(version: version, mono: mono, rateIndex: rateIndex), out var info));
        Assert.Equal(rate, info.SampleRate);
        Assert.Equal(576, info.EncoderDelay);
        Assert.Equal(1502, info.EncoderPadding);
    }

    [Fact]
    public void MissingOptionalXingFields_MoveTheLameExtension()
    {
        // Frames + bytes only (no seek table, no quality): the extension sits 8 bytes in.
        Assert.True(Mp3GaplessInfo.TryParse(FirstFrame(flags: 0x03, delay: 1105, padding: 300), out var info));
        Assert.Equal(1105, info.EncoderDelay);
        Assert.Equal(300, info.EncoderPadding);
    }

    [Fact]
    public void LeadingJunk_IsSkippedToTheFirstFrame()
    {
        var data = new byte[37];
        var frame = FirstFrame();
        var all = new byte[data.Length + frame.Length];
        frame.CopyTo(all, data.Length);
        Assert.True(Mp3GaplessInfo.TryParse(all, out var info));
        Assert.Equal(1502, info.EncoderPadding);
    }

    [Fact]
    public void PlainFirstFrame_HasNoGaplessInfo()
    {
        Assert.False(Mp3GaplessInfo.TryParse(FirstFrame(tag: "\0\0\0\0"), out _));
    }

    [Fact]
    public void XingWithoutLameExtension_HasNoGaplessInfo()
    {
        var frame = FirstFrame(encoder: null);
        Assert.False(Mp3GaplessInfo.TryParse(frame, out _));
    }

    [Fact]
    public void NotAnMp3_HasNoGaplessInfo()
    {
        Assert.False(Mp3GaplessInfo.TryParse(Encoding.ASCII.GetBytes("fLaC and then some bytes that are not mpeg"), out _));
        Assert.False(Mp3GaplessInfo.TryParse(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void TailTrim_IsPaddingMinusDecoderDelay_AtTheEngineRate()
    {
        // Harness 10-11: avformat left exactly padding - 529 extra samples (973 / 602 / 190).
        var info = new Mp3GaplessInfo(44100, 576, 1502, Vbr: false);
        Assert.Equal(973, info.DecodedTailPadding);
        Assert.Equal(973, info.TailTrimFrames(44100));
        Assert.Equal((int)Math.Round(973 * 48000.0 / 44100), info.TailTrimFrames(48000));
        // Padding under the decoder delay leaves nothing to cut.
        Assert.Equal(0, new Mp3GaplessInfo(44100, 576, 200, Vbr: false).TailTrimFrames(44100));
    }

    [Fact]
    public void Id3v2Size_IsSyncsafePlusHeaderAndFooter()
    {
        var header = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0x00, 0x01, 0x7F, 0x7F };
        Assert.Equal(10 + 0x7FFF, Mp3GaplessInfo.Id3v2TotalSize(header));
        header[5] = 0x10; // footer present
        Assert.Equal(20 + 0x7FFF, Mp3GaplessInfo.Id3v2TotalSize(header));
    }

    [Fact]
    public void TryRead_SkipsAnId3v2TagLargerThanTheSearchWindow()
    {
        // 200 KB of tag (cover art) in front of the first frame.
        const int tagBody = 200 * 1024;
        var path = Path.Combine(Path.GetTempPath(), $"noctis-mp3gapless-{Guid.NewGuid():N}.mp3");
        try
        {
            using (var f = File.Create(path))
            {
                f.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0,
                    (byte)((tagBody >> 21) & 0x7F), (byte)((tagBody >> 14) & 0x7F),
                    (byte)((tagBody >> 7) & 0x7F), (byte)(tagBody & 0x7F) });
                f.Write(new byte[tagBody]);
                f.Write(FirstFrame(tag: "Xing", padding: 1131));
            }
            Assert.True(Mp3GaplessInfo.TryRead(path, out var info));
            Assert.Equal(new Mp3GaplessInfo(44100, 576, 1131, Vbr: true), info);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryRead_MissingFile_IsFalse()
    {
        Assert.False(Mp3GaplessInfo.TryRead(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp3"), out _));
    }
}
