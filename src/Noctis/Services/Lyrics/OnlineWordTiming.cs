using System.Globalization;
using System.Text.RegularExpressions;
using Noctis.Models;
using System.Text.Json;
using Noctis.Services.LyricsStudio;

namespace Noctis.Services.Lyrics;

/// <summary>Where a song's word timing came from (#200): shown on the lyrics page and in the word timing window.</summary>
internal enum WordTimingSource { WordLyrics, LrcRed, Unison, Kugou }

/// <summary>
/// One song's automatic online word timing (#200): lrc.red, then Unison, then Kugou, word-synced only, the same
/// recording (title/artist from the source's own match, length within <see cref="LengthSlack"/>). Kugou's KRC arrives as
/// ELRC and is carried as TTML too, so every source can be written beside WordLyrics' .elrc without touching it.
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
    }

    readonly ILrcRedLyricsService _lrcRed;
    readonly IUnisonLyricsService _unison;
    readonly IKugouLyricsService _kugou;
    readonly Func<bool> _lrcRedEnabled;

    public OnlineWordTiming(ILrcRedLyricsService lrcRed, IUnisonLyricsService unison, IKugouLyricsService kugou, Func<bool> lrcRedEnabled)
    {
        _lrcRed = lrcRed;
        _unison = unison;
        _kugou = kugou;
        _lrcRedEnabled = lrcRedEnabled;
    }

    /// <summary>All sources are asked at once (owner, 2026-10-29): one that fails or has nothing costs no extra wait;
    /// the first in order (lrc.red, Unison, Kugou) with word timing for this recording wins.</summary>
    public async Task<Lookup> FindAsync(string artist, string title, double durationSeconds, CancellationToken ct)
    {
        var asks = new List<(WordTimingSource Source, Task<LrcLibResult?> Answer)>();
        if (_lrcRedEnabled()) asks.Add((WordTimingSource.LrcRed, Ask(() => _lrcRed.SearchLyricsAsync(artist, title, durationSeconds, ct))));
        asks.Add((WordTimingSource.Unison, Ask(() => _unison.SearchLyricsAsync(artist, title, durationSeconds, ct))));
        asks.Add((WordTimingSource.Kugou, Ask(() => _kugou.SearchLyricsAsync(artist, title, durationSeconds, ct))));
        bool transient = false;
        foreach (var (source, answer) in asks)
        {
            try
            {
                if (WordTimed(await answer, durationSeconds) is { } ttml) return new Lookup(source, ttml, transient);
            }
            catch (LyricsProviderException) { transient = true; }
        }
        return new Lookup(null, null, transient);
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
    public static string? Mark(string ttml, WordTimingSource source, double shift)
    {
        if (SourceOf(ttml) != null) return null;
        var root = Root.Match(ttml);
        if (!root.Success) return null;
        var mark = $" xmlns:plus=\"{Namespace}\" plus:source=\"{Name(source)}\" plus:shift=\"{shift.ToString("0.00", CultureInfo.InvariantCulture)}\"";
        return ttml.Insert(root.Index + root.Length, mark);
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
}
