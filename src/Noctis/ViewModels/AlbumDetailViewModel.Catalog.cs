using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Noctis Plus: an online album on the album page itself. The songs the library has are the
/// page's normal rows (real tracks: same menus, same playback); the ones it lacks follow in
/// the same row shape with a download button. The album is display-only: never added to the
/// library, and a library refresh never closes the page for not finding it there.
/// </summary>
public partial class AlbumDetailViewModel
{
    private CatalogAlbumOpen? _catalog;
    private Services.Search.IOnlineMusicSearch? _catalogOnline;
    // A library album the catalog completed (its missing songs shown under the owned ones),
    // as opposed to an online album the library has nothing of.
    private bool _catalogEnriches;
    private CancellationTokenSource? _enrichStop;
    private Func<CancellationToken, Task<CatalogAlbumOpen?>>? _retryCatalog;
    private bool _retryEnriches;
    private bool _catalogDisposed;
    private bool _catalogRetryAvailable;
    public bool CanRetryCatalog => !_catalogDisposed && _catalogRetryAvailable && !IsCatalogLoading && HasCatalogNotice && _retryCatalog is not null;

    /// <summary>The page shows catalog songs (an online album, or a library album completed by the catalog).</summary>
    public bool IsCatalogAlbum => _catalog is not null;

    /// <summary>An online album opened from Search, For you or an artist page: not in the library.</summary>
    public bool IsOnlineAlbum => !_catalogEnriches && (_catalog is not null || Album.CatalogReleaseId is not null);

    /// <summary>The album's songs the library does not have yet, in album order.</summary>
    public ObservableCollection<ArtistCatalogSong> CatalogMissing { get; } = [];
    public bool HasCatalogMissing => CatalogMissing.Count > 0;

    /// <summary>"Not in your library" heads the missing rows only when owned rows sit above them.</summary>
    public bool ShowCatalogMissingHeader => HasCatalogMissing && Tracks.Count > 0;

    /// <summary>Play, Shuffle and Add to Queue need local songs, or (owner #100) this page's toggle on: an online album
    /// with none hides them otherwise.</summary>
    public bool ShowPlayActions => !IsOnlineAlbum && !IsCatalogLoading || Tracks.Count > 0 || Streams;

    // ── Owner #100: the header toggle: this album's songs you don't have play too ──
    private string StreamPage => "album:" + (_catalog?.Album.Release.Id ?? Album.Name);
    public bool StreamMissing
    {
        get => _player.StreamsPage(StreamPage);
        set { _player.SetStreamPage(StreamPage, value); NotifyStreamToggle(); }
    }
    public bool ShowStreamToggle => HasCatalogMissing && _catalogOnline is not null;
    public string StreamToggleTip => StreamToggle.Tip(StreamMissing);
    [RelayCommand] private void ToggleStreamMissing() => StreamMissing = !StreamMissing;
    private void NotifyStreamToggle()
    {
        OnPropertyChanged(nameof(StreamMissing)); OnPropertyChanged(nameof(StreamToggleTip));
        OnPropertyChanged(nameof(ShowStreamToggle)); OnPropertyChanged(nameof(ShowPlayActions));
    }
    private bool Streams => _catalog is not null && HasCatalogMissing && _catalogOnline is not null && StreamMissing;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ArtistCatalogSong, Track> _standIns = new();

    /// <summary>What Play, Shuffle and Play from take: the album's songs in album order, the ones you don't have as
    /// stand-ins when this page's toggle is on. Owner 10-20: they only play (stream cache, "Add to songs"), like Search, also
    /// on an album you have some songs of; only a playlist's songs join Songs.</summary>
    private List<Track> PlaybackList(IEnumerable<Track> owned)
    {
        var list = InAlbumOrder(owned);
        if (!Streams) return list;
        foreach (var row in CatalogMissing.OrderBy(r => r.Rank))
        {
            if (row.Song.Online is not { } song) continue;
            var standIn = _standIns.GetValue(row, _ => PlayerViewModel.StandIn(_catalogOnline!, song, keep: false, StreamPage));
            var at = list.FindIndex(t => t.IsPendingStream ? false : t.TrackNumber > row.Rank);
            if (at < 0) list.Add(standIn); else list.Insert(at, standIn);
        }
        return list;
    }

    // Owner #34: an online album's page opens at once; skeleton rows stand in until its songs are read.
    private CatalogRelease? _catalogLoading;
    private bool _enrichLoading;
    public bool IsCatalogLoading => _catalogLoading is not null || _enrichLoading;
    public string CatalogLoadingText => Tracks.Count > 0 ? "Loading more songs" : "Loading songs";
    internal bool IsCatalogLoadingFor(string releaseId) => _catalogLoading?.Id == releaseId;
    /// <summary>Title-bar widths of the skeleton rows (varied, like real titles).</summary>
    public IReadOnlyList<double> CatalogSkeletonRows { get; } = [210, 160, 240, 180, 220, 150];

    internal void ShowCatalogLoading(CatalogRelease release)
    {
        _retryEnriches = false;
        _retryCatalog = async ct => await (FreeMusicPlugin.Current?.CatalogPages
            ?? throw new InvalidOperationException("Online catalog is unavailable.")).ReadAlbumAsync(release, ct, refresh: true);
        _catalogLoading = release;
        OnPropertyChanged(nameof(IsCatalogLoading));
        OnPropertyChanged(nameof(ShowPlayActions));
        OnPropertyChanged(nameof(CanRetryCatalog));
        RetryCatalogCommand.NotifyCanExecuteChanged();
        _ = LoadCatalogCoverAsync(release);
    }

    internal void ShowCatalogFailed(string message)
    {
        _catalogLoading = null;
        OnPropertyChanged(nameof(IsCatalogLoading));
        _catalogRetryAvailable = message.Length > 0;
        CatalogNotice = message;
        OnPropertyChanged(nameof(CanRetryCatalog));
    }

    /// <summary>A quiet line (partial catalog answer, a download message).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogNotice))] private string _catalogNotice = "";
    public bool HasCatalogNotice => CatalogNotice.Length > 0;
    partial void OnCatalogNoticeChanged(string value)
    {
        OnPropertyChanged(nameof(CanRetryCatalog));
        RetryCatalogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRetryCatalog))]
    private Task RetryCatalog() => _retryCatalog is { } load ? EnrichCatalogAsync(load, _retryEnriches) : Task.CompletedTask;

    /// <summary>
    /// A library album: find the same release in the catalog (in the background, metadata only)
    /// and show the songs the library lacks under the owned rows (owner, 2026-10-17: owning one
    /// song of an album hid the rest). Quietly does nothing when the catalog is off, offline or
    /// has no matching release.
    /// </summary>
    internal void EnrichFromCatalog()
    {
        if (_catalog is not null || Album.CatalogReleaseId is not null || Album.Tracks.Count == 0) return;
        if (Services.OnlineAccess.Shared.IsOffline || FreeMusicPlugin.Current?.CatalogPages is not { Enabled: true } pages) return;
        var artist = Album.Artist;
        if (string.IsNullOrWhiteSpace(artist) || artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)) return;
        var name = Album.Name;
        var titles = Album.Tracks.Select(t => t.Title).ToArray();
        var localAlbums = pages.LocalAlbums(artist); // take the observable library snapshot on the UI thread
        _ = EnrichCatalogAsync(async ct =>
        {
            var (identity, _) = await pages.IdentifyAsync(artist, localAlbums, titles, ct);
            if (identity is null) throw new InvalidOperationException("Couldn't identify this album's artist.");
            var release = await pages.FindLibraryReleaseAsync(identity, name, ct);
            if (release is null) throw new InvalidOperationException("This album wasn't found in the online catalog.");
            return await pages.ReadAlbumAsync(release, ct);
        });
    }

    /// <summary>Paint feedback before any catalog IO; keep the owned rows and guard late answers.</summary>
    internal async Task EnrichCatalogAsync(Func<CancellationToken, Task<CatalogAlbumOpen?>> load, bool enriches = true)
    {
        if (_catalogDisposed) return;
        _retryCatalog = load;
        _retryEnriches = enriches;
        _enrichStop?.Cancel();
        var stop = _enrichStop = new CancellationTokenSource();
        var previousCatalog = _catalog;
        _catalogRetryAvailable = false;
        CatalogNotice = "";
        _enrichLoading = true;
        OnPropertyChanged(nameof(IsCatalogLoading));
        OnPropertyChanged(nameof(CatalogLoadingText));
        OnPropertyChanged(nameof(ShowPlayActions));
        OnPropertyChanged(nameof(CanRetryCatalog));
        RetryCatalogCommand.NotifyCanExecuteChanged();
        try
        {
            var open = await Task.Run(() => load(stop.Token));
            if (stop.IsCancellationRequested) return;
            if (open is null) throw new InvalidOperationException("No matching album was returned.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (stop.IsCancellationRequested || !ReferenceEquals(_enrichStop, stop) || !ReferenceEquals(_catalog, previousCatalog)) return;
                _catalogEnriches = enriches;
                ShowCatalog(open);
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or TimeoutException
                                       or InvalidDataException or InvalidOperationException or SourcesWaitingException or ArgumentException or UnauthorizedAccessException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_catalogDisposed || stop.IsCancellationRequested || !ReferenceEquals(_enrichStop, stop)) return;
                _catalogRetryAvailable = true;
                CatalogNotice = "Couldn't load album songs. Try again.";
                Services.DebugLog.Write("Catalog", "Album loading failed: " + ArtistCatalog.Explain(ex));
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_enrichStop, stop)) return;
                _enrichStop = null;
                _enrichLoading = false;
                OnPropertyChanged(nameof(IsCatalogLoading));
                OnPropertyChanged(nameof(ShowPlayActions));
                OnPropertyChanged(nameof(CanRetryCatalog));
                RetryCatalogCommand.NotifyCanExecuteChanged();
            });
            stop.Dispose();
        }
    }

    internal void ShowCatalog(CatalogAlbumOpen open)
    {
        _catalog = open;
        var wasLoading = _catalogLoading?.Id == open.Album.Release.Id;
        _catalogLoading = null;
        OnPropertyChanged(nameof(IsCatalogLoading));
        OnPropertyChanged(nameof(CanRetryCatalog));
        if (!_catalogEnriches)
        {
            Album.TrackCount = Math.Max(open.Album.Release.Count, open.Album.Songs.Count);
            Album.TotalDuration = open.Album.Songs.Count >= Album.TrackCount && open.Album.Songs.All(s => s.Seconds is > 0)
                ? TimeSpan.FromSeconds(open.Album.Songs.Sum(s => s.Seconds!.Value)) : TimeSpan.Zero;
            OnPropertyChanged(nameof(Album)); // the header's facts read these plain values
        }
        if (Services.Search.OnlineMusicSearch.Current is { } online && !ReferenceEquals(online, _catalogOnline))
        {
            if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
            _catalogOnline = online;
            online.DownloadsChanged += CatalogDownloadsChanged;
        }
        // The catalog's own wording mentions a "Download all" this page does not have.
        _catalogRetryAvailable = open.Album.Songs.Count == 0 || open.Album.Notice.Length > 0;
        CatalogNotice = open.Album.Songs.Count == 0 ? "No album songs returned. Try again."
            : open.Album.Notice.Length > 0 ? "Some of this album's songs are not in the online catalog." : "";
        if (HasCatalogNotice && FreeMusicPlugin.Current?.CatalogPages is { } pages)
        {
            _retryEnriches = _catalogEnriches;
            _retryCatalog = async ct => await pages.ReadAlbumAsync(open.Album.Release, ct, refresh: true);
        }
        RefreshCatalogOwnership();
        OnPropertyChanged(nameof(IsCatalogAlbum));
        OnPropertyChanged(nameof(IsOnlineAlbum));
        // A library album keeps its own cover.
        if (!_catalogEnriches && !wasLoading) _ = LoadCatalogCoverAsync(open.Album.Release);
    }

    /// <summary>Which album songs the library has right now (after a download lands, a scan).
    /// Owned ones become the page's track rows; the rest stay download rows.</summary>
    private void RefreshCatalogOwnership()
    {
        if (_catalog is not { } open) return;
        var locals = _library.Tracks.Where(t => File.Exists(t.FilePath)).ToList();
        var byId = locals.ToDictionary(t => t.Id);
        var owned = new List<Track>();
        var missing = new List<ArtistCatalogSong>();
        var number = 0;
        foreach (var song in open.Album.Songs)
        {
            number++;
            Track? local = open.Matches.TryGetValue(song.Id, out var match) && match.Local is { } l ? byId.GetValueOrDefault(l.Id) : null;
            local ??= locals.FirstOrDefault(t => RecordingMatch.Fits(song.Request, t.Artist, t.Title, t.Duration));
            if (local is not null) { if (!owned.Contains(local)) owned.Add(local); continue; }
            // A library album: a song it already has under another title/edition stays a library row.
            if (_catalogEnriches && Tracks.Any(t => RecordingMatch.Fits(song.Request, t.Artist, t.Title, t.Duration))) continue;
            var existing = CatalogMissing.FirstOrDefault(r => r.CatalogId == song.Id);
            missing.Add(existing ?? new ArtistCatalogSong(song.Number > 0 ? song.Number : number,
                new SearchSongItem((_catalogOnline as FinderOnlineSearch)?.Remember(new CatalogSearchSong(song, open.Album.Release, open.Album.Release.Artwork))
                    ?? new Services.Search.OnlineSong(song.Id, song.Title, string.Join(", ", song.Artists.Select(a => a.Name)), open.Album.Release.Title, song.Seconds, song.Explicit)),
                song.Id));
        }
        // A library album keeps its own rows (all of them, in its own order); only an online
        // album's rows are the songs the library turned out to have.
        if (!_catalogEnriches && !IsShownTrackList(owned))
        {
            Tracks.ReplaceAll(owned);
            BuildDiscGroups();
        }
        SearchViewModel.Sync(CatalogMissing, missing);
        RefreshCatalogDownloads();
        OnPropertyChanged(nameof(HasCatalogMissing));
        OnPropertyChanged(nameof(ShowCatalogMissingHeader));
        OnPropertyChanged(nameof(ShowStreamToggle));
        OnPropertyChanged(nameof(ShowPlayActions));
        OnPropertyChanged(nameof(ShowAddAlbumDescription));
        DownloadMissingCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadCatalogCoverAsync(CatalogRelease release)
    {
        if (FreeMusicPlugin.Current?.CatalogPages is not { } pages || release.Artwork.Length == 0) return;
        try
        {
            await pages.Catalog.ProgressiveArtworkAsync(release.Artwork, path => Dispatcher.UIThread.InvokeAsync(() =>
            {
                if ((_catalog?.Album.Release.Id ?? _catalogLoading?.Id) == release.Id && File.Exists(path)) { Album.ArtworkPath = path; HeaderArtPath = path; }
            }).GetTask(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or TimeoutException or InvalidDataException) { }
    }

    private void CatalogDownloadsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RefreshCatalogDownloads);

    private void RefreshCatalogDownloads()
    {
        RefreshCatalogPlayback();
        if (_catalogOnline is not { } online) return;
        foreach (var row in CatalogMissing) if (row.Song.Online is { } song) row.Song.Apply(online.StatusOf(song));
    }
    private void RefreshCatalogPlayback() { foreach (var row in CatalogMissing) row.Song.ApplyPlayback(_player); }

    [RelayCommand] private Task DownloadCatalogSong(ArtistCatalogSong? row) => DownloadCatalogAsync(row is null ? [] : [row], now: true);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayCatalogSong(ArtistCatalogSong? row)
    {
        if (row?.Song.Online is not { } song || _catalogOnline is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        if (Streams)
        {
            // Owner #100: the album from this song on, like any other row.
            var list = PlaybackList(Tracks);
            var at = _standIns.TryGetValue(row, out var standIn) ? list.IndexOf(standIn) : -1;
            if (at >= 0) { CatalogNotice = ""; _player.PlayFromRow(list, at); return; }
        }
        try { CatalogNotice = ""; await _player.PlayOnlineAsync(online, song); CatalogNotice = ""; }
        catch (Exception ex) { CatalogNotice = ex.Message; }
    }
    [RelayCommand] private Task QueueCatalogSong(ArtistCatalogSong? row) => DownloadCatalogAsync(row is null ? [] : [row], now: false);

    /// <summary>Every song of the album the library lacks, into the download queue (in album order).</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadMissing))]
    private Task DownloadMissing() => DownloadCatalogAsync(CatalogMissing.Where(r => r.Song.CanStartDownload || r.Song.IsStreamed).ToList(), now: false);
    private bool CanDownloadMissing() => CatalogMissing.Any(r => r.Song.CanStartDownload || r.Song.IsStreamed);

    private async Task DownloadCatalogAsync(IReadOnlyList<ArtistCatalogSong> rows, bool now)
    {
        var songs = rows.Select(r => r.Song.Online).OfType<Services.Search.OnlineSong>().ToList();
        if (songs.Count == 0 || _catalogOnline is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        try { CatalogNotice = await online.DownloadAsync(songs, now, CancellationToken.None) is var message && message == "Downloading." ? "" : message; }
        catch (Exception ex) { CatalogNotice = ex.Message; }
        RefreshCatalogDownloads();
    }

    private void DisposeCatalog()
    {
        _catalogDisposed = true;
        _catalogLoading = null; // a closed page is never filled later
        _enrichLoading = false;
        _enrichStop?.Cancel();
        if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
        _catalogOnline = null;
    }
}
