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
    private readonly CatalogPages _pages;
    private readonly Func<bool> _signedIn;
    private readonly Action _signIn;
    private readonly Func<SpotifyPlaylists?> _playlists;
    private readonly ConcurrentDictionary<string, CatalogSong> _songs = new();
    private readonly ConcurrentDictionary<string, Download> _restored = new();
    private readonly ConcurrentDictionary<string, CatalogArtist> _artists = new();
    private readonly ConcurrentDictionary<string, CatalogRelease> _albums = new();

    /// <summary>How many playlists a search asks for (owner #69); the All view shows one row of them.</summary>
    internal const int PlaylistLimit = 24;

    public FinderOnlineSearch(Func<ArtistCatalog?> catalog, DiscoveryActions actions, CatalogPages pages, Func<bool> signedIn, Action signIn,
        Func<SpotifyPlaylists?>? playlists = null)
    {
        _catalog = catalog; _actions = actions; _pages = pages; _signedIn = signedIn; _signIn = signIn; _playlists = playlists ?? (() => null);
        _actions.Downloads.Changed += OnDownloadChanged;
    }

    public void Detach() => _actions.Downloads.Changed -= OnDownloadChanged;

    private void OnDownloadChanged(Download _) => DownloadsChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? DownloadsChanged;

    public void WarmUp()
    {
        _catalog()?.WarmUp();
        _playlists()?.WarmUpSearch();
    }

    public void Retry() => _catalog()?.ForgetGuessedWaits();

    public string Route => _catalog()?.RouteKey ?? "";

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
        // Playlists (#69) come from another service and never hold up songs: shown whenever they land.
        var playlists = _playlists() is { } imports && text.Length >= 2 ? FindPlaylistsAsync(imports, text, warnings, ct) : null;
        OnlineSearchResult Result(bool complete) => Build(catalog, query, artists, songs, albums, matches, complete, warnings) with
        {
            Playlists = playlists is { IsCompletedSuccessfully: true } ? playlists.Result : [],
        };

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
            yield return Result(false);
        }

        songs = await catalog.RefineTitleAsync(query, songs, ct).ConfigureAwait(false);
        yield return Result(false);

        // An artist search ("drake"): that artist's own top songs lead the song list.
        var hits = SearchRanking.Build(query, artists, songs, albums, fansOf: catalog.FansOf);
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
            if (added) yield return Result(false);
        }

        // Which of these the library already has (same recording: title, artist, length).
        if (songs.Count > 0)
        {
            try { matches = await _actions.MatchAsync(songs.Select(s => s.Song), ct, reuse: true).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Enqueue("Library check: " + ex.Message); }
        }
        if (playlists is { IsCompleted: false })
        {
            yield return Result(false);
            await playlists.ConfigureAwait(false);
        }
        yield return Result(true);
    }

    private OnlineSearchResult Build(ArtistCatalog catalog, FinderQuery query, List<CatalogArtist> artists, List<CatalogSearchSong> songs, List<CatalogRelease> albums,
        Dictionary<string, DiscoveryMatch>? matches, bool complete, ConcurrentQueue<string> warnings)
    {
        var hits = SearchRanking.Build(query, artists, songs, albums, fansOf: catalog.FansOf);
        // Searching an artist: their own songs lead, songs they only feature on follow.
        var leading = hits.FirstOrDefault() is { Artist: { } a } && BotText.Key(a.Name) == BotText.Key(query.Text) ? a.Id : null;
        var songRows = new List<OnlineSong>();
        foreach (var hit in hits.Where(h => h.Track is not null))
        {
            var s = hit.Track!; _songs[s.Song.Id] = s.Song;
            var local = matches is not null && matches.TryGetValue(s.Song.Id, out var m) ? m.Local?.Id : null;
            // The owner prefers the catalog's popularity order here: a hit the artist is featured
            // on ("All The Stars" for SZA) may lead their own songs. No own-song bonus.
            var own = 0;
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
    internal static double Scale(double finder)
    {
        // Above 90 the curve flattens towards 97 without reaching it: strong online matches keep
        // their order among themselves but never outrank an equally strong library match.
        var s = Math.Max(0, finder * 0.92);
        return s <= 90 ? s : 90 + 7 * (1 - Math.Exp(-(s - 90) / 10));
    }

    private static async Task<IReadOnlyList<OnlinePlaylist>> FindPlaylistsAsync(SpotifyPlaylists imports, string text, ConcurrentQueue<string> warnings, CancellationToken ct)
    {
        try { return await PlaylistPageAsync(imports, text, 0, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Enqueue("Playlists: " + ArtistCatalog.Explain(ex)); return []; }
    }

    public async Task<IReadOnlyList<OnlinePlaylist>> MorePlaylistsAsync(string query, int offset, CancellationToken ct)
    {
        query = query.Trim();
        if (_playlists() is not { } imports || query.Length is < 2 or > 200 || offset is <= 0 or > 1000) return [];
        return await PlaylistPageAsync(imports, query, offset, ct).ConfigureAwait(false);
    }

    // One page of PlaylistLimit, best first. Scores fall with the page, so a later page always sorts below an earlier one.
    private static async Task<IReadOnlyList<OnlinePlaylist>> PlaylistPageAsync(SpotifyPlaylists imports, string text, int offset, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (found, source) = await imports.SearchAsync(text, PlaylistLimit, ct, offset).ConfigureAwait(false);
        Noctis.Services.DebugLog.Write("Search", $"\"{text}\" from {offset}: {found.Count} playlists from {source} in {clock.ElapsedMilliseconds} ms ({PlaylistCatalog.LastCounts})");
        return found.Select((p, i) => new OnlinePlaylist(p.Found.Url, p.Found.Name, p.Found.Owner, p.Found.Url, p.Found.Artwork,
            p.Found.Tracks, p.Found.Saves, p.SavedAs, 10_000 - offset - i)).ToList();
    }

    public string? CachedArtwork(string url) => url.Length == 0 ? null : _catalog()?.SavedArtwork(url);

    public async Task<string?> ArtworkAsync(string url, CancellationToken ct)
    {
        if (url.Length == 0 || _catalog() is not { } catalog) return null;
        try { return await catalog.ArtworkAsync(url, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or InvalidDataException) { return null; }
    }

    // Owner #102: a queue row's cover. One the page already showed (any size) is used at once; else the small one.
    // Owner #104: a song of an imported playlist saved without covers takes the one filled in since.
    public string? CachedQueueCover(OnlineSong song)
        => CoverUrl(song) is not { Length: > 0 } url ? null
            : CachedArtwork(url) ?? (CatalogArtwork.ThumbUrl(url) is { } thumb ? CachedArtwork(thumb) : null);
    private string? CoverUrl(OnlineSong song) => song.ArtworkUrl.Length > 0 ? song.ArtworkUrl : _playlists()?.ArtworkFor(song.Id);

    public async Task<string?> QueueCoverAsync(OnlineSong song, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var url = CoverUrl(song);
        if (url is null && _playlists() is { } imports) url = await imports.ArtworkForAsync(song.Id, ct).ConfigureAwait(false);
        if (url is not { Length: > 0 } || _catalog() is not { } catalog) return null;
        string? path = null;
        try
        {
            // The quick lane: a page loading hundreds of row covers does not hold the queue's thirty up.
            path = CatalogArtwork.ThumbUrl(url) is { } thumb && thumb != url ? await catalog.ArtworkAsync(thumb, ct, quick: true).ConfigureAwait(false) : null;
            path ??= await catalog.ArtworkAsync(url, ct, quick: true).ConfigureAwait(false); // the small one was refused: the cover as it is
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or InvalidDataException) { }
        if (path is not null)
            Noctis.Services.DebugLog.Write("Queue", $"cover of \"{song.Title}\" in {clock.ElapsedMilliseconds} ms ({new FileInfo(path).Length / 1024} KB)");
        else Noctis.Services.DebugLog.Write("Queue", $"no cover for \"{song.Title}\" ({(url is { Length: > 0 } ? "not loaded" : "none listed")})");
        return path;
    }

    public bool CanDownload => _signedIn();

    public void RequestSignIn() => _signIn();

    /// <summary>A recommendation uses the same native download actions as a search result.</summary>
    internal OnlineSong Remember(CatalogSearchSong track)
    {
        var s = track.Song;
        _songs[s.Id] = s;
        if (track.Album is { } album) _albums[album.Id] = album;
        return new(s.Id, s.Title, string.Join(", ", s.Artists.Select(a => a.Name)), track.Album?.Title ?? "",
            s.Seconds, s.Explicit, CatalogArtwork.SizedUrl(track.Artwork.Length > 0 ? track.Artwork : track.Album?.Artwork, CardArtwork) ?? "",
            AlbumId: track.Album?.Id ?? "");
    }

    public IDownloadQueue Downloads => _queue ??= new FinderDownloadQueue(_actions.Downloads);
    private FinderDownloadQueue? _queue;

    public OnlineDownloadStatus StatusOf(OnlineSong song)
    {
        if (!_songs.TryGetValue(song.Id, out var catalogSong)) return OnlineDownloadStatus.None;
        var request = catalogSong.Request;
        var d = _actions.Downloads.History.LastOrDefault(x => x.Track.Request is { } r && SpotifyPlaylists.SameRecording(request, r) && x.State != DownloadState.Removed);
        if (d is null) return OnlineDownloadStatus.None;
        return d.State switch
        {
            DownloadState.Running => new(OnlineDownloadState.Downloading, Math.Clamp(d.Progress, 0, 1), d.Stage, d.Step), // a 0..1 share
            // Owner #87: played only (in the stream cache, not added): offered for Songs; gone from the cache: nothing.
            DownloadState.Saved or DownloadState.AlreadyThere when _actions.Downloads.IsCached(d) && d.KeepIn is null =>
                d.SavedPath is { } cached && File.Exists(cached) ? new(OnlineDownloadState.Streamed, 1) : OnlineDownloadStatus.None,
            DownloadState.Saved or DownloadState.AlreadyThere => new(OnlineDownloadState.Saved, 1),
            DownloadState.Failed => new(OnlineDownloadState.Failed, 0, d.Error),
            DownloadState.Cancelled => OnlineDownloadStatus.None,
            _ => new(OnlineDownloadState.Queued, 0, d.Error),
        };
    }

    public string? SavedPathOf(OnlineSong song)
    {
        if (!_songs.TryGetValue(song.Id, out var catalogSong)) return null;
        var d = _actions.Downloads.History.LastOrDefault(x => x.Track.Request is { } r && SpotifyPlaylists.SameRecording(catalogSong.Request, r) && x.State != DownloadState.Removed);
        return d is { State: DownloadState.Saved or DownloadState.AlreadyThere, SavedPath: { } path } && !_actions.Downloads.IsCached(d) ? path : null;
    }

    public string? CompletedPlaybackPath(OnlineSong song)
    {
        if (!_songs.TryGetValue(song.Id, out var catalogSong)) return null;
        var d = _actions.Downloads.History.LastOrDefault(x => x.Track.Request is { } r && SpotifyPlaylists.SameRecording(catalogSong.Request, r) && x.State != DownloadState.Removed);
        return d is { State: DownloadState.Saved or DownloadState.AlreadyThere, SavedPath: { } path } ? path : null;
    }

    public bool CancelQueued(OnlineSong song)
    {
        if (!_songs.TryGetValue(song.Id, out var catalogSong)) return false;
        var d = _actions.Downloads.Active.LastOrDefault(x => x.Track.Request is { } r && SpotifyPlaylists.SameRecording(catalogSong.Request, r));
        // Owner #100: skipped past: stopped even mid-transfer, unless it is the song being heard.
        if (d is null || !d.IsActive || !_actions.Downloads.IsCached(d) || d.KeepIn is not null || d.Playback?.HasReaders == true || d.State == DownloadState.Running && d.Progress >= .85) return false;
        try { return _actions.Downloads.Remove(d); }
        catch (Exception) { return false; } // the queue file could not be saved: it simply downloads
    }

    public void SetPlaybackWindow(string? currentPath, IReadOnlyList<OnlineSong> songs)
    {
        var names = songs.Select(s => _songs.TryGetValue(s.Id, out var c) ? _actions.TrackFor(c.Request).FileName : _restored.GetValueOrDefault(s.Id)?.Track.FileName).OfType<string>().ToList();
        if (currentPath is not null) names.Add(Path.GetFileName(currentPath));
        _actions.Downloads.SetPlaybackWindow(names);
    }

    public Noctis.Models.Track? RestorePlaybackTrack(string path, Guid id)
    {
        if (!IsCachedFile(path) || File.Exists(path)) return null;
        var d = _actions.Downloads.History.LastOrDefault(d => d.Track.Request is not null &&
            string.Equals(Path.Combine(d.Folder, d.Track.FileName + Path.GetExtension(path)), path, StringComparison.OrdinalIgnoreCase));
        if (d is null) return null;
        var key = "restored:" + id;
        _restored[key] = d;
        var song = new OnlineSong(key, d.Track.Title, d.Track.Artist, Seconds: d.Track.Duration?.TotalSeconds);
        var track = Noctis.ViewModels.PlayerViewModel.StandIn(this, song, keep: false);
        track.Id = id; track.FilePath = path; track.IsExternal = true;
        return track;
    }

    public async Task PrefetchAsync(OnlineSong song, bool keep, CancellationToken ct)
    {
        if (!_songs.TryGetValue(song.Id, out var catalogSong)) return;
        await _actions.QueueAsync([catalogSong], priority: true, ct, stream: !keep).ConfigureAwait(false);
    }

    public async Task AskAheadAsync(OnlineSong song, CancellationToken ct)
    {
        if (!_songs.TryGetValue(song.Id, out var catalogSong) || !CanDownload) return;
        if (StatusOf(song).State != OnlineDownloadState.None) return; // already downloading or saved
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var asked = new AskOnlyStream();
        await _actions.TrackFor(catalogSong.Request).Fetch(asked, null, ct).ConfigureAwait(false);
        Noctis.Services.DebugLog.Write("Finder", $"asked ahead \"{song.Title}\": ready in the chat in {clock.Elapsed.TotalSeconds:0.0} s");
    }

    public async Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct)
    {
        var wanted = songs.Select(s => _songs.TryGetValue(s.Id, out var c) ? c : null).Where(c => c is not null).Cast<CatalogSong>().ToList();
        if (wanted.Count == 0) return "Search again, then choose the song.";
        var queued = await _actions.QueueAsync(wanted, priority: now, ct).ConfigureAwait(false);
        if (queued.Added + queued.Existing == 0) return queued.Owned > 0 ? "Already in your library." : "Nothing to download.";
        return now ? "Downloading." : "Added to downloads.";
    }

    public bool IsStreamedFile(string? path)
    {
        if (_actions.Downloads.CacheFolder is not { } cache || !StreamCache.Contains(cache, path)) return false;
        if (StreamedDownload(path!) is { KeepIn: not null }) return false;
        try { return !File.Exists(Path.Combine(_actions.LibraryFolder, Path.GetFileName(path!))); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return true; }
    }

    public bool IsCachedFile(string? path) => _actions.Downloads.CacheFolder is { } cache && StreamCache.Contains(cache, path);

    /// <summary>Owner #87: "Add to songs" from the player (and #88: a playlist add). The copy runs off the UI thread.</summary>
    public Task<StreamedAdd> AddStreamedAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        if (!IsCachedFile(path)) return new StreamedAdd("Already in library", null);
        // Where the library copy is: the download folder it went to, else the one it goes to now.
        var download = StreamedDownload(path);
        var library = download?.KeepIn ?? _actions.LibraryFolder;
        var target = Path.Combine(library, Path.GetFileName(path));
        if (!IsStreamedFile(path)) return new StreamedAdd("Already in library", target);
        if (download is not null)
        {
            var (copy, pending) = _actions.Downloads.Keep(download, library);
            return new StreamedAdd(pending ? "Saving to library…" : "Saved to library", copy ?? target);
        }
        // Its download was cleared from the list: the cached file itself is copied.
        if (!File.Exists(path)) throw new IOException("This song is no longer in the stream cache. Play it again, then add it.");
        var copied = StreamCache.CopyToLibrary(path, library);
        Noctis.Services.Search.OnlineMusicSearch.PublishSavedFile(copied);
        return new StreamedAdd("Saved to library", copied);
    }, ct);

    /// <summary>The played-only download behind a cached file (saved, or still arriving under that name).</summary>
    private Download? StreamedDownload(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return _actions.Downloads.History.LastOrDefault(d => _actions.Downloads.IsCached(d) && d.State != DownloadState.Removed &&
            (d.SavedPath is { } saved ? string.Equals(Path.GetFullPath(saved), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                : string.Equals(d.Track.FileName, name, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<Noctis.Models.Track> PreparePlaybackAsync(OnlineSong song, CancellationToken ct) => PreparePlaybackAsync(song, keep: false, ct);

    public async Task<Noctis.Models.Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct)
    {
        Download download;
        bool restored = _restored.TryGetValue(song.Id, out var parked);
        if (restored)
            download = _actions.Downloads.Start(parked!.Track, parked.Folder, priority: true);
        else
        {
        if (!_songs.TryGetValue(song.Id, out var catalogSong)) throw new InvalidOperationException("Search again, then choose the song.");
        // Owner #87: Play only streams; the song is kept in the stream cache, not added to Songs.
        await _actions.QueueAsync([catalogSong], priority: true, ct, stream: !keep);
        var match = (await _actions.MatchAsync([catalogSong], ct))[catalogSong.Id];
        if (match.Local is { } local) return Make(local.Path, local.Id, external: false);
        download = match.Download ?? throw new IOException("The download request could not be found. Open Downloads to retry it.");
        }
        // Owner #83: the song clicked to play last is fetched now, ahead of every song clicked before it.
        _actions.Downloads.PlayNow(download);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (download.SavedPath is { } saved && File.Exists(saved))
            {
                if (_actions.Downloads.IsCached(download)) StreamCache.Touch(saved); // played again: its days start over
                return Make(saved);
            }
            if (!restored && download.Playback?.ReadyPath is { } ready) return Make(ready);
            // A temporary outage/rate-limit wait must not be mistaken for a missing recording.
            // Stay on the same request; user skip/pause still cancels this wait immediately.
            if (download.State == DownloadState.Failed)
                throw new IOException(download.Error.Length > 0 ? download.Error : "The download is waiting. Open Downloads to retry it.");
            if (download.State is DownloadState.Cancelled or DownloadState.Removed)
                throw new IOException("The download was removed or interrupted.");
            await Task.Delay(200, ct);
        }

        Noctis.Models.Track Make(string path, Guid? id = null, bool external = true) => new()
        {
            Id = id ?? Guid.NewGuid(), FilePath = path, Title = song.Title, Artist = song.ArtistDisplay,
            Album = song.Album, Duration = TimeSpan.FromSeconds(song.Seconds is > 0 ? song.Seconds.Value : 0),
            IsExplicit = song.Explicit == true, IsExternal = external,
            AlbumArtworkPath = CachedArtwork(song.ArtworkUrl) ?? ""
        };
    }

    public void OpenArtist(OnlineArtist artist)
    {
        // Related-artist tiles already carry a provider id; they need not have appeared
        // in this Search instance first. Never turn navigation into a name-only search.
        if (_artists.TryGetValue(artist.Id, out var a)) _pages.OpenArtist(a);
        else if (System.Text.RegularExpressions.Regex.IsMatch(artist.Id, @"^(apple|deezer):[1-9][0-9]{0,17}$"))
            _pages.OpenArtist(new(artist.Id, artist.Name, artist.Genre, artist.PictureUrl));
    }

    public async Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct)
    {
        if (_albums.TryGetValue(album.Id, out var a)) await _pages.OpenAlbumAsync(a, ct);
    }
}
