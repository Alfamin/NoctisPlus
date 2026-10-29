using System.Text.RegularExpressions;
using Noctis.Models;

namespace Noctis.Services.Lyrics;

/// <summary>
/// Owner #220: online word timing often comes with its words censored: lrc.red masks a whole word ("****"), Kugou part
/// of one ("n**ga", "sh*t"). Each masked word takes the real word from an uncensored copy of the same lyrics (LRCLIB),
/// found by the words around it; a partly masked word no copy settles is read from its visible letters. Only the
/// masked words change: the timing and every other word stay exactly as they were.
/// </summary>
internal static partial class OnlineTimingUncensor
{
    // A span's text: leading space/punctuation, the word, trailing punctuation/space ("—****", "****'s", " n**ga,").
    [GeneratedRegex(@"(?<word>[\p{L}\p{M}'’]*\*+[\p{L}\p{M}'’*]*)")]
    private static partial Regex MaskedRegex();

    [GeneratedRegex(@"(<span\b[^>]*>)([^<]*)(</span>)")]
    private static partial Regex SpanRegex();

    [GeneratedRegex(@"\[[^\]]*\]|<[^>]*>")]
    private static partial Regex LrcTagRegex();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}'’*]+")]
    private static partial Regex WordRegex();

    /// <summary>The usual words, most likely first, for a partly masked word no uncensored copy settles.</summary>
    private static readonly string[] Usual =
    [
        "nigga", "niggas", "nigger", "niggers", "fuck", "fucking", "fuckin'", "fucked", "fucker", "fuckers", "motherfucker",
        "motherfuckers", "motherfuckin'", "mothafuckin'", "mothafucka", "shit", "shits", "shitty", "bitch", "bitches",
        "pussy", "dick", "dicks", "damn", "ass", "asses", "hoe", "hoes", "cock", "cunt",
    ];

    [GeneratedRegex(@"\s*[\(\[](?:feat|ft|with|prod)\.?\s[^\)\]]*[\)\]]|\s*\[[^\]]*\]|\s+-\s+.*(?:remaster|version|edit|live).*$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleExtrasRegex();

    /// <summary>
    /// The uncensored copy's words for a song (LRCLIB). Its own names, the main artist with the title without "(feat. …)"
    /// or a remaster note, and a search are asked at once; of the copies of this length the one with the most swear words
    /// wins: LRCLIB also holds clean versions, and not only with asterisks (Lil Baby "Commercial": "(Whoa) talkin'", "the -").
    /// Null when none has the song uncensored.
    /// </summary>
    public static async Task<List<string>?> CopyAsync(ILrcLibService lrcLib, string artist, string title, double seconds, CancellationToken ct)
    {
        var mainArtist = artist.Split([",", " & ", " feat", " ft.", " x ", " X ", " and "], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var plainTitle = TitleExtrasRegex().Replace(title, "").Trim();
        async Task<IEnumerable<LrcLibResult>> Get(string a, string t)
        {
            try { return await lrcLib.GetLyricsAsync(a, t, seconds, ct).ConfigureAwait(false) is { } r ? [r] : []; }
            catch (LyricsProviderException) { return []; }
        }
        async Task<IEnumerable<LrcLibResult>> Search()
        {
            try { return await lrcLib.SearchLyricsAsync(mainArtist, plainTitle, ct).ConfigureAwait(false); }
            catch (LyricsProviderException) { return []; }
        }
        var asks = new List<Task<IEnumerable<LrcLibResult>>> { Get(artist, title), Search() };
        if (mainArtist != artist || plainTitle != title) asks.Add(Get(mainArtist, plainTitle));
        var found = (await Task.WhenAll(asks).ConfigureAwait(false)).SelectMany(r => r)
            .Where(r => !r.Instrumental && (seconds <= 0 || r.Duration <= 0 || Math.Abs(r.Duration - seconds) <= 5));
        List<string>? best = null; var bestSwears = 0;
        foreach (var copy in found)
        {
            var words = WordsOf(copy.SyncedLyrics ?? copy.PlainLyrics);
            if (words.Count == 0 || words.Any(w => w.Contains('*'))) continue;   // a clean version with asterisks
            var swears = words.Count(w => Profane(Norm(w)));
            if (best == null || swears > bestSwears) { best = words; bestSwears = swears; }
        }
        return best;
    }

    public static bool Needs(string? ttml) => ttml != null && ttml.Contains('*') && SpanRegex().Matches(ttml).Any(s => s.Groups[2].Value.Contains('*'));

    /// <summary>The words of an LRC / plain lyric text, in order (time tags dropped).</summary>
    public static List<string> WordsOf(string? lyrics) =>
        lyrics == null ? [] : WordRegex().Matches(LrcTagRegex().Replace(lyrics, " ")).Select(m => m.Value).ToList();

    /// <summary>The document with its masked words filled in where they can be; how many were.</summary>
    public static (string Ttml, int Filled, int Masked) Apply(string ttml, IReadOnlyList<string>? reference)
    {
        var spans = SpanRegex().Matches(ttml).Cast<Match>().ToList();
        var words = spans.Select(s => Norm(s.Groups[2].Value)).ToList();
        var refNorm = reference?.Select(Norm).ToList() ?? [];
        var masked = 0; var filled = 0;
        var result = new System.Text.StringBuilder(ttml.Length);
        var last = 0;
        for (var i = 0; i < spans.Count; i++)
        {
            var text = spans[i].Groups[2].Value;
            if (!text.Contains('*')) continue;
            var m = MaskedRegex().Match(text);
            if (!m.Success) continue;
            masked++;
            var real = FromReference(words, i, m.Groups["word"].Value, reference, refNorm, spans.Count) ?? FromLetters(m.Groups["word"].Value);
            if (real == null) continue;
            real = MatchCase(m.Groups["word"].Value, real);
            filled++;
            var group = spans[i].Groups[2];
            result.Append(ttml, last, group.Index - last);
            result.Append(text[..m.Index]).Append(real.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")).Append(text[(m.Index + m.Length)..]);
            last = group.Index + group.Length;
        }
        if (filled == 0) return (ttml, 0, masked);
        result.Append(ttml, last, ttml.Length - last);
        return (result.ToString(), filled, masked);
    }

    /// <summary>
    /// The copy's word between the same neighbours: the words right before and after it, in a row (a masked neighbour
    /// lets the row go on). A swear word or slur needs two of them; any other word (a copy that differs a little would
    /// put "but" or "yeah" there) needs two on each side. The likely spot in the song breaks a tie.
    /// </summary>
    private static string? FromReference(List<string> words, int at, string mask, IReadOnlyList<string>? reference, List<string> refNorm, int total)
    {
        if (reference is not { Count: > 0 }) return null;
        int best = -1, bestScore = 0; var bestDistance = double.MaxValue; var bestProfane = false;
        var expected = refNorm.Count * (double)at / Math.Max(1, total);
        for (var j = 0; j < refNorm.Count; j++)
        {
            if (refNorm[j].Contains('*') || !Fits(mask, refNorm[j])) continue;
            int Run(int step)
            {
                var run = 0;
                for (var k = 1; k <= 4; k++)
                {
                    int w = at + step * k, r = j + step * k;
                    if (w < 0 || r < 0 || w >= words.Count || r >= refNorm.Count) break;
                    if (words[w].Contains('*')) continue;                 // another masked word: the row goes on
                    if (words[w].Length == 0) continue;
                    if (words[w] != refNorm[r]) break;
                    run++;
                }
                return run;
            }
            int left = Run(-1), right = Run(1);
            var sure = Profane(refNorm[j]) ? left + right >= 2 : left >= 2 && right >= 2;
            if (!sure) continue;
            // A swear word or slur fitting the spot comes before any other word (what clean versions mask most).
            var profane = Profane(refNorm[j]);
            var score = left + right;
            var distance = Math.Abs(j - expected);
            if (best < 0 || profane && !bestProfane || profane == bestProfane && (score > bestScore || score == bestScore && distance < bestDistance))
            {
                best = j; bestScore = score; bestDistance = distance; bestProfane = profane;
            }
        }
        return best < 0 ? null : reference[best].Replace("\u2019", "'");
    }

    static readonly string[] Stems = ["nigg", "fuck", "shit", "bitch", "puss", "dick", "damn", "cock", "cunt", "whore", "slut",
        "motherf", "mothaf", "muthaf", "bastard", "goddamn", "asshole", "titt"];

    static readonly HashSet<string> Words = new(StringComparer.Ordinal) { "ass", "asses", "hoe", "hoes", "tit", "tits", "cum", "fag", "fags" };

    /// <summary>A word a clean version masks: a swear word or a slur.</summary>
    internal static bool Profane(string word) => Words.Contains(word) || Stems.Any(s => word.StartsWith(s, StringComparison.Ordinal));

    /// <summary>The letters a mask shows must be the word's own: "n**ga" fits "nigga", "****" fits any word.</summary>
    private static bool Fits(string mask, string word)
    {
        var m = Norm(mask);
        var lead = m.TakeWhile(c => c != '*').Count();
        var tail = m.Reverse().TakeWhile(c => c != '*').Count();
        if (lead == 0 && tail == 0) return word.Length > 1;
        if (word.Length < lead + tail + 1) return false;
        return word.StartsWith(m[..lead], StringComparison.Ordinal) && word.EndsWith(m[^tail..], StringComparison.Ordinal);
    }

    /// <summary>A partly masked word read from its letters and length; a whole-word mask is never guessed.</summary>
    private static string? FromLetters(string mask)
    {
        var m = Norm(mask);
        if (m.All(c => c == '*')) return null;
        return Usual.FirstOrDefault(w => w.Length == m.Length && Fits(m, w))
            ?? Usual.FirstOrDefault(w => Fits(m, w) && Math.Abs(w.Length - m.Length) <= 1);
    }

    private static string MatchCase(string mask, string real)
    {
        var first = mask.FirstOrDefault(char.IsLetter);
        if (mask.Where(char.IsLetter).Any() && mask.Where(char.IsLetter).All(char.IsUpper) && mask.Count(char.IsLetter) > 1) return real.ToUpperInvariant();
        return first != default && char.IsUpper(first) ? char.ToUpperInvariant(real[0]) + real[1..] : real;
    }


    private static string Norm(string text) =>
        new string(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '*' || c == '\'').ToArray()).Trim('\'');
}
