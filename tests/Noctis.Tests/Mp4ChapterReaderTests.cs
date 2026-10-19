using System;
using System.IO;
using System.Linq;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Chapters of an MP4 audiobook (.m4b, Discord Luwi): the fixture is 3 s of AAC with three
/// chapters written by ffmpeg, which stores them twice — a QuickTime chapter text track
/// (tref/chap) and a Nero chpl box. Each layout is read alone by renaming the other's box
/// in a copy, so real files carrying only one of them are covered too.
/// </summary>
public class Mp4ChapterReaderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", name);

    private static readonly (double Start, string Title)[] Expected =
    {
        (0, "Opening Credits"),
        (1, "Chapter One — Начало"),
        (2.5, "Epilogue"),
    };

    private static void AssertExpected(System.Collections.Generic.IReadOnlyList<TrackChapter> chapters)
    {
        Assert.Equal(Expected.Length, chapters.Count);
        for (var i = 0; i < Expected.Length; i++)
        {
            Assert.Equal(Expected[i].Start, chapters[i].Start.TotalSeconds, 3);
            Assert.Equal(Expected[i].Title, chapters[i].Title);
        }
    }

    /// <summary>The fixture's bytes with every <paramref name="from"/> box type renamed.</summary>
    private static MemoryStream Patched(string from, string to)
    {
        var bytes = File.ReadAllBytes(Fixture("aac_chapters_tiny.m4b"));
        var needle = Encoding.ASCII.GetBytes(from);
        var hits = 0;
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, 4).SequenceEqual(needle)) continue;
            Encoding.ASCII.GetBytes(to).CopyTo(bytes, i);
            hits++;
        }
        Assert.Equal(1, hits);
        return new MemoryStream(bytes);
    }

    [Fact]
    public void Reads_BothLayouts_FromThePath() =>
        AssertExpected(Mp4ChapterReader.Read(Fixture("aac_chapters_tiny.m4b")));

    [Fact]
    public void Reads_QuickTimeChapterTrack_Alone()
    {
        using var stream = Patched("chpl", "free");
        AssertExpected(Mp4ChapterReader.Read(stream));
    }

    [Fact]
    public void Reads_NeroChapters_Alone()
    {
        // tref/chap renamed: no track points at the chapter text any more.
        using var stream = Patched("chap", "xxxx");
        AssertExpected(Mp4ChapterReader.Read(stream));
    }

    [Fact]
    public void FileWithoutChapters_ReadsNone() =>
        Assert.Empty(Mp4ChapterReader.Read(Fixture("alac_96k_tiny.m4a")));

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(4000)]
    public void TruncatedOrNonMp4_ReadsNoneWithoutThrowing(int length)
    {
        var bytes = File.ReadAllBytes(Fixture("aac_chapters_tiny.m4b")).Take(length).ToArray();
        using var stream = new MemoryStream(bytes);
        Assert.Empty(Mp4ChapterReader.Read(stream));
    }

    [Fact]
    public void Garbage_ReadsNone()
    {
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        using var stream = new MemoryStream(bytes);
        Assert.Empty(Mp4ChapterReader.Read(stream));
    }

    [Fact]
    public void MissingFile_ReadsNone() =>
        Assert.Empty(Mp4ChapterReader.Read(Path.Combine(Path.GetTempPath(), $"noctis-{Guid.NewGuid():N}.m4b")));

    [Theory]
    [InlineData("book.m4b", true)]
    [InlineData("song.M4A", true)]
    [InlineData("clip.mp4", true)]
    [InlineData("song.mp3", false)]
    [InlineData("song.flac", false)]
    [InlineData(null, false)]
    public void IsMp4Family(string? path, bool expected) =>
        Assert.Equal(expected, Mp4ChapterReader.IsMp4Family(path));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.99, 0)]
    [InlineData(1, 1)]
    [InlineData(2.4, 1)]
    [InlineData(2.5, 2)]
    [InlineData(99, 2)]
    public void IndexAt_IsTheLastChapterStartedByThePosition(double seconds, int expected)
    {
        var chapters = Expected.Select(e => new TrackChapter(TimeSpan.FromSeconds(e.Start), e.Title)).ToList();
        Assert.Equal(expected, TrackChapter.IndexAt(chapters, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void IndexAt_BeforeTheFirstChapter_IsMinusOne()
    {
        var chapters = new[] { new TrackChapter(TimeSpan.FromSeconds(5), "Late start") };
        Assert.Equal(-1, TrackChapter.IndexAt(chapters, TimeSpan.FromSeconds(1)));
        Assert.Equal(-1, TrackChapter.IndexAt(Array.Empty<TrackChapter>(), TimeSpan.Zero));
    }
}
