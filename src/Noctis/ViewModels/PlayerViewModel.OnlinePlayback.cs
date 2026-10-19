using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

public partial class PlayerViewModel
{
    private CancellationTokenSource? _onlinePlay;
    private string? _onlinePreparingSongId;
    private Task? _onlinePlayTask;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingForAudio))] private bool _isPreparingOnlinePlayback;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingForAudio))] private bool _isBuffering;
    public bool IsWaitingForAudio => IsPreparingOnlinePlayback || IsBuffering;
    /// <summary>What the download behind the song being prepared is doing (asking, switching bot, receiving, saving).</summary>
    [ObservableProperty] private OnlineDownloadStep _onlinePreparingStep;
    [ObservableProperty] private double _onlinePreparingProgress;
    [ObservableProperty] private bool _canSeekCurrent = true;
    [ObservableProperty] private string? _onlinePlayingSongId;
    private bool _inputWaiting;
    private long _lastOnlineAdvance;
    private TimeSpan _lastOnlinePosition;

    private void UpdateInputWaiting(bool waiting)
    {
        _inputWaiting = waiting;
        UpdateAudibleBuffering(Position);
        CanSeekCurrent = _audioPlayer.CanSeek;
    }
    private void UpdateAudibleBuffering(TimeSpan position)
    {
        var now = Environment.TickCount64;
        if (position > _lastOnlinePosition) _lastOnlineAdvance = now;
        _lastOnlinePosition = position;
        // Decoder read-ahead can wait while buffered PCM is still audibly playing.
        IsBuffering = _inputWaiting && State == Models.PlaybackState.Playing && now - _lastOnlineAdvance >= 1000;
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
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            source.DownloadsChanged -= StepChanged;
            if (ReferenceEquals(_onlinePlay, stop)) _onlinePlay = null;
            if (_onlinePlay is null) { IsPreparingOnlinePlayback = false; _onlinePreparingSongId = null; }
            stop.Dispose();
        }
    }

    /// <summary>Owner #87: the playing song was only streamed (stream cache, not in Songs): the bar offers Add to songs.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddToSongsTip))] private bool _isStreamedTrack;
    /// <summary>File name of a hearted played-only song: its library copy is marked favorite once imported.</summary>
    private string? _favoriteWhenImported;
    /// <summary>Shown briefly after Add, in place of the button.</summary>
    [ObservableProperty] private bool _justAddedToSongs;
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
        if (!IsStreamedTrack) JustAddedToSongs = false;
        AddToSongsMessage = "";
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task AddToSongs()
    {
        if (CurrentTrack?.FilePath is not { } path || OnlineMusicSearch.Current is not { } source) return;
        var track = CurrentTrack;
        try
        {
            AddToSongsMessage = (await source.AddStreamedAsync(path, CancellationToken.None)).Message;
            if (!ReferenceEquals(CurrentTrack, track)) return;
            IsStreamedTrack = false;
            JustAddedToSongs = true;
            await Task.Delay(2500);
            if (ReferenceEquals(CurrentTrack, track)) JustAddedToSongs = false;
        }
        catch (Exception ex)
        {
            // The song stays streamed, so Add can be pressed again (disk full, folder unavailable).
            UpdateStreamedTrack();
            AddToSongsMessage = ex.Message;
        }
    }

    private void CancelOnlinePlayback()
    {
        var stop = _onlinePlay; _onlinePlay = null; _onlinePreparingSongId = null;
        stop?.Cancel();
        IsPreparingOnlinePlayback = false;
    }
}
