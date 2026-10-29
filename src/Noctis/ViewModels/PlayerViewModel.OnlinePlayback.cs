using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

public partial class PlayerViewModel
{
    private CancellationTokenSource? _onlinePlay;
    private string? _onlinePreparingSongId;
    private PendingStream? _onlinePreparingRequest;
    private Task? _onlinePlayTask;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingForAudio))] private bool _isPreparingOnlinePlayback;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingForAudio))] private bool _isBuffering;
    public bool IsWaitingForAudio => IsPreparingOnlinePlayback || IsBuffering;
    /// <summary>What the download behind the song being prepared is doing (asking, switching bot, receiving, saving).</summary>
    [ObservableProperty] private OnlineDownloadStep _onlinePreparingStep;
    [ObservableProperty] private double _onlinePreparingProgress;
    [ObservableProperty] private bool _canSeekCurrent = true;
    /// <summary>Owner #204: the share of the song a seek can reach while it still downloads (1: all of it, not streaming).
    /// The seek bar draws it faintly under the played part.</summary>
    [ObservableProperty] private double _seekableFraction = 1;
    [ObservableProperty] private string? _onlinePlayingSongId;

    private Models.Track? _seekableLoggedFor;
    private double _seekableLogged;

    private void RefreshSeekable()
    {
        CanSeekCurrent = _audioPlayer.CanSeek;
        var fraction = _audioPlayer.SeekableUntil is { } until && Duration.TotalSeconds > 0
            ? Math.Clamp(until / Duration, 0, 1) : 1;
        // Once per song and then every 10 % (a song starts at 0, so "the line was 1 before" never fired): the bar's value.
        if (fraction < 1 && CurrentTrack is { } track && (!ReferenceEquals(_seekableLoggedFor, track) || fraction >= _seekableLogged + 0.1))
        {
            _seekableLoggedFor = track; _seekableLogged = fraction;
            Services.DebugLog.Write("Playback", $"download line: {fraction:P0} of \"{track.Title}\" reachable (seek enabled {CanSeekCurrent})");
        }
        SeekableFraction = fraction;
    }

    /// <summary>Owner #177: the playing song is on one of For you's pages, so the lyrics page offers Not for me beside Add.
    /// The For you shelf answers both (set by Search); without it the button never shows.</summary>
    [ObservableProperty] private bool _canNotForMe;
    internal Func<string, bool>? IsForYouSong { get; set; }
    internal Action<string>? DismissForYouSong { get; set; }
    internal void RefreshNotForMe()
    {
        try { CanNotForMe = IsStreamedTrack && OnlinePlayingSongId is { } id && IsForYouSong?.Invoke(id) == true; }
        catch (Exception) { CanNotForMe = false; } // a feature fault must never break track changes
    }
    partial void OnOnlinePlayingSongIdChanged(string? value) => RefreshNotForMe();
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NotForMe()
    {
        if (CanNotForMe && OnlinePlayingSongId is { } id) DismissForYouSong?.Invoke(id);
        RefreshNotForMe();
    }
    private bool _inputWaiting;
    private long _lastOnlineAdvance;
    private TimeSpan _lastOnlinePosition;

    private void UpdateInputWaiting(bool waiting)
    {
        _inputWaiting = waiting;
        UpdateAudibleBuffering(Position);
        RefreshSeekable();
    }
    private void UpdateAudibleBuffering(TimeSpan position)
    {
        var now = Environment.TickCount64;
        if (position > _lastOnlinePosition) _lastOnlineAdvance = now;
        _lastOnlinePosition = position;
        // Decoder read-ahead can wait while buffered PCM is still audibly playing.
        IsBuffering = _reachSeek != null || _inputWaiting && State == Models.PlaybackState.Playing && now - _lastOnlineAdvance >= 1000;
    }

    /// <summary>Owner #213: a seek past the part downloaded so far, waiting for the download to get there.</summary>
    private TimeSpan? _reachSeek;

    /// <summary>
    /// Owner #213 ("it goes there, then comes back"): while a song still downloads, a seek past what has arrived goes
    /// there and waits, like a video: the bar and the lyrics stay on the chosen spot, the song is silent and shows as
    /// buffering, and it plays from there the moment the download reaches it. False when the target is reachable now.
    /// </summary>
    private bool HoldSeekUntilReached(TimeSpan target)
    {
        if (_audioPlayer.SeekableUntil is not { } until || target <= until || target <= TimeSpan.Zero || Duration <= TimeSpan.Zero) return false;
        var first = _reachSeek == null;
        _reachSeek = target;
        _audioPlayer.IsMuted = true;
        Position = target;
        PositionText = FormatTime(target);
        PositionFraction = Math.Clamp(target / Duration, 0, 1);
        RemainingTimeText = FormatTime(Duration - target);
        IsBuffering = true;
        Services.DebugLog.Write("Playback", $"seek past the download: waits at {target.TotalSeconds:0.0} s (reachable {until.TotalSeconds:0.0} s){(first ? "" : ", moved")}");
        Seeked?.Invoke(this, target);
        return true;
    }

    /// <summary>The download reached the waiting spot (or finished): the song plays from there.</summary>
    private void TryFinishReachSeek()
    {
        if (_reachSeek is not { } target) return;
        if (_audioPlayer.SeekableUntil is { } until && target > until) return;
        _reachSeek = null;
        _lastSeekTime = DateTime.UtcNow;
        _lastCommittedSeekTarget = target;
        _audioPlayer.Seek(target);
        _audioPlayer.IsMuted = IsMuted;
        UpdateAudibleBuffering(target);
        Services.DebugLog.Write("Playback", $"seek past the download: reached, playing from {target.TotalSeconds:0.0} s");
        Seeked?.Invoke(this, target);
    }

    /// <summary>Another seek, another song: a waiting seek is dropped and the sound comes back.</summary>
    private void CancelReachSeek()
    {
        if (_reachSeek == null) return;
        _reachSeek = null;
        _audioPlayer.IsMuted = IsMuted;
        UpdateAudibleBuffering(Position);
        Services.DebugLog.Write("Playback", "seek past the download: dropped (another seek or song)");
    }

    /// <summary>Latest Play wins across Search and album pages. Local playback, pause,
    /// skip and shutdown revoke preparation; the durable download continues separately.</summary>
    internal Task PlayOnlineAsync(IOnlineMusicSearch source, OnlineSong song, bool keep = false)
    {
        // Owner #29: more clicks on the song being prepared wait for the same request; only another song restarts it.
        if (_onlinePlay is not null && _onlinePreparingSongId == song.Id && _onlinePlayTask is { } pending) return pending;
        return _onlinePlayTask = PlayOnlineCoreAsync(source, song, keep);
    }

    private async Task PlayOnlineCoreAsync(IOnlineMusicSearch source, OnlineSong song, bool keep)
    {
        CancelOnlinePlayback();
        if (OnlinePlayingSongId == song.Id && CurrentTrack is not null) { PlayPauseCommand.Execute(null); return; }
        var stop = _onlinePlay = new();
        _onlinePreparingSongId = song.Id;
        _onlinePreparingRequest = new(source, song, keep);
        source.SetPlaybackWindow(null, [song]);
        Watch(source);
        WireQueueStreaming();
        IsPreparingOnlinePlayback = true;
        // Owner #54: the Play button shows what the download is doing while the song is prepared.
        void ShowStep()
        {
            if (!ReferenceEquals(_onlinePlay, stop)) return;
            var status = source.StatusOf(song);
            OnlinePreparingStep = status.State == OnlineDownloadState.Downloading ? status.Step : OnlineDownloadStep.None;
            OnlinePreparingProgress = status.Progress;
        }
        void StepChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(ShowStep);
        ShowStep();
        source.DownloadsChanged += StepChanged;
        try
        {
            var track = await source.PreparePlaybackAsync(song, keep, stop.Token);
            if (stop.IsCancellationRequested || !ReferenceEquals(_onlinePlay, stop)) return;
            _onlinePlay = null;
            // Preserve the current queue. This one explicit request replaces its playing
            // song, while Add to downloads continues to be entirely playback-neutral.
            if (CurrentTrack is { } outgoing) { History.Insert(0, outgoing); TrimHistory(); }
            PlayTrack(_library.Tracks.FirstOrDefault(t => t.Id == track.Id) ?? track);
            OnlinePlayingSongId = song.Id;
            PostStreamAhead();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            source.DownloadsChanged -= StepChanged;
            if (ReferenceEquals(_onlinePlay, stop)) _onlinePlay = null;
            if (_onlinePlay is null) { IsPreparingOnlinePlayback = false; _onlinePreparingSongId = null; _onlinePreparingRequest = null; }
            stop.Dispose();
            PostStreamAhead();
        }
    }

    /// <summary>Owner #87: the playing song was only streamed (stream cache, not in Songs): the bar offers Add to songs.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddToSongsTip))] private bool _isStreamedTrack;
    partial void OnIsStreamedTrackChanged(bool value) => RefreshNotForMe();
    /// <summary>File name of a hearted played-only song: its library copy is marked favorite once imported.</summary>
    private string? _favoriteWhenImported;
    /// <summary>Shown briefly after Add, in place of the button.</summary>
    [ObservableProperty] private bool _justAddedToSongs;
    /// <summary>Owner #178: the song the check mark belongs to. Skipping on within its 2.5 s to another streamed song
    /// carried the check over, and it stayed there.</summary>
    private Models.Track? _justAddedFor;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddToSongsTip))] private string _addToSongsMessage = "";
    /// <summary>The button's tooltip: why the last Add failed, else what it does.</summary>
    public string AddToSongsTip => IsStreamedTrack && AddToSongsMessage.Length > 0 && !JustAddedToSongs ? AddToSongsMessage : "Add to songs";

    private bool _watchingAdds;
    private void UpdateStreamedTrack()
    {
        // Owner #88: a song added from anywhere (a playlist, Search) hides the bar's Add for it at once.
        if (!_watchingAdds)
        {
            _watchingAdds = true;
            OnlineMusicSearch.FileSaved += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsStreamedTrack) UpdateStreamedTrack(); });
        }
        try { IsStreamedTrack = CurrentTrack is { IsExternal: true, FilePath: { } path } && OnlineMusicSearch.Current?.IsStreamedFile(path) == true; }
        catch (Exception) { IsStreamedTrack = false; } // a feature fault must never break track changes
        if (!IsStreamedTrack || !ReferenceEquals(CurrentTrack, _justAddedFor)) { JustAddedToSongs = false; _justAddedFor = null; }
        AddToSongsMessage = "";
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task AddToSongs()
    {
        if (CurrentTrack?.FilePath is not { } path || OnlineMusicSearch.Current is not { } source) return;
        var track = CurrentTrack;
        try
        {
            var result = await source.AddStreamedAsync(path, CancellationToken.None);
            if (!ReferenceEquals(CurrentTrack, track)) return;
            AddToSongsMessage = result.Message;
            if (string.IsNullOrWhiteSpace(result.LibraryPath)) return;
            IsStreamedTrack = false;
            _justAddedFor = track;
            JustAddedToSongs = true;
            await Task.Delay(2500);
            if (ReferenceEquals(_justAddedFor, track)) { JustAddedToSongs = false; _justAddedFor = null; }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(CurrentTrack, track)) return;
            // The song stays streamed, so Add can be pressed again (disk full, folder unavailable).
            UpdateStreamedTrack();
            AddToSongsMessage = ex.Message;
        }
    }

    private void CancelOnlinePlayback()
    {
        var stop = _onlinePlay; _onlinePlay = null; _onlinePreparingSongId = null; _onlinePreparingRequest = null;
        stop?.Cancel();
        IsPreparingOnlinePlayback = false;
    }
}
