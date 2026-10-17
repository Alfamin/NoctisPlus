using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Noctis.Services.Search;

namespace FreeMusicFinder;

/// <summary>
/// Finder's catalog behind the app's own Search page: the same unified search (Deezer, Apple as
/// fallback; Finder's ranking, namesake and edition rules) and the same download queue, without
/// Finder's window. Results stream in as each catalog answers.
/// </summary>
internal sealed class FinderOnlineSearch : IOnlineMusicSearch
{
    /// <summary>Row covers (40 px, sharp at 2x and above) and card covers (up to ~180 px at 1.5x).</summary>
    internal const int RowArtwork = 160, CardArtwork = 320;

    private readonly Func<ArtistCatalog?> _catalog;
    private readonly DiscoveryActions _actions;
    private readonly NativeCatalogPages _pages;
    private readonly Func<bool> _signedIn;
    private readonly Action _signIn;
    private readonly ConcurrentDictionary<string, CatalogSong> _songs = new();
    private readonly ConcurrentDictionary<string, CatalogArtist> _artists = new();
    private readonly ConcurrentDictionary<string, CatalogRelease> _albums = new();

    public FinderOnlineSearch(Func<ArtistCatalog?> catalog, DiscoveryActions actions, NativeCatalogPages pages, Func<bool> signedIn, Action signIn)
    {
        _catalog = catalog; _actions = actions; _pages = pages; _signedIn = signedIn; _signIn = signIn;
        _actions.Downloads.Changed += OnDownloadChanged;
    }

    public void Detach() => _actions.Downloads.Changed -= OnDownloadChanged;

    private void OnDownloadChanged(Download _) => DownloadsChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? DownloadsChanged;

    public void WarmUp() => _catalog()?.WarmUp();

    private ArtistCatalog Catalog => _catalog() ?? throw new InvalidOperationException("Free Music Finder has stopped.");

    public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length is 0 or > 200) yield break;
        var catalog = Catalog;
        var query = FinderQuery.Parse(text);
        List<CatalogArtist> artists = []; List<CatalogSearchSong> songs = []; List<CatalogRelease> albums = [];
        var warnings = new ConcurrentQueue<string>();
        Dictionary<string, DiscoveryMatch>? matches = null;

        async Task Read<T>(string section, Func<Task<CatalogResult<List<T>>>> read, Action<List<T>> publish)
        {
            try
            {
                var result = await read().ConfigureAwait(false);
                if (result.Warning.Length > 0) warnings.Enqueue(result.Warning);
                publish(result.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Enqueue(section + ": " + ArtistCatalog.Explain(ex)); }
        }

        // The three searches run at once on one shared (HTTP/2) connection; each answer is shown as it lands.
        var pending = new List<Task>
        {
            Read("Songs", () => catalog.SearchSongsAsync(query.Term, ct), v => songs = v),
            Read("Artists", () => catalog.SearchAsync(query.Artist.Length > 0 ? query.Artist : query.Text, false, ct), v => artists = v),
            Read("Albums", () => catalog.SearchAlbumsAsync(query.Term, ct), v => albums = v),
        };
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            ct.ThrowIfCancellationRequested();
            yield return Build(query, artists, songs, albums, matches, complete: false, warnings);
        }

        // An artist search ("drake"): that artist's own top songs lead the song list.
        var hits = SearchRanking.Build(query, artists, songs, albums);
        if (hits.FirstOrDefault() is { Artist: { } leading } && BotText.Key(leading.Name) == BotText.Key(text))
        {
            var added = false;
            try
            {
                var own = await catalog.ArtistSongsAsync(leading, ct).ConfigureAwait(false);
                songs = own.Value.Concat(songs).DistinctBy(s => s.Song.Id).ToList();
                added = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Enqueue("Artist songs: " + ArtistCatalog.Explain(ex)); }
            if (added) yield return Build(query, artists, songs, albums, matches, complete: false, warnings);
        }

        // Which of these the library already has (same recording: title, artist, length).
        if (songs.Count > 0)
        {
            try { matches = await _actions.MatchAsync(songs.Select(s => s.Song), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Enqueue("Library check: " + ex.Message); }
        }
        yield return Build(query, artists, songs, albums, matches, complete: true, warnings);
    }

    private OnlineSearchResult Build(FinderQuery query, List<CatalogArtist> artists, List<CatalogSearchSong> songs, List<CatalogRelease> albums,
        Dictionary<string, DiscoveryMatch>? matches, bool complete, ConcurrentQueue<string> warnings)
    {
        var hits = SearchRanking.Build(query, artists, songs, albums);
        // Searching an artist: their own songs lead, songs they only feature on follow.
        var leading = hits.FirstOrDefault() is { Artist: { } a } && BotText.Key(a.Name) == BotText.Key(query.Text) ? a.Id : null;
        var songRows = new List<OnlineSong>();
        foreach (var hit in hits.Where(h => h.Track is not null))
        {
            var s = hit.Track!; _songs[s.Song.Id] = s.Song;
            var local = matches is not null && matches.TryGetValue(s.Song.Id, out var m) ? m.Local?.Id : null;
            var own = leading is not null && s.Song.Artists.FirstOrDefault()?.Id == leading ? 6 : 0;
            songRows.Add(new(s.Song.Id, s.Song.Title, string.Join(", ", s.Song.Artists.Select(a => a.Name)), s.Album?.Title ?? "",
                s.Song.Seconds, s.Song.Explicit, CatalogArtwork.SizedUrl(s.Artwork.Length > 0 ? s.Artwork : s.Album?.Artwork, RowArtwork) ?? "",
                local, s.Album?.Id ?? "", Scale(hit.Score + own)));
        }
        songRows = songRows.OrderByDescending(r => r.Score).ToList();
        var artistRows = SearchRanking.Artists(hits).Select(h =>
        {
            _artists[h.Artist!.Id] = h.Artist;
            return new OnlineArtist(h.Artist.Id, h.Artist.Name, CatalogArtwork.SizedUrl(h.Artist.Picture, CardArtwork) ?? "", h.Artist.Genre, Scale(h.ArtistRank ?? h.Score));
        }).DistinctBy(a => a.Id).ToList();
        var albumRows = hits.Where(h => h.Album is not null).Select(h =>
        {
            var a = h.Album!; _albums[a.Id] = a;
            return new OnlineAlbum(a.Id, a.Title, a.Artist.Name, a.Date.Length >= 4 ? a.Date[..4] : "", a.Kind is "single" or "ep" ? a.Kind : "album",
                a.Count, a.Explicit, CatalogArtwork.SizedUrl(a.Artwork, CardArtwork) ?? "", Scale(h.Score));
        }).DistinctBy(a => a.Id).ToList();
        var top = hits.FirstOrDefault();
        return new OnlineSearchResult(query.Text, songRows, artistRows, albumRows, complete, string.Join(" ", warnings.Distinct()))
        {
            Top = top is null ? null : (top.Kind switch { "Artists" => "artist", "Songs" => "song", _ => "album" }, top.Id),
        };
    }

    /// <summary>Finder's ranking (exact name ≈ 96-110, every word ≈ 76-90, spread ≈ 50-70) onto the
    /// library index's 0-100 scale, so an exact online match and an exact library match tie and the
    /// library's own bonus decides.</summary>
    internal static double Scale(double finder) => Math.Clamp(finder * 0.92, 0, 100);

    public string? CachedArtwork(string url) => url.Length == 0 ? null : _catalog()?.SavedArtwork(url);

    public async Task<string?> ArtworkAsync(string url, CancellationToken ct)
    {
        if (url.Length == 0 || _catalog() is not { } catalog) return null;
        try { return await catalog.ArtworkAsync(url, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or InvalidDataException) { return null; }
    }

    public bool CanDownload => _signedIn();

    public void RequestSignIn() => _signIn();

    public OnlineDownloadStatus StatusOf(OnlineSong song)
    {
        if (!_songs.TryGetValue(song.Id, out var catalogSong)) return OnlineDownloadStatus.None;
        var request = catalogSong.Request;
        var d = _actions.Downloads.History.LastOrDefault(x => x.Track.Request is { } r && SpotifyPlaylists.SameRecording(request, r) && x.State != DownloadState.Removed);
        if (d is null) return OnlineDownloadStatus.None;
        return d.State switch
        {
            DownloadState.Running => new(OnlineDownloadState.Downloading, d.Progress / 100.0),
            DownloadState.Saved or DownloadState.AlreadyThere => new(OnlineDownloadState.Saved, 1),
            DownloadState.Failed => new(OnlineDownloadState.Failed, 0, d.Error),
            DownloadState.Cancelled => OnlineDownloadStatus.None,
            _ => new(OnlineDownloadState.Queued, 0, d.Error),
        };
    }

    public async Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct)
    {
        var wanted = songs.Select(s => _songs.TryGetValue(s.Id, out var c) ? c : null).Where(c => c is not null).Cast<CatalogSong>().ToList();
        if (wanted.Count == 0) return "Search again, then choose the song.";
        var queued = await _actions.QueueAsync(wanted, priority: now, ct).ConfigureAwait(false);
        if (queued.Added + queued.Existing == 0) return queued.Owned > 0 ? "Already in your library." : "Nothing to download.";
        return now ? "Downloading." : "Added to downloads.";
    }

    public void OpenArtist(OnlineArtist artist)
    {
        if (_artists.TryGetValue(artist.Id, out var a)) _pages.OpenArtist(a);
    }

    public async Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct)
    {
        if (_albums.TryGetValue(album.Id, out var a)) await _pages.OpenAlbumAsync(a, ct);
    }
}
