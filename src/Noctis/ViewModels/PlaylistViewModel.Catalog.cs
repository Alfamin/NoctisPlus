using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

public sealed record PlaylistUnavailableRow(int Position);

/// <summary>
/// Owner #36: an imported Spotify/Deezer playlist lists EVERY song, not only the ones the library has. Owned songs
/// keep their normal template; missing songs occupy their source positions with native Play/download controls.
/// A downloaded song replaces its missing row in place. Browsing never downloads audio.
/// </summary>
public partial class PlaylistViewModel
{
    /// <summary>Missing songs and unavailable entries, identified by source position.</summary>
    public BulkObservableCollection<object> CatalogRows { get; } = [];
    private TracksThenRows? _rows;
    public TracksThenRows Rows => _rows ??= new TracksThenRows(Tracks, CatalogRows, BuildOrderedPlaylistRows);
    public bool HasCatalogMissing => CatalogRows.Count > 0;
    // Owner #48: header Play/Shuffle/queue act on owned songs only. With none owned they would do nothing, so they
    // hide, as on an online album; each missing row keeps its own Play.
    public bool ShowPlayActions => (Tracks.Count > 0 || !HasCatalogMissing || StreamMissing && !IsPreview) && !IsPreviewLoading;
    partial void OnTrackCountChanged(int value) => OnPropertyChanged(nameof(ShowPlayActions));
    private int CatalogMissingCount => CatalogRows.Count;
    private IReadOnlyList<ImportedPlaylistEntry>? _importedEntries;
    // Source positions include unowned songs. Reordering just the local subset would detach the import
    // and discard those positions; ordinary and fully downloaded playlists retain native drag-reorder.
    private bool HasUnownedImportEntries => _importedEntries?.Any(e => e.LocalId is null) == true;

    private IOnlineMusicSearch? _catalogOnline;
    private readonly CancellationTokenSource _catalogArtStop = new();
    private readonly HashSet<ArtistCatalogSong> _catalogArtLoading = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogNotice))] private string _catalogNotice = "";
    public bool HasCatalogNotice => CatalogNotice.Length > 0;

    private void RefreshCatalogRows()
    {
        ShowImportedEntries(IsPreview ? _previewEntries : _streamingImports?.EntriesFor(PlaylistId));
    }

    internal void ShowImportedEntries(IReadOnlyList<ImportedPlaylistEntry>? entries)
    {
        _importedEntries = entries;
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
        foreach (var entry in entries ?? [])
        {
            if (entry.LocalId is not null) continue;
            var position = entry.Position;
            if (entry.Song is not { } song) { wanted.Add(new PlaylistUnavailableRow(position)); continue; }
            if (existing.TryGetValue((song.Id, position), out var row) && row.Song.ArtworkUrl == entry.Artwork &&
                row.Song.AlbumTitle == entry.Album && row.Song.Title == song.Title &&
                row.Song.ArtistDisplay == string.Join(", ", song.Artists.Select(a => a.Name))) { wanted.Add(row); continue; }
            var item = (online as FinderOnlineSearch)?.Remember(new CatalogSearchSong(song, null, entry.Artwork))
                ?? new OnlineSong(song.Id, song.Title, string.Join(", ", song.Artists.Select(a => a.Name)), "", song.Seconds, song.Explicit, entry.Artwork);
            item = item with { Album = entry.Album };
            wanted.Add(new ArtistCatalogSong(position, new SearchSongItem(item), song.Id));
        }
        if (!CatalogRows.SequenceEqual(wanted)) CatalogRows.ReplaceAll(wanted);
        else _rows?.Refresh();
        RefreshCatalogDownloads();
        OnPropertyChanged(nameof(HasCatalogMissing));
        OnPropertyChanged(nameof(ShowStreamToggle));
        OnPropertyChanged(nameof(ShowPlayActions));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(TrackCountText));
        UpdateEmptyStateFlags(entries?.Count ?? Tracks.Count);
        RefreshPlaylistDuration();
    }

    private IReadOnlyList<object>? BuildOrderedPlaylistRows()
    {
        if (_importedEntries is null) return null;
        var local = Tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => new Queue<Track>(g));
        var missing = CatalogRows.ToDictionary(r => r is ArtistCatalogSong song ? song.Rank : ((PlaylistUnavailableRow)r).Position);
        var result = new List<object>();
        var filter = string.IsNullOrWhiteSpace(_currentFilter) ? null : SearchQuery.Parse(_currentFilter);
        foreach (var entry in _importedEntries)
        {
            if (entry.LocalId is { } id)
            {
                // Tracks already has the local filter applied. Never turn a filtered-out local row into a download.
                if (local.TryGetValue(id, out var copies) && copies.TryDequeue(out var track)) result.Add(track);
            }
            else if (missing.TryGetValue(entry.Position, out var row))
            {
                if (filter is null || row is ArtistCatalogSong s && filter.Matches(CatalogTrack(s))) result.Add(row);
            }
        }
        // Preserve native sorting across BOTH kinds of song. Manual is exact source order, duplicates included.
        if (SortMode != PlaylistSortMode.Manual)
        {
            var pairs = result.Select(row => (Row: row, Track: row as Track ?? (row is ArtistCatalogSong s ? CatalogTrack(s) : new Track()))).ToArray();
            var byTrack = pairs.GroupBy(p => p.Track, ReferenceEqualityComparer.Instance)
                .ToDictionary(g => g.Key, g => g.First().Row, ReferenceEqualityComparer.Instance);
            return SortTracks(pairs.Select(p => p.Track).ToList(), SortMode, TrackAddedAt).Select(t => byTrack[t]).ToArray();
        }
        return result;
    }

    private static Track CatalogTrack(ArtistCatalogSong row) => new()
    {
        Title = row.Song.Title, Artist = row.Song.ArtistDisplay, Album = row.Song.Online?.Album ?? "",
        Duration = TimeSpan.FromSeconds(row.Song.Online?.Seconds is > 0 and <= 86400 ? row.Song.Online.Seconds.Value : 0),
    };

    private void RefreshPlaylistDuration() => TotalDuration = DurationText.Total(TimeSpan.FromTicks(Rows.Sum(row => row switch
    {
        Track track => track.Duration.Ticks,
        ArtistCatalogSong song => CatalogTrack(song).Duration.Ticks,
        _ => 0L,
    })));

    // Called when a native row is realized: a large import must not fetch thousands of covers on opening.
    internal async void EnsureCatalogArtwork(ArtistCatalogSong row)
    {
        if (_streamingDisposed || _catalogOnline is not { } online || row.Song.ArtworkUrl.Length == 0 ||
            row.Song.ArtworkPath is { Length: > 0 } || !_catalogArtLoading.Add(row)) return;
        try
        {
            var art = online.CachedArtwork(row.Song.ArtworkUrl) ?? await online.ArtworkAsync(row.Song.ArtworkUrl, _catalogArtStop.Token);
            if (!_streamingDisposed && CatalogRows.Contains(row)) row.Song.ArtworkPath = art;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Services.DebugLog.Write("Playlist", $"Artwork unavailable: {ex.GetType().Name}"); }
        finally { _catalogArtLoading.Remove(row); }
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

    // ── Owner #95: the playlist plays its songs you don't have too ──

    /// <summary>Owner #100: this playlist's header toggle: its songs you don't have play too (fetched as the queue reaches
    /// them). Off: only yours. Per playlist, remembered.</summary>
    public bool StreamMissing
    {
        get => _player.StreamsPage(StreamPage);
        set { _player.SetStreamPage(StreamPage, value); OnPropertyChanged(); OnPropertyChanged(nameof(StreamToggleTip)); OnPropertyChanged(nameof(ShowPlayActions)); }
    }
    public bool ShowStreamToggle => HasCatalogMissing && !IsPreview && _catalogOnline is not null;
    public string StreamToggleTip => StreamToggle.Tip(StreamMissing);
    [RelayCommand] private void ToggleStreamMissing() => StreamMissing = !StreamMissing;
    private string StreamPage => "playlist:" + PlaylistId;

    private bool Streams => StreamMissing && HasCatalogMissing && !IsPreview && _catalogOnline is not null;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ArtistCatalogSong, Track> _standIns = new();
    private Track StandInFor(ArtistCatalogSong row, OnlineSong song)
        => _standIns.GetValue(row, _ => PlayerViewModel.StandIn(_catalogOnline!, song, keep: false, StreamPage));

    /// <summary>What Play, Shuffle and the queue actions take: the playlist's rows in their order (missing songs as
    /// stand-ins) when streaming is on, else only the songs you have, as before.</summary>
    internal List<Track> PlaybackList()
    {
        if (!Streams) return Tracks.ToList();
        var list = new List<Track>(Rows.Count);
        foreach (var row in Rows)
            if (row is Track track) list.Add(track);
            else if (row is ArtistCatalogSong { Song.Online: { } song } missing) list.Add(StandInFor(missing, song));
        return list;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayCatalogSong(ArtistCatalogSong? row)
    {
        if (row?.Song.Online is not { } song || _catalogOnline is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        if (Streams)
        {
            // The whole playlist from this song on (or shuffled around it), like any other row.
            var list = PlaybackList();
            var at = list.IndexOf(StandInFor(row, song));
            if (at >= 0) { CatalogNotice = ""; _player.PlayFromRow(list, at); return; }
        }
        // Owner 10-20 (was #88): playing only streams, a playlist's songs too; its Download button keeps it in Songs.
        try { CatalogNotice = ""; await _player.PlayOnlineAsync(online, song); CatalogNotice = ""; }
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
        _catalogArtStop.Cancel();
        _catalogArtStop.Dispose();
        if (_catalogOnline is not null) _catalogOnline.DownloadsChanged -= CatalogDownloadsChanged;
        _catalogOnline = null;
    }
}
