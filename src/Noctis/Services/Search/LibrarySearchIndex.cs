using System.Globalization;
using System.Text;
using Noctis.Models;

namespace Noctis.Services.Search;

/// <summary>
/// The Search page's library half: an in-memory word index over every visible track, album and
/// artist, so library results answer on every keystroke without waiting for anything.
/// Words are case- and accent-folded ("Beyoncé" = "beyonce", "Don't" = "dont"); each typed word
/// must start a word of the item ("dra" finds Drake, "lo fi" finds "Lo-Fi Beats").
/// The index is rebuilt off the UI thread when the library changes and swapped in whole, so a
/// query never sees a half-built index.
/// </summary>
public sealed class LibrarySearchIndex : IDisposable
{
    private readonly ILibraryService _library;
    private Snapshot _snapshot = Snapshot.Empty;
    private int _generation;

    public LibrarySearchIndex(ILibraryService library)
    {
        _library = library;
        _library.LibraryUpdated += OnLibraryUpdated;
        Rebuild();
    }

    /// <summary>Raised (on a worker thread) when a rebuilt index has been swapped in.</summary>
    public event EventHandler? Rebuilt;

    public void Dispose() => _library.LibraryUpdated -= OnLibraryUpdated;

    private void OnLibraryUpdated(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var generation = Interlocked.Increment(ref _generation);
        IReadOnlyList<Track> tracks; IReadOnlyList<Album> albums; IReadOnlyList<Artist> artists;
        try { tracks = _library.Tracks.ToArray(); albums = _library.Albums.ToArray(); artists = _library.Artists.ToArray(); }
        catch (InvalidOperationException) { return; } // the list changed while copying; the next update rebuilds
        _ = Task.Run(() =>
        {
            var built = Snapshot.Build(tracks, albums, artists);
            if (generation != Volatile.Read(ref _generation)) return;
            _snapshot = built;
            Rebuilt?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>For tests and the first query after startup: builds the index now, on this thread.</summary>
    internal void RebuildNow() => _snapshot = Snapshot.Build(_library.Tracks.ToArray(), _library.Albums.ToArray(), _library.Artists.ToArray());

    public LibrarySearchResult Search(string query, int songLimit = 50, int albumLimit = 24, int artistLimit = 24)
    {
        var words = Words(query);
        if (words.Length == 0) return LibrarySearchResult.Empty;
        var key = string.Concat(words);
        var s = _snapshot;

        var songs = new List<Scored<Track>>();
        foreach (var t in s.Tracks)
        {
            var score = Score(words, key, t.Title, t.TitleKey, t.Other);
            if (score <= 0) continue;
            // Typing an artist's name ("drake") means that artist's songs: the same weight the
            // online catalog gives them, so the library's own copies lead. A credited guest
            // ("feat. Drake") ranks just under the main artist.
            if (t.ArtistKey == key) score = Math.Max(score, 92);
            else if (AllStart(t.Artists, words)) score = Math.Max(score, 84);
            score += Math.Min(8, Math.Log2(1 + t.Track.PlayCount) * 1.5) + (t.Track.IsFavorite ? 2 : 0);
            songs.Add(new(t.Track, score));
        }
        var albums = new List<Scored<Album>>();
        foreach (var a in s.Albums)
        {
            var score = Score(words, key, a.Title, a.TitleKey, a.Other);
            if (score <= 0) continue;
            if (a.ArtistKey == key) score = Math.Max(score, 60);
            albums.Add(new(a.Album, score));
        }
        var artists = new List<Scored<Artist>>();
        foreach (var a in s.Artists)
        {
            var score = Score(words, key, a.Title, a.TitleKey, Array.Empty<string>());
            if (score <= 0) continue;
            artists.Add(new(a.Artist, score + Math.Min(6, Math.Log2(1 + a.Artist.TrackCount))));
        }
        return new LibrarySearchResult(
            Top(songs, songLimit), Top(albums, albumLimit), Top(artists, artistLimit));
    }

    private static List<Scored<T>> Top<T>(List<Scored<T>> items, int limit)
    {
        items.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (items.Count > limit) items.RemoveRange(limit, items.Count - limit);
        return items;
    }

    /// <summary>0 = not a match. 100 = the name typed exactly; 85 = the name starts with what was
    /// typed; 70 = every word is in the name; 40-55 = the words are spread over name, artist and album.</summary>
    internal static double Score(string[] words, string key, string[] title, string titleKey, string[] other)
    {
        var inTitle = 0;
        foreach (var w in words)
        {
            if (StartsAny(title, w)) inTitle++;
            else if (!StartsAny(other, w)) return 0;
        }
        if (titleKey == key) return 100;
        if (inTitle == words.Length) return titleKey.StartsWith(key, StringComparison.Ordinal) ? 85 : 70;
        return 40 + 15.0 * inTitle / words.Length;
    }

    private static bool StartsAny(string[] tokens, string word)
    {
        foreach (var t in tokens) if (t.StartsWith(word, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Every typed word is a whole word of the credits (not just a prefix: "dra" is not an artist intent).</summary>
    private static bool AllStart(string[] tokens, string[] words)
    {
        foreach (var w in words) if (Array.IndexOf(tokens, w) < 0) return false;
        return true;
    }

    /// <summary>Folded words: lower case, no accents, apostrophes dropped inside a word, every
    /// other non-letter/digit a separator.</summary>
    public static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        string decomposed;
        try { decomposed = text.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposed = text; }
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c is '\'' or '’' or '‘') continue;
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words.ToArray();
    }

    private sealed record TrackEntry(Track Track, string[] Title, string TitleKey, string ArtistKey, string[] Other, string[] Artists);
    private sealed record AlbumEntry(Album Album, string[] Title, string TitleKey, string ArtistKey, string[] Other);
    private sealed record ArtistEntry(Artist Artist, string[] Title, string TitleKey);

    private sealed record Snapshot(TrackEntry[] Tracks, AlbumEntry[] Albums, ArtistEntry[] Artists)
    {
        public static readonly Snapshot Empty = new(Array.Empty<TrackEntry>(), Array.Empty<AlbumEntry>(), Array.Empty<ArtistEntry>());

        public static Snapshot Build(IReadOnlyList<Track> tracks, IReadOnlyList<Album> albums, IReadOnlyList<Artist> artists)
        {
            var t = new TrackEntry[tracks.Count];
            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                var title = Words(track.Title);
                var artist = Words(track.Artist);
                var credits = artist.Concat(Words(track.AlbumArtist)).Distinct().ToArray();
                t[i] = new(track, title, string.Concat(title), string.Concat(artist),
                    credits.Concat(Words(track.Album)).Distinct().ToArray(), credits);
            }
            var a = new AlbumEntry[albums.Count];
            for (var i = 0; i < albums.Count; i++)
            {
                var album = albums[i];
                var title = Words(album.Name);
                var artist = Words(album.Artist);
                a[i] = new(album, title, string.Concat(title), string.Concat(artist), artist);
            }
            var r = new ArtistEntry[artists.Count];
            for (var i = 0; i < artists.Count; i++)
            {
                var title = Words(artists[i].Name);
                r[i] = new(artists[i], title, string.Concat(title));
            }
            return new(t, a, r);
        }
    }
}

public readonly record struct Scored<T>(T Item, double Score);

public sealed record LibrarySearchResult(IReadOnlyList<Scored<Track>> Songs, IReadOnlyList<Scored<Album>> Albums, IReadOnlyList<Scored<Artist>> Artists)
{
    public static readonly LibrarySearchResult Empty = new(Array.Empty<Scored<Track>>(), Array.Empty<Scored<Album>>(), Array.Empty<Scored<Artist>>());
}
