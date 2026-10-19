using System.Text.RegularExpressions;

namespace FreeMusicFinder;

internal sealed record CatalogSearchSong(CatalogSong Song, CatalogRelease? Album, string Artwork = "");

internal sealed partial class ArtistCatalog
{
    private static string SearchText(string text)
    {
        text = text.Trim();
        if (text.Length is < 1 or > 200 || text.Any(char.IsControl)) throw new ArgumentException("Enter an artist, song or album (up to 200 characters).");
        return text;
    }
    public Task<CatalogResult<List<CatalogSearchSong>>> SearchSongsAsync(string query, CancellationToken ct)
    {
        query = SearchText(query);
        return Cached("songs-search-v2:" + query.ToUpperInvariant(), TimeSpan.FromDays(1), false, async () =>
        {
            try
            {
                using var data = await Get("deezer", "/search/track?limit=50&q=" + Uri.EscapeDataString(query), ct);
                var rows = Data(data.RootElement).Select(t =>
                {
                    var song = DeezerSong(t); if (song is null) return null;
                    var album = t.TryGetProperty("album", out var a) ? DeezerRelease(a, song.Artists[0]) : null;
                    return new CatalogSearchSong(song, album, album?.Artwork ?? "");
                }).Where(t => t is not null).Cast<CatalogSearchSong>().DistinctBy(t => t.Song.Id).ToList();
                if (rows.Count == 0) throw Invalid("No Deezer song matches; trying Apple.");
                return rows;
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                using var data = await Get("apple", "/search?entity=song&media=music&country=US&explicit=Yes&limit=50&term=" + Uri.EscapeDataString(query), ct);
                return Results(data.RootElement).Select(t => AppleSong(t) is {} song ? new CatalogSearchSong(song, AppleRelease(t), Text(t, "artworkUrl100")) : null)
                    .Where(t => t is not null).Cast<CatalogSearchSong>().DistinctBy(t => t.Song.Id).ToList();
            }
        }, ct, fallback: rows => rows.Count > 0 && rows.All(r => r.Song.Id.StartsWith("apple:", StringComparison.Ordinal)));
    }
    /// <summary>Deezer's songs for a bot's track link, asked afresh: the day-cached search above can hold only Apple
    /// songs (no link a bot takes) when Deezer failed once (owner #81, SZA "Saturn").</summary>
    public async Task<List<CatalogSong>> DeezerTracksAsync(string query, CancellationToken ct)
    {
        query = SearchText(query);
        using var data = await Get("deezer", "/search/track?limit=25&q=" + Uri.EscapeDataString(query), ct);
        return Data(data.RootElement).Select(DeezerSong).OfType<CatalogSong>().ToList();
    }
    public Task<CatalogResult<List<CatalogRelease>>> SearchAlbumsAsync(string query, CancellationToken ct)
    {
        query = SearchText(query);
        return Cached("albums-search-v2:" + query.ToUpperInvariant(), TimeSpan.FromDays(1), false, async () =>
        {
            try
            {
                using var data = await Get("deezer", "/search/album?limit=25&q=" + Uri.EscapeDataString(query), ct);
                var rows = Data(data.RootElement).Select(a => DeezerRelease(a, new("", ""))).Where(a => a is not null && a.Artist.Id.Length > 0).Cast<CatalogRelease>().DistinctBy(a => a.Id).ToList();
                if (rows.Count == 0) throw Invalid("No Deezer album matches; trying Apple.");
                return RelevanceEditions(rows);
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                using var data = await Get("apple", "/search?entity=album&media=music&country=US&explicit=Yes&limit=25&term=" + Uri.EscapeDataString(query), ct);
                return RelevanceEditions(Results(data.RootElement).Where(a => Text(a, "wrapperType") == "collection").Select(AppleRelease).Where(a => a is not null).Cast<CatalogRelease>());
            }
        }, ct, fallback: rows => rows.Count > 0 && rows.All(r => r.Id.StartsWith("apple:", StringComparison.Ordinal)));
    }
    /// <summary>Disambiguate strong same-title recordings using public artist reach. Only an ambiguous
    /// title needs these small cached lookups; the first results are already visible while they run.</summary>
    internal async Task<List<CatalogSearchSong>> RefineTitleAsync(FinderQuery query, List<CatalogSearchSong> songs, CancellationToken ct)
    {
        if (query.Artist.Length > 0) return songs;
        var candidates = songs.Where(s => BotText.Key(s.Song.Title) == BotText.Key(query.Text) && s.Song.Popularity >= 300000)
            .SelectMany(s => s.Song.Artists.Take(1)).Where(a => a.Id.StartsWith("deezer:")).DistinctBy(a => a.Id).Take(3).ToList();
        if (candidates.Count < 2) return songs;
        var reach = await Task.WhenAll(candidates.Select(async a =>
        {
            try
            {
                var result = await Cached("artist-reach-v1:" + a.Id, TimeSpan.FromDays(7), false, async () =>
                {
                    using var data = await Get("deezer", "/artist/" + Split(a.Id).Id, ct);
                    return (int)Math.Clamp(Number(data.RootElement, "nb_fan"), 0, 100_000_000);
                }, ct);
                return (a.Id, Fans: result.Value);
            }
            catch (Exception ex) when (Recoverable(ex)) { return (a.Id, Fans: 0); }
        }));
        var byId = reach.ToDictionary(a => a.Id, a => a.Fans);
        return songs.Select(s => s with { Song = s.Song with { ArtistReach = s.Song.Artists.Take(1).Select(a => byId.GetValueOrDefault(a.Id)).FirstOrDefault() } }).ToList();
    }
    internal static List<CatalogRelease> RelevanceEditions(IEnumerable<CatalogRelease> values)
    {
        var rows = values.ToList();
        return CatalogEditions.Consolidate(rows).OrderBy(r => rows.FindIndex(a => a.Artist.Id == r.Artist.Id && a.Kind == r.Kind && CatalogEditions.Same(a.Title, r.Title))).ToList();
    }
    public Task<CatalogResult<List<CatalogSearchSong>>> ArtistSongsAsync(CatalogArtist artist, CancellationToken ct)
    {
        var (provider, id) = Split(artist.Id);
        return Cached("artist-search-songs:" + artist.Id, TimeSpan.FromDays(1), false, async () =>
        {
            using var data = await Get(provider, provider == "apple" ? "/lookup?id=" + id + "&entity=song&limit=25&country=US" : "/artist/" + id + "/top?limit=25", ct);
            return (provider == "apple" ? Results(data.RootElement) : Data(data.RootElement)).Select(t =>
            {
                var song = provider == "apple" ? AppleSong(t) : DeezerSong(t);
                if (song is null || !song.Artists.Any(a => a.Id == artist.Id)) return null;
                var album = provider == "apple" ? AppleRelease(t) : t.TryGetProperty("album", out var a) ? DeezerRelease(a, artist) : null;
                return new CatalogSearchSong(song, album, album?.Artwork ?? "");
            }).Where(t => t is not null).Cast<CatalogSearchSong>().DistinctBy(s => s.Song.Id).ToList();
        }, ct);
    }
}

internal sealed record FinderQuery(string Text, string Term, string Title, string Artist)
{
    internal static FinderQuery Parse(string text)
    {
        text = text.Trim();
        // An explicit "title by artist" is a hint, not a persistent identity choice.
        var split = text.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        var title = split > 0 ? text[..split].Trim() : text;
        var artist = split > 0 ? text[(split + 4)..].Trim() : "";
        if (new[] { "me", "you", "us", "them", "myself", "yourself", "himself", "herself" }.Contains(artist.ToLowerInvariant())) return new(text, text, text, "");
        return artist.Length == 0 ? new(text, text, text, "") : new(text, title + " " + artist, title, artist);
    }
}

internal sealed record FinderHit(string Kind, string Id, string Title, string Subtitle, double Score,
    CatalogArtist? Artist = null, CatalogSearchSong? Track = null, CatalogRelease? Album = null, double? ArtistRank = null);

/// <summary>Explainable relevance, provider order and local album evidence. No name allowlist,
/// popularity cutoff, generative model, download or cross-provider identity substitution.</summary>
internal static class SearchRanking
{
    // Each word without accents, as BotText.Key compares names: "rosalia despecha" finds ROSALÍA - DESPECHÁ (#59, live:
    // with accents kept, only a karaoke cover spelled without them matched and became the Top result).
    private static HashSet<string> Words(string text) => WordList(text).ToHashSet(StringComparer.Ordinal);
    private static List<string> WordList(string text) => Regex.Matches(text.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => BotText.Key(m.Value)).Where(w => w.Length > 0).ToList();
    /// <summary>The same name with a small typo: at most one edit for short names, two from 10 letters on.</summary>
    internal static bool NearName(string queryKey, string nameKey)
    {
        if (queryKey.Length < 4 || nameKey.Length < 4 || Math.Abs(queryKey.Length - nameKey.Length) > 2) return false;
        var limit = Math.Max(queryKey.Length, nameKey.Length) >= 10 ? 2 : 1;
        var previous = new int[nameKey.Length + 1];
        var current = new int[nameKey.Length + 1];
        for (var j = 0; j <= nameKey.Length; j++) previous[j] = j;
        for (var i = 1; i <= queryKey.Length; i++)
        {
            current[0] = i; var best = i;
            for (var j = 1; j <= nameKey.Length; j++)
            {
                var cost = queryKey[i - 1] == nameKey[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }
            if (best > limit) return false;
            (previous, current) = (current, previous);
        }
        return previous[nameKey.Length] is > 0 and var distance && distance <= limit;
    }

    private static double Fit(string query, string title, string artist = "")
    {
        var q = BotText.Key(query); var t = BotText.Key(title);
        if (q.Length == 0 || t.Length == 0) return 0;
        if (q == t) return 88;
        var wanted = Words(query); var name = Words(title); var combined = Words(title + " " + artist);
        if (wanted.Count == 0) return 0;
        var coverage = (double)wanted.Count(combined.Contains) / wanted.Count;
        var titleCoverage = (double)wanted.Count(name.Contains) / wanted.Count;
        return coverage * 64 + titleCoverage * 12 + (t.StartsWith(q, StringComparison.Ordinal) ? 5 : 0);
    }
    internal static List<FinderHit> Build(FinderQuery query, IReadOnlyList<CatalogArtist> artists,
        IReadOnlyList<CatalogSearchSong> songs, IReadOnlyList<CatalogRelease> albums, ArtistIdentity? identity = null,
        Func<CatalogArtist, long?>? fansOf = null)
    {
        var output = new List<FinderHit>();
        // A typed name one or two letters off ("post melone") still means the famous artist ("Post Malone") when that
        // artist is vastly more popular than a tiny exact namesake (owner, 2026-10-18). Comparable namesakes keep the
        // exact match first.
        var queryKey = BotText.Key(query.Text);
        var exactFans = artists.Where(a => BotText.Key(a.Name) == queryKey).Select(a => fansOf?.Invoke(a) ?? -1).DefaultIfEmpty(-1).Max();
        var dominant = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < artists.Count; i++)
        {
            var a = artists[i]; var key = BotText.Key(a.Name); var exact = key == queryKey;
            var near = !exact && query.Artist.Length == 0 && NearName(queryKey, key);
            var fans = fansOf?.Invoke(a) ?? -1;
            if (near && fans >= 100_000 && fans >= 1000 * Math.Max(1, exactFans)) dominant.Add(a.Id);
            var support = songs.Count(s => s.Song.Artists.Any(by => by.Id == a.Id));
            var fit = near ? 82 : Fit(query.Artist.Length > 0 ? query.Artist : query.Text, a.Name);
            var score = fit + (exact || dominant.Contains(a.Id) ? 18 : 0) + Math.Min(12, support * 2) - Math.Min(4, i * .15);
            if (fans >= 0) score += Math.Min(12, 2 * Math.Log10(1d + fans));
            if (query.Artist.Length > 0) score -= 28;
            var matched = identity?.LibraryMatch == true && identity.Artist?.Id == a.Id;
            if (matched) score += 16;
            output.Add(new("Artists", a.Id, a.Name, matched ? "Matches your library · " + string.Join(", ", identity!.Albums.Take(3)) : "Artist" + (a.Genre.Length > 0 ? " · " + a.Genre : ""), score, Artist: a));
        }
        var artistIntent = identity?.Artist ?? output.Where(h => h.Artist is not null && (BotText.Key(h.Artist.Name) == queryKey || dominant.Contains(h.Artist.Id)))
            .OrderByDescending(h => h.Score).Select(h => h.Artist).FirstOrDefault();
        foreach (var s in DeduplicateSongs(songs))
        {
            var by = string.Join(", ", s.Song.Artists.Select(a => a.Name));
            var score = Fit(query.Term, s.Song.Title, by);
            if (query.Artist.Length > 0)
                score = Math.Max(Fit(query.Text, s.Song.Title, by), Fit(query.Artist, by) >= 60 ? Fit(query.Title, s.Song.Title) + Fit(query.Artist, by) * .35 : 0);
            if (BotText.Key(query.Title) == BotText.Key(BotText.Plain(s.Song.Title))) score += 8;
            // An exact title can be a famous recording rather than a literal artist namesake.
            // Use the provider's actual recording rank, never an artist-name allowlist. Non-title
            // (artist/featured-artist) searches retain the provider's existing order.
            if (BotText.Key(query.Title) == BotText.Key(BotText.Plain(s.Song.Title)))
                score += 24 * Math.Clamp(s.Song.Popularity / 1_000_000d, 0, 1) +
                    (s.Song.ArtistReach > 0 ? 8 * Math.Log10(1 + s.Song.ArtistReach) : 0);
            // "drake passionfruit" / "passionfruit drake": the artist's name and the title, without "by". The words
            // that name the credited artist are the artist; the rest must be the title. Otherwise a cover titled
            // "Passionfruit (Drake Cover)" wins on title words over Drake's own song (owner, 2026-10-18).
            if (query.Artist.Length == 0 && TitleBesideArtist(query.Text, s.Song) is { } rest && BotText.Key(rest) == BotText.Key(BotText.Plain(s.Song.Title)))
                score = Math.Max(score, 88 + 8 + 24 * Math.Clamp(s.Song.Popularity / 1_000_000d, 0, 1) +
                    (s.Song.ArtistReach > 0 ? 8 * Math.Log10(1 + s.Song.ArtistReach) : 0));
            // Typing an artist's name ("drake") means songs BY that artist, before songs merely called "Drake"
            // (owner, 2026-10-18). Their order then follows popularity, below.
            if (artistIntent is not null && query.Artist.Length == 0 && s.Song.Artists.Any(a => a.Id == artistIntent.Id)) score += 40;
            var position = songs.ToList().FindIndex(a => a.Song.Id == s.Song.Id);
            // Among comparable matches the more popular recording leads (Deezer's own rank), so covers, karaoke and
            // "in the style of" versions sit below the song people mean.
            var popular = 20 * Math.Clamp(s.Song.Popularity / 1_000_000d, 0, 1);
            output.Add(new("Songs", s.Song.Id, s.Song.Title, "Song · " + by, score + popular + 3 + 8d / (1 + position * .2), Track: s));
        }
        var orderedAlbums = ArtistCatalog.RelevanceEditions(albums);
        for (var position = 0; position < orderedAlbums.Count; position++)
        {
            var a = orderedAlbums[position];
            // Edition words do not hide a relevant album, but never merge its distinct ID.
            var title = Regex.Replace(a.Title, @"\s*[\(\[]deluxe(?:\s+(?:edition|version))?[\)\]]", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var score = Math.Max(Fit(query.Term, a.Title, a.Artist.Name), Fit(query.Term, title, a.Artist.Name));
            if (query.Artist.Length > 0) score = Math.Max(Fit(query.Text, a.Title, a.Artist.Name), Fit(query.Artist, a.Artist.Name) >= 60 ? Fit(query.Title, a.Title) + Fit(query.Artist, a.Artist.Name) * .35 : 0);
            score += 7 + 12d / (1 + position * .2);
            if (a.Kind == "album" && a.Count > 1 && BotText.Key(query.Title) == BotText.Key(title))
                foreach (var hit in output.Where(h => h.Track is { } track && BotText.Key(query.Title) == BotText.Key(BotText.Plain(track.Song.Title)) && track.Song.Artists.Any(by => by.Id == a.Artist.Id)))
                    score = Math.Max(score, hit.Score + 1);
            output.Add(new("Albums", a.Id, a.Title, (a.Kind is "single" ? "Single" : a.Kind == "ep" ? "EP" : "Album") + " · " + a.Artist.Name, score, Album: a));
        }
        // Artists is a view of this music search, not another literal-name lookup. Link
        // relevant tracks/releases to their credited source IDs without guessing names.
        var related = new Dictionary<string, (CatalogArtist Artist, double Rank, double SourceScore, string Reason)>();
        foreach (var hit in output.Where(h => h.Track is not null).OrderByDescending(h => h.Score))
        {
            foreach (var by in hit.Track!.Song.Artists)
            {
                var titleMatch = Fit(query.Title, hit.Title) >= 60;
                var named = query.Artist.Length == 0 && TitleBesideArtist(query.Text, hit.Track!.Song) is not null && Words(by.Name) is { Count: > 0 } byWords && byWords.All(Words(query.Text).Contains);
                if (!titleMatch && !named && Fit(query.Text, by.Name) < 60 && (query.Artist.Length == 0 || Fit(query.Artist, by.Name) < 60)) continue;
                Remember(by, hit.Score + 18, hit.Score, titleMatch ? "Artist of “" + hit.Title + "”" : "Artist · " + by.Name);
            }
        }
        foreach (var hit in output.Where(h => h.Album is not null))
            if (Fit(query.Title, hit.Title) >= 60) Remember(hit.Album!.Artist, hit.Score + 16, hit.Score, "Artist of “" + hit.Title + "”");
        foreach (var (_, context) in related.OrderByDescending(p => p.Value.Rank).Take(30))
        {
            var at = output.FindIndex(h => h.Artist?.Id == context.Artist.Id);
            if (at >= 0)
            {
                var existing = output[at];
                output[at] = existing with { ArtistRank = Math.Max(existing.Score, context.Rank), Subtitle = existing.Subtitle.StartsWith("Matches your library") ? existing.Subtitle + " · " + context.Reason : context.Reason };
            }
            else output.Add(new("Artists", context.Artist.Id, context.Artist.Name, context.Reason, context.SourceScore - 1, Artist: context.Artist, ArtistRank: context.Rank));
        }
        // Typing an artist's name leads with that artist (and its releases) above its own songs. A more popular
        // recording with that exact title by someone else may still lead.
        if (artistIntent is not null && query.Artist.Length == 0 && output.FindIndex(h => h.Artist?.Id == artistIntent.Id) is var leading and >= 0)
        {
            var best = output.Where(h => h.Kind != "Artists").OrderByDescending(h => h.Score).FirstOrDefault();
            if (best?.Track is { } track && track.Song.Artists.Any(a => a.Id == artistIntent.Id) && best.Score >= output[leading].Score)
                output[leading] = output[leading] with { Score = best.Score + 1 };
        }
        var ranked = output.OrderByDescending(h => h.Score).ThenBy(h => h.Kind == "Albums" ? 0 : h.Kind == "Songs" ? 1 : 2).ToList();
        // Keep the Artists shelf consistent with this search's strongest result. Related album
        // context must not push a literal namesake ahead of the artist of the leading song.
        var credited = ranked.FirstOrDefault() switch
        {
            { Track: { } first } => first.Song.Artists.Select(a => a.Id).ToArray(),
            { Album: { } first } => [first.Artist.Id],
            _ => [],
        };
        if (credited.Length > 0)
        {
            var rank = ranked.Where(h => h.Artist is not null).Select(h => h.ArtistRank ?? h.Score).DefaultIfEmpty(0).Max() + 1;
            for (var i = 0; i < ranked.Count; i++)
                if (ranked[i].Artist is { } by && Array.IndexOf(credited, by.Id) is var credit && credit >= 0)
                    ranked[i] = ranked[i] with { ArtistRank = rank - credit * .01 };
        }
        return ranked;
        void Remember(CatalogArtist by, double rank, double sourceScore, string reason)
        {
            if (!related.TryGetValue(by.Id, out var old) || rank > old.Rank) related[by.Id] = (by, rank, sourceScore, reason);
        }
    }
    /// <summary>The query without the words of one credited artist's name, when it contains all of them and
    /// something else; otherwise null.</summary>
    private static string? TitleBesideArtist(string query, CatalogSong song)
    {
        var words = WordList(query);
        foreach (var artist in song.Artists)
        {
            var name = Words(artist.Name);
            if (name.Count == 0 || !name.All(words.Contains)) continue;
            var rest = words.ToList();
            foreach (var word in name) rest.Remove(word);
            if (rest.Count > 0) return string.Join(" ", rest);
        }
        return null;
    }
    internal static IEnumerable<FinderHit> Artists(IEnumerable<FinderHit> hits) => hits.Where(h => h.Artist is not null).OrderByDescending(h => h.ArtistRank ?? h.Score);
    internal static IEnumerable<CatalogSearchSong> DeduplicateSongs(IEnumerable<CatalogSearchSong> songs) => songs
        .GroupBy(s => (Title: CatalogEditions.Key(s.Song.Title), Artist: string.Join("|", s.Song.Artists.Select(a => a.Id).Order()), Seconds: (int)Math.Round(s.Song.Seconds ?? -1)))
        .Select(g => g.OrderByDescending(s => s.Song.Explicit == true ? 2 : s.Song.Explicit is null ? 1 : 0).First());
}
