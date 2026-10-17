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
    private FinderOnlineSearch? _catalogOnline;

    /// <summary>The page shows an online album (opened from Search, For you or an artist page).</summary>
    public bool IsCatalogAlbum => _catalog is not null;

    /// <summary>The album's songs the library does not have yet, in album order.</summary>
    public ObservableCollection<ArtistCatalogSong> CatalogMissing { get; } = [];
    public bool HasCatalogMissing => CatalogMissing.Count > 0;

    /// <summary>"Not in your library" heads the missing rows only when owned rows sit above them.</summary>
    public bool ShowCatalogMissingHeader => HasCatalogMissing && Tracks.Count > 0;

    /// <summary>Play, Shuffle and Add to Queue need local songs: an online album with none hides them.</summary>
    public bool ShowPlayActions => !IsCatalogAlbum || Tracks.Count > 0;

    /// <summary>A quiet line (partial catalog answer, a download message).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogNotice))] private string _catalogNotice = "";
    public bool HasCatalogNotice => CatalogNotice.Length > 0;

    internal void ShowCatalog(CatalogAlbumOpen open)
    {
        _catalog = open;
        if (FreeMusicPlugin.Current?.OnlineSearch is { } online && !ReferenceEquals(online, _catalogOnline))
        {
            if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
            _catalogOnline = online;
            online.DownloadsChanged += CatalogDownloadsChanged;
        }
        // The catalog's own wording mentions a "Download all" this page does not have.
        CatalogNotice = open.Album.Notice.Length > 0 ? "Some of this album's songs are not in the online catalog." : "";
        RefreshCatalogOwnership();
        OnPropertyChanged(nameof(IsCatalogAlbum));
        _ = LoadCatalogCoverAsync(open.Album.Release);
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
            var existing = CatalogMissing.FirstOrDefault(r => r.CatalogId == song.Id);
            missing.Add(existing ?? new ArtistCatalogSong(song.Number > 0 ? song.Number : number,
                new SearchSongItem(_catalogOnline?.Remember(new CatalogSearchSong(song, open.Album.Release, open.Album.Release.Artwork))
                    ?? new Services.Search.OnlineSong(song.Id, song.Title, string.Join(", ", song.Artists.Select(a => a.Name)), open.Album.Release.Title, song.Seconds, song.Explicit)),
                song.Id));
        }
        if (!IsShownTrackList(owned))
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
                if (_catalog?.Album.Release == release && File.Exists(path)) { Album.ArtworkPath = path; HeaderArtPath = path; }
            }).GetTask(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or TimeoutException or InvalidDataException) { }
    }

    private void CatalogDownloadsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RefreshCatalogDownloads);

    private void RefreshCatalogDownloads()
    {
        if (_catalogOnline is not { } online) return;
        foreach (var row in CatalogMissing) if (row.Song.Online is { } song) row.Song.Apply(online.StatusOf(song));
    }

    [RelayCommand] private Task DownloadCatalogSong(ArtistCatalogSong? row) => DownloadCatalogAsync(row is null ? [] : [row], now: true);
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
        if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
        _catalogOnline = null;
    }
}
