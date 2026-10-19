using System.Buffers.Binary;
using System.Text;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Reads the chapter list of an MP4-family file (.m4b audiobooks, .m4a, .mp4). TagLib# has
/// no chapter API, and LibVLC's (libvlc_media_player_get_full_chapter_descriptions) only
/// answers for the media a player has open — and with the forced avformat demuxer it named
/// every chapter after the album (harness 10-13: "Noctis Test Audiobook" x3, the native
/// demuxer read the real titles). So the two layouts are read here, straight from the boxes:
///
///   QuickTime chapter track — the audio trak's tref/chap names a text track whose samples
///     are the titles (16-bit length + UTF-8/UTF-16 text), timed by its stts. iTunes, Audible
///     conversions and ffmpeg write it; it wins when present.
///   Nero chapters — moov/udta/chpl: start (100 ns units) + title per entry. mp4v2/Nero
///     tools, and ffmpeg again (ffmpeg writes both).
///
/// Blocking file I/O — call off the UI thread. Never throws: a file without chapters, or
/// one that isn't MP4 at all, reads as no chapters.
/// </summary>
public static class Mp4ChapterReader
{
    // moov holds every sample table; a 30-hour audiobook's is a few MB. Anything far past
    // that is not a file worth reading chapters from.
    private const long MaxMoovBytes = 64L * 1024 * 1024;
    private const int MaxChapters = 10_000;
    private const int MaxTitleBytes = 4096;

    /// <summary>Extensions whose files are MP4 containers that may carry chapters.</summary>
    public static bool IsMp4Family(string? path) =>
        Path.GetExtension(path ?? string.Empty).ToLowerInvariant() is ".m4a" or ".m4b" or ".mp4" or ".alac";

    /// <summary>The file's chapters sorted by start; empty when it has none or can't be read.</summary>
    public static IReadOnlyList<TrackChapter> Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (Exception)
        {
            return Array.Empty<TrackChapter>();
        }
    }

    /// <summary>The chapters of the MP4 in <paramref name="stream"/> (seekable).</summary>
    public static IReadOnlyList<TrackChapter> Read(Stream stream)
    {
        try
        {
            var moov = ReadTopLevelBox(stream, "moov");
            if (moov == null) return Array.Empty<TrackChapter>();
            var chapters = ReadChapterTrack(stream, moov);
            if (chapters.Count == 0) chapters = ReadNeroChapters(moov);
            chapters.Sort((a, b) => a.Start.CompareTo(b.Start));
            return chapters;
        }
        catch (Exception)
        {
            // Truncated or malformed boxes: no chapters rather than a failed play.
            return Array.Empty<TrackChapter>();
        }
    }

    // ── Box walking ──

    /// <summary>The payload of the first top-level box of <paramref name="type"/>, or null
    /// (also MetadataService's way to the ALAC config atom when moov follows mdat).</summary>
    internal static byte[]? ReadTopLevelBox(Stream stream, string type)
    {
        var header = new byte[16];
        long pos = 0, length = stream.Length;
        while (pos + 8 <= length)
        {
            stream.Position = pos;
            stream.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(header, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - pos; // runs to the end of the file
            }
            if (size < headerSize || pos + size > length) return null;

            if (Encoding.ASCII.GetString(header, 4, 4) == type)
            {
                var payloadSize = size - headerSize;
                if (payloadSize > MaxMoovBytes) return null;
                var payload = new byte[payloadSize];
                stream.ReadExactly(payload);
                return payload;
            }
            pos += size;
        }
        return null;
    }

    /// <summary>A child box: its four-character type and payload range within the buffer.</summary>
    private readonly record struct Box(string Type, int Start, int End);

    private static IEnumerable<Box> Children(byte[] data, int start, int end)
    {
        var pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var headerSize = 8;
            if (size == 1)
            {
                if (pos + 16 > end) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < headerSize || pos + size > end) yield break;
            yield return new Box(Encoding.ASCII.GetString(data, pos + 4, 4), pos + headerSize, (int)(pos + size));
            pos += (int)size;
        }
    }

    private static Box? Child(byte[] data, Box parent, string type) =>
        Children(data, parent.Start, parent.End).Cast<Box?>().FirstOrDefault(b => b!.Value.Type == type);

    private static Box? Descend(byte[] data, Box root, params string[] types)
    {
        Box? box = root;
        foreach (var type in types)
        {
            box = Child(data, box.Value, type);
            if (box == null) return null;
        }
        return box;
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));

    // ── QuickTime chapter track ──

    private static List<TrackChapter> ReadChapterTrack(Stream stream, byte[] moov)
    {
        var root = new Box("moov", 0, moov.Length);
        var tracks = Children(moov, root.Start, root.End).Where(b => b.Type == "trak").ToList();

        var chapterIds = new List<uint>();
        foreach (var trak in tracks)
            if (Descend(moov, trak, "tref", "chap") is { } chap)
                for (var p = chap.Start; p + 4 <= chap.End; p += 4)
                    chapterIds.Add(U32(moov, p));

        foreach (var id in chapterIds)
        {
            if (tracks.Cast<Box?>().FirstOrDefault(t => TrackId(moov, t!.Value) == id) is not { } trak) continue;
            // Text titles only: a chapter track can also be a picture track (chapter art).
            var handler = Descend(moov, trak, "mdia", "hdlr") is { } hdlr && hdlr.End - hdlr.Start >= 12
                ? Encoding.ASCII.GetString(moov, hdlr.Start + 8, 4) : string.Empty;
            if (handler is not ("text" or "sbtl")) continue;
            var chapters = ReadTextSamples(stream, moov, trak);
            if (chapters.Count > 0) return chapters;
        }
        return new List<TrackChapter>();
    }

    private static uint TrackId(byte[] moov, Box trak)
    {
        if (Child(moov, trak, "tkhd") is not { } tkhd) return 0;
        var version = moov[tkhd.Start];
        var offset = tkhd.Start + (version == 1 ? 20 : 12); // after version/flags + creation/modification times
        return offset + 4 <= tkhd.End ? U32(moov, offset) : 0;
    }

    private static List<TrackChapter> ReadTextSamples(Stream stream, byte[] moov, Box trak)
    {
        var result = new List<TrackChapter>();
        if (Descend(moov, trak, "mdia", "mdhd") is not { } mdhd) return result;
        var timescaleOffset = mdhd.Start + (moov[mdhd.Start] == 1 ? 20 : 12);
        if (timescaleOffset + 4 > mdhd.End) return result;
        var timescale = U32(moov, timescaleOffset);
        if (timescale == 0) return result;

        if (Descend(moov, trak, "mdia", "minf", "stbl") is not { } stbl) return result;
        var starts = SampleStarts(moov, stbl);
        var offsets = SampleOffsets(moov, stbl, out var sizes);
        var count = Math.Min(Math.Min(starts.Count, offsets.Count), MaxChapters);

        var buffer = new byte[MaxTitleBytes];
        for (var i = 0; i < count; i++)
        {
            var size = (int)Math.Min(sizes[i], MaxTitleBytes);
            if (size < 2 || offsets[i] + size > stream.Length) continue;
            stream.Position = offsets[i];
            stream.ReadExactly(buffer, 0, size);
            var textLength = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(buffer), size - 2);
            var start = TimeSpan.FromSeconds(starts[i] / (double)timescale);
            result.Add(new TrackChapter(start, DecodeTitle(buffer.AsSpan(2, textLength))));
        }
        return result;
    }

    /// <summary>Each sample's start in the track's timescale, from the stts run lengths.</summary>
    private static List<ulong> SampleStarts(byte[] moov, Box stbl)
    {
        var starts = new List<ulong>();
        if (Child(moov, stbl, "stts") is not { } stts || stts.Start + 8 > stts.End) return starts;
        var entries = U32(moov, stts.Start + 4);
        ulong time = 0;
        var p = stts.Start + 8;
        for (uint e = 0; e < entries && p + 8 <= stts.End && starts.Count < MaxChapters; e++, p += 8)
        {
            var samples = U32(moov, p);
            var delta = U32(moov, p + 4);
            for (uint s = 0; s < samples && starts.Count < MaxChapters; s++)
            {
                starts.Add(time);
                time += delta;
            }
        }
        return starts;
    }

    /// <summary>Each sample's file offset (stsc chunk map over stco/co64) and size (stsz).</summary>
    private static List<long> SampleOffsets(byte[] moov, Box stbl, out List<uint> sizes)
    {
        var offsets = new List<long>();
        sizes = new List<uint>();
        if (Child(moov, stbl, "stsz") is not { } stsz || stsz.Start + 12 > stsz.End) return offsets;
        var uniformSize = U32(moov, stsz.Start + 4);
        var sampleCount = Math.Min(U32(moov, stsz.Start + 8), MaxChapters);
        for (var i = 0; i < sampleCount; i++)
        {
            if (uniformSize != 0) sizes.Add(uniformSize);
            else if (stsz.Start + 12 + i * 4 + 4 <= stsz.End) sizes.Add(U32(moov, stsz.Start + 12 + i * 4));
            else break;
        }

        var chunks = new List<long>();
        if (Child(moov, stbl, "stco") is { } stco && stco.Start + 8 <= stco.End)
        {
            var n = U32(moov, stco.Start + 4);
            for (var p = stco.Start + 8; chunks.Count < n && p + 4 <= stco.End; p += 4) chunks.Add(U32(moov, p));
        }
        else if (Child(moov, stbl, "co64") is { } co64 && co64.Start + 8 <= co64.End)
        {
            var n = U32(moov, co64.Start + 4);
            for (var p = co64.Start + 8; chunks.Count < n && p + 8 <= co64.End; p += 8) chunks.Add((long)U64(moov, p));
        }
        if (Child(moov, stbl, "stsc") is not { } stsc || stsc.Start + 8 > stsc.End) return offsets;

        // stsc runs: (first chunk, samples per chunk, description); a run lasts until the next one's first chunk.
        var runs = new List<(uint FirstChunk, uint SamplesPerChunk)>();
        var entries = U32(moov, stsc.Start + 4);
        for (var p = stsc.Start + 8; runs.Count < entries && p + 12 <= stsc.End; p += 12)
            runs.Add((U32(moov, p), U32(moov, p + 4)));

        var sample = 0;
        for (var r = 0; r < runs.Count && sample < sizes.Count; r++)
        {
            var lastChunk = r + 1 < runs.Count ? runs[r + 1].FirstChunk - 1 : (uint)chunks.Count;
            for (var chunk = runs[r].FirstChunk; chunk <= lastChunk && chunk >= 1 && chunk <= chunks.Count && sample < sizes.Count; chunk++)
            {
                var offset = chunks[(int)chunk - 1];
                for (uint s = 0; s < runs[r].SamplesPerChunk && sample < sizes.Count; s++)
                {
                    offsets.Add(offset);
                    offset += sizes[sample++];
                }
            }
        }
        return offsets;
    }

    private static string DecodeTitle(ReadOnlySpan<byte> text)
    {
        // QuickTime text samples are UTF-8 unless they open with a UTF-16 byte-order mark.
        if (text.Length >= 2 && text[0] == 0xFE && text[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(text[2..]).Trim();
        if (text.Length >= 2 && text[0] == 0xFF && text[1] == 0xFE)
            return Encoding.Unicode.GetString(text[2..]).Trim();
        return Encoding.UTF8.GetString(text).TrimEnd('\0').Trim();
    }

    // ── Nero chapters (moov/udta/chpl) ──

    private static List<TrackChapter> ReadNeroChapters(byte[] moov)
    {
        var result = new List<TrackChapter>();
        if (Descend(moov, new Box("moov", 0, moov.Length), "udta", "chpl") is not { } chpl) return result;
        // version (1) + flags (3), then — in version 1, which every writer uses — 4 reserved bytes.
        var p = chpl.Start + 4 + (moov[chpl.Start] != 0 ? 4 : 0);
        if (p >= chpl.End) return result;
        int count = moov[p++];
        for (var i = 0; i < count && p + 9 <= chpl.End; i++)
        {
            var start = U64(moov, p);
            int length = moov[p + 8];
            p += 9;
            if (p + length > chpl.End) break;
            // 100 ns units: exactly TimeSpan ticks.
            var ticks = start > (ulong)TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue.Ticks : (long)start;
            result.Add(new TrackChapter(TimeSpan.FromTicks(ticks), DecodeTitle(moov.AsSpan(p, length))));
            p += length;
        }
        return result;
    }
}
