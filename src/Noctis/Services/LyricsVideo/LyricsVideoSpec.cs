using Noctis.Models;

namespace Noctis.Services.LyricsVideo;

/// <summary>Owner #243: how the lyrics page is arranged in the Reel.</summary>
public enum LyricsVideoLayout
{
    /// <summary>The desktop lyrics page (cover and title left, lyrics right) as a wide panel framed in the Reel.</summary>
    SideBySide,
    /// <summary>Small cover and title in one row at the top, the lyrics below.</summary>
    CompactHeader,
    /// <summary>Cover, title and artist on top, the lyrics flowing below.</summary>
    FullPage,
}

/// <summary>Owner #243: what moves behind the page.</summary>
public enum LyricsVideoBackground
{
    /// <summary>The lyrics page's flowing cover: the blurred cover drifting in two copies, breathing on the beat.</summary>
    FlowingCover,
    /// <summary>A slow liquid field of the cover's own colours.</summary>
    Aurora,
}

/// <summary>One sung word: its text as laid out (trailing space included) and its track-time window in seconds.</summary>
public sealed record LyricsVideoWord(string Text, double Start, double End, bool LeadIn, bool TrailOut,
    bool IsEmphasis, double HeldMs, bool IsLastInLine, int EmphasisCharCount);

/// <summary>One lyric line in track seconds. <see cref="Words"/> null: the line is lit as a whole.</summary>
public sealed record LyricsVideoLine(string Text, double Start, double? End, IReadOnlyList<LyricsVideoWord>? Words)
{
    /// <summary>The page's lines as the video draws them: synced, non-empty, not the intro dots.</summary>
    public static IReadOnlyList<LyricsVideoLine> From(IEnumerable<LyricLine> lines)
    {
        var result = new List<LyricsVideoLine>();
        foreach (var line in lines)
        {
            if (line.IsIntroPlaceholder || line.IsBackgroundOnly || line.Timestamp is not { } start) continue;
            var text = (line.Text ?? "").Trim();
            if (text.Length == 0) continue;
            IReadOnlyList<LyricsVideoWord>? words = null;
            if (line.Words is { Count: > 0 } timed)
            {
                var list = new List<LyricsVideoWord>(timed.Count);
                for (var i = 0; i < timed.Count; i++)
                {
                    var w = timed[i];
                    var end = w.End ?? (i + 1 < timed.Count ? timed[i + 1].Start : line.EndTimestamp ?? w.Start + TimeSpan.FromSeconds(0.6));
                    list.Add(new LyricsVideoWord(w.Text, w.Start.TotalSeconds, Math.Max(w.Start.TotalSeconds, end.TotalSeconds),
                        w.SweepLeadIn, w.SweepTrailOut, w.IsEmphasis, w.HeldDurationMs, w.IsLastInLine, w.EmphasisCharCount));
                }
                if (list.Any(w => w.Text.Trim().Length > 0)) words = list;
            }
            result.Add(new LyricsVideoLine(text, start.TotalSeconds, line.EndTimestamp?.TotalSeconds, words));
        }
        return result;
    }

    /// <summary>When the singing of this line is over: its end, its last word's end, else <paramref name="next"/>'s start.</summary>
    public double SungEnd(LyricsVideoLine? next) =>
        End ?? (Words is { Count: > 0 } w ? w[^1].End : next?.Start ?? Start + 4);
}

/// <summary>Everything one Reel needs. Times are track seconds; the clip runs over lines <see cref="FirstLine"/>..<see cref="LastLine"/>.</summary>
public sealed record LyricsVideoSpec
{
    public const int Width = 1080;
    public const int Height = 1920;
    public const int Fps = 30;

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public bool IsExplicit { get; init; }
    public string? CoverPath { get; init; }
    /// <summary>Every synced line of the song (lines outside the clip still show above and below it).</summary>
    public required IReadOnlyList<LyricsVideoLine> Lines { get; init; }
    public int FirstLine { get; init; }
    public int LastLine { get; init; }
    public LyricsVideoLayout Layout { get; init; } = LyricsVideoLayout.SideBySide;
    public LyricsVideoBackground Background { get; init; } = LyricsVideoBackground.FlowingCover;
    public bool Intro { get; init; } = true;
    public bool Outro { get; init; } = true;
    /// <summary>Beats per minute for the background's pulse when no audio is analysed (0: none).</summary>
    public int Bpm { get; init; }

    /// <summary>Length of the intro: the page arrives while the music already plays.</summary>
    public const double IntroSeconds = 2.0;
    /// <summary>Without an intro the first line still gets a short run-up, so it is not sung on frame one.</summary>
    public const double PlainLeadSeconds = 0.9;
    public const double OutroSeconds = 2.6;
    public const double PlainTailSeconds = 0.8;
    /// <summary>Reels runs up to 90 s; longer picks are cut at the line that crosses it.</summary>
    public const double MaxSeconds = 90;

    /// <summary>Track time of the video's first frame.</summary>
    public double ClipStart => Math.Max(0, Lines[FirstLine].Start - (Intro ? IntroSeconds : PlainLeadSeconds));

    /// <summary>Track time the last picked line is over (its sung end).</summary>
    public double LyricsEnd => Lines[LastLine].SungEnd(LastLine + 1 < Lines.Count ? Lines[LastLine + 1] : null);

    /// <summary>Track time of the video's end.</summary>
    public double ClipEnd => LyricsEnd + (Outro ? OutroSeconds : PlainTailSeconds);

    public double Duration => ClipEnd - ClipStart;

    public int FrameCount => (int)Math.Ceiling(Duration * Fps);
}
