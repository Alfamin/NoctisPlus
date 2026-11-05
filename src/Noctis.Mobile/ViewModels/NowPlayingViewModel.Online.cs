using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;

namespace Noctis.Mobile.ViewModels;

public sealed partial class NowPlayingViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPause))] private bool _isPreparingOnline;
    public bool ShowPause => IsPlaying || IsPreparingOnline && _onlinePlayWhenReady;
    private CancellationTokenSource? _onlinePrepare, _onlineAhead;
    private bool _onlinePlayWhenReady;
    private bool _onlineSessionActivated, _onlineReadyPaused;
    private bool _onlineRequestStarted;
    private string _onlineWindow = "";
    private DateTime _onlineSettleUntil;
    private readonly HashSet<IOnlineMusicSearch> _onlineSources = new(ReferenceEqualityComparer.Instance);
    private bool _onlineNetworkWatching;
    internal Func<TimeSpan, CancellationToken, Task> OnlineDelay { get; set; } = Task.Delay;
    internal Task OnlinePreparation { get; private set; } = Task.CompletedTask;
    internal Task OnlineAheadWork { get; private set; } = Task.CompletedTask;
    public IOnlineMusicSearch? OnlineSearch { get; set; }

    public void PlayOnlineTracks(IOnlineMusicSearch source, IReadOnlyList<OnlineSong> songs, int index, string? label = null)
    {
        if (index < 0 || index >= songs.Count) return;
        if (IsPreparingOnlineSong(source, songs[index].Id)) return; // repeated tap, not a second bot request
        var tracks = songs.Select(s => s.LocalTrackId is { } id && _library.GetTrackById(id) is { IsFileMissing: false } local
            ? local : OnlineStandIn.Create(source, s, keep: false, label)).ToArray();
        PlayTracks(tracks, index, label);
    }

    internal bool IsPreparingOnlineSong(IOnlineMusicSearch source, string id) => IsPreparingOnline
        && CurrentTrack?.PendingStream is Noctis.ViewModels.PendingStream p && ReferenceEquals(p.Source, source) && p.Song.Id == id;

    internal void CancelOnlineSong(IOnlineMusicSearch source, string id)
    { if (IsPreparingOnlineSong(source, id)) CancelOnlinePrepare(); }

    internal void RemoveOnlineRecommendation(IOnlineMusicSearch source, OnlineSong song, bool artist)
    {
        static string Primary(string value) => value.Split(',')[0].Trim();
        bool Matches(Track t) => t.PendingStream is Noctis.ViewModels.PendingStream p && p.Page == "For You"
            && ReferenceEquals(p.Source, source) && (p.Song.Id == song.Id || artist &&
                string.Equals(Primary(p.Song.ArtistDisplay), Primary(song.ArtistDisplay), StringComparison.OrdinalIgnoreCase));
        var currentRemoved = _queue.RemoveWhere(Matches);
        _shuffleSource = _shuffleSource.Where(t => !Matches(t)).ToArray();
        if (currentRemoved) { CancelOnlinePrepare(); _player.Stop(); Next(); }
        QueueChanged();
    }

    private void CancelOnlinePrepare()
    {
        _onlinePrepare?.Cancel(); _onlinePrepare = null;
        _onlineRequestStarted = false;
        IsPreparingOnline = false;
    }

    private void SettleOnlineQueue()
    { _onlineSettleUntil = DateTime.UtcNow.AddSeconds(10); _onlineWindow = ""; }

    private void StartTrack(Track track, TimeSpan? fromPosition)
    {
        CancelOnlinePrepare();
        _onlineSessionActivated = true; _onlineReadyPaused = false;
        if (track.PendingStream is not Noctis.ViewModels.PendingStream pending) { StartResolvedTrack(track, fromPosition); return; }
        if (CompletedOnlinePath(pending) is { } saved)
        { track.FilePath = saved; track.IsExternal = true; StartResolvedTrack(track, fromPosition); return; }
        _onlinePlayWhenReady = true; IsPreparingOnline = true;
        _player.Stop(); // a pending URI is never handed to the decoder
        _unrecordedPlay = null;
        CurrentTrack = track; Duration = track.Duration; Position = fromPosition ?? TimeSpan.Zero;
        ErrorText = ""; IsPlaying = false; IsPreparingOnline = true; _onlinePlayWhenReady = true;
        var stop = _onlinePrepare = new();
        SyncUpNext(); PrepareUpcoming(); SaveStateNow();
        OnlinePreparation = PrepareOnlineAsync(track, pending, fromPosition, stop);
    }

    private async Task PrepareOnlineAsync(Track track, Noctis.ViewModels.PendingStream pending, TimeSpan? position, CancellationTokenSource stop)
    {
        try
        {
            var ready = await PrepareWithRecoveryAsync(pending, stop);
            _marshal(() =>
            {
                if (_disposed || stop.IsCancellationRequested || !ReferenceEquals(stop, _onlinePrepare)
                    || !ReferenceEquals(track, _queue.Current)) return;
                track.FilePath = ready.FilePath; track.IsExternal = ready.IsExternal;
                if (!string.IsNullOrEmpty(ready.AlbumArtworkPath)) track.AlbumArtworkPath = ready.AlbumArtworkPath;
                if (ready.Duration > TimeSpan.Zero) track.Duration = ready.Duration;
                IsPreparingOnline = false;
                if (_onlinePlayWhenReady) StartResolvedTrack(track, position);
                else { _onlineReadyPaused = true; Duration = track.Duration; OnPropertyChanged(nameof(CurrentTrack)); SaveStateNow(); }
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _marshal(() =>
            {
                if (_disposed || stop.IsCancellationRequested || !ReferenceEquals(stop, _onlinePrepare)) return;
                IsPreparingOnline = false; IsPlaying = false;
                _onlineSessionActivated = false; UpdateOnlineWindow();
                ErrorText = OnlineResultsViewModel.Short(ex); // keep the queue; a network error is not an EOF
            });
        }
        finally
        {
            _marshal(() => { if (ReferenceEquals(_onlinePrepare, stop)) _onlinePrepare = null; stop.Dispose(); });
        }
    }

    private async Task<Track> PrepareWithRecoveryAsync(Noctis.ViewModels.PendingStream pending, CancellationTokenSource stop)
    {
        while (true)
        {
            await _onlineAccess.WaitAsync(stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (!pending.Source.CanDownload) throw new InvalidOperationException("Sign in to Telegram to play this song");
            try
            {
                var prepared = pending.Source.PreparePlaybackAsync(pending.Song, pending.Keep, stop.Token);
                _onlineRequestStarted = true; // the current request precedes ahead work
                _marshal(() => { if (ReferenceEquals(stop, _onlinePrepare)) UpdateOnlineWindow(); });
                // A source may finish after a skip; observe late faults, without owning its download.
                _ = prepared.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return await prepared.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException ex) when (!stop.IsCancellationRequested &&
                (ex is OfflineModeException || _onlineAccess.IsOffline))
            {
                _onlineRequestStarted = false;
                // Keep the selected track and pause intent. A quick off/on can finish before
                // cancellation arrives, so wait briefly if the gate has already reopened.
                if (!_onlineAccess.IsOffline) await Task.Delay(250, stop.Token);
            }
        }
    }

    private static string? CompletedOnlinePath(Noctis.ViewModels.PendingStream pending)
    {
        var path = pending.Source.CompletedPlaybackPath(pending.Song);
        return path is not null && File.Exists(path) ? path : null;
    }

    private void OnOnlineModeChanged(bool offline) => _marshal(UpdateOnlineWindow);
    private void OnOnlineDownloadsChanged(object? sender, EventArgs e) => _marshal(() =>
    { if (!_disposed) PrepareUpcoming(); });

    private void UpdateOnlineWindow()
    {
        if (_disposed) return;
        var current = _queue.Current?.PendingStream as Noctis.ViewModels.PendingStream;
        var head = _queue.UpNext.Take(4).ToArray(); // positions BEFORE filtering owned/cached songs
        var pending = head.Select(t => t.PendingStream).OfType<Noctis.ViewModels.PendingStream>();
        foreach (var source in pending.Select(p => p.Source).Concat(current is null ? [] : [current.Source]))
            if (_onlineSources.Add(source)) source.DownloadsChanged += OnOnlineDownloadsChanged;
        if (_onlineSources.Count > 0 && !_onlineNetworkWatching)
        { _onlineNetworkWatching = true; _onlineAccess.Changed += OnOnlineModeChanged; }
        if (_onlineSources.Count == 0) return; // no extra work/subscriptions for ordinary local queues
        var key = string.Join("|", head.Prepend(_queue.Current).Select(t => t is null ? "" :
            t.Id + ":" + (t.PendingStream is Noctis.ViewModels.PendingStream p ? RuntimeHelpers.GetHashCode(p.Source) : 0)))
            + ":" + _onlineAccess.IsOffline + ":" + _onlineRequestStarted + ":" + _onlineSettleUntil.Ticks;
        if (key == _onlineWindow) return;
        _onlineWindow = key;
        _onlineAhead?.Cancel();
        _onlineAhead?.Dispose();
        var stop = _onlineAhead = new();
        var settling = !_onlineSessionActivated || DateTime.UtcNow < _onlineSettleUntil;
        foreach (var source in _onlineSources)
        {
            var wanted = new List<OnlineSong>();
            if (current is not null && ReferenceEquals(current.Source, source)) wanted.Add(current.Song);
            if (!_onlineAccess.IsOffline && !settling)
                wanted.AddRange(head.Take(Math.Clamp(source.DownloadAheadSongs, 0, 4))
                    .Select(t => t.PendingStream).OfType<Noctis.ViewModels.PendingStream>()
                    .Where(p => ReferenceEquals(p.Source, source)).Select(p => p.Song));
            try { source.SetPlaybackWindow(_queue.Current?.FilePath, wanted); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("Queue", $"Online window unavailable: {ex.GetType().Name}"); }
        }
        OnlineAheadWork = AheadAsync(key, head, stop);
    }

    private async Task AheadAsync(string key, Track[] head, CancellationTokenSource stop)
    {
        try
        {
            var ct = stop.Token;
            if (!_onlineSessionActivated || _onlineAccess.IsOffline || head.Length == 0
                || IsPreparingOnline && !_onlineRequestStarted) return;
            var remaining = _onlineSettleUntil - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero) await OnlineDelay(remaining, ct);
            if (_disposed || stop.IsCancellationRequested || key != _onlineWindow || _onlineAccess.IsOffline) return;
            // Publish the full audio window after settling, without another timer or duplicate asks.
            foreach (var source in _onlineSources)
            {
                var wanted = head.Take(Math.Clamp(source.DownloadAheadSongs, 0, 4)).Select(t => t.PendingStream)
                    .OfType<Noctis.ViewModels.PendingStream>().Where(p => ReferenceEquals(p.Source, source)).Select(p => p.Song).ToList();
                if (_queue.Current?.PendingStream is Noctis.ViewModels.PendingStream current && ReferenceEquals(current.Source, source)) wanted.Insert(0, current.Song);
                source.SetPlaybackWindow(_queue.Current?.FilePath, wanted);
            }
            for (var i = 0; i < head.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (head[i].PendingStream is not Noctis.ViewModels.PendingStream p || !p.Source.CanDownload || CompletedOnlinePath(p) is not null
                    || p.Source.StatusOf(p.Song).State == OnlineDownloadState.Failed) continue;
                if (i < Math.Clamp(p.Source.DownloadAheadSongs, 0, 4)) await p.Source.PrefetchAsync(p.Song, p.Keep, ct);
                else await p.Source.AskAheadAsync(p.Song, ct);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("Queue", $"Ahead work deferred: {ex.GetType().Name}"); }
        // The owning window cancels and disposes this CTS. The engine owns file lifetimes.
    }

    private IEnumerable<Track> OnlineQueueTracks() => _queue.UpNext.Concat(_queue.History).Concat(_shuffleSource)
        .Concat(_queue.Current is { } current ? [current] : []).DistinctBy(t => t.Id);

    private Dictionary<Guid, MobileOnlineQueueSong>? OnlineSnapshot()
    {
        if (_onlineSources.Count == 0) return null;
        var tracks = OnlineQueueTracks().Where(t => t.PendingStream is Noctis.ViewModels.PendingStream).ToDictionary(t => t.Id,
            t => { var p = (Noctis.ViewModels.PendingStream)t.PendingStream!; return new MobileOnlineQueueSong(p.Song, p.Keep, p.Page); });
        return tracks.Count > 0 ? tracks : null;
    }

    private void DisposeOnlineQueue()
    {
        CancelOnlinePrepare(); _onlineAhead?.Cancel(); _onlineAhead?.Dispose();
        _onlineAccess.Changed -= OnOnlineModeChanged;
        foreach (var source in _onlineSources)
        {
            source.DownloadsChanged -= OnOnlineDownloadsChanged;
            try { source.SetPlaybackWindow(null, []); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("Queue", $"Window cleanup unavailable: {ex.GetType().Name}"); }
        }
        _onlineSources.Clear();
    }
}
