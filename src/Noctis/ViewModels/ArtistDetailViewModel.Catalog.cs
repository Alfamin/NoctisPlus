using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;
using Noctis.Models;
using Noctis.Helpers;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

public partial class ArtistDetailViewModel
{
    internal IReadOnlyList<string> IdentityAlbums => _allReleases.Select(a => a.Name).ToArray();
    internal IReadOnlyList<string> IdentityTitles => _allSongs.Select(t => t.Title).ToArray();
    private CancellationTokenSource? _catalogSongsStop;
    private FinderOnlineSearch? _catalogOnline;
    private List<CatalogSearchSong> _catalogTracks = [];
    private List<ArtistCatalogSong> _catalogRows = [];
    private string? _catalogArtistId;
    public ObservableCollection<ArtistCatalogSong> CatalogPopular { get; } = [];
    public ObservableCollection<ArtistCatalogSong> CatalogSongs { get; } = [];
    public bool HasCatalogSongs => _catalogRows.Count > 0;
    public int DisplaySongCount => HasCatalogSongs ? _catalogRows.Count : SongCount;
    public bool ShowNoPopular => HasCatalogSongs ? CatalogPopular.Count == 0 : !HasPopular;
    public bool ShowNoAllSongs => HasCatalogSongs ? CatalogSongs.Count == 0 : !HasAllSongs;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogSongStatus)), NotifyPropertyChangedFor(nameof(IsCatalogSongsLoading))]
    private string _catalogSongStatus = "";
    public bool HasCatalogSongStatus => CatalogSongStatus.Length > 0;
    public bool IsCatalogSongsLoading => CatalogSongStatus.StartsWith("Loading", StringComparison.Ordinal);

    internal void SetCatalogArtist(ArtistCatalog catalog, CatalogArtist artist, bool refresh = false)
    {
        if (_cts.IsCancellationRequested || _catalogArtistId == artist.Id && !refresh) return;
        if (FreeMusicPlugin.Current is not { OnlineSearch: { } online, Discovery: { } actions }) return;
        _catalogArtistId = artist.Id;
        _catalogSongsStop?.Cancel(); _catalogSongsStop?.Dispose();
        var stop = _catalogSongsStop = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
        _catalogOnline = online;
        online.DownloadsChanged += CatalogDownloadsChanged;
        _ = LoadCatalogSongs(catalog, artist, online, actions, refresh, stop.Token);
    }

    private async Task LoadCatalogSongs(ArtistCatalog catalog, CatalogArtist artist, FinderOnlineSearch online, DiscoveryActions actions, bool refresh, CancellationToken ct)
    {
        CatalogSongStatus = "Loading songs…";
        try
        {
            var result = await catalog.ArtistSongsAsync(artist, refresh, ct);
            var matches = await actions.MatchAsync(result.Value.Select(t => t.Song), ct);
            ct.ThrowIfCancellationRequested();
            _catalogTracks = result.Value;
            var locals = _library.Tracks.Where(t => File.Exists(t.FilePath)).ToDictionary(t => t.Id);
            var rows = result.Value.Select((t, i) =>
            {
                var registered = online.Remember(t);
                var local = matches.TryGetValue(t.Song.Id, out var m) && m.Local is { } owned ? locals.GetValueOrDefault(owned.Id) : null;
                return new ArtistCatalogSong(i + 1, local is not null ? new SearchSongItem(local) : new SearchSongItem(registered), t.Song.Id);
            }).ToList();
            var represented = rows.Select(r => r.Song.Local?.Id).OfType<Guid>().ToHashSet();
            var onlineCount = rows.Count;
            rows.AddRange(_allSongs.Where(t => File.Exists(t.FilePath) && !represented.Contains(t.Id))
                .Select((t, i) => new ArtistCatalogSong(onlineCount + i + 1, new SearchSongItem(t))));
            SetCatalogRows(rows);
            CatalogSongStatus = result.Warning;
            if (_catalogRows.Count == 0) CatalogSongStatus = "No online songs returned. Your library songs are shown.";
            foreach (var row in _catalogRows.Where(r => r.Song.IsOnline)) _ = LoadCatalogSongArt(row.Song, online, ct);
            RefreshCatalogDownloads();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) CatalogSongStatus = "Online songs unavailable: " + ArtistCatalog.Explain(ex) + " Your library songs are kept."; }
    }

    internal void SetCatalogRows(List<ArtistCatalogSong> rows)
    {
        _catalogRows = rows;
        OnPropertyChanged(nameof(HasCatalogSongs));
        OnPropertyChanged(nameof(DisplaySongCount));
        OnPropertyChanged(nameof(ShowNoPopular));
        OnPropertyChanged(nameof(ShowNoAllSongs));
        NotifyStreamToggle(); // owner #100
        FilterCatalogSongs();
    }
    private void FilterCatalogSongs()
    {
        var rows = _catalogRows.Where(r => SearchText.Matches(r.Song.Title, _query)).ToList();
        SearchViewModel.Sync(CatalogSongs, rows);
        SearchViewModel.Sync(CatalogPopular, rows.Take(MaxPopular).ToList());
        OnPropertyChanged(nameof(ShowNoPopular));
        OnPropertyChanged(nameof(ShowNoAllSongs));
        NotifyStreamToggle(); // owner #100
    }

    private async Task LoadCatalogSongArt(SearchSongItem row, FinderOnlineSearch online, CancellationToken ct)
    {
        try
        {
            var path = online.CachedArtwork(row.ArtworkUrl) ?? await online.ArtworkAsync(row.ArtworkUrl, ct);
            if (!ct.IsCancellationRequested) row.ArtworkPath = path;
        }
        catch (OperationCanceledException) { }
    }
    private void CatalogDownloadsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_cts.IsCancellationRequested) return;
        // Progress can fire often. Ownership is rechecked on the library's completed
        // update, not by walking every local track on each transferred packet.
        RefreshCatalogDownloads();
    });
    private void RefreshCatalogDownloads()
    {
        if (_catalogOnline is not { } online) return;
        foreach (var row in _catalogRows) if (row.Song.Online is { } song) row.Song.Apply(online.StatusOf(song));
    }
    private void RefreshCatalogOwnership()
    {
        if (_catalogTracks.Count == 0 || _catalogRows.Count == 0 || _catalogOnline is not { } online) return;
        var locals = _library.Tracks.Where(t => File.Exists(t.FilePath)).ToArray();
        var changed = false;
        var rows = _catalogRows.Select(row =>
        {
            var metadata = _catalogTracks.FirstOrDefault(t => row.CatalogId == t.Song.Id);
            if (metadata is null) return row;
            var local = locals.FirstOrDefault(t => RecordingMatch.Fits(metadata.Song.Request, t.Artist, t.Title, t.Duration));
            if (local?.Id == row.Song.Local?.Id) return row;
            changed = true;
            return new ArtistCatalogSong(row.Rank, local is not null ? new SearchSongItem(local) : new SearchSongItem(online.Remember(metadata)), metadata.Song.Id);
        }).ToList();
        if (changed) SetCatalogRows(rows);
    }
    // ── Owner #100: the header toggle: this artist's songs you don't have play too ──
    private string StreamPage => "artist:" + ArtistName.ToLowerInvariant();
    public bool StreamMissing
    {
        get => _player.StreamsPage(StreamPage);
        set { _player.SetStreamPage(StreamPage, value); NotifyStreamToggle(); }
    }
    public bool ShowStreamToggle => _catalogOnline is not null && _catalogRows.Any(r => r.Song.Local is null && r.Song.Online is not null);
    public string StreamToggleTip => StreamToggle.Tip(StreamMissing);
    [RelayCommand] private void ToggleStreamMissing() => StreamMissing = !StreamMissing;
    internal void NotifyStreamToggle()
    {
        OnPropertyChanged(nameof(StreamMissing)); OnPropertyChanged(nameof(StreamToggleTip)); OnPropertyChanged(nameof(ShowStreamToggle));
    }
    private bool Streams => StreamMissing && ShowStreamToggle;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ArtistCatalogSong, Track> _standIns = new();
    private Track? CatalogTrack(ArtistCatalogSong row)
        => row.Song.Local is { } local ? (File.Exists(local.FilePath) ? local : null)
            : row.Song.Online is { } song && _catalogOnline is { } online
                ? _standIns.GetValue(row, _ => PlayerViewModel.StandIn(online, song, keep: false, StreamPage)) : null;
    /// <summary>The artist's songs in the catalog's order (the ones you don't have as stand-ins), then library songs the
    /// catalog does not list. Null when the toggle is off or nothing is missing.</summary>
    private List<Track>? StreamingList(IEnumerable<Track> library)
    {
        if (!Streams) return null;
        var list = _catalogRows.Select(CatalogTrack).OfType<Track>().DistinctBy(t => t.Id).ToList();
        var ids = list.Select(t => t.Id).ToHashSet();
        list.AddRange(library.Where(t => ids.Add(t.Id)));
        return list;
    }

    [RelayCommand] private async Task PlayCatalogSong(ArtistCatalogSong? row)
    {
        if (row is not null && Streams && CatalogTrack(row) is { } picked)
        {
            // Owner #100: the artist's songs from this one on, the ones you don't have fetched as they come up.
            var list = StreamingList([])!;
            var at = list.IndexOf(picked);
            if (at >= 0) { _player.PlayFromRow(list, at); return; }
        }
        if (row?.Song.Local is { } track && File.Exists(track.FilePath))
        {
            var tracks = _catalogRows.Select(r => r.Song.Local).OfType<Track>().Where(t => File.Exists(t.FilePath)).DistinctBy(t => t.Id).ToList();
            _player.PlayFromRow(tracks, tracks.IndexOf(track));
        }
        // An online song plays at once while it downloads, exactly like Search's Play (owner, 2026-10-18: it only
        // downloaded, then needed a second click to play).
        else if (row?.Song.Online is { } song && _catalogOnline is { } online)
        {
            if (!online.CanDownload) { online.RequestSignIn(); return; }
            try { await _player.PlayOnlineAsync(online, song); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { CatalogSongStatus = ex.Message; }
            RefreshCatalogDownloads();
        }
    }
    [RelayCommand] private Task DownloadCatalogSongNow(ArtistCatalogSong? row) => DownloadCatalogSong(row, true);
    [RelayCommand] private Task QueueCatalogDownload(ArtistCatalogSong? row) => DownloadCatalogSong(row, false);
    private async Task DownloadCatalogSong(ArtistCatalogSong? row, bool now)
    {
        if (row?.Song.Online is not { } song || _catalogOnline is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        try { CatalogSongStatus = await online.DownloadAsync([song], now, _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CatalogSongStatus = ex.Message; }
        RefreshCatalogDownloads();
    }
    private void DisposeCatalogSongs()
    {
        _catalogSongsStop?.Cancel(); _catalogSongsStop?.Dispose();
        if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
    }
    internal void ClearCatalogSongs()
    {
        DisposeCatalogSongs();
        _catalogSongsStop = null; _catalogOnline = null; _catalogArtistId = null;
        _catalogTracks.Clear(); SetCatalogRows([]); CatalogSongStatus = "";
    }
}

public sealed record ArtistCatalogSong(int Rank, SearchSongItem Song, string CatalogId = "");
