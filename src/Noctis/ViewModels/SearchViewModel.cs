using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

/// <summary>
/// The Search page (Noctis Plus): one search over the library and, when Free Music Finder is on,
/// the online catalog. Library results answer on every keystroke from <see cref="LibrarySearchIndex"/>;
/// online results follow ~200 ms after typing stops and stream in as each catalog answers. An online
/// song the library already has is shown as the library's own row, never twice.
/// </summary>
public sealed partial class SearchViewModel : ViewModelBase, ISearchable, IDisposable
{
    internal static readonly TimeSpan OnlineDelay = TimeSpan.FromMilliseconds(200);
    private const double LibraryBonus = 6;

    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly Action<string> _openArtist;
    private readonly Action<Album> _openAlbum;
    private readonly LibrarySearchIndex _index;
    private readonly Dictionary<string, SearchSongItem> _songItems = new();
    private readonly Dictionary<string, SearchArtistItem> _artistItems = new();
    private readonly Dictionary<string, SearchAlbumItem> _albumItems = new();
    private CancellationTokenSource? _onlineCts;
    private OnlineSearchResult? _online;
    private LibrarySearchResult _local = LibrarySearchResult.Empty;
    private string _query = "";
    private IOnlineMusicSearch? _source;

    public SearchViewModel(ILibraryService library, PlayerViewModel player, Action<string> openArtist, Action<Album> openAlbum)
    {
        _library = library;
        _player = player;
        _openArtist = openArtist;
        _openAlbum = openAlbum;
        _index = new LibrarySearchIndex(library);
        _index.Rebuilt += OnIndexRebuilt;
        OnlineMusicSearch.CurrentChanged += OnSourceChanged;
        AttachSource(OnlineMusicSearch.Current);
    }

    public ObservableCollection<SearchSongItem> Songs { get; } = new();
    public ObservableCollection<SearchArtistItem> Artists { get; } = new();
    public ObservableCollection<SearchAlbumItem> Albums { get; } = new();

    /// <summary>All, Songs, Artists or Albums.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAllFilter), nameof(IsSongsFilter), nameof(IsArtistsFilter), nameof(IsAlbumsFilter))]
    private string _filter = "All";

    public bool IsAllFilter => Filter == "All";
    public bool IsSongsFilter => Filter == "Songs";
    public bool IsArtistsFilter => Filter == "Artists";
    public bool IsAlbumsFilter => Filter == "Albums";

    /// <summary>The single best match (an artist, song or album), shown large on the All view.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTopResult), nameof(TopSong), nameof(TopArtist), nameof(TopAlbum))]
    private object? _topResult;

    public bool HasTopResult => TopResult is not null;
    public SearchSongItem? TopSong => TopResult as SearchSongItem;
    public SearchArtistItem? TopArtist => TopResult as SearchArtistItem;
    public SearchAlbumItem? TopAlbum => TopResult as SearchAlbumItem;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasQuery), nameof(ShowEmptyPrompt))]
    private string _queryText = "";

    public bool HasQuery => QueryText.Trim().Length > 0;
    public bool ShowEmptyPrompt => !HasQuery;

    /// <summary>Online results are on their way and none has arrived yet: skeletons stand in.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSongSkeletons), nameof(ShowCardSkeletons), nameof(ShowNoResults))]
    private bool _isOnlinePending;

    /// <summary>Online results are still arriving (the first may already be on screen).</summary>
    [ObservableProperty] private bool _isOnlineLoading;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoResults), nameof(HasSongs), nameof(HasArtists), nameof(HasAlbums), nameof(ShowSongSkeletons), nameof(ShowCardSkeletons))]
    private int _resultVersion;

    /// <summary>Placeholder rows: three beside the top result, five on the Songs filter.</summary>
    public IReadOnlyList<int> SkeletonsShort { get; } = [1, 2, 3];
    public IReadOnlyList<int> SkeletonsLong { get; } = [1, 2, 3, 4, 5];

    public bool HasSongs => Songs.Count > 0;
    public bool HasArtists => Artists.Count > 0;
    public bool HasAlbums => Albums.Count > 0;
    public bool ShowSongSkeletons => IsOnlinePending && Songs.Count < SongCap;
    public bool ShowCardSkeletons => IsOnlinePending && Albums.Count == 0;
    public bool ShowNoResults => HasQuery && !IsOnlinePending && !IsOnlineLoading && Songs.Count == 0 && Artists.Count == 0 && Albums.Count == 0;

    /// <summary>True while an online catalog is available (Free Music Finder switched on).</summary>
    public bool HasOnline => _source is not null;

    private int SongCap => Filter == "All" ? 5 : 50;
    private int ArtistCap => Filter == "All" ? _artistsPerRow : 40;
    private int AlbumCap => Filter == "All" ? _albumsPerRow : 40;
    private int _artistsPerRow = 7, _albumsPerRow = 7;

    /// <summary>The All view shows one full row of artists and of albums; the view says how many fit.</summary>
    public void SetRowCapacity(int artists, int albums)
    {
        if (artists == _artistsPerRow && albums == _albumsPerRow) return;
        _artistsPerRow = artists;
        _albumsPerRow = albums;
        if (Filter == "All" && _query.Length > 0) Publish();
    }

    /// <summary>Always "nothing to clear": a section switch must not wipe Search's last query,
    /// which the page keeps for when the user comes back.</summary>
    public bool IsSearchCleared => true;

    /// <summary>The top bar's debounced text (ISearchable). The page also follows every keystroke
    /// through <see cref="SetQuery"/>; repeating the same text is a no-op.</summary>
    public void ApplyFilter(string query) => SetQuery(query);

    /// <summary>The search box changed: library results now, online results after a short pause.</summary>
    public void SetQuery(string? text)
    {
        text ??= "";
        if (text == QueryText) return;
        QueryText = text;
        Run(text.Trim(), force: false);
    }

    private void Run(string query, bool force)
    {
        if (!force && query == _query) return;
        _query = query;
        _onlineCts?.Cancel();
        _onlineCts = null;
        // A re-run for a library change keeps what is on screen (no skeleton flash); the online
        // half answers again from its caches and refreshes which songs are now owned.
        if (!force) { _online = null; Status = ""; }
        if (query.Length == 0)
        {
            _local = LibrarySearchResult.Empty;
            IsOnlinePending = IsOnlineLoading = false;
            Publish();
            return;
        }
        var localClock = System.Diagnostics.Stopwatch.StartNew();
        _local = _index.Search(query);
        if (!force)
            Trace($"\"{query}\": {_local.Songs.Count} songs, {_local.Artists.Count} artists, {_local.Albums.Count} albums in {localClock.Elapsed.TotalMilliseconds:0.0} ms");
        IsOnlinePending = !force && _source is not null;
        IsOnlineLoading = _source is not null;
        Publish();
        if (_source is { } source)
        {
            var cts = _onlineCts = new CancellationTokenSource();
            _ = RunOnlineAsync(source, query, cts, force ? TimeSpan.Zero : OnlineDelay);
        }
    }

    /// <summary>Search timings: the session log, and in a Debug build also the file named by
    /// NOCTISPLUS_DEBUG_TIMING (developer checks of search speed).</summary>
    private static void Trace(string line)
    {
        DebugLog.Write("Search", line);
#if DEBUG
        if (Environment.GetEnvironmentVariable("NOCTISPLUS_DEBUG_TIMING") is { Length: > 0 } path)
            try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch (IOException) { }
#endif
    }

    /// <summary>The page was opened: warm the online connection so the first query is quick.</summary>
    public void OnShown() => _source?.WarmUp();

    private async Task RunOnlineAsync(IOnlineMusicSearch source, string query, CancellationTokenSource cts, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = true;
            await foreach (var result in source.SearchAsync(query, cts.Token).WithCancellation(cts.Token))
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(cts, _onlineCts)) return;
                _online = result;
                IsOnlinePending = false;
                if (result.Complete) IsOnlineLoading = false;
                Status = result.Warning;
                Publish();
                if (first || result.Complete)
                    Trace($"{(result.Complete ? "complete" : "first")} after {clock.ElapsedMilliseconds} ms: {result.Songs.Count} songs, {result.Artists.Count} artists, {result.Albums.Count} albums");
                first = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(cts, _onlineCts)) Status = "Online results are unavailable right now: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _onlineCts)) IsOnlinePending = IsOnlineLoading = false;
        }
    }

    [RelayCommand]
    private void SelectFilter(string? filter)
    {
        if (filter is not ("All" or "Songs" or "Artists" or "Albums") || filter == Filter) return;
        Filter = filter;
        Publish();
    }

    // ── Merging the two halves ──

    private void Publish()
    {
        var songs = MergeSongs();
        var artists = MergeArtists();
        var albums = MergeAlbums();
        TopResult = PickTop(songs, artists, albums);
        Sync(Songs, songs.Take(SongCap).ToList());
        Sync(Artists, artists.Take(ArtistCap).ToList());
        Sync(Albums, albums.Take(AlbumCap).ToList());
        RefreshDownloadStates();
        LoadArtwork();
        ResultVersion++;
    }

    private List<SearchSongItem> MergeSongs()
    {
        var rows = new List<(SearchSongItem Item, double Score)>();
        var seen = new HashSet<Guid>();
        foreach (var hit in _local.Songs)
            if (seen.Add(hit.Item.Id)) rows.Add((Song("l:" + hit.Item.Id, () => new SearchSongItem(hit.Item)), hit.Score + LibraryBonus));
        foreach (var song in _online?.Songs ?? [])
        {
            if (song.LocalTrackId is { } id)
            {
                // The library has this recording: its own row stands for it.
                if (!seen.Add(id)) continue;
                if (_library.GetTrackById(id) is { } track)
                {
                    rows.Add((Song("l:" + id, () => new SearchSongItem(track)), song.Score + LibraryBonus));
                    continue;
                }
            }
            rows.Add((Song("o:" + song.Id, () => new SearchSongItem(song)), song.Score));
        }
        return rows.OrderByDescending(r => r.Score).Select(r => r.Item).ToList();
    }

    private List<SearchArtistItem> MergeArtists()
    {
        var rows = new List<(SearchArtistItem Item, double Score)>();
        var byName = new Dictionary<string, int>();
        foreach (var hit in _local.Artists)
        {
            var key = NameKey(hit.Item.Name);
            if (byName.ContainsKey(key)) continue;
            byName[key] = rows.Count;
            rows.Add((Artist("l:" + key, () => new SearchArtistItem(hit.Item)), hit.Score + LibraryBonus));
        }
        foreach (var artist in _online?.Artists ?? [])
        {
            var key = NameKey(artist.Name);
            if (byName.TryGetValue(key, out var at))
            {
                // Same name as a library artist: one tile, the library's, with the online picture if it has none.
                rows[at].Item.AttachOnline(artist);
                rows[at] = (rows[at].Item, Math.Max(rows[at].Score, artist.Score + LibraryBonus));
                continue;
            }
            byName[key] = rows.Count;
            rows.Add((Artist("o:" + artist.Id, () => new SearchArtistItem(artist)), artist.Score));
        }
        return rows.OrderByDescending(r => r.Score).Select(r => r.Item).ToList();
    }

    private List<SearchAlbumItem> MergeAlbums()
    {
        var rows = new List<(SearchAlbumItem Item, double Score)>();
        var owned = new Dictionary<string, int>();
        foreach (var hit in _local.Albums)
        {
            var key = AlbumKey(hit.Item.Name, hit.Item.Artist);
            if (owned.ContainsKey(key)) continue;
            owned[key] = rows.Count;
            rows.Add((Album("l:" + hit.Item.Id, () => new SearchAlbumItem(hit.Item)), hit.Score + LibraryBonus));
        }
        foreach (var album in _online?.Albums ?? [])
        {
            var key = AlbumKey(album.Title, album.ArtistName);
            if (owned.TryGetValue(key, out var at))
            {
                rows[at] = (rows[at].Item, Math.Max(rows[at].Score, album.Score + LibraryBonus));
                continue;
            }
            owned[key] = rows.Count;
            rows.Add((Album("o:" + album.Id, () => new SearchAlbumItem(album)), album.Score));
        }
        return rows.OrderByDescending(r => r.Score).Select(r => r.Item).ToList();
    }

    private object? PickTop(List<SearchSongItem> songs, List<SearchArtistItem> artists, List<SearchAlbumItem> albums)
    {
        if (_query.Length == 0) return null;
        var key = NameKey(_query);
        // Typing an artist's name: the artist leads (library tile first when there is one).
        if (artists.FirstOrDefault(a => NameKey(a.Name) == key) is { } artist) return artist;
        if (albums.FirstOrDefault(a => NameKey(a.Title) == key) is { } album && songs.FirstOrDefault(s => NameKey(s.Title) == key) is null) return album;
        return songs.FirstOrDefault() ?? (object?)artists.FirstOrDefault() ?? albums.FirstOrDefault();
    }

    internal static string NameKey(string text) => string.Concat(LibrarySearchIndex.Words(text));

    private static readonly HashSet<string> EditionWords = new(StringComparer.Ordinal)
        { "deluxe", "edition", "version", "remastered", "remaster", "expanded", "anniversary", "bonus", "tracks", "explicit", "clean" };

    /// <summary>Title + artist, folded, edition words dropped: "Views (Deluxe)" by Drake = "Views".</summary>
    internal static string AlbumKey(string title, string artist)
        => string.Concat(LibrarySearchIndex.Words(title).Where(w => !EditionWords.Contains(w))) + "\n" + NameKey(artist);

    private SearchSongItem Song(string key, Func<SearchSongItem> make)
    {
        if (!_songItems.TryGetValue(key, out var item)) _songItems[key] = item = make();
        return item;
    }

    private SearchArtistItem Artist(string key, Func<SearchArtistItem> make)
    {
        if (!_artistItems.TryGetValue(key, out var item)) _artistItems[key] = item = make();
        return item;
    }

    private SearchAlbumItem Album(string key, Func<SearchAlbumItem> make)
    {
        if (!_albumItems.TryGetValue(key, out var item)) _albumItems[key] = item = make();
        return item;
    }

    /// <summary>Brings a shown list in line with the wanted one in place, so an unchanged row
    /// keeps its container (and its loaded cover) instead of being rebuilt.</summary>
    internal static void Sync<T>(ObservableCollection<T> list, List<T> wanted) where T : class
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (!wanted.Contains(list[i])) list.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = list.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at < 0) list.Insert(i, wanted[i]);
            else list.Move(at, i);
        }
    }

    // ── Covers ──

    private CancellationTokenSource? _artCts;

    /// <summary>Online covers for what is on screen, in screen order: from the disk cache at once,
    /// else fetched (the catalog runs six at a time).</summary>
    private void LoadArtwork()
    {
        if (_source is not { } source) return;
        _artCts?.Cancel();
        var cts = _artCts = new CancellationTokenSource();
        var wanted = new List<IOnlineArtwork>();
        if (TopResult is IOnlineArtwork top) wanted.Add(top);
        wanted.AddRange(Songs.OfType<IOnlineArtwork>());
        wanted.AddRange(Artists);
        wanted.AddRange(Albums);
        foreach (var item in wanted)
        {
            if (item.ArtworkPath is { Length: > 0 } || item.ArtworkUrl.Length == 0) continue;
            if (source.CachedArtwork(item.ArtworkUrl) is { } cached) { item.ArtworkPath = cached; continue; }
            _ = FetchAsync(item);
        }

        async Task FetchAsync(IOnlineArtwork item)
        {
            var path = await source.ArtworkAsync(item.ArtworkUrl, cts.Token);
            if (path is not null && !cts.IsCancellationRequested) Dispatcher.UIThread.Post(() => item.ArtworkPath ??= path);
        }
    }

    // ── Actions ──

    [RelayCommand]
    private void PlaySong(SearchSongItem? item)
    {
        if (item?.Local is not { } track) return;
        if (track.IsNowPlaying) { _player.PlayPauseCommand.Execute(null); return; }
        var tracks = Songs.Where(s => s.Local is not null).Select(s => s.Local!).ToList();
        if (!tracks.Contains(track)) tracks.Insert(0, track);
        _player.PlayFromRow(tracks, tracks.IndexOf(track));
    }

    [RelayCommand]
    private async Task DownloadSong(SearchSongItem? item)
    {
        if (item?.Online is not { } song || _source is not { } source) return;
        if (!source.CanDownload) { source.RequestSignIn(); return; }
        item.DownloadState = OnlineDownloadState.Queued;
        try { Status = await source.DownloadAsync([song], now: true, CancellationToken.None) is var message && message == "Downloading." ? "" : message; }
        catch (Exception ex) { Status = ex.Message; }
        RefreshDownloadStates();
    }

    [RelayCommand]
    private async Task QueueSongDownload(SearchSongItem? item)
    {
        if (item?.Online is not { } song || _source is not { } source) return;
        if (!source.CanDownload) { source.RequestSignIn(); return; }
        try { Status = await source.DownloadAsync([song], now: false, CancellationToken.None); }
        catch (Exception ex) { Status = ex.Message; }
        RefreshDownloadStates();
    }

    [RelayCommand]
    private void OpenArtist(SearchArtistItem? item)
    {
        if (item is null) return;
        if (item.Local is not null) _openArtist(item.Local.Name);
        else if (item.Online is { } online && _source is { } source) source.OpenArtist(online);
    }

    [RelayCommand]
    private void OpenArtistByName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) _openArtist(name);
    }

    [RelayCommand]
    private async Task OpenAlbum(SearchAlbumItem? item)
    {
        if (item is null) return;
        if (item.Local is not null) { _openAlbum(item.Local); return; }
        if (item.Online is not { } online || _source is not { } source) return;
        try { await source.OpenAlbumAsync(online, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex.Message; }
    }

    // ── Downloads and library changes ──

    private void RefreshDownloadStates()
    {
        if (_source is not { } source) return;
        foreach (var item in Songs)
            if (item.Online is { } song) item.Apply(source.StatusOf(song));
        if (TopSong is { Online: { } top } topItem) topItem.Apply(source.StatusOf(top));
    }

    private void OnDownloadsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RefreshDownloadStates);

    /// <summary>The library changed (a download landed, a scan finished): search again, which is
    /// instant from the caches, so finished downloads turn into library rows.</summary>
    private void OnIndexRebuilt(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_query.Length == 0) return;
        // Library results follow every update (instant). The online half is asked again only
        // once a scan has settled: mid-scan the library updates every second or so, and the
        // ownership check needs the finished library.
        if (_library.IsScanning) { _local = _index.Search(_query); Publish(); }
        else Run(_query, force: true);
    });

    private void OnSourceChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => AttachSource(OnlineMusicSearch.Current));

    private void AttachSource(IOnlineMusicSearch? source)
    {
        if (ReferenceEquals(source, _source)) return;
        if (_source is not null) _source.DownloadsChanged -= OnDownloadsChanged;
        _source = source;
        if (_source is not null) _source.DownloadsChanged += OnDownloadsChanged;
        OnPropertyChanged(nameof(HasOnline));
    }

    public void Dispose()
    {
        _onlineCts?.Cancel();
        _artCts?.Cancel();
        _index.Rebuilt -= OnIndexRebuilt;
        _index.Dispose();
        OnlineMusicSearch.CurrentChanged -= OnSourceChanged;
        AttachSource(null);
    }
}

/// <summary>A result with an online cover still to fetch.</summary>
public interface IOnlineArtwork
{
    string ArtworkUrl { get; }
    string? ArtworkPath { get; set; }
}

/// <summary>A song row: a library track, or an online song with a download button.</summary>
public sealed partial class SearchSongItem : ObservableObject, IOnlineArtwork
{
    public SearchSongItem(Track track)
    {
        Local = track;
        Title = track.Title;
        ArtistDisplay = track.ArtistDisplay;
        AlbumTitle = track.Album;
        DurationText = track.DurationFormatted;
        IsExplicit = track.IsExplicit;
        _artworkPath = track.AlbumArtworkPath;
        ArtworkUrl = "";
    }

    public SearchSongItem(OnlineSong song)
    {
        Online = song;
        Title = song.Title;
        ArtistDisplay = song.ArtistDisplay;
        AlbumTitle = song.Album;
        DurationText = song.Seconds is { } s ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "";
        IsExplicit = song.Explicit == true;
        ArtworkUrl = song.ArtworkUrl;
    }

    public Track? Local { get; }
    public OnlineSong? Online { get; }
    public bool IsLocal => Local is not null;
    public bool IsOnline => Local is null;
    public string Title { get; }
    public string ArtistDisplay { get; }
    public string AlbumTitle { get; }
    public string DurationText { get; }
    public bool IsExplicit { get; }
    public string ArtworkUrl { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasArtwork))]
    private string? _artworkPath;

    public bool HasArtwork => !string.IsNullOrEmpty(ArtworkPath);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartDownload), nameof(IsDownloading), nameof(IsQueued), nameof(IsSaved), nameof(IsFailed))]
    private OnlineDownloadState _downloadState;

    /// <summary>0..1 while downloading.</summary>
    [ObservableProperty] private double _progress;

    [ObservableProperty] private string _downloadMessage = "";

    public bool CanStartDownload => IsOnline && DownloadState is OnlineDownloadState.None or OnlineDownloadState.Failed;
    public bool IsDownloading => IsOnline && DownloadState == OnlineDownloadState.Downloading;
    public bool IsQueued => IsOnline && DownloadState == OnlineDownloadState.Queued;
    public bool IsSaved => IsOnline && DownloadState == OnlineDownloadState.Saved;
    public bool IsFailed => IsOnline && DownloadState == OnlineDownloadState.Failed;

    internal void Apply(OnlineDownloadStatus status)
    {
        DownloadState = status.State;
        Progress = status.Progress;
        DownloadMessage = status.Message;
    }
}

/// <summary>An artist tile: a library artist (maybe with an online picture) or an online one.</summary>
public sealed partial class SearchArtistItem : ObservableObject, IOnlineArtwork
{
    public SearchArtistItem(Artist artist)
    {
        Local = artist;
        Name = artist.Name;
        _artworkPath = artist.ImagePath;
        Subtitle = "Artist";
    }

    public SearchArtistItem(OnlineArtist artist)
    {
        Online = artist;
        Name = artist.Name;
        ArtworkUrl = artist.PictureUrl;
        Subtitle = artist.Genre.Length > 0 ? artist.Genre : "Artist";
    }

    public Artist? Local { get; }
    public OnlineArtist? Online { get; private set; }
    public string Name { get; }
    public string Subtitle { get; private set; }
    public string ArtworkUrl { get; private set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasArtwork))]
    private string? _artworkPath;

    public bool HasArtwork => !string.IsNullOrEmpty(ArtworkPath);

    internal void AttachOnline(OnlineArtist artist)
    {
        Online ??= artist;
        if (string.IsNullOrEmpty(ArtworkPath) && ArtworkUrl.Length == 0) ArtworkUrl = artist.PictureUrl;
        if (Subtitle == "Artist" && artist.Genre.Length > 0) Subtitle = artist.Genre;
    }
}

/// <summary>An album card: a library album, or an online release (badge, opens the native page).</summary>
public sealed partial class SearchAlbumItem : ObservableObject, IOnlineArtwork
{
    public SearchAlbumItem(Album album)
    {
        Local = album;
        Title = album.Name;
        Subtitle = album.TileSubtitle;
        IsExplicit = album.IsExplicit;
        _artworkPath = album.ArtworkPath;
        ArtworkUrl = "";
    }

    public SearchAlbumItem(OnlineAlbum album)
    {
        Online = album;
        Title = album.Title;
        var kind = album.Kind switch { "single" => "Single", "ep" => "EP", _ => "" };
        Subtitle = string.Join(" · ", new[] { album.ArtistName, kind, album.Year }.Where(p => p.Length > 0));
        IsExplicit = album.Explicit == true;
        ArtworkUrl = album.ArtworkUrl;
    }

    public Album? Local { get; }
    public OnlineAlbum? Online { get; }
    public bool IsOnline => Local is null;
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsExplicit { get; }
    public string ArtworkUrl { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasArtwork))]
    private string? _artworkPath;

    public bool HasArtwork => !string.IsNullOrEmpty(ArtworkPath);
}
