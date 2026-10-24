namespace FreeMusicFinder;

/// <summary>
/// Owner #155: a song found from part of its lyrics, in the unified search. Spotify's song search does the hard part
/// (typos, missing apostrophes, "doin"/"doing", a wrong word or two, popularity: Drake's 8 of 10 words beat a copy's
/// 10 of 10). This decides when its answer is a lyric match worth leading with, and which catalog song it is.
/// </summary>
internal static class LyricSearch
{
    /// <summary>Fewer words than this are a name, not a line of lyrics; the search stays exactly as it was.</summary>
    internal const int MinWords = 4;
    /// <summary>A lyric answer never holds a search up longer than this.</summary>
    internal static TimeSpan Budget = TimeSpan.FromSeconds(4);
    /// <summary>How many of Spotify's songs are looked at; the first that the catalog has is the answer.</summary>
    internal const int Candidates = 5;

    /// <summary>The query's words, folded the way names are compared (case, accents, punctuation and apostrophes gone).</summary>
    internal static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return words;
        // Apostrophes stay inside a word ("don't" = "dont"); spaces, dashes and other marks part words.
        foreach (var part in System.Text.RegularExpressions.Regex.Split(text, @"[^\p{L}\p{N}\p{M}'’]+"))
            if (BotText.Key(part) is { Length: > 0 } key) words.Add(key);
        return words;
    }

    internal static bool LooksLikeLyric(string text) => Words(text).Count >= MinWords;

    /// <summary>The query names the song (title, maybe its artist) instead of quoting its lyrics: nearly all of its words
    /// are the title's or the artist's. Then the catalog's own ranking stands.</summary>
    internal static bool NamesSong(string query, string title, IEnumerable<string> artists)
    {
        var words = Words(query).Where(w => !Filler.Contains(w)).ToList();
        if (words.Count == 0) return false;
        var known = Words(CatalogEditions.Title(title)).Concat(artists.SelectMany(Words)).ToList();
        var covered = words.Count(w => known.Any(k => Near(w, k)));
        return covered >= Math.Ceiling(words.Count * 0.8);
    }

    /// <summary>Words people add around a song's name ("hotline bling by drake", "... song lyrics") that are neither.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal) { "by", "song", "lyrics", "lyric", "feat", "ft", "featuring" };

    /// <summary>Two words that are the same word as typed: equal, or one is the other without its final g ("doin"/"doing").</summary>
    internal static bool Near(string a, string b)
        => a == b || a.Length >= 3 && b.Length >= 3 && (a + "g" == b || b + "g" == a);

    /// <summary>The catalog's copy of Spotify's song: same title (bracketed additions only with a matching length) by an
    /// artist that fits, within three seconds when both lengths are known. The most popular copy wins.</summary>
    internal static CatalogSearchSong? Match(SpotifyTrackHit hit, IEnumerable<CatalogSearchSong> songs)
    {
        var length = hit.Seconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        return songs
            .Where(c => c.Song.Artists.Count > 0 && hit.Artists.Any(artist => c.Song.Artists.Any(a =>
                BotText.SameSong(artist, hit.Title, length, a.Name, c.Song.Title, TimeSpan.FromSeconds(c.Song.Seconds ?? 0)))))
            .OrderByDescending(c => c.Song.Popularity)
            .FirstOrDefault();
    }
}
