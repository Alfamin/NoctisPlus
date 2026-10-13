using Noctis.Models;

namespace Noctis.Services;

/// <summary>Result of one <see cref="LyricsTimeline.Update"/>.</summary>
/// <param name="LineChanged">True when the active line is a different line than before the call.</param>
/// <param name="ActiveIndex">Index of the active line in the timeline's list, or -1.</param>
/// <param name="ActiveLine">The active line, or null before the first synced line.</param>
/// <param name="ActiveSetChanged">True when <see cref="LyricsTimeline.ActiveIndices"/> changed —
/// always when <paramref name="LineChanged"/>, and also when an overlapping earlier line
/// joined or left the set while the active line stayed.</param>
public readonly record struct LyricsTimelineStep(
    bool LineChanged, int ActiveIndex, LyricLine? ActiveLine, bool ActiveSetChanged);

/// <summary>
/// The synced-lyrics engine, UI-free: walks a monotonic line cursor per position
/// sample (O(1) amortised, rewinds on seek), flips <see cref="LyricLine.IsActive"/>
/// on the outgoing/incoming lines, and drives the word layers of the active lines
/// (<see cref="LyricLine.CurrentWordIndex"/>, <see cref="LyricLine.BackgroundWordIndex"/>,
/// <see cref="LyricLine.TransliterationWordIndex"/>, <see cref="WordTiming.Progress"/>).
/// Lifted verbatim from the desktop LyricsViewModel so the Android lyrics page and the
/// desktop page share one clock.
/// The caller owns the line list and feeds the audible position (engine position
/// minus output latency, smoothed by <see cref="LyricsPlaybackClock"/>).
///
/// Overlapping lines (TTML duets: each &lt;p&gt; has its own begin/end, so the second
/// voice can start while the first is still singing) light together, as Apple Music
/// does: <see cref="ActiveLine"/> stays the newest started line (the scroll anchor), and
/// an earlier line whose sung end runs well past the next line's start stays
/// co-active — lit, its words sweeping on their own clock — until that end passes.
/// </summary>
public sealed class LyricsTimeline
{
    /// <summary>
    /// A line only stays lit past the next line's start when its sung end overlaps that
    /// start by at least this much. Shorter overlaps are boundary timing slop — ELRC end
    /// tags written a beat late, Apple's pre-roll on the incoming line — not two voices,
    /// and honouring them would flash the finished line lit for a frame or two.
    /// </summary>
    public static readonly TimeSpan MinOverlap = TimeSpan.FromMilliseconds(300);

    /// <summary>How many lines before the active one are checked for a still-sung
    /// overlap. Lines are sorted by start but not by end, so this bounds the scan.</summary>
    private const int CoActiveScanBack = 8;

    private readonly IReadOnlyList<LyricLine> _lines;
    private readonly TimeSpan _wordLookahead;
    private readonly TimeSpan _lineLookahead;

    private int _cursor;
    private TimeSpan _lastPosition = TimeSpan.MinValue;

    // The active set (co-active lines then the active line, ascending by index), and a
    // scratch pair the next set is resolved into before the two are swapped — reused
    // so the per-frame word clock allocates nothing.
    private List<LyricLine> _activeLines = new();
    private List<int> _activeIndices = new();
    private List<LyricLine> _nextLines = new();
    private List<int> _nextIndices = new();

    /// <param name="lines">The synced line list to walk; the caller owns and mutates it.</param>
    /// <param name="wordLookahead">Lead for word-timed lines (desktop: 80 ms). Small, so the
    /// sweep matches the vocal instead of trailing UI dispatch latency.</param>
    /// <param name="lineLookahead">Lead for lines WITHOUT word timings (desktop: 80 ms plus half
    /// the line-glide travel time). A plain line has no sweep, so its only cue is the glide
    /// bringing it to the anchor; activating it early lets the glide cover half its travel
    /// when the vocal starts.</param>
    public LyricsTimeline(IReadOnlyList<LyricLine> lines, TimeSpan wordLookahead, TimeSpan lineLookahead)
    {
        _lines = lines;
        _wordLookahead = wordLookahead;
        _lineLookahead = lineLookahead;
    }

    /// <summary>The active line, or null.</summary>
    public LyricLine? ActiveLine { get; private set; }

    /// <summary>Index of <see cref="ActiveLine"/> in the list, or -1.</summary>
    public int ActiveIndex { get; private set; } = -1;

    /// <summary>Indices of every lit line, ascending: still-sung overlapping lines before
    /// <see cref="ActiveIndex"/>, then <see cref="ActiveIndex"/> itself. Empty when nothing
    /// is active. The list is live — copy it to keep a snapshot.</summary>
    public IReadOnlyList<int> ActiveIndices => _activeIndices;

    /// <summary>The lines of <see cref="ActiveIndices"/>, in the same order.</summary>
    public IReadOnlyList<LyricLine> ActiveLines => _activeLines;

    /// <summary>Forget the cursor and the active lines (track change, lyrics reload).
    /// Deactivates the lines it was holding.</summary>
    public void Reset()
    {
        if (ActiveLine is { } l) l.IsActive = false;
        foreach (var line in _activeLines) line.IsActive = false;
        _activeLines.Clear();
        _activeIndices.Clear();
        ActiveLine = null;
        ActiveIndex = -1;
        Rewind();
    }

    /// <summary>
    /// Distance from line <paramref name="index"/> to the nearest lit line — the active
    /// line <paramref name="primaryIndex"/> or any of <paramref name="activeIndices"/> —
    /// so the opacity/blur ramps put every co-active line at full focus.
    /// </summary>
    public static int DistanceToNearestActive(int index, int primaryIndex, IReadOnlyList<int> activeIndices)
    {
        var best = Math.Abs(index - primaryIndex);
        for (int i = 0; i < activeIndices.Count && best > 0; i++)
            best = Math.Min(best, Math.Abs(index - activeIndices[i]));
        return best;
    }

    /// <summary>Rewind the cursor to the start without touching the active line; the next
    /// <see cref="Update"/> re-resolves from the top (an explicit seek).</summary>
    public void Rewind()
    {
        _cursor = 0;
        _lastPosition = TimeSpan.MinValue;
    }

    /// <summary>
    /// Advance to <paramref name="position"/>. Word-timed lines activate on the word
    /// lookahead, plain lines on the line lookahead: a single line-level lead would switch
    /// lines early, force-completing the outgoing line's final word sweep before its end
    /// and leaving the incoming words dark — a visible jump-then-pause at every boundary.
    /// </summary>
    public LyricsTimelineStep Update(TimeSpan position)
    {
        if (_lines.Count == 0) return new(false, ActiveIndex, ActiveLine, false);

        // Seek-backwards detection: rewind the cursor so we don't miss earlier lines.
        // 750ms threshold tolerates small non-monotonic jitter from the player position poll.
        if (_lastPosition != TimeSpan.MinValue &&
            position + TimeSpan.FromMilliseconds(750) < _lastPosition)
        {
            _cursor = 0;
        }
        _lastPosition = position;

        TimeSpan AdjustedFor(LyricLine l) => Adjusted(position, l);

        // Clamp cursor into range (collection may have shrunk).
        if (_cursor >= _lines.Count) _cursor = _lines.Count - 1;
        if (_cursor < 0) _cursor = 0;

        // Advance forward while the next synced line's timestamp has been reached.
        while (_cursor + 1 < _lines.Count)
        {
            var next = _lines[_cursor + 1];
            if (next.Timestamp.HasValue && next.Timestamp.Value <= AdjustedFor(next))
                _cursor++;
            else
                break;
        }

        // Mirror walk backwards. The forward walk above has no threshold, but rewinding
        // used to depend solely on the 750ms reset — and a timeline drag never produces
        // a drop that large in one sample, because this runs every 100ms off the sync
        // timer and every rendered frame off the word clock. The cursor stayed parked on
        // a later line, no candidate matched, and the safety branch below held the stale
        // line: dragging backwards froze the lyrics until release finally delivered a big
        // enough discontinuity. Stepping back per sample makes both directions resolve on
        // the same tick, and still costs nothing during normal forward playback.
        var rewound = false;
        while (_cursor > 0)
        {
            var current = _lines[_cursor];
            if (current.Timestamp.HasValue && current.Timestamp.Value > AdjustedFor(current))
            {
                _cursor--;
                rewound = true;
            }
            else
                break;
        }

        var candidate = _lines[_cursor];
        LyricLine? bestMatch = null;
        int bestIndex = -1;
        if (candidate.Timestamp.HasValue && candidate.Timestamp.Value <= AdjustedFor(candidate))
        {
            bestMatch = candidate;
            bestIndex = _cursor;
        }

        // Safety: if no match found but we're past the start and have a current line,
        // keep the current line active (prevents "all dimmed" state from transient glitches).
        // Skipped when the walk above rewound to the very first line and even that one is
        // still ahead: the position genuinely sits before the first lyric, so holding the
        // stale line there would re-freeze exactly what the backward walk exists to fix.
        if (bestMatch == null && !rewound && ActiveLine != null && position.TotalSeconds > 1)
        {
            // The held line stays; overlapping lines around it still come and go.
            var heldSetChanged = ResolveActiveSet(position);
            UpdateActiveWords(position);
            return new(false, ActiveIndex, ActiveLine, heldSetChanged);
        }

        var changed = bestMatch != ActiveLine;
        ActiveLine = bestMatch;
        ActiveIndex = bestIndex;
        var setChanged = ResolveActiveSet(position);

        UpdateActiveWords(position);
        return new(changed, ActiveIndex, ActiveLine, changed || setChanged);
    }

    private TimeSpan Adjusted(TimeSpan position, LyricLine l) =>
        position + (l.HasWords || l.HasBackgroundWords || l.HasTransliterationWords ? _wordLookahead : _lineLookahead);

    /// <summary>
    /// Rebuilds the active set around <see cref="ActiveLine"/>: earlier lines still being
    /// sung over it (see <see cref="IsStillSungOverlap"/>), then the active line. Lines
    /// leaving the set are deactivated, lines in it are (re)activated. Returns true when
    /// the set differs from the previous one.
    /// </summary>
    private bool ResolveActiveSet(TimeSpan position)
    {
        _nextLines.Clear();
        _nextIndices.Clear();
        var primary = ActiveLine;
        if (primary != null)
        {
            // The index is only trusted while it still points at the line (the safety
            // branch can hold a line across a list the caller has since mutated).
            if (ActiveIndex >= 0 && ActiveIndex < _lines.Count && ReferenceEquals(_lines[ActiveIndex], primary))
            {
                for (int i = Math.Max(0, ActiveIndex - CoActiveScanBack); i < ActiveIndex; i++)
                {
                    if (IsStillSungOverlap(i, position))
                    {
                        _nextLines.Add(_lines[i]);
                        _nextIndices.Add(i);
                    }
                }
            }
            _nextLines.Add(primary);
            _nextIndices.Add(ActiveIndex);
        }

        var changed = _nextLines.Count != _activeLines.Count;
        for (int i = 0; !changed && i < _nextLines.Count; i++)
            changed = !ReferenceEquals(_nextLines[i], _activeLines[i]) || _nextIndices[i] != _activeIndices[i];

        foreach (var line in _activeLines)
        {
            if (!_nextLines.Contains(line))
                Deactivate(line);
        }
        // Safety: ensure the active lines stay active even if something reset them.
        foreach (var line in _nextLines)
        {
            if (!line.IsActive)
                line.IsActive = true;
        }

        (_activeLines, _nextLines) = (_nextLines, _activeLines);
        (_activeIndices, _nextIndices) = (_nextIndices, _activeIndices);
        return changed;
    }

    /// <summary>
    /// True when the earlier line at <paramref name="index"/> is still being sung: it has
    /// started, its sung end (<see cref="SungEnd"/>) is still ahead on the same lookahead
    /// clock that activates it, and that end overlaps the next synced line's start by at
    /// least <see cref="MinOverlap"/>. Lines with no known end (plain LRC) never qualify.
    /// </summary>
    private bool IsStillSungOverlap(int index, TimeSpan position)
    {
        var line = _lines[index];
        if (line.Timestamp is not { } start || SungEnd(line) is not { } end) return false;
        if (NextSyncedLineStart(index) is not { } nextStart || end - nextStart < MinOverlap) return false;
        var adjusted = Adjusted(position, line);
        return start <= adjusted && adjusted < end;
    }

    /// <summary>The latest end any layer of the line knows (line end, last word ends,
    /// background and romanization ends); null when none is known.</summary>
    private static TimeSpan? SungEnd(LyricLine line)
    {
        TimeSpan? end = line.EndTimestamp;
        static TimeSpan? Later(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : (a > b ? a : b);
        if (line.HasWords) end = Later(end, line.Words![^1].End);
        end = Later(end, line.BackgroundEndTimestamp);
        if (line.HasBackgroundWords) end = Later(end, line.BackgroundWords![^1].End);
        end = Later(end, line.TransliterationEndTimestamp);
        if (line.HasTransliterationWords) end = Later(end, line.TransliterationWords![^1].End);
        return end;
    }

    /// <summary>
    /// Deactivates a line leaving the active set — leaving it fully swept (index past the
    /// end) so the bright overlay keeps covering the words while the base layer fades back
    /// to full opacity. Snapping to -1 here blanked the overlay instantly, which read as
    /// the finished line dimming for a beat. Re-entry recomputes the real index.
    /// </summary>
    private static void Deactivate(LyricLine line)
    {
        line.IsActive = false;
        if (line.HasWords)
            line.CurrentWordIndex = line.Words!.Count;
        if (line.HasBackgroundWords)
            line.BackgroundWordIndex = line.BackgroundWords!.Count;
        if (line.HasTransliterationWords)
            line.TransliterationWordIndex = line.TransliterationWords!.Count;
    }

    /// <summary>Drives the word layers of every active line, each on its own clock.</summary>
    private void UpdateActiveWords(TimeSpan position)
    {
        for (int i = 0; i < _activeLines.Count; i++)
            UpdateLineWords(_activeLines[i], _activeIndices[i], position);
    }

    /// <summary>
    /// Advances CurrentWordIndex on an active line when word-level timings are present.
    /// No-op when the line has no words — existing line-level highlight is all that renders.
    /// </summary>
    private void UpdateLineWords(LyricLine line, int index, TimeSpan position)
    {
        if (!line.HasWords && !line.HasBackgroundWords && !line.HasTransliterationWords) return;

        var adjusted = position + _wordLookahead;

        if (line.HasWords)
            DriveWordLayer(line.Words!, adjusted, line.EndTimestamp, index,
                line.CurrentWordIndex, i => line.CurrentWordIndex = i);

        // Background vocals (adlibs) run as an independent layer with their own clock.
        if (line.HasBackgroundWords)
            DriveWordLayer(line.BackgroundWords!, adjusted, line.BackgroundEndTimestamp, index,
                line.BackgroundWordIndex, i => line.BackgroundWordIndex = i);

        // Word-timed romanization (TTML transliteration) — its own clock, like the adlibs.
        if (line.HasTransliterationWords)
            DriveWordLayer(line.TransliterationWords!, adjusted, line.TransliterationEndTimestamp, index,
                line.TransliterationWordIndex, i => line.TransliterationWordIndex = i);
    }

    /// <summary>Advances one word layer's current-word index and sweeps the active word.</summary>
    private void DriveWordLayer(
        IReadOnlyList<WordTiming> words, TimeSpan adjusted, TimeSpan? layerEnd, int lineIndex,
        int currentIndex, Action<int> setIndex)
    {
        // Past the layer's end → last word remains highlighted until the line changes.
        int target;
        if (adjusted < words[0].Start)
        {
            target = -1;
        }
        else
        {
            target = words.Count - 1;
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var end = w.End ?? (i + 1 < words.Count ? words[i + 1].Start : TimeSpan.MaxValue);
                if (adjusted < end)
                {
                    target = i;
                    break;
                }
            }
        }

        if (currentIndex != target)
            setIndex(target);

        // Drive the AMLL-style sweep on the current word AND its immediate
        // neighbours, on the same lookahead-adjusted clock as the index above.
        // BandProgress keeps moving a little past both ends of each word, so the
        // feathered edge finishes crossing the previous token while it is already
        // entering the next — clamping at [0,1] here is what used to park the band
        // at every token boundary of a slow passage. Before the line starts
        // (target -1) this pre-rolls word 0; past the layer end the last word
        // settles at the inert-past sentinel (fully lit) until the line changes.
        var first = Math.Max(0, target - 1);
        var last = Math.Min(words.Count - 1, target + 1);
        for (int i = first; i <= last; i++)
        {
            var w = words[i];
            // Last word of a start-tag-only layer has no end anywhere — bound it by
            // the next line's start (capped) so it sweeps instead of snapping to lit.
            var end = w.End ?? (i + 1 < words.Count
                ? words[i + 1].Start
                : layerEnd ?? KaraokeSweep.ResolveOpenLastWordEnd(w.Start, NextSyncedLineStart(lineIndex)));
            // Words joined from several timed spans sweep on their syllables' own
            // clocks — a linear ramp would outrun the voice on a held syllable.
            var progress = w.Syllables is { Count: > 1 } syllables
                ? KaraokeSweep.SyllableBandProgress(
                    syllables, w.Start.TotalSeconds, end.TotalSeconds, adjusted.TotalSeconds)
                : KaraokeSweep.BandProgress(
                    w.Start.TotalSeconds, end.TotalSeconds, adjusted.TotalSeconds);
            if (w.Progress != progress)
                w.Progress = progress;
        }
    }

    /// <summary>Start of the first synced line after line <paramref name="index"/>; null when none.</summary>
    private TimeSpan? NextSyncedLineStart(int index)
    {
        if (index < 0) return null;
        for (int i = index + 1; i < _lines.Count; i++)
        {
            var ts = _lines[i].Timestamp;
            if (ts.HasValue) return ts;
        }
        return null;
    }
}
