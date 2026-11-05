using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services;
using Noctis.Services.Search;

namespace Noctis.Mobile.ViewModels;

/// <summary>Native phone shelf/page over Plus discovery. Rows reuse Search's proven playback,
/// progressive file and download actions; opening Home only fetches metadata.</summary>
public sealed partial class ForYouViewModel : MobilePage, IDisposable
{
    private readonly IOnlineDiscovery _source;
    private readonly IOnlineMusicSearch _online;
    private readonly Action<Action> _marshal;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private IReadOnlyList<RecommendationPage> _pages = [];
    private CancellationTokenSource? _load, _undo;
    private string? _receipt;
    private int _generation, _page;
    private bool _disposed;
    private bool _awaitingLibrary;
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
    private (OnlineSong Song, Guid Track)? _played;
    public ForYouViewModel(ShellViewModel shell, IOnlineDiscovery source, IOnlineMusicSearch online,
        Action<Action>? marshal = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Shell = shell; _source = source; _online = online;
        _awaitingLibrary = shell.Library.SongCount == 0;
        shell.Library.Refreshed += LibraryReady;
        _marshal = marshal ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _delay = delay ?? Task.Delay;
        Actions = new OnlineResultsViewModel(online, _marshal, tracks => shell.Player.PlayTracks(tracks, 0, "For You"), shell.OpenTelegramSignIn) { LazyArtwork = true };
        Actions.UseOnlineQueue(shell.Player, "For You");
        Actions.Acted += (song, action) =>
        {
            if (action == RecommendationAction.Play && shell.Player.CurrentTrack is { } track) _played = (song, track.Id);
            _ = Safely(() => _source.RecordAsync(CurrentId, song, action));
        };
    }
    public ShellViewModel Shell { get; }
    public override string Title => "For You";
    public OnlineResultsViewModel Actions { get; }
    public IEnumerable<OnlineSongItem> PreviewRows => Actions.Songs.Take(5);
    public bool HasSongs => Actions.Songs.Count > 0;
    public bool HasPages => _pages.Count > 1;
    public string PageLabel => HasPages ? $"{_page + 1} / {_pages.Count}" : "";
    private string CurrentId => _pages.Count > _page ? _pages[_page].Id : "";
    public bool ShowSkeletons => IsLoading && !HasSongs;
    public IReadOnlyList<int> Skeletons { get; } = [0, 1, 2];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSkeletons))] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasUndo;

    public async Task LoadAsync(bool refresh = false)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _load?.Cancel(); _load?.Dispose();
        var stop = _load = new(); var generation = ++_generation;
        IsLoading = true;
        try
        {
            var pages = await _source.LoadAsync(refresh, stop.Token).ConfigureAwait(false);
            _marshal(() =>
            {
                if (_disposed || stop.IsCancellationRequested || generation != _generation) return;
                var selected = CurrentId;
                _pages = pages; _page = refresh ? 0 : Math.Max(0, pages.ToList().FindIndex(p => p.Id == selected));
                ShowPage();
                DebugLog.Write("ForYou", $"Phone picks loaded: {pages.Sum(p => p.Songs.Count)} songs / {pages.Count} pages in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex.Message.StartsWith("LIBRARY_SCANNING:", StringComparison.Ordinal)) _awaitingLibrary = true;
            _marshal(() => { if (!_disposed && generation == _generation) Status = HasSongs ? "Showing saved picks" : "Couldn't load picks. Try again"; });
            DebugLog.Write("ForYou", $"Phone lookup unavailable: {ex.GetType().Name}");
        }
        finally { _marshal(() => { if (!_disposed && generation == _generation) { IsLoading = false; LibraryReady(null, EventArgs.Empty); } }); }
    }

    private void LibraryReady(object? sender, EventArgs e)
    {
        if (_disposed || !_awaitingLibrary || IsLoading || HasSongs || Shell.SelectedTab != MobileTab.Home
            || Shell.Library.SongCount == 0 || Shell.Library.Service.IsScanning || Shell.Library.Service.IsPublishingPartial) return;
        _awaitingLibrary = false; // one scan completion, not a network request on every play/progress event
        _ = LoadAsync();
    }

    private void ShowPage()
    {
        var page = _pages.ElementAtOrDefault(_page);
        Actions.ShowRecommendations(page?.Songs ?? []);
        Status = HasSongs ? "" : page?.Notice ?? "Play some music to shape your picks";
        OnPropertyChanged(nameof(PreviewRows)); OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(HasPages)); OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(ShowSkeletons));
    }
    public void Suspend()
    { _load?.Cancel(); _generation++; IsLoading = false; }
    public void RecordVisible(string songId)
    {
        if (Actions.Songs.FirstOrDefault(s => s.Song.Id == songId) is { } row) Actions.EnsureArtwork(row);
        var page = CurrentId;
        if (page.Length > 0 && _shown.Add(page + "\n" + songId))
            _ = Safely(() => _source.ShownAsync(page, [songId]));
    }
    [RelayCommand(AllowConcurrentExecutions = true)] private Task RefreshAsync() { _online.Retry(); return LoadAsync(true); }
    [RelayCommand] private void PreviousPage() { if (_page > 0) { _page--; ShowPage(); } }
    [RelayCommand] private void NextPage() { if (_page + 1 < _pages.Count) { _page++; ShowPage(); } }

    [RelayCommand]
    private void More(OnlineSongItem? item)
    {
        if (item is null) return;
        Shell.ShowOptions(OptionsMenuViewModel.ForItems([
            new(Shell, "Download now", false, () => _ = Actions.DownloadNowCommand.ExecuteAsync(item)),
            new(Shell, "Add to downloads", false, () => _ = Actions.DownloadCommand.ExecuteAsync(item)),
            new(Shell, "Not for me", false, () => _ = HideAsync(item, false)),
            new(Shell, "Hide artist", false, () => _ = HideAsync(item, true)),
        ]));
    }
    internal async Task HideAsync(OnlineSongItem item, bool artist)
    {
        try
        {
            var receipt = await _source.HideAsync(CurrentId, item.Song, artist);
            if (receipt is null) return;
            Shell.Player.RemoveOnlineRecommendation(_online, item.Song, artist);
            foreach (var row in Actions.Songs.Where(s => artist
                ? string.Equals(s.Song.ArtistDisplay, item.Song.ArtistDisplay, StringComparison.OrdinalIgnoreCase)
                : s.Song.Id == item.Song.Id)) Actions.CancelPlay(row.Song.Id);
            if (_played is { } playing && playing.Track == Shell.Player.CurrentTrack?.Id
                && (playing.Song.Id == item.Song.Id || artist && playing.Song.ArtistDisplay == item.Song.ArtistDisplay))
                Shell.Player.NextCommand.Execute(null);
            _undo?.Cancel(); _undo?.Dispose();
            var stop = _undo = new(); _receipt = receipt; HasUndo = true;
            _ = ExpireAsync(stop);
            // Feedback is visible immediately; a slow metadata refresh must not leave the rejected card in place.
            _pages = _pages.Select(p => p with { Songs = p.Songs.Where(s => artist
                ? !string.Equals(s.ArtistDisplay, item.Song.ArtistDisplay, StringComparison.OrdinalIgnoreCase)
                : s.Id != item.Song.Id).ToArray() }).ToArray();
            ShowPage();
            await LoadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "Couldn't save that choice. Try again"; }
    }
    private async Task ExpireAsync(CancellationTokenSource stop)
    {
        try { await _delay(TimeSpan.FromSeconds(8), stop.Token); _marshal(() => { if (!_disposed && ReferenceEquals(stop, _undo)) { HasUndo = false; _receipt = null; } }); }
        catch (OperationCanceledException) { }
    }
    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_receipt is not { } receipt) return;
        try
        {
            if (await _source.UndoAsync(receipt)) await LoadAsync();
            HasUndo = false; _receipt = null; _undo?.Cancel();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "Couldn't undo. Try again"; }
    }
    private static async Task Safely(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("ForYou", $"Feedback unavailable: {ex.GetType().Name}"); } }
    public void Dispose()
    { _disposed = true; _generation++; _load?.Cancel(); _undo?.Cancel(); Shell.Library.Refreshed -= LibraryReady; Actions.Dispose(); }
}
