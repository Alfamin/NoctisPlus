using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services.Search;
using Noctis.Services;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Downloads (Noctis Plus): the online download queue as a normal page, reached from Search.
/// Rows update in place (progress several times a second), so a busy queue never rebuilds.
/// </summary>
public sealed partial class DownloadsViewModel : ViewModelBase, IDisposable
{
    private readonly Dictionary<object, DownloadRow> _rows = new(ReferenceEqualityComparer.Instance);
    private IDownloadQueue? _queue;
    private bool _refreshPosted;
    private bool _disposed;
    private readonly ILibraryService? _library;
    private readonly PlayerViewModel? _player;
    private readonly Action<Track>? _openAlbum;
    private readonly Action<string>? _openArtist;
    private Dictionary<string, Track> _tracksByPath = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<Track> _tracks = [];
    private ILookup<string, Track>? _tracksByTitle;
    public LibrarySongsViewModel? LibraryActions { get; }

    public DownloadsViewModel(ILibraryService? library = null, PlayerViewModel? player = null,
        LibrarySongsViewModel? libraryActions = null, Action<Track>? openAlbum = null, Action<string>? openArtist = null)
    {
        _library = library;
        _player = player;
        LibraryActions = libraryActions;
        _openAlbum = openAlbum;
        _openArtist = openArtist;
        if (_library is not null) _library.LibraryUpdated += OnLibraryUpdated;
        RebuildLibrary();
        OnlineMusicSearch.CurrentChanged += OnSourceChanged;
        Attach(OnlineMusicSearch.Current?.Downloads);
    }

    /// <summary>Running, next, queued and failed requests.</summary>
    public ObservableCollection<DownloadRow> Pending { get; } = new();

    /// <summary>Finished this session and before (most recent first).</summary>
    public ObservableCollection<DownloadRow> Finished { get; } = new();

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private bool _hasHeld;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _hasPending;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _hasFinished;
    [ObservableProperty] private string _status = "";

    public bool IsEmpty => !HasPending && !HasFinished;
    public bool IsAvailable => _queue is not null;

    private void OnSourceChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    { if (!_disposed) Attach(OnlineMusicSearch.Current?.Downloads); });

    private void OnLibraryUpdated(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        RebuildLibrary();
        foreach (var row in Finished) Resolve(row);
    });

    private void RebuildLibrary()
    {
        _tracks = _library?.Tracks.ToArray() ?? [];
        _tracksByTitle = null;
        _tracksByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _tracks)
            if (PathKey(track.FilePath) is { } key) _tracksByPath.TryAdd(key, track);
    }

    private static string? PathKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private void Resolve(DownloadRow row)
    {
        Track? track = null;
        if (row.Entry is { } entry && row.IsSaved)
        {
            if (!string.IsNullOrWhiteSpace(entry.SavedPath))
            {
                if (PathKey(entry.SavedPath) is { } key) _tracksByPath.TryGetValue(key, out track);
            }
            else
            {
                // Old history may not know its output path. Only accept an unambiguous recording,
                // never a title-only match or a different live/remix/length version.
                var request = new FreeMusicFinder.MusicRequest(entry.Title, entry.Artist, entry.Seconds);
                _tracksByTitle ??= _tracks.ToLookup(t => FreeMusicFinder.RecordingMatch.TitleKey(t.Title));
                var matches = _tracksByTitle[FreeMusicFinder.RecordingMatch.TitleKey(entry.Title)]
                    .Where(t => FreeMusicFinder.RecordingMatch.Fits(request, t.Artist, t.Title, t.Duration)).Take(2).ToArray();
                if (matches.Length == 1) track = matches[0];
            }
        }
        row.Local = track;
    }

    internal void Attach(IDownloadQueue? queue)
    {
        if (ReferenceEquals(queue, _queue)) return;
        if (_queue is not null) _queue.Changed -= OnQueueChanged;
        _queue = queue;
        if (_queue is not null) _queue.Changed += OnQueueChanged;
        OnPropertyChanged(nameof(IsAvailable));
        Refresh();
    }

    /// <summary>Progress fires many times a second: coalesce into one refresh per frame-ish.</summary>
    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (_disposed || _refreshPosted) return;
        _refreshPosted = true;
        Dispatcher.UIThread.Post(() => { _refreshPosted = false; if (!_disposed) Refresh(); }, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        // Startup cache loading does not always announce an update. Navigation still catches it.
        var libraryRebuilt = _library is not null && _tracks.Count != _library.Tracks.Count;
        if (libraryRebuilt) RebuildLibrary();
        var entries = _queue?.Snapshot() ?? [];
        var pending = new List<DownloadRow>();
        var finished = new List<DownloadRow>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
        {
            seen.Add(entry.Handle);
            if (!_rows.TryGetValue(entry.Handle, out var row)) _rows[entry.Handle] = row = new DownloadRow();
            var needsResolve = row.Entry is null || row.State != entry.State || row.Entry.SavedPath != entry.SavedPath;
            row.Apply(entry);
            if (needsResolve || libraryRebuilt) Resolve(row);
            (entry.State == OnlineDownloadState.Saved ? finished : pending).Add(row);
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList()) _rows.Remove(gone);
        SearchViewModel.Sync(Pending, pending);
        SearchViewModel.Sync(Finished, finished);

        var running = pending.Count(r => r.IsDownloading);
        var failed = pending.Count(r => r.IsFailed);
        var queued = pending.Count - running - failed;
        HasPending = pending.Count > 0;
        HasFinished = finished.Count > 0;
        HasFailed = failed > 0;
        HasHeld = pending.Any(r => r.IsHeld);
        IsPaused = _queue?.IsPaused ?? false;
        var parts = new List<string>();
        if (running > 0) parts.Add("1 downloading");
        if (queued > 0) parts.Add($"{queued} queued");
        if (failed > 0) parts.Add($"{failed} failed");
        if (parts.Count == 0 && finished.Count > 0) parts.Add($"{finished.Count} downloaded");
        Summary = string.Join(" · ", parts) + (IsPaused && queued > 0 ? " · paused" : "");
    }

    private void Act(Action action)
    {
        try { action(); Status = ""; }
        catch (Exception ex) { Status = "The queue could not be saved: " + ex.Message; }
        Refresh();
    }

    /// <summary>Set by the main window: the empty page's Find music opens Search.</summary>
    internal Action? OpenSearch { get; set; }
    [RelayCommand] private void FindMusic() => OpenSearch?.Invoke();

    [RelayCommand] private void TogglePause() => Act(() => _queue?.SetPaused(!IsPaused));
    [RelayCommand] private void RetryFailed() => Act(() => _queue?.RetryFailed());
    [RelayCommand] private void StartHeld() => Act(() => _queue?.StartHeld());
    [RelayCommand] private void DownloadNext(DownloadRow? row) { if (row?.Entry is { } e) Act(() => _queue?.DownloadNext(e)); }
    [RelayCommand] private void Remove(DownloadRow? row) { if (row?.Entry is { } e) Act(() => _queue?.Remove(e)); }
    [RelayCommand]
    private async Task ImportSaved(DownloadRow? row)
    {
        if (_library is null || row?.Entry?.SavedPath is not { } path || !row.IsSaved) return;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The downloaded file was moved or removed. Retry the download.");
            await _library.ImportFilesAsync([path]); RebuildLibrary(); Resolve(row);
            Status = row.Local is not null ? "Added to library." : "The file is saved, but Noctis could not read its audio metadata. Check the file or retry the download.";
        }
        catch (Exception ex) { Status = "DOWNLOAD_IMPORT_FAILED: " + ex.Message; }
    }
    [RelayCommand] private void PlayTrack(Track? track)
    {
        if (track is null || _player is null) return;
        var tracks = Finished.Select(r => r.Local).OfType<Track>().ToList();
        var at = tracks.IndexOf(track);
        if (at >= 0) _player.PlayFromRow(tracks, at);
    }
    [RelayCommand] private void TogglePlayTrack(Track? track)
    {
        if (track?.IsNowPlaying == true) _player?.PlayPauseCommand.Execute(null);
        else PlayTrack(track);
    }
    [RelayCommand] private void ShuffleFinished() => _player?.PlayShuffled(Finished.Select(r => r.Local).OfType<Track>().ToList());
    [RelayCommand] private void ViewAlbum(Track? track) { if (track is not null) _openAlbum?.Invoke(track); }
    [RelayCommand] private void ViewArtist(string? artist) { if (!string.IsNullOrWhiteSpace(artist)) _openArtist?.Invoke(artist); }

    /// <summary>Clear asks first, in the app's confirmation dialog: the running transfer stops.</summary>
    [RelayCommand]
    private async Task Clear()
    {
        if (_queue is null) return;
        var answer = await Views.ConfirmationDialog.ShowAsync(new Views.ConfirmationRequest(
            "Every queued and failed request is removed and the current download stops. Songs already downloaded stay in your library.",
            Title: "Clear downloads?",
            ConfirmText: "Clear",
            Width: 420));
        if (answer.Confirmed) Act(() => _queue.Clear());
    }

    public void Dispose()
    {
        _disposed = true;
        if (_library is not null) _library.LibraryUpdated -= OnLibraryUpdated;
        OnlineMusicSearch.CurrentChanged -= OnSourceChanged;
        Attach(null);
    }
}

/// <summary>One request on the Downloads page.</summary>
public sealed partial class DownloadRow : ObservableObject
{
    public DownloadEntry? Entry { get; private set; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private OnlineDownloadStep _step;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDownloading), nameof(IsQueued), nameof(IsFailed), nameof(IsSaved), nameof(CanMoveUp))]
    private OnlineDownloadState _state;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanMoveUp))] private bool _isNext;
    [ObservableProperty] private bool _isHeld;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsActionable), nameof(IsAddingToLibrary), nameof(DisplayDetail))]
    private Track? _local;
    public bool IsActionable => IsSaved && Local is not null;
    public bool IsAddingToLibrary => IsSaved && Local is null;
    public string DisplayDetail => IsAddingToLibrary ? "Downloaded · not in library yet" : Detail;

    public bool IsDownloading => State == OnlineDownloadState.Downloading;
    public bool IsQueued => State == OnlineDownloadState.Queued;
    public bool IsFailed => State == OnlineDownloadState.Failed;
    public bool IsSaved => State == OnlineDownloadState.Saved;
    /// <summary>"Download next": a queued request not already at the front, or a failed one (retry first).</summary>
    public bool CanMoveUp => IsFailed || (IsQueued && !IsNext);

    internal void Apply(DownloadEntry entry)
    {
        Entry = entry;
        Title = entry.Title;
        Artist = entry.Artist;
        Detail = entry.Detail;
        Progress = entry.Progress;
        Step = entry.Step;
        State = entry.State;
        IsNext = entry.IsNext;
        IsHeld = entry.IsHeld;
        OnPropertyChanged(nameof(IsActionable));
        OnPropertyChanged(nameof(IsAddingToLibrary));
        OnPropertyChanged(nameof(DisplayDetail));
    }
}
