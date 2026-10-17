namespace Noctis.Services.LyricsStudio;

/// <summary>Which times a nudge moves: start and end together, or one edge.</summary>
public enum NudgeEdge { Whole, Start, End }

/// <summary>
/// One review line's times as the Studio keeps them: word starts in order (a word ends where
/// the next one starts, the last one at <see cref="End"/>), so moving one word's end is moving
/// the next word's start.
/// </summary>
public sealed record NudgeLine(IReadOnlyList<TimeSpan> WordStarts, TimeSpan End)
{
    public bool SameTimes(NudgeLine other) => End == other.End && WordStarts.SequenceEqual(other.WordStarts);
}

/// <summary>
/// The rules behind the Studio's nudge buttons and keys (user ask, Discord "Manual lyrics
/// editing" 10-14). One nudge moves the selected line, or one word of it, by a step:
/// <list type="bullet">
/// <item>Clamp, never push: only the chosen times move. A nudge that would cross a limit stops
/// at it; a time already past that limit (a file with crossed lines) stays put rather than
/// jump, and a nudge away from it moves by the step only.</item>
/// <item>Nothing goes below 0:00 or past the song's end (when its length is known).</item>
/// <item>A line's start stays between the previous and next lines' starts, so lines never
/// change order. A line's end may run into the next line (duets and overlapping lines are
/// real); it only stays after its own last word.</item>
/// <item>A word stays inside its line and in order: every word keeps at least
/// <see cref="Gap"/> (10 ms, the precision LRC writes) after the one before it, and the line
/// end stays at least that far past its last word.</item>
/// <item>A whole line moves with its words; a line edge on a line with no word timings
/// (words spread evenly) re-spreads its words over the new span.</item>
/// </list>
/// </summary>
public static class TimingNudge
{
    /// <summary>The least time kept between neighbouring starts, and between a last word and its line end.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(10);

    /// <summary>The small step (LRC's 10 ms precision) and the large one.</summary>
    public static readonly TimeSpan FineStep = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan CoarseStep = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Unbounded = TimeSpan.MaxValue / 4;

    /// <summary>
    /// The line's times after one nudge; the same times when nothing may move.
    /// </summary>
    /// <param name="wordIndex">The word to move, or -1 for the whole line.</param>
    /// <param name="previousStart">The previous line's start; null for the first line.</param>
    /// <param name="nextStart">The next line's start; null for the last line.</param>
    /// <param name="duration">The song's length; zero or less when unknown (no upper limit).</param>
    /// <param name="spread">The line's words are an even spread, not timed: a line edge re-spreads them.</param>
    public static NudgeLine Apply(NudgeLine line, int wordIndex, NudgeEdge edge, TimeSpan delta,
        TimeSpan? previousStart, TimeSpan? nextStart, TimeSpan duration, bool spread = false)
    {
        var s = line.WordStarts.ToArray();
        var end = line.End;
        var n = s.Length;
        if (n == 0 || delta == TimeSpan.Zero || wordIndex >= n) return line;

        var floor = previousStart is { } p ? Max(p + Gap, TimeSpan.Zero) : TimeSpan.Zero;
        var endCeiling = duration > TimeSpan.Zero ? duration : Unbounded;
        var startCeiling = nextStart is { } q ? q - Gap : endCeiling - Gap;

        if (wordIndex < 0)
        {
            switch (edge)
            {
                case NudgeEdge.Whole:
                {
                    var d = Move(TimeSpan.Zero, delta, floor - s[0], Min(startCeiling - s[0], endCeiling - end));
                    for (var i = 0; i < n; i++) s[i] += d;
                    end += d;
                    break;
                }
                case NudgeEdge.Start when spread:
                {
                    var start = Move(s[0], delta, floor, Min(startCeiling, end - Gap * n));
                    Spread(s, start, end);
                    break;
                }
                case NudgeEdge.Start:
                    s[0] = Move(s[0], delta, floor, Min(startCeiling, (n > 1 ? s[1] : end) - Gap));
                    break;
                case NudgeEdge.End when spread:
                    end = Move(end, delta, s[0] + Gap * n, endCeiling);
                    Spread(s, s[0], end);
                    break;
                default:
                    end = Move(end, delta, s[^1] + Gap, endCeiling);
                    break;
            }
            return Result(line, s, end);
        }

        var w = wordIndex;
        // Where this word's start may go: after the word before it (or, for the first word,
        // after the previous line's start); before the word after it (or the line end).
        var startLow = w == 0 ? floor : s[w - 1] + Gap;
        var startHigh = (w + 1 < n ? s[w + 1] : end) - Gap;
        if (w == 0) startHigh = Min(startHigh, startCeiling);
        // Where its end may go: the next word's start, kept before the word after that (or the
        // line end); for the last word, the line end itself.
        var endHigh = w + 1 < n ? (w + 2 < n ? s[w + 2] : end) - Gap : endCeiling;

        switch (edge)
        {
            case NudgeEdge.Start:
                s[w] = Move(s[w], delta, startLow, startHigh);
                break;
            case NudgeEdge.End when w + 1 < n:
                s[w + 1] = Move(s[w + 1], delta, s[w] + Gap, endHigh);
                break;
            case NudgeEdge.End:
                end = Move(end, delta, s[w] + Gap, endHigh);
                break;
            default:
            {
                var wordEnd = w + 1 < n ? s[w + 1] : end;
                var high = endHigh - wordEnd;
                if (w == 0) high = Min(high, startCeiling - s[w]);
                var d = Move(TimeSpan.Zero, delta, startLow - s[w], high);
                s[w] += d;
                if (w + 1 < n) s[w + 1] += d;
                else end += d;
                break;
            }
        }
        return Result(line, s, end);
    }

    /// <summary>
    /// <paramref name="value"/> moved by <paramref name="delta"/>: a later move stops at
    /// <paramref name="high"/>, an earlier one at <paramref name="low"/>. A value already past
    /// the limit it moves toward stays put; one past the other limit moves back by the step only.
    /// </summary>
    private static TimeSpan Move(TimeSpan value, TimeSpan delta, TimeSpan low, TimeSpan high)
    {
        var target = value + delta;
        if (delta > TimeSpan.Zero && target > high) return Max(value, high);
        if (delta < TimeSpan.Zero && target < low) return Min(value, low);
        return target;
    }

    private static void Spread(TimeSpan[] starts, TimeSpan start, TimeSpan end)
    {
        var slice = (end - start) / starts.Length;
        for (var i = 0; i < starts.Length; i++) starts[i] = start + slice * i;
    }

    private static NudgeLine Result(NudgeLine line, TimeSpan[] starts, TimeSpan end)
    {
        var moved = new NudgeLine(starts, end);
        return moved.SameTimes(line) ? line : moved;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
