namespace FreeMusicFinder;

internal sealed record ArtistIdentity(CatalogArtist? Artist, List<CatalogArtist> Candidates, List<string> Albums, bool LibraryMatch, string Notice = "");

internal static class ArtistEvidence
{
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    { "unknown", "unknownalbum", "untitled", "unreleased", "singles", "album", "various", "variousartists", "greatesthits", "bestof" };
    internal static List<string> Albums(IEnumerable<string> titles) => titles.Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 1000)
        .Where(t => CatalogEditions.Key(t).Length > 0 && !Generic.Contains(CatalogEditions.Key(t)))
        .DistinctBy(CatalogEditions.Key).Take(12).ToList();
    internal static List<string> Matches(IEnumerable<string> albums, IEnumerable<CatalogRelease> releases)
    {
        var available = releases.Select(r => CatalogEditions.Key(r.Title)).ToHashSet();
        return albums.Where(a => available.Contains(CatalogEditions.Key(a))).ToList();
    }
    internal static bool Strong(IReadOnlyList<string> matches) => matches.Count >= 2 || matches.Count == 1 && CatalogEditions.Key(matches[0]).Length >= 8;
}
