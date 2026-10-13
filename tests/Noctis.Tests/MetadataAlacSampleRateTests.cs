using System;
using System.IO;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Hi-res ALAC: TagLib# reads the MP4 sample entry's 16-bit rate field, which cannot hold
// 88.2–192 kHz — a 96 kHz file scanned as 48 kHz (and lost its Hi-Res badge). The ALAC
// decoder config atom is the authority; the scanner now prefers it whenever it is valid,
// not only when TagLib returned nothing.
public class MetadataAlacSampleRateTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", name);

    [Fact]
    public void HiResAlac_SampleRateComesFromTheDecoderConfigAtom()
    {
        // 0.02 s mono 24-bit ALAC at 96 kHz (ffmpeg); TagLib# alone reports 48000 for it.
        var track = new MetadataService().ReadTrackMetadata(Fixture("alac_96k_tiny.m4a"));

        Assert.NotNull(track);
        Assert.Equal(96000, track!.SampleRate);
        Assert.Equal(24, track.BitsPerSample);
        Assert.Contains("alac", track.Codec, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".m4a")]
    [InlineData(".m4b")]
    public void HiResAlac_WithItsMoovPastTheFirstMegabyte_StillReadsTheAtomRate(string ext)
    {
        // ffmpeg writes moov after mdat unless told +faststart — the Audio Converter's own
        // ALAC output does. Past the scanner's first-megabyte window the decoder config atom
        // went unread, and a 96 kHz file of any real length scanned as 48 kHz (harness 10-13:
        // a 6 s 96k/24 ALAC from ffmpeg read 48000; the same file remuxed +faststart, 96000).
        // Same file, a 1.1 MB free box slid in ahead of moov (chunk offsets untouched).
        var bytes = File.ReadAllBytes(Fixture("alac_96k_tiny.m4a"));
        var moov = IndexOfBox(bytes, "moov");
        const int padding = 1_100_000;
        var padded = new byte[bytes.Length + padding];
        bytes.AsSpan(0, moov).CopyTo(padded);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(padded.AsSpan(moov), padding);
        "free"u8.CopyTo(padded.AsSpan(moov + 4));
        bytes.AsSpan(moov).CopyTo(padded.AsSpan(moov + padding));
        var path = Path.Combine(Path.GetTempPath(), $"noctis-alac-moov-end-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, padded);
        try
        {
            var service = new MetadataService();
            var track = service.ReadTrackMetadata(path);
            Assert.NotNull(track);
            Assert.Equal(96000, track!.SampleRate);
            Assert.Equal(24, track.BitsPerSample);
            Assert.Equal(96000, service.ReadFileInfo(path)!.SampleRate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Offset of the first top-level box of <paramref name="type"/>.</summary>
    private static int IndexOfBox(byte[] bytes, string type)
    {
        var pos = 0;
        while (pos + 8 <= bytes.Length)
        {
            if (System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4) == type) return pos;
            pos += (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos));
        }
        throw new InvalidDataException(type + " not found");
    }

    [Theory]
    [InlineData(48000, 96000, 96000)] // atom wins over the overflowed sample-entry rate
    [InlineData(0, 44100, 44100)]     // TagLib read nothing: the old fallback still works
    [InlineData(44100, 0, 44100)]     // no usable atom: TagLib's value stays
    [InlineData(44100, 44100, 44100)]
    public void ResolveAlacSampleRate_PrefersAValidAtomRate(int tagLib, int atom, int expected) =>
        Assert.Equal(expected, MetadataService.ResolveAlacSampleRate(tagLib, atom));
}
