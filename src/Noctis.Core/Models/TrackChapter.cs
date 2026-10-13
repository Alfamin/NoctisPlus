namespace Noctis.Models;

/// <summary>
/// A chapter mark inside one audio file (an .m4b audiobook's chapters): where it starts and
/// what it is called. <see cref="Title"/> may be empty when the file names none.
/// </summary>
public sealed record TrackChapter(TimeSpan Start, string Title)
{
    /// <summary>
    /// Index of the chapter playing at <paramref name="position"/>: the last one starting at or
    /// before it, or -1 before the first (or with no chapters). <paramref name="chapters"/> is
    /// sorted by start, as <see cref="Services.Mp4ChapterReader"/> returns them.
    /// </summary>
    public static int IndexAt(IReadOnlyList<TrackChapter> chapters, TimeSpan position)
    {
        var index = -1;
        for (var i = 0; i < chapters.Count && chapters[i].Start <= position; i++)
            index = i;
        return index;
    }
}
