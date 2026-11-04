using System.Text;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

/// <summary>
/// One recording as For you sees it, across albums, providers and spellings. Only presentation noise is dropped: clean or
/// explicit labels, remaster tags, "bonus track" and featured guests named in the title. Live, remix, edit, acoustic,
/// instrumental, demo, sped up and every other version word stay in the key, so those recordings stay apart.
/// </summary>
internal static class DiscoverIdentity
{
    private const string Noise = @"explicit|clean(?:ed)?|edited\s+version|censored|uncensored|album\s+version|bonus\s+track|"
        + @"(?:\d{4}\s+)?(?:digital(?:ly)?\s+)?remaster(?:ed)?(?:\s+(?:version|edition|\d{4}))*|"
        + @"(?:feat\.?|ft\.?|featuring|with)\s+[^\)\]]+";
    private static readonly Regex Brackets = new(@"\s*[\(\[]\s*(?:" + Noise + @")\s*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Suffix = new(@"\s+[-–—]\s*(?:" + Noise + @")\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Lengths further apart than this are different recordings even under one title (an unlabelled extended mix).</summary>
    public const double SameLengthSeconds = 4;

    public static string Title(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.Normalize(NormalizationForm.FormKC);
        for (int i = 0; i < 3; i++)
        {
            var next = Suffix.Replace(Brackets.Replace(t, " "), "");
            if (next == t) break;
            t = next;
        }
        var key = BotText.Key(t);
        return key.Length == 0 ? BotText.Key(title) : key;
    }

    public static string Artist(string? artist) => BotText.Key(Taste.MainArtist(artist));

    /// <summary>Title and main artist, without the length: what "heard before" and "shown before" are matched on.</summary>
    public static string Key(string? title, string? artist)
    {
        var a = Artist(artist); var t = Title(title);
        return a.Length == 0 || t.Length == 0 ? "" : t + "|" + a;
    }

    public static string Key(CatalogSong song) => Key(song.Title, song.Artists.FirstOrDefault()?.Name);

    /// <summary>The same recording: one key, and lengths that agree (or one is unknown).</summary>
    public static bool Same(CatalogSong a, CatalogSong b)
    {
        var key = Key(a);
        if (key.Length == 0 || key != Key(b)) return false;
        return a.Seconds is not { } x || b.Seconds is not { } y || x <= 0 || y <= 0 || Math.Abs(x - y) <= SameLengthSeconds;
    }

    /// <summary>
    /// Keeps the first of each recording, in order. The explicit edition replaces an earlier clean one in place, so a
    /// ranking is never changed by which edition a catalog happened to list first.
    /// </summary>
    public static List<T> Distinct<T>(IEnumerable<T> items, Func<T, CatalogSong> song)
    {
        var kept = new List<T>();
        var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var s = song(item); var key = Key(s);
            if (key.Length == 0) { kept.Add(item); continue; }
            if (!byKey.TryGetValue(key, out var slots)) byKey[key] = slots = [];
            int same = slots.FindIndex(i => Same(song(kept[i]), s));
            if (same < 0) { slots.Add(kept.Count); kept.Add(item); continue; }
            int at = slots[same];
            if (s.Explicit == true && song(kept[at]).Explicit != true) kept[at] = item;
        }
        return kept;
    }
}
