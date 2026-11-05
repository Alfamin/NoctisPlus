using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services.Search;

namespace Noctis.Mobile.ViewModels;

/// <summary>One online song row under the library results.</summary>
public sealed partial class OnlineSongItem(OnlineSong song) : ObservableObject
{
    public OnlineSong Song { get; private set; } = song;
    public string Title => Song.Title;
    public string Subtitle => Song.Album.Length > 0 ? $"{Song.ArtistDisplay} · {Song.Album}" : Song.ArtistDisplay;
    [ObservableProperty] private string? _artworkPath;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isWorking;
    /// <summary>Queued, downloading or saved: Download does nothing more.</summary>
    [ObservableProperty] private bool _isRequested;

    internal void Update(OnlineSong song)
    {
        if (Song == song) return;
        var artworkChanged = Song.ArtworkUrl != song.ArtworkUrl;
        Song = song;
        OnPropertyChanged(nameof(Song));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        if (artworkChanged) ArtworkPath = null;
    }
    internal bool ArtworkPending;
}

/// <summary>
/// The online half of phone Search (Music Finder). Library results stay synchronous in
/// <see cref="SearchPageViewModel"/>; this asks the online source after a short pause in typing, and
/// only the latest query's answers are shown (each new query cancels the last). A song the library
/// already has (the source's recording match, <see cref="OnlineSong.LocalTrackId"/>) is left to the
/// library row. A tap on Play or Download for a song that is already being fetched or queued does
/// nothing, so repeated taps never repeat a bot request.
/// </summary>
public sealed partial class OnlineResultsViewModel : ObservableObject, IDisposable
{
    internal const int SongCap = 10;
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(450);

    private readonly IOnlineMusicSearch _source;
    private readonly Action<Action> _marshal;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<IReadOnlyList<Track>> _play;
    private readonly Action _signIn;
    private CancellationTokenSource? _query;
    private CancellationTokenSource? _playing;
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private int _generation;
    private NowPlayingViewModel? _queuePlayer;
    private string? _queueLabel;

    internal void UseOnlineQueue(NowPlayingViewModel player, string label)
    {
        if (_queuePlayer is not null) _queuePlayer.PropertyChanged -= OnQueuePlaybackChanged;
        _queuePlayer = player; _queueLabel = label;
        player.PropertyChanged += OnQueuePlaybackChanged;
    }
    private void OnQueuePlaybackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(NowPlayingViewModel.IsPreparingOnline) or nameof(NowPlayingViewModel.CurrentTrack))) return;
        foreach (var row in Songs) row.IsWorking = _queuePlayer?.IsPreparingOnlineSong(_source, row.Song.Id) == true;
    }

    public OnlineResultsViewModel(IOnlineMusicSearch source, Action<Action> marshal, Action<IReadOnlyList<Track>> play,
        Action signIn, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _source = source; _marshal = marshal; _play = play; _signIn = signIn;
        _delay = delay ?? Task.Delay;
        _source.DownloadsChanged += OnDownloadsChanged;
    }

    public ObservableCollection<OnlineSongItem> Songs { get; } = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSongs))] private int _songCount;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _message;
    public bool HasSongs => SongCount > 0;
    internal event Action<OnlineSong, RecommendationAction>? Acted;
    internal bool LazyArtwork { get; init; }
    internal void ShowRecommendations(IReadOnlyList<OnlineSong> songs) => Show(songs, "");
    internal void CancelPlay(string songId)
    {
        if (_playingItem?.Song.Id == songId) _playing?.Cancel();
        _queuePlayer?.CancelOnlineSong(_source, songId);
    }
    internal void EnsureArtwork(OnlineSongItem item)
    {
        if (item.ArtworkPath is null && item.Song.ArtworkUrl.Length > 0 && !item.ArtworkPending)
            _ = FetchArtworkAsync(item, _query?.Token ?? default);
    }

    /// <summary>The query changed. Empty clears at once; otherwise the online search starts after
    /// <see cref="Debounce"/> unless another change comes first.</summary>
    public void Search(string? text)
    {
        _query?.Cancel();
        var generation = ++_generation;
        var query = text?.Trim() ?? "";
        Songs.Clear(); SongCount = 0; Message = null;
        if (query.Length < 2) { IsSearching = false; return; }
        var cts = _query = new CancellationTokenSource();
        IsSearching = true;
        _ = RunAsync(query, generation, cts.Token);
    }

    private async Task RunAsync(string query, int generation, CancellationToken ct)
    {
        try
        {
            await _delay(Debounce, ct).ConfigureAwait(false);
            _source.WarmUp();
            await foreach (var result in _source.SearchAsync(query, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                // The source's recording match: the library row already stands for it.
                var songs = result.Songs.Where(s => s.LocalTrackId is null).DistinctBy(s => s.Id).Take(SongCap).ToList();
                _marshal(() => { if (generation == _generation) Show(songs, result.Warning); });
                if (result.Complete) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _marshal(() => { if (generation == _generation) Message = "Online search failed. Try again"; });
        }
        finally
        {
            _marshal(() => { if (generation == _generation) IsSearching = false; });
        }
    }

    private void Show(IReadOnlyList<OnlineSong> songs, string warning)
    {
        var known = Songs.ToDictionary(i => i.Song.Id);
        var next = new List<OnlineSongItem>(songs.Count);
        foreach (var song in songs)
        {
            var item = known.TryGetValue(song.Id, out var old) ? old : new OnlineSongItem(song);
            item.Update(song);
            next.Add(item);
            Refresh(item);
            if (item.ArtworkPath is null && song.ArtworkUrl.Length > 0)
            {
                item.ArtworkPath = _source.CachedArtwork(song.ArtworkUrl);
                if (!LazyArtwork) EnsureArtwork(item);
            }
        }
        StableRows.Update(Songs, next);
        SongCount = Songs.Count;
        Message = warning.Length > 0 ? warning : null;
    }

    private async Task FetchArtworkAsync(OnlineSongItem item, CancellationToken ct)
    {
        item.ArtworkPending = true;
        var url = item.Song.ArtworkUrl;
        try
        {
            var path = await _source.ArtworkAsync(url, ct).ConfigureAwait(false);
            if (path != null) _marshal(() => { if (item.Song.ArtworkUrl == url) item.ArtworkPath = path; });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { _marshal(() => item.ArtworkPending = false); }
    }

    /// <summary>
    /// Plays the song. Concurrent on purpose: a tap on B while A is still being fetched must reach here to
    /// cancel A's wait (the toolkit's default refuses a second execution while the first runs). The latest tap
    /// wins; a repeated tap on the song already being waited for does nothing; a superseded wait never plays,
    /// even if it completes later. Cancelling the wait never cancels the shared download (the source keeps it).
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayAsync(OnlineSongItem? item)
    {
        if (item is null) return;
        var completed = _source.CompletedPlaybackPath(item.Song);
        if (!_source.CanDownload && item.Song.LocalTrackId is null && !(completed is not null && File.Exists(completed)))
        { Message = "Sign in to Telegram to play online songs"; _signIn(); return; }
        if (_queuePlayer is { } queue)
        {
            var index = Songs.IndexOf(item);
            if (index < 0) return;
            if (queue.IsPreparingOnlineSong(_source, item.Song.Id)) return;
            Message = null;
            queue.PlayOnlineTracks(_source, Songs.Select(r => r.Song).ToArray(), index, _queueLabel);
            Acted?.Invoke(item.Song, RecommendationAction.Play);
            return;
        }
        if (_playing is { } current && _playingItem == item && !current.IsCancellationRequested) return;
        _playing?.Cancel();
        var cts = _playing = new CancellationTokenSource();
        var previous = _playingItem;
        _playingItem = item;
        if (previous != null && previous != item) previous.IsWorking = false;
        item.IsWorking = true; Message = null;
        try
        {
            var track = await _source.PreparePlaybackAsync(item.Song, keep: false, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(_playing, cts))
            {
                _play([track]);
                Acted?.Invoke(item.Song, RecommendationAction.Play);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ReferenceEquals(_playing, cts)) Message = Short(ex);
        }
        finally
        {
            if (ReferenceEquals(_playing, cts)) { _playing = null; _playingItem = null; item.IsWorking = false; }
            cts.Dispose();
            Refresh(item);
        }
    }
    private OnlineSongItem? _playingItem;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DownloadAsync(OnlineSongItem? item) => DownloadAsync(item, false);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DownloadNowAsync(OnlineSongItem? item) => DownloadAsync(item, true);

    private async Task DownloadAsync(OnlineSongItem? item, bool now)
    {
        if (item is null) return;
        if (!_source.CanDownload) { Message = "Sign in to Telegram to download"; _signIn(); return; }
        Refresh(item);
        if (item.IsRequested || !_inFlight.Add("dl:" + item.Song.Id)) return;
        try
        {
            Message = await _source.DownloadAsync([item.Song], now, CancellationToken.None);
            Acted?.Invoke(item.Song, now ? RecommendationAction.Download : RecommendationAction.Queue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Short(ex); }
        finally { _inFlight.Remove("dl:" + item.Song.Id); Refresh(item); }
    }

    private void Refresh(OnlineSongItem item)
    {
        if (_queuePlayer is not null) item.IsWorking = _queuePlayer.IsPreparingOnlineSong(_source, item.Song.Id);
        var status = _source.StatusOf(item.Song);
        item.Progress = status.Progress;
        item.IsRequested = status.State is OnlineDownloadState.Queued or OnlineDownloadState.Downloading or OnlineDownloadState.Saved;
        item.Status = status.State switch
        {
            OnlineDownloadState.Queued => "Queued",
            OnlineDownloadState.Downloading => $"{Math.Clamp(status.Progress, 0, 1):P0}",
            OnlineDownloadState.Saved => "Saved",
            OnlineDownloadState.Failed => status.Message.Length > 0 ? status.Message : "Failed",
            _ => "",
        };
    }

    private int _refreshQueued;
    private void OnDownloadsChanged(object? sender, EventArgs e)
    {
        // Progress fires often and on any thread: one refresh pass at a time on the UI thread.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _marshal(() => { Volatile.Write(ref _refreshQueued, 0); foreach (var item in Songs) Refresh(item); });
    }

    internal static string Short(Exception ex)
    {
        if (ex is Noctis.Services.OfflineModeException) return "Offline mode";
        var m = ex.Message ?? "";
        var colon = m.IndexOf(": ", StringComparison.Ordinal);
        // Finder errors read "CODE: sentence"; show the sentence only.
        var text = colon > 0 && m[..colon].All(c => char.IsUpper(c) || c == '_') ? m[(colon + 2)..] : m;
        return text.Length is > 0 and <= 140 ? text : "Couldn't get this song. Try again";
    }

    public void Dispose()
    {
        if (_queuePlayer is not null) _queuePlayer.PropertyChanged -= OnQueuePlaybackChanged;
        _source.DownloadsChanged -= OnDownloadsChanged;
        _query?.Cancel();
        _playing?.Cancel();
    }
}

/// <summary>Music Finder's download queue on the phone: progress, pause/resume, retry, remove, clear.</summary>
public sealed partial class FinderDownloadsViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadQueue _queue;
    private readonly Action<Action> _marshal;
    private int _refreshQueued;

    public FinderDownloadsViewModel(IDownloadQueue queue, Action<Action> marshal)
    {
        _queue = queue; _marshal = marshal;
        _queue.Changed += OnChanged;
        Refresh();
    }

    public ObservableCollection<DownloadRow> Entries { get; } = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PauseLabel))] private bool _isPaused;
    [ObservableProperty] private bool _hasEntries;
    [ObservableProperty] private bool _hasFailed;
    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    internal void Refresh()
    {
        var known = Entries.ToDictionary(r => r.Entry.Handle);
        var rows = _queue.Snapshot().Select(e =>
        {
            var row = known.TryGetValue(e.Handle, out var current) ? current : new DownloadRow(e);
            row.Update(e);
            return row;
        }).ToList();
        StableRows.Update(Entries, rows);
        IsPaused = _queue.IsPaused;
        HasEntries = Entries.Count > 0;
        HasFailed = rows.Any(r => r.Entry.State == OnlineDownloadState.Failed);
    }

    [RelayCommand] private void TogglePause() { _queue.SetPaused(!_queue.IsPaused); Refresh(); }
    [RelayCommand] private void RetryFailed() { _queue.RetryFailed(); Refresh(); }
    [RelayCommand] private void Clear() { Run(_queue.Clear); }
    [RelayCommand] private void Remove(DownloadRow? row) { if (row != null) Run(() => _queue.Remove(row.Entry)); }
    [ObservableProperty] private string? _errorText;

    /// <summary>A storage failure (the queue file cannot be written) is shown, never swallowed: the request stays.</summary>
    private void Run(Action change)
    {
        ErrorText = null;
        try { change(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorText = "Couldn't save the download list. Check storage"; }
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _marshal(() => { Volatile.Write(ref _refreshQueued, 0); Refresh(); });
    }

    public void Dispose() => _queue.Changed -= OnChanged;
}

public sealed class DownloadRow(DownloadEntry entry) : ObservableObject
{
    public DownloadEntry Entry { get; private set; } = entry;
    internal void Update(DownloadEntry entry)
    {
        if (Entry == entry) return;
        var old = Entry;
        Entry = entry;
        OnPropertyChanged(nameof(Entry));
        if (old.Title != entry.Title) OnPropertyChanged(nameof(Title));
        if (old.Artist != entry.Artist || old.State != entry.State || old.Progress != entry.Progress
            || old.Detail != entry.Detail || old.IsHeld != entry.IsHeld) OnPropertyChanged(nameof(Subtitle));
        if (old.Progress != entry.Progress) OnPropertyChanged(nameof(Progress));
        if (old.State != entry.State) OnPropertyChanged(nameof(IsRunning));
    }
    public string Title => Entry.Title;
    public string Subtitle => Entry.State switch
    {
        OnlineDownloadState.Queued => Entry.IsHeld ? $"{Entry.Artist} · Waiting" : $"{Entry.Artist} · Queued",
        OnlineDownloadState.Downloading => $"{Entry.Artist} · {Math.Clamp(Entry.Progress, 0, 1):P0}",
        OnlineDownloadState.Saved => $"{Entry.Artist} · Saved",
        OnlineDownloadState.Failed => Entry.Detail.Length > 0 ? $"{Entry.Artist} · {Entry.Detail}" : $"{Entry.Artist} · Failed",
        _ => Entry.Artist,
    };
    public double Progress => Entry.Progress;
    public bool IsRunning => Entry.State == OnlineDownloadState.Downloading;
}

/// <summary>Keep row controls, selection and scroll anchors when only their progress/metadata changed.</summary>
internal static class StableRows
{
    internal static void Update<T>(ObservableCollection<T> rows, IReadOnlyList<T> next) where T : class
    {
        var kept = next.ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--) if (!kept.Contains(rows[i])) rows.RemoveAt(i);
        for (var i = 0; i < next.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], next[i])) continue;
            var previous = rows.IndexOf(next[i]);
            if (previous >= 0) rows.Move(previous, i);
            else rows.Insert(i, next[i]);
        }
    }
}
