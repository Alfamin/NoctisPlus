using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

/// <summary>What a stand-in in the queue is (owner #95): a playlist song not on this computer yet.</summary>
public sealed record PendingStream(IOnlineMusicSearch Source, OnlineSong Song, bool Keep);

/// <summary>
/// Owner #95: playing a playlist plays the songs you don't have too, like a streaming app. Each one stands in the queue as a
/// <see cref="Track"/> with <see cref="Track.PendingStream"/>; the next few are downloaded ahead in queue order (what was
/// added or shuffled, not the playlist's order) and swapped for the real song when saved. One the queue reaches before then is
/// fetched on the spot (it starts as soon as enough has arrived). A stand-in is never the playing song nor saved.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>Owner #100: the next <see cref="AskAheadSongs"/> stand-ins (within <see cref="StreamAheadPlaces"/> queue places)
    /// are made ready in the bot's chat; only the first <see cref="StreamAheadSongs"/> of them are downloaded.</summary>
    internal const int StreamAheadPlaces = 10, StreamAheadSongs = 2, AskAheadSongs = 7;

    /// <summary>Settings → Playback, "Play songs you don't have". Off: only the songs you have are queued.</summary>
    [ObservableProperty] private bool _streamMissingSongs = true;

    /// <summary>The stand-in being fetched to play (its song left the queue; the song before it was already put in History).</summary>
    private Track? _preparingStandIn;
    private readonly Dictionary<string, PendingStream> _askedAhead = new(StringComparer.Ordinal);
    private bool _aheadPosted, _streamingWired;
    private IOnlineMusicSearch? _streamingSource;

    /// <summary>A stand-in for a song not on this computer; the same song gives the same id.</summary>
    public static Track StandIn(IOnlineMusicSearch source, OnlineSong song, bool keep) => new()
    {
        Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("stream:" + song.Id))),
        Title = song.Title,
        Artist = song.ArtistDisplay,
        Album = song.Album,
        Duration = TimeSpan.FromSeconds(song.Seconds is > 0 and <= 86400 ? song.Seconds.Value : 0),
        IsExplicit = song.Explicit == true,
        PendingStream = new PendingStream(source, song, keep),
    };

    private void PlayStandIn(Track standIn, PendingStream pending)
    {
        WireQueueStreaming();
        _ = PlayStandInAsync(standIn, pending);
    }

    private async Task PlayStandInAsync(Track standIn, PendingStream pending)
    {
        CancelOnlinePlayback();
        var stop = _onlinePlay = new();
        _onlinePreparingSongId = pending.Song.Id;
        _preparingStandIn = standIn;
        IsPreparingOnlinePlayback = true;
        void ShowStep()
        {
            if (!ReferenceEquals(_onlinePlay, stop)) return;
            var status = pending.Source.StatusOf(pending.Song);
            OnlinePreparingStep = status.State == OnlineDownloadState.Downloading ? status.Step : OnlineDownloadStep.None;
            OnlinePreparingProgress = status.Progress;
        }
        void StepChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(ShowStep);
        ShowStep();
        pending.Source.DownloadsChanged += StepChanged;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        wait.CancelAfter(StandInWait); // the download itself goes on and lands in Songs
        try
        {
            if (!pending.Source.CanDownload) throw new InvalidOperationException("not signed in to the download service");
            var track = await pending.Source.PreparePlaybackAsync(pending.Song, pending.Keep, wait.Token);
            if (stop.IsCancellationRequested || !ReferenceEquals(_onlinePlay, stop)) return;
            _onlinePlay = null;
            _streamFailures = 0;
            var real = _library.Tracks.FirstOrDefault(t => t.Id == track.Id) ?? track;
            SwapStandIn(standIn, real);
            PlayTrack(real);
            OnlinePlayingSongId = pending.Song.Id;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Skipped past (not put back as next): its download is dropped if it has not started, so skipping quickly
            // through many songs does not leave a pile of downloads behind.
            if (!UpNext.Contains(standIn)) pending.Source.CancelQueued(pending.Song);
        }
        catch (Exception ex)
        {
            // Neither bot had it, no connection, or it took too long: the queue goes on with the next song, as a missing
            // file would. Not signed in, or three in a row: the other songs to fetch would fail the same way and are left out.
            Services.DebugLog.Write("Queue", $"stream of \"{pending.Song.Title}\" failed: {(ex is OperationCanceledException ? "took too long" : ex.Message)}");
            if (ReferenceEquals(_onlinePlay, stop))
            {
                _onlinePlay = null;
                IsPreparingOnlinePlayback = false;
                if (!pending.Source.CanDownload || ++_streamFailures >= 3)
                {
                    DropStandIns();
                    _streamFailures = 0;
                    if (!pending.Source.CanDownload && !_askedSignIn) { _askedSignIn = true; pending.Source.RequestSignIn(); }
                }
                // On a later turn: a failure right away happens inside the queue's own advance, which ignores a nested
                // one. Only if nothing else started meanwhile; the song before is already in History.
                var before = CurrentTrack;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_onlinePlay is not null || !ReferenceEquals(CurrentTrack, before) || _preparingStandIn is not null) return;
                    _preparingStandIn = standIn;
                    try { AdvanceQueue(QueueAdvanceReason.Error); }
                    finally { if (ReferenceEquals(_preparingStandIn, standIn)) _preparingStandIn = null; }
                });
            }
        }
        finally
        {
            pending.Source.DownloadsChanged -= StepChanged;
            if (ReferenceEquals(_onlinePlay, stop)) _onlinePlay = null;
            if (_onlinePlay is null) { IsPreparingOnlinePlayback = false; _onlinePreparingSongId = null; }
            // Whatever ended this fetch (played, failed at the queue's end, or another Play took over), it no longer holds
            // the queue; a newer stand-in being fetched is left alone.
            if (ReferenceEquals(_preparingStandIn, standIn)) _preparingStandIn = null;
            stop.Dispose();
            PostStreamAhead(); // owner #100: asking ahead waited for this song; it goes on now
        }
    }

    /// <summary>The longest a song still to be fetched holds the queue before the next song plays instead.</summary>
    internal static TimeSpan StandInWait = TimeSpan.FromSeconds(60);
    private int _streamFailures;
    private bool _askedSignIn;

    /// <summary>Pause or Previous while the next song is still being fetched: stop fetching and put it back as next (the
    /// song before it comes off History again). False when nothing was being fetched.</summary>
    private bool PutBackStandIn()
    {
        if (_preparingStandIn is not { } standIn) return false;
        _preparingStandIn = null;
        CancelOnlinePlayback();
        if (CurrentTrack != null && History.Count > 0 && ReferenceEquals(History[0], CurrentTrack))
        {
            History.RemoveAt(0);
            if (_queueHistoryDepth > 0) _queueHistoryDepth--;
        }
        UpNext.Insert(0, standIn);
        return true;
    }

    /// <summary>The fetched song takes its stand-in's place everywhere the queue keeps songs (up next, the repeat-all
    /// cycle, the order Shuffle off returns to), so a second pass plays the file instead of asking again.</summary>
    private void SwapStandIn(Track standIn, Track real)
    {
        var at = UpNext.IndexOf(standIn);
        if (at >= 0) UpNext[at] = real;
        for (int i = 0; i < _repeatCycleTracks.Count; i++) if (ReferenceEquals(_repeatCycleTracks[i], standIn)) _repeatCycleTracks[i] = real;
        for (int i = 0; i < _originalQueue.Count; i++) if (ReferenceEquals(_originalQueue[i], standIn)) _originalQueue[i] = real;
    }

    /// <summary>Every song still to be fetched leaves the queue (the toggle went off, or fetching cannot work now).</summary>
    private void DropStandIns()
    {
        for (int i = UpNext.Count - 1; i >= 0; i--)
            if (UpNext[i].IsPendingStream) UpNext.RemoveAt(i);
        _repeatCycleTracks.RemoveAll(t => t.IsPendingStream);
    }

    partial void OnStreamMissingSongsChanged(bool value)
    {
        if (value) { PostStreamAhead(); return; }
        // Off: only the songs on this computer play; one being fetched gives way to the next of them.
        var fetching = _preparingStandIn != null;
        DropStandIns();
        if (fetching) { CancelOnlinePlayback(); IsPreparingOnlinePlayback = false; AdvanceQueue(QueueAdvanceReason.Error); }
    }

    private void WireQueueStreaming()
    {
        if (_streamingWired) return;
        _streamingWired = true;
        UpNext.CollectionChanged += (_, _) => PostStreamAhead();
        // A saved song is imported a moment after its download ends.
        _library.LibraryUpdated += (_, _) => PostStreamAhead();
        PostStreamAhead();
    }

    private void PostStreamAhead()
    {
        if (_aheadPosted) return;
        _aheadPosted = true;
        // Owner #100: right after the change that caused it (a whole queue replace is one pass), not when the app is idle.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _aheadPosted = false;
            // Downloading ahead is a nicety: whatever goes wrong in it never touches playback.
            try { StreamAhead(); }
            catch (Exception ex) { Services.DebugLog.Write("Queue", "download ahead skipped: " + ex.Message); }
        }, Avalonia.Threading.DispatcherPriority.Normal);
    }

    // Owner #100: asks ahead, one at a time (the bots answer one song at a time), nearest first.
    private readonly HashSet<string> _askedSongs = new(StringComparer.Ordinal);
    private (PendingStream Song, CancellationTokenSource Stop)? _asking;

    /// <summary>The next stand-ins after the downloaded ones are asked for, so the bot has sent them into the chat by the
    /// time they are downloaded. One no longer among them stops being asked (the bot may still answer; that is kept).</summary>
    private void AskAhead(List<PendingStream> wanted)
    {
        if (_asking is { } current && (!wanted.Any(p => p.Song.Id == current.Song.Song.Id) || _preparingStandIn is not null))
        {
            current.Stop.Cancel(); // the song to play now goes first (it needs the bots too)
            _asking = null;
        }
        if (_asking is not null || _preparingStandIn is not null) return;
        if (wanted.FirstOrDefault(p => !_askedSongs.Contains(p.Song.Id) && p.Source.CanDownload
                && p.Source.StatusOf(p.Song).State == OnlineDownloadState.None) is not { } next) return;
        var stop = new CancellationTokenSource();
        _asking = (next, stop);
        _ = Task.Run(async () =>
        {
            var retry = false;
            try { await next.Source.AskAheadAsync(next.Song, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { retry = true; }
            catch (Exception ex)
            {
                // Neither bot has it, or a limit: it is downloaded (and skipped if it fails) as the queue reaches it.
                Services.DebugLog.Write("Queue", $"asking ahead for \"{next.Song.Title}\" failed: {ex.Message}");
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!retry) _askedSongs.Add(next.Song.Id);
                if (_asking is { } now && ReferenceEquals(now.Stop, stop)) _asking = null;
                stop.Dispose();
                PostStreamAhead();
            });
        });
    }

    /// <summary>The next stand-ins (within <see cref="StreamAheadPlaces"/>) are downloaded in queue order, the first one in
    /// the fast lane; saved ones are swapped for the real song so crossfade and gapless treat them like any other.</summary>
    private void StreamAhead()
    {
        var head = UpNext.Take(StreamAheadPlaces).ToList();
        // Owner #100: the next two stand-ins still to be saved are downloaded; the next ones after them, up to seven in all,
        // are only asked for (ready in the bot's chat).
        var fetch = new List<(int At, PendingStream Song)>();
        var ask = new List<PendingStream>();
        for (int i = 0; i < head.Count; i++)
        {
            if (head[i].PendingStream is not PendingStream pending) continue;
            Watch(pending.Source);
            if (pending.Source.StatusOf(pending.Song).State == OnlineDownloadState.Saved)
            {
                // Saved: once the library has imported it, the stand-in becomes the real song.
                if (pending.Source.SavedPathOf(pending.Song) is { } path &&
                    _library.Tracks.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } local)
                    SwapStandIn(head[i], local);
                continue;
            }
            if (fetch.Count + ask.Count >= AskAheadSongs) break;
            if (pending.Keep && fetch.Count < StreamAheadSongs) fetch.Add((i, pending));
            else ask.Add(pending);
        }
        // Owner #95/#100: the queue was skipped past, cleared, reshuffled, edited or replaced: a download ahead that is no
        // longer among the next two is stopped, even mid-transfer (not the song being heard or fetched to play).
        if (_askedAhead.Count > 0)
        {
            var near = fetch.Select(f => f.Song.Song.Id).ToHashSet(StringComparer.Ordinal);
            if (_preparingStandIn?.PendingStream is PendingStream fetching) near.Add(fetching.Song.Id);
            foreach (var (id, gone) in _askedAhead.Where(a => !near.Contains(a.Key)).ToList())
            {
                _askedAhead.Remove(id);
                gone.Source.CancelQueued(gone.Song);
            }
        }
        AskAhead(ask);
        foreach (var (_, pending) in fetch)
        {
            var status = pending.Source.StatusOf(pending.Song);
            if (status.State is OnlineDownloadState.Downloading or OnlineDownloadState.Queued) continue;
            if (!pending.Source.CanDownload || !_askedAhead.TryAdd(pending.Song.Id, pending)) continue;
            // Part of playing, like Play itself: the fast lanes (two at once), also while bulk downloads are paused (live:
            // a paused queue held the 2nd and 3rd song back).
            const bool now = true;
            _ = Task.Run(async () =>
            {
                try { await pending.Source.DownloadAsync([pending.Song], now, CancellationToken.None); }
                catch (Exception ex)
                {
                    // Live: refused while the library was still scanning. Not asked after all: asked again shortly.
                    Services.DebugLog.Write("Queue", $"download ahead of \"{pending.Song.Title}\" failed, trying again shortly: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => { _askedAhead.Remove(pending.Song.Id); PostStreamAhead(); });
                }
            });
        }
    }

    private void Watch(IOnlineMusicSearch source)
    {
        if (ReferenceEquals(_streamingSource, source)) return;
        if (_streamingSource is not null) _streamingSource.DownloadsChanged -= OnStreamDownloadsChanged;
        _streamingSource = source;
        source.DownloadsChanged += OnStreamDownloadsChanged;
    }

    private void OnStreamDownloadsChanged(object? sender, EventArgs e) => PostStreamAhead();
}
