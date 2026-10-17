using System.Text;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

/// <summary>One explicit-preferred tile per edition. Store/clean/regional aliases are
/// presentation duplicates; deluxe, live, remaster and anniversary editions stay distinct.</summary>
internal static class CatalogEditions
{
    private const string Noise = @"(?:explicit|clean(?:ed)?|edited|censored|uncensored|international|int['’]?l|intl)(?:\s+(?:version|edition))?";
    private static readonly Regex Brackets = new(@"\s*[\(\[]\s*" + Noise + @"\s*[\)\]]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Suffix = new(@"\s+[-–—]\s*" + Noise + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Deluxe = new(@"\bdeluxe\s+(?:version|edition)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static string Title(string title)
    {
        var result = Suffix.Replace(Brackets.Replace(title, " "), "").Trim();
        return result.Length == 0 ? title : Regex.Replace(result, @"\s+", " ");
    }
    internal static string Key(string title) => BotText.Key(Deluxe.Replace(Title(title).Normalize(NormalizationForm.FormKC), "Deluxe"));
    internal static bool Same(string first, string second) => Key(first) == Key(second);
    internal static bool Matches(CatalogRelease release, string title, string artist) => Same(release.Title, title) && BotText.Key(release.Artist.Name) == BotText.Key(artist);
    internal static List<CatalogRelease> Consolidate(IEnumerable<CatalogRelease> releases) => releases
        .GroupBy(r => (r.Artist.Id, Title: Key(r.Title), Kind: r.Kind.ToLowerInvariant()))
        .Select(group => group.OrderByDescending(r => r.Explicit == true ? 2 : r.Explicit is null ? 1 : 0)
            .ThenByDescending(r => r.Count).ThenByDescending(r => r.Artwork.Length > 0)
            .ThenByDescending(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).First())
        .Select(r => r with { Title = Title(r.Title) })
        .OrderByDescending(r => r.Date, StringComparer.Ordinal).ToList();
}
