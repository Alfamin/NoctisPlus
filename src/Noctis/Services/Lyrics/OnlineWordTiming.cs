using System.Globalization;
using System.Text.RegularExpressions;
using Noctis.Models;
using System.Text.Json;
using Noctis.Services.LyricsStudio;

namespace Noctis.Services.Lyrics;

/// <summary>Where a song's word timing came from (#200): shown on the lyrics page and in the word timing window.</summary>
internal enum WordTimingSource { WordLyrics, LrcRed, Unison, Kugou }

/// <summary>
/// One song's automatic online word timing (#200): lrc.red, then Unison, then Kugou (owner #209), word-synced only, the same recording
/// (title/artist from the source's own match, length within <see cref="LengthSlack"/>), as a TTML document so it can be
/// written beside WordLyrics' .elrc without touching it (an ELRC answer is carried as TTML too).
/// Never the paid Musixmatch, never lrc.red's model sync (user press only). Misses are remembered; failures are not.
/// </summary>
internal sealed class OnlineWordTiming
{
    public const double LengthSlack = 1.0;

    /// <summary>Found = a word-timed TTML for this recording (not yet checked against the audio, see <see cref="OnlineTimingFit"/>).
    /// Transient = some source could not answer (offline, busy, error): no miss is remembered, ask again later.</summary>
    public sealed record Lookup(WordTimingSource? Source, string? Ttml, bool Transient)
    {
        public bool Found => Ttml != null;
        /// <summary>What each source answered and how fast, for the session log ("lrc.red words 412 ms, Unison none 230 ms").</summary>
        public string Trace { get; init; } = "";
    }

    readonly ILrcRedLyricsService _lrcRed;
    readonly IUnisonLyricsService _unison;
    readonly IKugouLyricsService? _kugou;
    readonly Func<bool> _lrcRedEnabled;
    readonly ILrcLibService? _uncensored;

    /// <param name="uncensored">Owner #220: an uncensored copy of the lyrics (LRCLIB), asked alongside, for masked words.</param>
    public OnlineWordTiming(ILrcRedLyricsService lrcRed, IUnisonLyricsService unison, Func<bool> lrcRedEnabled, IKugouLyricsService? kugou = null,
        ILrcLibService? uncensored = null)
    {
        _lrcRed = lrcRed;
        _unison = unison;
        _kugou = kugou;
        _lrcRedEnabled = lrcRedEnabled;
        _uncensored = uncensored;
    }

    /// <summary>How long a masked answer waits for the uncensored copy, beyond the sources' own time.</summary>
    internal static readonly TimeSpan UncensorWait = TimeSpan.FromSeconds(2.5);

    /// <summary>The uncensored copy's words, or null (none, failed, no source).</summary>
    Task<List<string>?> Reference(string artist, string title, double durationSeconds, CancellationToken ct)
    {
        if (_uncensored is not { } lrcLib) return Task.FromResult<List<string>?>(null);
        return Task.Run(async () =>
        {
            try
            {
                return await OnlineTimingUncensor.CopyAsync(lrcLib, artist, title, durationSeconds, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
        }, ct);
    }

    /// <summary>Owner #220: the masked words of a found timing filled in (the copy asked earlier, or now).</summary>
    internal async Task<(string Ttml, string Note)> UncensorAsync(string ttml, Task<List<string>?> reference, CancellationToken ct)
    {
        if (!OnlineTimingUncensor.Needs(ttml)) return (ttml, "");
        await Task.WhenAny(reference, Task.Delay(UncensorWait, ct)).ConfigureAwait(false);
        var words = reference.IsCompletedSuccessfully ? reference.Result : null;
        var (clean, filled, masked) = OnlineTimingUncensor.Apply(ttml, words);
        return (clean, $"; uncensored {filled} of {masked} masked words{(words == null ? " (no LRCLIB copy)" : "")}");
    }

    /// <summary>The same for a timing found earlier (a saved file): the copy is asked now.</summary>
    internal Task<(string Ttml, string Note)> UncensorAsync(string ttml, string artist, string title, double durationSeconds, CancellationToken ct) =>
        UncensorAsync(ttml, Reference(artist, title, durationSeconds, ct), ct);

    /// <summary>How long a word-timed answer that is ready waits for a better-ranked source still answering.</summary>
    internal static readonly TimeSpan PreferWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Owner #209 order: lrc.red, Unison, Kugou (measured on 150 of the owner's songs: Unison rarely has a song, 4 of 150,
    /// but times it like lrc.red; Kugou has 111 of 150, rougher). All are asked at once, so one that fails or has nothing
    /// costs no extra wait; the best-ranked word-timed answer wins, and a ready one waits for a better-ranked source still
    /// answering only <see cref="PreferWait"/>. WordLyrics comes after all of them (the lane's caller).
    /// </summary>
    public async Task<Lookup> FindAsync(string artist, string title, double durationSeconds, CancellationToken ct)
    {
        var asks = new List<(WordTimingSource Source, Task<LrcLibResult?> Answer)>();
        var reference = Reference(artist, title, durationSeconds, ct);   // owner #220: asked alongside, used only for masked words
        if (_lrcRedEnabled()) asks.Add((WordTimingSource.LrcRed, Ask(() => _lrcRed.SearchLyricsAsync(artist, title, durationSeconds, ct))));
        asks.Add((WordTimingSource.Unison, Ask(() => _unison.SearchLyricsAsync(artist, title, durationSeconds, ct))));
        if (_kugou is { } kugou) asks.Add((WordTimingSource.Kugou, Ask(() => kugou.SearchLyricsAsync(artist, title, durationSeconds, ct))));
        var transient = false;
        var timed = new string?[asks.Count];
        var read = new bool[asks.Count];
        var said = new string?[asks.Count];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string Trace() => string.Join(", ", asks.Select((a, i) => OnlineTimingSidecar.Name(a.Source) + " " + (said[i] ?? "still asking")));
        while (true)
        {
            for (var i = 0; i < asks.Count; i++)
            {
                if (read[i] || !asks[i].Answer.IsCompleted) continue;
                read[i] = true;
                var ms = (int)clock.ElapsedMilliseconds;
                try
                {
                    var answer = await asks[i].Answer.ConfigureAwait(false);
                    timed[i] = WordTimed(answer, durationSeconds);
                    said[i] = (timed[i] != null ? "words" : answer == null ? "none" : "not word-timed or another length") + $" {ms} ms";
                }
                // Any failure of one source (its own error, a timeout, a dropped connection) is "could not answer":
                // never a miss, and never the whole lookup thrown away (the lane would ask the same song again at once).
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    transient = true;
                    said[i] = $"error {ms} ms ({e.GetType().Name}: {e.Message})";
                }
            }
            var best = Array.FindIndex(timed, t => t != null);
            var betterOut = Enumerable.Range(0, best < 0 ? asks.Count : best).Any(i => !read[i]);
            if (best >= 0 && (!betterOut || clock.Elapsed >= PreferWait))
            {
                var (clean, note) = await UncensorAsync(timed[best]!, reference, ct).ConfigureAwait(false);
                return new Lookup(asks[best].Source, clean, transient) { Trace = Trace() + note };
            }
            if (best < 0 && !betterOut) return new Lookup(null, null, transient) { Trace = Trace() };
            var waits = asks.Where((_, i) => !read[i]).Select(a => (Task)a.Answer).ToList();
            if (best >= 0) waits.Add(Task.Delay(PreferWait - clock.Elapsed, ct));
            await Task.WhenAny(waits).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    static Task<LrcLibResult?> Ask(Func<Task<LrcLibResult?>> search)
    {
        try { return search(); }
        catch (Exception e) { return Task.FromException<LrcLibResult?>(e); }
    }

    /// <summary>The result as a word-timed TTML document, or null when it is line-timed/plain or another length.</summary>
    internal static string? WordTimed(LrcLibResult? result, double durationSeconds)
    {
        if (result == null || result.Instrumental) return null;
        if (result.Duration > 0 && durationSeconds > 0 && Math.Abs(result.Duration - durationSeconds) > LengthSlack) return null;
        var ttml = result.OriginalTtml;
        if (ttml == null && !string.IsNullOrWhiteSpace(result.SyncedLyrics) && result.SyncedLyrics is { } synced)
        {
            var lines = ExistingLyricsLoader.ParseTimed(synced);
            ttml = lines.Count == 0 ? null : TimedLyricsBuilder.BuildTtml(lines);
        }
        return ttml != null && OnlineTimingFit.WordsOf(ttml).Count >= OnlineTimingFit.MinWords ? ttml : null;
    }
}

/// <summary>
/// Songs no source has word timing for, remembered for <see cref="Keep"/> so the library backfill does not ask again
/// every start (#200). Only definitive misses go in; a failure (offline, busy) is never remembered. Bounded; a damaged
/// file starts empty.
/// </summary>
internal sealed class OnlineTimingMisses
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    public const int Max = 20000;
    readonly string _path;
    readonly object _gate = new();
    Dictionary<string, DateTime>? _misses;

    public OnlineTimingMisses(string path) => _path = path;

    public static string Key(string artist, string title, double durationSeconds) =>
        $"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}|{Math.Round(durationSeconds)}";

    public bool IsMiss(string key, DateTime nowUtc)
    {
        lock (_gate) return Load().TryGetValue(key, out var at) && nowUtc - at < Keep;
    }

    public void Remember(string key, DateTime nowUtc)
    {
        lock (_gate)
        {
            var misses = Load();
            misses[key] = nowUtc;
            foreach (var old in misses.Where(m => nowUtc - m.Value >= Keep).Select(m => m.Key).ToList()) misses.Remove(old);
            if (misses.Count > Max)
                foreach (var old in misses.OrderBy(m => m.Value).Take(misses.Count - Max).Select(m => m.Key).ToList()) misses.Remove(old);
            Save(misses);
        }
    }

    public void Forget(string key)
    {
        lock (_gate) { if (Load().Remove(key)) Save(_misses!); }
    }

    Dictionary<string, DateTime> Load()
    {
        if (_misses != null) return _misses;
        try { _misses = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return _misses ??= new();
    }

    void Save(Dictionary<string, DateTime> misses)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(misses));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The mark an online word timing carries inside its own .ttml (#200): which source it came from and the fit shift that
/// was applied, as namespaced attributes on the root element. The credit travels with the file; TTML readers ignore
/// foreign attributes; and only a marked .ttml is ever replaced by a later online timing.
/// </summary>
internal static class OnlineTimingSidecar
{
    public const string Namespace = "urn:noctisplus:online-timing";

    static readonly Regex Root = new(@"<tt\b", RegexOptions.CultureInvariant);
    static readonly Regex Source = new(@"\splus:source\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant);

    public static string Name(WordTimingSource source) => source switch
    {
        WordTimingSource.LrcRed => "lrc.red",
        WordTimingSource.Unison => "Unison",
        WordTimingSource.Kugou => "Kugou",
        _ => "WordLyrics",
    };

    /// <summary>The document with the mark on its root; null when it has no &lt;tt&gt; root or is marked already.</summary>
    /// <param name="unverified">Owner #215: the fit check was not sure of it: shown until WordLyrics' timing replaces it.</param>
    public static string? Mark(string ttml, WordTimingSource source, double shift, bool unverified = false)
    {
        if (SourceOf(ttml) != null) return null;
        var root = Root.Match(ttml);
        if (!root.Success) return null;
        var mark = $" xmlns:plus=\"{Namespace}\" plus:source=\"{Name(source)}\" plus:shift=\"{shift.ToString("0.00", CultureInfo.InvariantCulture)}\"" +
                   (unverified ? $" {UnverifiedMark}" : "");
        return ttml.Insert(root.Index + root.Length, mark);
    }

    const string UnverifiedMark = "plus:fit=\"unsure\"";

    /// <summary>Owner #215: an online timing kept on screen while WordLyrics times the song (it counts as no word timing
    /// for WordLyrics, and makes way when WordLyrics' timing lands).</summary>
    public static bool Unverified(string? ttml) => SourceOf(ttml) != null && ttml!.Contains(UnverifiedMark, StringComparison.Ordinal);

    /// <summary>A song whose .ttml is such an unverified online timing.</summary>
    public static bool HasUnverified(string audioPath)
    {
        foreach (var ttml in LyricsWriter.SidecarsOnDisk(audioPath, ".ttml"))
            try { if (Unverified(File.ReadAllText(ttml))) return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>The source an online timing names, or null for any other TTML (the user's own, Lyrics Studio's).</summary>
    public static WordTimingSource? SourceOf(string? ttml)
    {
        if (ttml == null || !ttml.Contains(Namespace, StringComparison.Ordinal)) return null;
        var m = Source.Match(ttml);
        if (!m.Success) return null;
        foreach (var s in new[] { WordTimingSource.LrcRed, WordTimingSource.Unison, WordTimingSource.Kugou })
            if (m.Groups[1].Value == Name(s)) return s;
        return null;
    }

    /// <summary>
    /// The song's online timing makes way so the timing just written beside it shows — a .ttml out-ranks the .elrc.
    /// Removed, not trashed: it can be fetched again, and a Recycle Bin prompt must not come from a background run.
    /// Only a marked .ttml; the user's own is never touched. Returns how many went.
    /// </summary>
    public static int StepAside(string audioPath)
    {
        var gone = 0;
        foreach (var ttml in LyricsWriter.SidecarsOnDisk(audioPath, ".ttml"))
        {
            try
            {
                if (SourceOf(File.ReadAllText(ttml)) == null) continue;
                File.Delete(ttml);
                gone++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return gone;
    }
}
