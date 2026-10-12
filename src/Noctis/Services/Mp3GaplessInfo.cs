using System.Buffers.Binary;

namespace Noctis.Services;

/// <summary>
/// The encoder delay and padding an MP3 carries in its LAME tag (the extension of the
/// Xing / Info header in the first frame), which a gapless player trims: the delay is
/// silence the encoder put in front of the audio, the padding is what fills out the last
/// frame (GitHub #127).
///
/// LibVLC 3 only trims them when its own MP3 demuxer feeds the mpg123 decoder. Under the
/// avformat demuxer, which Windows, macOS and the Linux AppImage force for fast VBR seeks,
/// FFmpeg consumes the Info frame, so every track ends with the padding as extra samples
/// (harness 10-11, VLC 3.0.20 and 3.0.23 alike: 973 / 602 / 190 extra samples on three
/// LAME files = padding - 529) — a 4-50 ms hole at every track boundary even when the
/// splice itself is sample-exact. The gapless engine cuts that tail off with this.
/// </summary>
internal readonly record struct Mp3GaplessInfo(int SampleRate, int EncoderDelay, int EncoderPadding, bool Vbr)
{
    /// <summary>The MP3 decoder's own delay (mpg123 / FFmpeg / LAME's decoder: 528 + 1).</summary>
    internal const int DecoderDelay = 529;

    // Enough to step over a normal ID3v2 tag without its pictures and still find the
    // first frame; the tag's own size skips large artwork by seeking.
    private const int SyncSearchBytes = 64 * 1024;
    private const int FirstFrameBytes = 512;

    /// <summary>Samples of padding a decoder emits after the audio: the tag's padding minus
    /// the decoder delay it includes (never below zero).</summary>
    public int DecodedTailPadding => Math.Max(0, EncoderPadding - DecoderDelay);

    /// <summary>
    /// Frames to cut from the end of the engine's segment for this file, at the engine's
    /// <paramref name="outputSampleRate"/> (LibVLC resamples to it before the engine sees
    /// the audio).
    /// </summary>
    public int TailTrimFrames(int outputSampleRate) =>
        SampleRate <= 0 || outputSampleRate <= 0
            ? 0
            : (int)Math.Round((double)DecodedTailPadding * outputSampleRate / SampleRate);

    /// <summary>Reads the LAME tag of a local MP3; false when the file has none or cannot be read.</summary>
    public static bool TryRead(string path, out Mp3GaplessInfo info)
    {
        info = default;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.SequentialScan);
            var header = new byte[10];
            long start = 0;
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length &&
                header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
            {
                start = Id3v2TotalSize(header);
            }
            if (start >= stream.Length) return false;
            stream.Position = start;
            var buffer = new byte[SyncSearchBytes + FirstFrameBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return TryParse(buffer.AsSpan(0, read), out info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Size of an ID3v2 tag from its 10-byte header, footer included. Pure.</summary>
    internal static long Id3v2TotalSize(ReadOnlySpan<byte> header)
    {
        var size = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14) | ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
        var footer = (header[5] & 0x10) != 0 ? 10 : 0;
        return 10L + size + footer;
    }

    /// <summary>
    /// Finds the first MPEG audio Layer III frame in <paramref name="data"/> (the bytes
    /// after any ID3v2 tag) and reads the LAME tag in it. Pure; internal for tests.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> data, out Mp3GaplessInfo info)
    {
        info = default;
        for (var i = 0; i + 4 <= data.Length && i <= SyncSearchBytes; i++)
        {
            if (!TryReadFrameHeader(data[i..], out var sampleRate, out var sideInfo))
                continue;
            var xing = i + 4 + sideInfo;
            if (xing + 8 > data.Length) return false;
            var tag = data.Slice(xing, 4);
            if (!tag.SequenceEqual("Xing"u8) && !tag.SequenceEqual("Info"u8))
                return false; // a plain first frame: no gapless information
            var flags = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(xing + 4, 4));
            var lame = xing + 8
                       + ((flags & 0x1) != 0 ? 4 : 0)    // frame count
                       + ((flags & 0x2) != 0 ? 4 : 0)    // byte count
                       + ((flags & 0x4) != 0 ? 100 : 0)  // seek table
                       + ((flags & 0x8) != 0 ? 4 : 0);   // quality
            // The LAME extension: a 9-byte encoder name ("LAME3.100", "Lavc62.28", ...),
            // then 12 bytes of fields, then delay and padding as two 12-bit numbers.
            if (lame + 24 > data.Length) return false;
            for (var c = 0; c < 4; c++)
            {
                var ch = data[lame + c];
                if (ch < 0x20 || ch > 0x7E) return false; // no LAME extension after the header
            }
            var d = data.Slice(lame + 21, 3);
            var delay = (d[0] << 4) | (d[1] >> 4);
            var padding = ((d[1] & 0x0F) << 8) | d[2];
            // LAME writes "Xing" for VBR/ABR files and "Info" for CBR ones.
            info = new Mp3GaplessInfo(sampleRate, delay, padding, Vbr: tag.SequenceEqual("Xing"u8));
            return true;
        }
        return false;
    }

    // MPEG audio frame header, Layer III only. sideInfo = bytes between the header and
    // the Xing/Info tag (17/32 for MPEG-1 mono/stereo, 9/17 for MPEG-2 and 2.5).
    private static bool TryReadFrameHeader(ReadOnlySpan<byte> h, out int sampleRate, out int sideInfo)
    {
        sampleRate = 0;
        sideInfo = 0;
        if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return false;
        var version = (h[1] >> 3) & 0x3;   // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5, 1 = reserved
        var layer = (h[1] >> 1) & 0x3;     // 1 = Layer III
        var bitrate = (h[2] >> 4) & 0xF;
        var rateIndex = (h[2] >> 2) & 0x3;
        if (version == 1 || layer != 1 || bitrate == 0xF || rateIndex == 3) return false;
        var baseRate = rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 };
        sampleRate = version switch { 3 => baseRate, 2 => baseRate / 2, _ => baseRate / 4 };
        var mono = ((h[3] >> 6) & 0x3) == 3;
        sideInfo = version == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        return true;
    }
}
