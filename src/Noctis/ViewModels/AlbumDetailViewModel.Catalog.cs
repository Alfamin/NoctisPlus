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

    /// <summary>The page shows catalog songs (an online album, or a library album completed by the catalog).</summary>
    public bool IsCatalogAlbum => _catalog is not null;

    /// <summary>An online album opened from Search, For you or an artist page: not in the library.</summary>
    public bool IsOnlineAlbum => _catalog is not null && !_catalogEnriches;

    /// <summary>The album's songs the library does not have yet, in album order.</summary>
    public ObservableCollection<ArtistCatalogSong> CatalogMissing { get; } = [];
    public bool HasCatalogMissing => CatalogMissing.Count > 0;

    /// <summary>"Not in your library" heads the missing rows only when owned rows sit above them.</summary>
    public bool ShowCatalogMissingHeader => HasCatalogMissing && Tracks.Count > 0;

    /// <summary>Play, Shuffle and Add to Queue need local songs: an online album with none hides them.</summary>
    public bool ShowPlayActions => !IsOnlineAlbum && !IsCatalogLoading || Tracks.Count > 0;

    // Owner #34: an online album's page opens at once; skeleton rows stand in until its songs are read.
    private CatalogRelease? _catalogLoading;
    public bool IsCatalogLoading => _catalogLoading is not null;
    internal bool IsCatalogLoadingFor(string releaseId) => _catalogLoading?.Id == releaseId;
    /// <summary>Title-bar widths of the skeleton rows (varied, like real titles).</summary>
    public IReadOnlyList<double> CatalogSkeletonRows { get; } = [210, 160, 240, 180, 220, 150];

    internal void ShowCatalogLoading(CatalogRelease release)
    {
        _catalogLoading = release;
        OnPropertyChanged(nameof(IsCatalogLoading));
        OnPropertyChanged(nameof(ShowPlayActions));
        _ = LoadCatalogCoverAsync(release);
    }

    internal void ShowCatalogFailed(string message)
    {
        _catalogLoading = null;
        OnPropertyChanged(nameof(IsCatalogLoading));
        CatalogNotice = message;
    }

    /// <summary>A quiet line (partial catalog answer, a download message).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogNotice))] private string _catalogNotice = "";
    public bool HasCatalogNotice => CatalogNotice.Length > 0;

    /// <summary>
    /// A library album: find the same release in the catalog (in the background, metadata only)
    /// and show the songs the library lacks under the owned rows (owner, 2026-10-17: owning one
    /// song of an album hid the rest). Quietly does nothing when the catalog is off, offline or
    /// has no matching release.
    /// </summary>
    internal void EnrichFromCatalog()
    {
        if (_catalog is not null || Album.CatalogReleaseId is not null || Album.Tracks.Count == 0) return;
        if (FreeMusicPlugin.Current?.CatalogPages is not { Enabled: true } pages) return;
        var artist = Album.Artist;
        if (string.IsNullOrWhiteSpace(artist) || artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)) return;
        _enrichStop?.Cancel();
        var stop = _enrichStop = new CancellationTokenSource();
        var name = Album.Name;
        var titles = Album.Tracks.Select(t => t.Title).ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                var (identity, _) = await pages.IdentifyAsync(artist, pages.LocalAlbums(artist), titles, stop.Token);
                if (identity is null) return;
                var profile = await pages.Catalog.SavedArtistAsync(identity, stop.Token)
                              ?? await pages.Catalog.ArtistAsync(identity, false, stop.Token);
                var release = profile.Value.Releases.FirstOrDefault(r => CatalogEditions.Matches(r, name, artist));
                if (release is null) return;
                var album = await pages.Catalog.AlbumAsync(release, false, stop.Token);
                var matches = await pages.Actions.MatchAsync(album.Value.Songs, stop.Token, reuse: true);
                if (stop.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (stop.IsCancellationRequested || _catalog is not null) return;
                    _catalogEnriches = true;
                    ShowCatalog(new CatalogAlbumOpen(album.Value, matches));
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or TimeoutException
                                           or InvalidDataException or InvalidOperationException or SourcesWaitingException or ArgumentException) { }
        });
    }

    internal void ShowCatalog(CatalogAlbumOpen open)
    {
        _catalog = open;
        var wasLoading = _catalogLoading?.Id == open.Album.Release.Id;
        _catalogLoading = null;
        OnPropertyChanged(nameof(IsCatalogLoading));
        if (wasLoading)
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
        CatalogNotice = open.Album.Notice.Length > 0 ? "Some of this album's songs are not in the online catalog." : "";
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
        try { CatalogNotice = "Buffering…"; await _player.PlayOnlineAsync(online, song); CatalogNotice = ""; }
        catch (Exception ex) { CatalogNotice = ex.Message; }
    }
    [RelayCommand] private Task QueueCatalogSong(ArtistCatalogSong? row) => DownloadCatalogAsync(row is null ? [] : [row], now: false);

    /// <summary>Every song of the album the library lacks, into the download queue (in album order).</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadMissing))]
    private Task DownloadMissing() => DownloadCatalogAsync(CatalogMissing.Where(r => r.Song.CanStartDownload).ToList(), now: false);
    private bool CanDownloadMissing() => CatalogMissing.Any(r => r.Song.CanStartDownload);

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
        _catalogLoading = null; // a closed page is never filled later
        _enrichStop?.Cancel();
        if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
        _catalogOnline = null;
    }
}
