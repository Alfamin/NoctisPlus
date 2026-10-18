using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

public partial class PlayerViewModel
{
    private CancellationTokenSource? _onlinePlay;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingForAudio))] private bool _isPreparingOnlinePlayback;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingForAudio))] private bool _isBuffering;
    public bool IsWaitingForAudio => IsPreparingOnlinePlayback || IsBuffering;
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
    internal async Task PlayOnlineAsync(IOnlineMusicSearch source, OnlineSong song)
    {
        CancelOnlinePlayback();
        if (OnlinePlayingSongId == song.Id && CurrentTrack is not null) { PlayPauseCommand.Execute(null); return; }
        var stop = _onlinePlay = new();
        IsPreparingOnlinePlayback = true;
        try
        {
            var track = await source.PreparePlaybackAsync(song, stop.Token);
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
            if (ReferenceEquals(_onlinePlay, stop)) _onlinePlay = null;
            if (_onlinePlay is null) IsPreparingOnlinePlayback = false;
            stop.Dispose();
        }
    }

    private void CancelOnlinePlayback()
    {
        var stop = _onlinePlay; _onlinePlay = null;
        stop?.Cancel();
        IsPreparingOnlinePlayback = false;
    }
}
