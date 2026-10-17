using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services.Search;

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

    public DownloadsViewModel()
    {
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

    private void OnSourceChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Attach(OnlineMusicSearch.Current?.Downloads));

    private void Attach(IDownloadQueue? queue)
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
        if (_refreshPosted) return;
        _refreshPosted = true;
        Dispatcher.UIThread.Post(() => { _refreshPosted = false; Refresh(); }, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        var entries = _queue?.Snapshot() ?? [];
        var pending = new List<DownloadRow>();
        var finished = new List<DownloadRow>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
        {
            seen.Add(entry.Handle);
            if (!_rows.TryGetValue(entry.Handle, out var row)) _rows[entry.Handle] = row = new DownloadRow();
            row.Apply(entry);
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

    [RelayCommand] private void TogglePause() => Act(() => _queue?.SetPaused(!IsPaused));
    [RelayCommand] private void RetryFailed() => Act(() => _queue?.RetryFailed());
    [RelayCommand] private void StartHeld() => Act(() => _queue?.StartHeld());
    [RelayCommand] private void DownloadNext(DownloadRow? row) { if (row?.Entry is { } e) Act(() => _queue?.DownloadNext(e)); }
    [RelayCommand] private void Remove(DownloadRow? row) { if (row?.Entry is { } e) Act(() => _queue?.Remove(e)); }

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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDownloading), nameof(IsQueued), nameof(IsFailed), nameof(IsSaved), nameof(CanMoveUp))]
    private OnlineDownloadState _state;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanMoveUp))] private bool _isNext;
    [ObservableProperty] private bool _isHeld;

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
        State = entry.State;
        IsNext = entry.IsNext;
        IsHeld = entry.IsHeld;
    }
}
