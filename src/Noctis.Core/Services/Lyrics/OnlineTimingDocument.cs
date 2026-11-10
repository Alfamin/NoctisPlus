using System.Globalization;
using System.Text.RegularExpressions;

namespace Noctis.Services.Lyrics;

/// <summary>Source label carried by an online word-timing document.</summary>
internal enum WordTimingSource { WordLyrics, LrcRed, Unison, Kugou }

/// <summary>
/// UI-free operations on online word-timing TTML. Kept in Core so the phone and desktop use
/// identical word-coverage and source-attribution rules; audio fit and file-sidecar management
/// remain host responsibilities.
/// </summary>
internal static class OnlineTimingDocument
{
    public const int MinWords = 30;
    public const string Namespace = "urn:noctisplus:online-timing";

    public readonly record struct Word(double Start, double End, bool First);

    private static readonly Regex Root = new(@"<tt\b", RegexOptions.CultureInvariant);
    private static readonly Regex Source = new(@"\splus:source\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant);

    /// <summary>Main-voice word onsets; split syllables are joined and background-only rows excluded.</summary>
    public static IReadOnlyList<Word> WordsOf(string? ttml)
    {
        var (lines, _) = TtmlParser.Parse(ttml, joinSplitWords: true);
        var words = new List<Word>();
        foreach (var line in lines ?? [])
        {
            if (line.IsBackgroundOnly || line.Words is not { Count: > 0 } row) continue;
            var first = true;
            foreach (var word in row)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                words.Add(new Word(word.Start.TotalSeconds, (word.End ?? word.Start).TotalSeconds, first));
                first = false;
            }
        }
        return words;
    }

    public static bool HasEnoughWords(string? ttml) => WordsOf(ttml).Count >= MinWords;

    public static string Name(WordTimingSource source) => source switch
    {
        WordTimingSource.LrcRed => "lrc.red",
        WordTimingSource.Unison => "Unison",
        WordTimingSource.Kugou => "Kugou",
        _ => "WordLyrics",
    };

    /// <summary>Adds provenance and fit state to an unmarked TTML root; null when malformed or already marked.</summary>
    public static string? Mark(string ttml, WordTimingSource source, double shift, bool unverified = false)
    {
        if (SourceOf(ttml) != null) return null;
        var root = Root.Match(ttml);
        if (!root.Success) return null;
        var mark = $" xmlns:plus=\"{Namespace}\" plus:source=\"{Name(source)}\" plus:shift=\"{shift.ToString("0.00", CultureInfo.InvariantCulture)}\"" +
                   (unverified ? $" {UnverifiedMark}" : "");
        return ttml.Insert(root.Index + root.Length, mark);
    }

    private const string UnverifiedMark = "plus:fit=\"unsure\"";

    public static bool Unverified(string? ttml) => SourceOf(ttml) != null && ttml!.Contains(UnverifiedMark, StringComparison.Ordinal);

    public static WordTimingSource? SourceOf(string? ttml)
    {
        if (ttml == null || !ttml.Contains(Namespace, StringComparison.Ordinal)) return null;
        var match = Source.Match(ttml);
        if (!match.Success) return null;
        foreach (var source in new[] { WordTimingSource.LrcRed, WordTimingSource.Unison, WordTimingSource.Kugou })
            if (match.Groups[1].Value == Name(source)) return source;
        return null;
    }
}
