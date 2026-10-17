using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

/// <summary>Existing recommendations on a native shelf. Its work stops when the user starts searching.</summary>
public sealed partial class ForYouShelfViewModel : ObservableObject, IDisposable
{
    private ForYouSource? _source;
    private FinderOnlineSearch? _online;
    private DiscoveryActions? _actions;
    private readonly bool _followFeature;
    private readonly TimeSpan _undoDuration;
    private CancellationTokenSource? _load, _undo;
    private DiscoverResult? _page;
    private (ForYouPick Pick, int Index, DiscoverResult? Page, ForYouSource Source)? _dismissed;
    private long _generation;
    private bool _active, _disposed;

    public ForYouShelfViewModel()
    {
        _followFeature = true;
        _undoDuration = TimeSpan.FromSeconds(8);
        OnlineMusicSearch.CurrentChanged += FeatureChanged;
        Attach(FreeMusicPlugin.Current?.ForYou, FreeMusicPlugin.Current?.OnlineSearch, FreeMusicPlugin.Current?.Discovery);
    }

    internal ForYouShelfViewModel(ForYouSource source, FinderOnlineSearch online, DiscoveryActions actions, TimeSpan? undoDuration = null)
    {
        _undoDuration = undoDuration ?? TimeSpan.FromSeconds(8);
        Attach(source, online, actions);
    }

    public ObservableCollection<ForYouPick> Picks { get; } = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasUndo;
    [ObservableProperty] private string _undoLabel = "";
    [ObservableProperty] private bool _hasNewPicks;
    [ObservableProperty] private bool _isExpanded = true;
    public bool IsAvailable => _source is not null && _online is not null;

    private void FeatureChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) Attach(FreeMusicPlugin.Current?.ForYou, FreeMusicPlugin.Current?.OnlineSearch, FreeMusicPlugin.Current?.Discovery);
    });

    private void Attach(ForYouSource? source, FinderOnlineSearch? online, DiscoveryActions? actions)
    {
        if (_source is not null) _source.PicksReady -= NewPicks;
        if (_online is not null) _online.DownloadsChanged -= DownloadsChanged;
        _load?.Cancel(); _generation++;
        _source = source; _online = online; _actions = actions;
        _page = null; Picks.Clear(); IsLoading = false;
        if (_source is not null) _source.PicksReady += NewPicks;
        if (_online is not null) _online.DownloadsChanged += DownloadsChanged;
        OnPropertyChanged(nameof(IsAvailable));
        if (_active && IsAvailable) _ = LoadAsync(false);
    }

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (!active) { _load?.Cancel(); _generation++; IsLoading = false; return; }
        if (_page is null) _ = LoadAsync(false);
        else
        {
            _load?.Dispose(); _load = new();
            foreach (var row in Picks.Where(r => !r.Song.HasArtwork)) _ = LoadArt(row, _load.Token);
            RefreshDownloadStates();
        }
    }

    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;
    [RelayCommand] private Task Refresh() => LoadAsync(true);

    internal async Task LoadAsync(bool refresh)
    {
        if (_disposed || _source is not { } source || _online is null) return;
        _load?.Cancel(); _load?.Dispose();
        var stop = _load = new();
        var generation = ++_generation;
        bool Current() => !_disposed && generation == _generation && !stop.IsCancellationRequested && ReferenceEquals(source, _source);
        IsLoading = true;
        Status = Picks.Count > 0 ? "Refreshing…" : "Finding music for you…";
        try
        {
            var progress = new Progress<string>(text => { if (Current()) Status = text; });
            var page = !refresh ? await source.SavedAsync(stop.Token) : null;
            page ??= await source.BuildAsync(refresh, progress, stop.Token);
            if (!Current()) return;
            // A cached page can predate a download or library scan. Filter those recordings
            // before displaying it; download actions still check the disk afresh.
            if (_actions is { } actions && page.Songs.Count > 0)
            {
                var matches = await actions.MatchAsync(page.Songs.Select(s => s.Track.Song), stop.Token, reuse: true);
                page = page with { Songs = page.Songs.Where(s => !matches.TryGetValue(s.Track.Song.Id, out var m) || m.Local is null && m.Download is null).ToList() };
            }
            if (Current()) Show(page, source);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { if (Current()) Status = "For you could not load: " + ArtistCatalog.Explain(ex); }
        finally { if (Current()) IsLoading = false; }
    }

    private void Show(DiscoverResult page, ForYouSource source)
    {
        _page = page;
        Picks.Clear();
        foreach (var pick in page.Songs.Where(s => !source.Hidden.HidesSong(s.Track.Song)).Take(20))
        {
            var row = new ForYouPick(pick, new SearchSongItem(_online!.Remember(pick.Track)));
            Picks.Add(row);
            _ = LoadArt(row, _load!.Token);
        }
        HasNewPicks = false;
        Status = page.Notice;
        if (Picks.Count == 0 && Status.Length == 0) Status = "No new picks right now. Listen to more music, then refresh.";
        RefreshDownloadStates();
    }

    private async Task LoadArt(ForYouPick row, CancellationToken ct)
    {
        var online = _online;
        if (online is null || row.Song.ArtworkUrl.Length == 0) return;
        try
        {
            var path = online.CachedArtwork(row.Song.ArtworkUrl) ?? await online.ArtworkAsync(row.Song.ArtworkUrl, ct);
            if (!_disposed && !ct.IsCancellationRequested && ReferenceEquals(online, _online)) row.Song.ArtworkPath = path;
        }
        catch (OperationCanceledException) { }
    }

    private void NewPicks(DiscoverResult _) => Dispatcher.UIThread.Post(() =>
    {
        // Do not move the cards under the pointer; Refresh adopts a newer page.
        if (!_disposed && _active && !IsLoading && Picks.Count > 0) HasNewPicks = true;
    });
    private void DownloadsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_disposed) RefreshDownloadStates(); });
    private void RefreshDownloadStates()
    {
        if (_online is null) return;
        foreach (var row in Picks) if (row.Song.Online is { } song) row.Song.Apply(_online.StatusOf(song));
    }

    [RelayCommand] private Task Download(ForYouPick? pick) => DownloadAsync(pick, true);
    [RelayCommand] private Task QueueDownload(ForYouPick? pick) => DownloadAsync(pick, false);
    [RelayCommand] private async Task OpenPick(ForYouPick? pick)
    {
        if (pick is null || _online is not { } online) return;
        if (pick.Pick.Track.Album is { } album)
        {
            try { await online.OpenAlbumAsync(new(album.Id, album.Title, album.Artist.Name), CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status = ex.Message; }
        }
        else OnlineMusicSearch.RequestOpen(pick.Song.Title + " " + pick.Song.ArtistDisplay);
    }
    [RelayCommand] private void OpenPickArtist(ForYouPick? pick)
    {
        if (pick?.Pick.Track.Song.Artists.FirstOrDefault() is { } artist)
            _online?.OpenArtist(new(artist.Id, artist.Name, Genre: artist.Genre));
    }
    private async Task DownloadAsync(ForYouPick? pick, bool now)
    {
        if (_disposed || pick?.Song.Online is not { } song || _online is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        try { Status = await online.DownloadAsync([song], now, CancellationToken.None); }
        catch (Exception ex) { Status = ex.Message; }
        RefreshDownloadStates();
    }

    [RelayCommand]
    private void NotForMe(ForYouPick? pick)
    {
        if (pick is null || _source is not { } source || !Picks.Contains(pick)) return;
        if (!source.Hidden.Hide(pick.Pick.Track.Song)) return;
        _undo?.Cancel(); _undo?.Dispose();
        var stop = _undo = new();
        _dismissed = (pick, Picks.IndexOf(pick), _page, source);
        Picks.Remove(pick);
        UndoLabel = "Hidden · " + pick.Song.Title;
        HasUndo = true;
        _ = ExpireUndo(stop);
    }

    private async Task ExpireUndo(CancellationTokenSource stop)
    {
        try
        {
            await Task.Delay(_undoDuration, stop.Token);
            if (!_disposed && ReferenceEquals(stop, _undo)) { HasUndo = false; _dismissed = null; }
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void Undo()
    {
        if (!HasUndo || _dismissed is not { } old) return;
        _undo?.Cancel();
        old.Source.Hidden.Undo(old.Pick.Pick.Track.Song);
        // Undo may finish after Refresh or a feature switch. Remove the preference in all
        // cases, but never put a card from an older page into the newly displayed page.
        if (ReferenceEquals(old.Page, _page) && ReferenceEquals(old.Source, _source))
        {
            _load?.Cancel(); _generation++; IsLoading = false;
            if (!Picks.Contains(old.Pick) && !old.Pick.Song.IsSaved && !old.Pick.Song.IsQueued)
                Picks.Insert(Math.Min(old.Index, Picks.Count), old.Pick);
        }
        HasUndo = false; _dismissed = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _load?.Cancel(); _undo?.Cancel();
        if (_followFeature) OnlineMusicSearch.CurrentChanged -= FeatureChanged;
        if (_source is not null) _source.PicksReady -= NewPicks;
        if (_online is not null) _online.DownloadsChanged -= DownloadsChanged;
    }
}

public sealed class ForYouPick
{
    internal DiscoverSong Pick { get; }
    internal ForYouPick(DiscoverSong pick, SearchSongItem song) { Pick = pick; Song = song; }
    public SearchSongItem Song { get; }
    public string Because => Pick.Because;
}
