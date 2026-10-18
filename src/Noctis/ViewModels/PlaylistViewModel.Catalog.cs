using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

/// <summary>"Not in your library" above an imported playlist's missing rows, when owned rows sit above them.</summary>
public sealed class PlaylistMissingHeader
{
    public static readonly PlaylistMissingHeader Instance = new();
    private PlaylistMissingHeader() { }
}

/// <summary>
/// Owner #36: an imported Spotify/Deezer playlist lists EVERY song, not only the ones the library has. Owned songs
/// are the playlist's normal rows; the rest follow in playlist order in the album page's catalog-row shape
/// (muted, numbered by their playlist position), with Play (progressive playback) and Download. Nothing downloads
/// on its own; a song that lands in the library moves into the owned rows at its position (the import's own
/// reconcile) and leaves these rows.
/// </summary>
public partial class PlaylistViewModel
{
    /// <summary>Header (when owned rows exist) and the missing songs, after <see cref="Tracks"/>.</summary>
    public ObservableCollection<object> CatalogRows { get; } = [];
    private TracksThenRows? _rows;
    /// <summary>The track list's items: every track (same indices as <see cref="Tracks"/>), then <see cref="CatalogRows"/>.</summary>
    public TracksThenRows Rows => _rows ??= new TracksThenRows(Tracks, CatalogRows);
    public bool HasCatalogMissing => CatalogRows.Count > 0;
    private int CatalogMissingCount => CatalogRows.Count(r => r is ArtistCatalogSong);

    private IOnlineMusicSearch? _catalogOnline;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogNotice))] private string _catalogNotice = "";
    public bool HasCatalogNotice => CatalogNotice.Length > 0;

    private void RefreshCatalogRows()
    {
        var missing = _streamingImports?.MissingFor(PlaylistId) ?? [];
        var online = OnlineMusicSearch.Current;
        if (!ReferenceEquals(online, _catalogOnline))
        {
            if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
            _catalogOnline = online;
            if (online is not null) online.DownloadsChanged += CatalogDownloadsChanged;
        }
        // A song listed twice is two rows (its two positions).
        var existing = CatalogRows.OfType<ArtistCatalogSong>().ToDictionary(r => (r.CatalogId, r.Rank));
        var wanted = new List<object>();
        foreach (var (position, song) in missing)
        {
            if (existing.TryGetValue((song.Id, position), out var row)) { wanted.Add(row); continue; }
            var item = (online as FinderOnlineSearch)?.Remember(new CatalogSearchSong(song, null))
                ?? new OnlineSong(song.Id, song.Title, string.Join(", ", song.Artists.Select(a => a.Name)), "", song.Seconds, song.Explicit);
            wanted.Add(new ArtistCatalogSong(position, new SearchSongItem(item), song.Id));
        }
        if (wanted.Count > 0 && Tracks.Count > 0) wanted.Insert(0, PlaylistMissingHeader.Instance);
        SearchViewModel.Sync(CatalogRows, wanted);
        RefreshCatalogDownloads();
        OnPropertyChanged(nameof(HasCatalogMissing));
    }

    /// <summary>The header only stands between owned and missing rows.</summary>
    private void UpdateCatalogHeader()
    {
        var shown = CatalogRows.Count > 0 && CatalogRows[0] is PlaylistMissingHeader;
        var wanted = Tracks.Count > 0 && CatalogRows.OfType<ArtistCatalogSong>().Any();
        if (shown && !wanted) CatalogRows.RemoveAt(0);
        else if (!shown && wanted) CatalogRows.Insert(0, PlaylistMissingHeader.Instance);
    }

    private void CatalogDownloadsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_streamingDisposed) RefreshCatalogDownloads();
    });

    private void RefreshCatalogDownloads()
    {
        foreach (var row in CatalogRows.OfType<ArtistCatalogSong>())
        {
            row.Song.ApplyPlayback(_player);
            if (_catalogOnline is { } online && row.Song.Online is { } song) row.Song.Apply(online.StatusOf(song));
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayCatalogSong(ArtistCatalogSong? row)
    {
        if (row?.Song.Online is not { } song || _catalogOnline is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        try { CatalogNotice = "Buffering…"; await _player.PlayOnlineAsync(online, song); CatalogNotice = ""; }
        catch (Exception ex) { CatalogNotice = ex.Message; }
    }

    [RelayCommand] private Task DownloadCatalogSong(ArtistCatalogSong? row) => DownloadCatalogAsync(row, now: true);
    [RelayCommand] private Task QueueCatalogSong(ArtistCatalogSong? row) => DownloadCatalogAsync(row, now: false);

    private async Task DownloadCatalogAsync(ArtistCatalogSong? row, bool now)
    {
        if (row?.Song.Online is not { } song || _catalogOnline is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        try { CatalogNotice = await online.DownloadAsync([song], now, CancellationToken.None) is var message && message == "Downloading." ? "" : message; }
        catch (Exception ex) { CatalogNotice = ex.Message; }
        RefreshCatalogDownloads();
    }

    private void DisposeCatalogRows()
    {
        if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
        _catalogOnline = null;
    }
}
