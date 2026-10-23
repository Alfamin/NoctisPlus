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
    internal Func<IOnlineMusicSearch, OnlineSong, Task>? PlayRequested { get; set; }
    private long _playGeneration;

    public ForYouShelfViewModel()
    {
        _followFeature = true;
        _undoDuration = TimeSpan.FromSeconds(8);
        Picks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowSkeletons));
        OnlineMusicSearch.CurrentChanged += FeatureChanged;
        Attach(FreeMusicPlugin.Current?.ForYou, FreeMusicPlugin.Current?.OnlineSearch, FreeMusicPlugin.Current?.Discovery);
    }

    internal ForYouShelfViewModel(ForYouSource source, FinderOnlineSearch online, DiscoveryActions actions, TimeSpan? undoDuration = null)
    {
        _undoDuration = undoDuration ?? TimeSpan.FromSeconds(8);
        Picks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowSkeletons));
        Attach(source, online, actions);
    }

    public ObservableCollection<ForYouPick> Picks { get; } = [];
    /// <summary>Owner #139: the pages the shelf can go back to, 1 the newest. Shown only when there is more than one.</summary>
    public ObservableCollection<ForYouPageTab> PageTabs { get; } = [];
    public bool HasPageTabs => PageTabs.Count > 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSkeletons), nameof(RefreshTip))] private bool _isLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RefreshTip))] private string _status = "";
    [ObservableProperty] private bool _hasUndo;
    [ObservableProperty] private string _undoLabel = "";
    [ObservableProperty] private bool _hasNewPicks;
    [ObservableProperty] private bool _isExpanded = true;
    public bool IsAvailable => _source is not null && _online is not null;
    /// <summary>First load with nothing to show yet: cover-shaped placeholders.</summary>
    public bool ShowSkeletons => IsLoading && Picks.Count == 0;

    /// <summary>While loading: how far along (0..1) and "7/20" for the Refresh button's ring,
    /// read from the steps' "7 of 20" progress; empty while a step has no count.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLoadCount))] private double _loadFraction;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLoadCount))] private string _loadCount = "";
    public bool HasLoadCount => LoadCount.Length > 0;

    /// <summary>The Refresh button's tooltip: the step in progress while loading.</summary>
    public string RefreshTip => IsLoading && Status.Length > 0 ? Status : "Refresh";

    private static readonly System.Text.RegularExpressions.Regex StepCount = new(@"(\d+) of (\d+)\s*$");

    private void ShowProgress(string text)
    {
        Status = text;
        if (StepCount.Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var done) && int.TryParse(m.Groups[2].Value, out var total) && total > 0)
        {
            LoadFraction = Math.Clamp((double)done / total, 0, 1);
            LoadCount = done + "/" + total;
        }
        else { LoadFraction = 0; LoadCount = ""; }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        if (!value) { LoadFraction = 0; LoadCount = ""; }
    }

    partial void OnIsExpandedChanged(bool value) { if (value) RecordShown(); }

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
            RecordShown();
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
            // A report posted late from the build must not overwrite the final status (the notice or "Couldn't refresh").
            var progress = new Progress<string>(text => { if (Current() && IsLoading) ShowProgress(text); });
            var page = !refresh ? await source.SavedAsync(stop.Token) : null;
            page ??= await source.BuildAsync(refresh, progress, stop.Token);
            if (!Current()) return;
            // No catalog answered: an outage, not an empty answer. Keep the picks on screen, or bring back the last saved page.
            if (page is { Songs.Count: 0, Unreachable: true } && (Picks.Count > 0 || source.LastSaved() is { Songs.Count: > 0 }))
            {
                if (Picks.Count == 0 && source.LastSaved() is { } saved) Show(saved, source);
                Status = "Couldn't refresh right now. Showing your last picks.";
                return;
            }
            page = await WithoutOwned(page, stop.Token);
            if (Current()) Show(page, source);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!Current()) return;
            // Never blank a shelf that had picks: keep what is on screen, or bring back the last
            // saved page (even an old one) and say quietly that it could not refresh.
            if (Picks.Count == 0 && source.LastSaved() is { Songs.Count: > 0 } saved) Show(saved, source);
            Status = Picks.Count > 0
                ? "Couldn't refresh right now. Showing your last picks."
                : "For you could not load: " + ArtistCatalog.Explain(ex);
        }
        finally { if (Current()) IsLoading = false; }
    }

    // A saved page can predate a download, a library scan or a listen. Filter those recordings before
    // displaying it (owner #139: a song listened to leaves its page); download actions still check the disk afresh.
    private async Task<DiscoverResult> WithoutOwned(DiscoverResult page, CancellationToken ct)
    {
        if (page.Songs.Count == 0) return page;
        if (_source is { } source)
        {
            var known = await source.FamiliarAsync(ct);
            page = page with { Songs = page.Songs.Where(s => !known.Contains(DiscoverIdentity.Key(s.Track.Song))).ToList() };
        }
        if (_actions is not { } actions || page.Songs.Count == 0) return page;
        var matches = await actions.MatchAsync(page.Songs.Select(s => s.Track.Song), ct, reuse: true);
        return page with { Songs = page.Songs.Where(s => !matches.TryGetValue(s.Track.Song.Id, out var m) || m.Local is null && m.Download is null).ToList() };
    }

    // ── Owner #139: the pages the shelf keeps, as a compact pager under it (‹ 1 2 3 … 10 ›, 1 the newest) ──
    private IReadOnlyList<DiscoverResult> _pages = [];
    private int _pageIndex;
    /// <summary>Numbers shown at most, gaps included: the first, the last, and the ones around the page on screen.</summary>
    internal const int PagerSlots = 7;
    public bool CanPreviousPage => HasPageTabs && _pageIndex > 0;
    public bool CanNextPage => HasPageTabs && _pageIndex < _pages.Count - 1;

    [RelayCommand] private Task ShowPage(ForYouPageTab? tab) => tab is { IsGap: false } ? GoToPage(tab.Number - 1) : Task.CompletedTask;
    [RelayCommand] private Task PreviousPage() => GoToPage(_pageIndex - 1);
    [RelayCommand] private Task NextPage() => GoToPage(_pageIndex + 1);

    /// <summary>Goes to a kept page. Its songs keep their page, so nothing counts twice.</summary>
    private async Task GoToPage(int index)
    {
        if (_disposed || index < 0 || index >= _pages.Count || index == _pageIndex && _page is not null || _source is not { } source || _online is null) return;
        _load?.Cancel(); _load?.Dispose();
        var stop = _load = new();
        var generation = ++_generation;
        IsLoading = false;
        try
        {
            var page = await WithoutOwned(_pages[index], stop.Token);
            if (!_disposed && generation == _generation && !stop.IsCancellationRequested && ReferenceEquals(source, _source)) Show(page, source);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void UpdatePageTabs(ForYouSource source)
    {
        _pages = source.Pages();
        var shown = _page?.Batch ?? "";
        _pageIndex = Math.Max(0, _pages.ToList().FindIndex(p => p.Batch.Length > 0 && p.Batch == shown));
        PageTabs.Clear();
        if (_pages.Count > 1)
            foreach (var number in PagerNumbers(_pages.Count, _pageIndex + 1))
                PageTabs.Add(new(number) { IsCurrent = number == _pageIndex + 1 });
        OnPropertyChanged(nameof(HasPageTabs)); OnPropertyChanged(nameof(CanPreviousPage)); OnPropertyChanged(nameof(CanNextPage));
    }

    /// <summary>The page numbers to show for <paramref name="count"/> pages with <paramref name="current"/> on screen; 0 is a gap (…).
    /// All of them when they fit, else the first, the last and a window round the current one, never wider than <see cref="PagerSlots"/>.</summary>
    internal static List<int> PagerNumbers(int count, int current)
    {
        if (count <= PagerSlots) return [.. Enumerable.Range(1, count)];
        int from = Math.Max(2, current - 1), to = Math.Min(count - 1, current + 1);
        if (current <= 3) { from = 2; to = 5; }
        else if (current >= count - 2) { from = count - 4; to = count - 1; }
        var numbers = new List<int> { 1 };
        if (from > 2) numbers.Add(0);
        for (int n = from; n <= to; n++) numbers.Add(n);
        if (to < count - 1) numbers.Add(0);
        numbers.Add(count);
        return numbers;
    }

    private void Show(DiscoverResult page, ForYouSource source)
    {
        _page = page;
        Picks.Clear();
        foreach (var pick in page.Songs.Where(s => !source.Hidden.HidesSong(s.Track.Song)).Take(20))
        {
            var row = new ForYouPick(pick, new SearchSongItem(_online!.Remember(pick.Track)), page.Batch ?? "", Picks.Count);
            Picks.Add(row);
            _ = LoadArt(row, _load!.Token);
        }
        HasNewPicks = false;
        Status = page.Notice;
        if (Picks.Count == 0 && Status.Length == 0) Status = "No new picks right now. Listen to more music, then refresh.";
        RefreshDownloadStates();
        RecordShown();
        UpdatePageTabs(source);
    }

    /// <summary>The page counts as shown once it is on screen: the shelf active and open. Recorded once per page, so
    /// coming back to it, Undo or a re-render never count it again. Retrieved-but-unshown picks are never recorded.</summary>
    private void RecordShown()
    {
        if (_disposed || !_active || !IsExpanded || _source is not { } source || _page is not { Batch.Length: > 0 } page || Picks.Count == 0) return;
        source.Feedback.Shown(page.Batch, Picks.Select(p => new DiscoverShown(DiscoverIdentity.Key(p.Pick.Track.Song), p.Pick.Track.Song.Id,
            DiscoverIdentity.Artist(p.Pick.Track.Song.Artists.FirstOrDefault()?.Name), p.Pick.Seed ?? "", p.Pick.Own, p.Position)).ToList());
    }

    /// <summary>The view's report of the cards at least half on screen. Only those count as seen when a passed-over song is
    /// rested; cards below the fold were retrieved, not shown. Ignored while the shelf is inactive or collapsed.</summary>
    internal void InView(IEnumerable<ForYouPick> visible)
    {
        if (_disposed || !_active || !IsExpanded || _source is not { } source || _page is not { Batch.Length: > 0 } page) return;
        source.Feedback.Seen(page.Batch, visible.Where(p => p.Batch == page.Batch).Select(p => p.Position).ToList());
    }

    private void Record(ForYouPick pick, DiscoverAction action)
    {
        var song = pick.Pick.Track.Song;
        _source?.Feedback.Record(pick.Batch, DiscoverIdentity.Key(song), DiscoverIdentity.Artist(song.Artists.FirstOrDefault()?.Name), song.Title, action);
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
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Play(ForYouPick? pick)
    {
        if (_disposed || pick?.Song.Online is not { } song || _online is not { } online || PlayRequested is not { } play) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        Record(pick, DiscoverAction.Play);
        var generation = ++_playGeneration;
        try
        {
            Status = ""; // the step icon shows the progress (owner #54)
            await play(online, song);
            if (!_disposed && generation == _playGeneration) Status = "";
        }
        catch (Exception ex) { if (!_disposed && generation == _playGeneration) Status = ex.Message; }
    }
    // Owner #34: one shared command that refused new runs while an album loaded greyed out every card
    // at once. Clicks run side by side; the newest one wins and cancels the album still loading.
    private CancellationTokenSource? _openStop;
    private string? _openingAlbum;
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task OpenPick(ForYouPick? pick)
    {
        if (pick is null || _online is not { } online) return;
        Record(pick, DiscoverAction.Open);
        if (pick.Pick.Track.Album is { } album)
        {
            if (_openStop is not null && _openingAlbum == album.Id) return; // already opening: its page is up
            _openStop?.Cancel();
            var stop = _openStop = new CancellationTokenSource();
            _openingAlbum = album.Id;
            try { await online.OpenAlbumAsync(new(album.Id, album.Title, album.Artist.Name), stop.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (ReferenceEquals(_openStop, stop)) Status = ex.Message; }
            finally { if (ReferenceEquals(_openStop, stop)) _openStop = null; stop.Dispose(); }
        }
        else OnlineMusicSearch.RequestOpen(pick.Song.Title + " " + pick.Song.ArtistDisplay);
    }
    [RelayCommand] private void OpenPickArtist(ForYouPick? pick)
    {
        if (pick?.Pick.Track.Song.Artists.FirstOrDefault() is { } artist)
        {
            Record(pick, DiscoverAction.Open);
            _online?.OpenArtist(new(artist.Id, artist.Name, Genre: artist.Genre));
        }
    }
    private async Task DownloadAsync(ForYouPick? pick, bool now)
    {
        if (_disposed || pick?.Song.Online is not { } song || _online is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        Record(pick, now ? DiscoverAction.Download : DiscoverAction.Queue);
        try { Status = await online.DownloadAsync([song], now, CancellationToken.None); }
        catch (Exception ex) { Status = ex.Message; }
        RefreshDownloadStates();
    }

    [RelayCommand]
    private void NotForMe(ForYouPick? pick)
    {
        if (pick is null || _source is not { } source || !Picks.Contains(pick)) return;
        if (!source.Hidden.Hide(pick.Pick.Track.Song)) return;
        Record(pick, DiscoverAction.Dismiss);
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
        old.Source.Feedback.Retract(old.Pick.Batch, DiscoverIdentity.Key(old.Pick.Pick.Track.Song), DiscoverAction.Dismiss);
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

    /// <summary>Forgets For you's dismissals, shown pages and saved page, then finds new picks. The library and its
    /// listening history are not touched.</summary>
    [RelayCommand]
    private Task ResetLearning()
    {
        if (_disposed || _source is not { } source) return Task.CompletedTask;
        _undo?.Cancel(); HasUndo = false; _dismissed = null;
        source.ResetLearning();
        _page = null; Picks.Clear(); _pages = []; PageTabs.Clear();
        OnPropertyChanged(nameof(HasPageTabs)); OnPropertyChanged(nameof(CanPreviousPage)); OnPropertyChanged(nameof(CanNextPage));
        return LoadAsync(true);
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

/// <summary>One of the shelf's page numbers (owner #139).</summary>
public sealed partial class ForYouPageTab : ObservableObject
{
    internal ForYouPageTab(int number) => Number = number;
    /// <summary>The page's number; 0 for a gap between numbers.</summary>
    public int Number { get; }
    public bool IsGap => Number == 0;
    public string Label => IsGap ? "…" : Number.ToString();
    [ObservableProperty] private bool _isCurrent;
}

public sealed class ForYouPick
{
    internal DiscoverSong Pick { get; }
    /// <summary>The page this card was shown on.</summary>
    internal string Batch { get; }
    /// <summary>Where it was placed when the page was shown (Undo puts it back without changing it).</summary>
    internal int Position { get; }
    internal ForYouPick(DiscoverSong pick, SearchSongItem song, string batch = "", int position = 0) { Pick = pick; Song = song; Batch = batch; Position = position; }
    public SearchSongItem Song { get; }
    public string Because => Pick.Because;
}
