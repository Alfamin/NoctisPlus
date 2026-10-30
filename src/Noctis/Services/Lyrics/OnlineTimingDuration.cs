using System.Buffers.Binary;
using Noctis.Models;
using Noctis.Features.ProgressivePlayback;

namespace Noctis.Services.Lyrics;

/// <summary>Use the recording's length, not a catalog estimate (#224). On partial files only
/// explicit total-sample/frame counts are safe; never estimate length from downloaded bytes.</summary>
internal static class OnlineTimingDuration
{
    public static double Seconds(Track track, string? recordingPath = null)
    {
        var path = recordingPath ?? track.FilePath;
        try
        {
            // Playback exposes the final name before the .part file is renamed. Read
            // only published bytes from its transfer instead of falling back to catalog length.
            if (ProgressiveTransfer.ReadPrefix(path) is { } prefix)
            {
                var offset = prefix.Length >= 10 && prefix.AsSpan(0, 3).SequenceEqual("ID3"u8) ? Mp3GaplessInfo.Id3v2TotalSize(prefix) : 0;
                if (offset >= 0 && offset < prefix.Length && HeaderSeconds(prefix.AsSpan((int)offset)) is { } partial) return partial;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[10];
            if (stream.ReadAtLeast(head, 10, false) == 10)
            {
                stream.Position = head[..3].SequenceEqual("ID3"u8) ? Mp3GaplessInfo.Id3v2TotalSize(head) : 0;
                var data = new byte[65536];
                var count = stream.ReadAtLeast(data, data.Length, false);
                if (HeaderSeconds(data.AsSpan(0, count)) is { } seconds) return seconds;
            }
            if (!ProgressiveTransfer.IsDownloading(path) && (recordingPath != null || !track.IsPendingStream && !track.IsRemoteStream))
            {
                using var file = TagLib.File.Create(path);
                if (file.Properties.Duration.TotalSeconds > 0) return file.Properties.Duration.TotalSeconds;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException) { }
        return track.Duration.TotalSeconds;
    }

    internal static double? HeaderSeconds(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 42 && data[..4].SequenceEqual("fLaC"u8) && (data[4] & 127) == 0
            && data[5] == 0 && data[6] == 0 && data[7] == 34)
        {
            var packed = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(18, 8));
            var rate = packed >> 44;
            var samples = packed & 0xFFFFFFFFFUL;
            return rate > 0 && samples > 0 ? (double)samples / rate : null;
        }
        for (var i = 0; i + 4 <= data.Length; i++)
        {
            if (!Mp3GaplessInfo.TryReadFrameHeader(data[i..], out var rate, out var side)) continue;
            var at = i + 4 + side + ((data[i + 1] & 1) == 0 ? 2 : 0);
            if (at + 12 > data.Length) continue;
            if (!data.Slice(at, 4).SequenceEqual("Xing"u8) && !data.Slice(at, 4).SequenceEqual("Info"u8)) continue;
            if ((BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at + 4, 4)) & 1) == 0) continue;
            var frames = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at + 8, 4));
            var samples = ((data[i + 1] >> 3) & 3) == 3 ? 1152 : 576;
            return frames > 0 ? (double)frames * samples / rate : null;
        }
        return null;
    }
}
