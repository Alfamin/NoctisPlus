using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

/// <summary>What a stand-in in the queue is (owner #95): a playlist song not on this computer yet.</summary>
/// <param name="Page">Owner #100: the page it was played from ("playlist:…", "album:…", "artist:…", "foryou"); its header toggle
/// decides whether that page's songs you don't have play.</param>
public sealed record PendingStream(IOnlineMusicSearch Source, OnlineSong Song, bool Keep, string? Page = null);

/// <summary>
/// Owner #95: playing a playlist plays the songs you don't have too, like a streaming app. Each one stands in the queue as a
/// <see cref="Track"/> with <see cref="Track.PendingStream"/>; the next few are downloaded ahead in queue order (what was
/// added or shuffled, not the playlist's order) and swapped for the real song when saved. One the queue reaches before then is
/// fetched on the spot (it starts as soon as enough has arrived). A stand-in is never the playing song nor saved.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>Owner #116: the next four queue positions count, including owned songs; only the next two positions download
    /// (owner #147: the source's Download ahead choice, 0 to 4; two by default).</summary>
    internal const int StreamAheadPlaces = 4, StreamAheadSongs = 2, AskAheadSongs = 4;

    /// <summary>Settings → Playback, "Play songs you don't have". Off: only the songs you have are queued.</summary>
    [ObservableProperty] private bool _streamMissingSongs = true;

    /// <summary>The stand-in being fetched to play (its song left the queue; the song before it was already put in History).</summary>
    private Track? _preparingStandIn;
    private readonly Dictionary<string, PendingStream> _askedAhead = new(StringComparer.Ordinal);
    private bool _aheadPosted, _streamingWired;
    private IOnlineMusicSearch? _streamingSource;

    /// <summary>A stand-in for a song not on this computer; the same song gives the same id.</summary>
    // ── Owner #100: the header toggle, per page (playlist, album, artist), remembered in the profile ──
    private readonly HashSet<string> _streamOffPages = new(StringComparer.Ordinal);
    /// <summary>The pages whose songs you don't have are left out (their toggle is off).</summary>
    public IReadOnlyCollection<string> StreamOffPages => _streamOffPages;
    public bool StreamsPage(string? page) => StreamMissingSongs && (page is null || !_streamOffPages.Contains(page));
    /// <summary>A stand-in that may stay in the queue now (its page's toggle is on).</summary>
    internal bool Streams(Track track) => track.PendingStream is PendingStream p && StreamsPage(p.Page);
    internal void LoadStreamOffPages(IEnumerable<string>? pages)
    {
        _streamOffPages.Clear();
        if (pages is not null) _streamOffPages.UnionWith(pages.Where(p => !string.IsNullOrWhiteSpace(p)));
        OnPropertyChanged(nameof(StreamOffPages));
    }
    public void SetStreamPage(string page, bool on)
    {
        if (on ? !_streamOffPages.Remove(page) : !_streamOffPages.Add(page)) return;
        OnPropertyChanged(nameof(StreamOffPages));
        if (on) { PostStreamAhead(); return; }
        // Off: that page's songs you don't have leave the queue; one being fetched gives way to the next song.
        var fetching = _preparingStandIn?.PendingStream is PendingStream p && p.Page == page;
        DropStandIns(page);
        if (fetching) { CancelOnlinePlayback(); IsPreparingOnlinePlayback = false; AdvanceQueue(QueueAdvanceReason.Error); }
    }

    public static Track StandIn(IOnlineMusicSearch source, OnlineSong song, bool keep, string? page = null) => new()
    {
        Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("stream:" + song.Id))),
        Title = song.Title,
        Artist = song.ArtistDisplay,
        Album = song.Album,
        Duration = TimeSpan.FromSeconds(song.Seconds is > 0 and <= 86400 ? song.Seconds.Value : 0),
        IsExplicit = song.Explicit == true,
        PendingStream = new PendingStream(source, song, keep, page),
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
        _onlinePreparingRequest = pending;
        pending.Source.SetPlaybackWindow(null, [pending.Song]);
        Watch(pending.Source);
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
        try
        {
            if (!pending.Source.CanDownload) throw new InvalidOperationException("not signed in to the download service");
            var track = await pending.Source.PreparePlaybackAsync(pending.Song, pending.Keep, wait.Token);
            if (stop.IsCancellationRequested || !ReferenceEquals(_onlinePlay, stop)) return;
            _onlinePlay = null;
            _streamFailures = 0;
            var real = _library.Tracks.FirstOrDefault(t => t.Id == track.Id) ?? track;
            if (_resumeTrackId == standIn.Id) _resumeTrackId = real.Id;
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
            if (_onlinePlay is null) { IsPreparingOnlinePlayback = false; _onlinePreparingSongId = null; _onlinePreparingRequest = null; }
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
    private void DropStandIns(string? page = null)
    {
        bool Drop(Track t) => t.PendingStream is PendingStream p && (page is null || p.Page == page);
        for (int i = UpNext.Count - 1; i >= 0; i--)
            if (Drop(UpNext[i])) UpNext.RemoveAt(i);
        _repeatCycleTracks.RemoveAll(Drop);
    }

    partial void OnStreamMissingSongsChanged(bool value)
    {
        if (value) { PostStreamAhead(); return; }
        // Off: only the songs on this computer play; one being fetched gives way to the next of them.
        var fetching = _preparingStandIn != null;
        DropStandIns();
        if (fetching) { CancelOnlinePlayback(); IsPreparingOnlinePlayback = false; AdvanceQueue(QueueAdvanceReason.Error); }
    }

    /// <summary>Stand-ins from the For you shelf (owner #143); no header toggle, so only Play songs you don't have decides.</summary>
    internal const string ForYouPage = "foryou";

    /// <summary>
    /// Owner #143: a For you song plays with the rest of the page shown (only that page) as the queue, from this song on, or
    /// shuffled around it, like a playlist row. False when the song should play alone instead: Play songs you don't have is
    /// off, or it is the song already playing (that click pauses or resumes it).
    /// </summary>
    internal bool PlayOnlinePage(IOnlineMusicSearch source, IReadOnlyList<OnlineSong> songs, int at)
    {
        if (at < 0 || at >= songs.Count || !StreamsPage(ForYouPage) || OnlinePlayingSongId == songs[at].Id) return false;
        PlayFromRow(songs.Select(s => StandIn(source, s, keep: false, ForYouPage)).ToList(), at);
        return true;
    }

    /// <summary>
    /// Owner #144: Not for me on the song playing, or being fetched to play, goes on to the next song (or pauses at the end of
    /// the queue); the song also leaves the songs to come.
    /// </summary>
    internal void SkipDismissed(OnlineSong song)
    {
        bool Same(Track t) => t.PendingStream is PendingStream p && p.Song.Id == song.Id;
        for (int i = UpNext.Count - 1; i >= 0; i--)
            if (Same(UpNext[i])) UpNext.RemoveAt(i);
        _repeatCycleTracks.RemoveAll(Same);
        var fetching = _onlinePreparingSongId == song.Id;
        if (OnlinePlayingSongId != song.Id && !fetching) return;
        if (CanGoNext()) { NextCommand.Execute(null); return; }
        if (fetching) { CancelOnlinePlayback(); IsPreparingOnlinePlayback = false; return; }
        if (State == PlaybackState.Playing) { _audioPlayer.Pause(); State = PlaybackState.Paused; }
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
    // ── Owner #102: the next songs you don't have show their cover in the queue (small, a few KB each) ──
    internal const int QueueCoverSongs = 30;
    private const int QueueCoverLoads = QueueCoverSongs; // owner #104: all at once (small, one HTTP/2 connection)
    private readonly HashSet<Track> _coverLoading = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Track> _noCover = new(ReferenceEqualityComparer.Instance);

    private void LoadQueueCovers()
    {
        var waiting = new List<Track>();
        foreach (var t in UpNext.Take(QueueCoverSongs))
        {
            if (t.PendingStream is not PendingStream p || t.HasAlbumArt || _coverLoading.Contains(t) || _noCover.Contains(t)) continue;
            // One already on disk (the playlist page showed it) at once; the rest are fetched.
            if (p.Source.CachedQueueCover(p.Song) is { } cached) t.ShowArtwork(cached);
            else waiting.Add(t);
        }
        foreach (var t in waiting.Take(Math.Max(0, QueueCoverLoads - _coverLoading.Count)))
        {
            var p = (PendingStream)t.PendingStream!;
            _coverLoading.Add(t);
            _ = Task.Run(async () =>
            {
                string? path = null;
                try { path = await p.Source.QueueCoverAsync(p.Song, CancellationToken.None); }
                catch (Exception ex) { Services.DebugLog.Write("Queue", $"cover of \"{p.Song.Title}\" not loaded: {ex.Message}"); }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (path is not null) t.ShowArtwork(path);
                    else _noCover.Add(t); // none: not asked again this session
                    _coverLoading.Remove(t);
                    if (waiting.Count > QueueCoverLoads) PostStreamAhead(); // the next ones
                }, Avalonia.Threading.DispatcherPriority.Background);
            });
        }
    }

    private void StreamAhead()
    {
        try { LoadQueueCovers(); }
        catch (Exception ex) { Services.DebugLog.Write("Queue", "queue covers skipped: " + ex.Message); }
        var head = UpNext.Take(StreamAheadPlaces).ToList();
        // Count queue positions BEFORE filtering missing songs. Owned/cached songs never extend either window.
        var fetch = new List<(int At, PendingStream Song)>();
        var ask = new List<PendingStream>();
        for (int i = 0; i < head.Count; i++)
        {
            if (head[i].PendingStream is not PendingStream pending) continue;
            Watch(pending.Source);
            var state = pending.Source.StatusOf(pending.Song).State;
            if (state == OnlineDownloadState.Saved)
            {
                // Saved: once the library has imported it, the stand-in becomes the real song.
                if (pending.Source.SavedPathOf(pending.Song) is { } path &&
                    _library.Tracks.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } local)
                { SwapStandIn(head[i], local); }
                continue;
            }
            // In the stream cache (plays at once), or neither bot had it (skipped when reached): nothing more to do.
            if (state is OnlineDownloadState.Streamed or OnlineDownloadState.Failed) continue;
            if (i < pending.Source.DownloadAheadSongs) fetch.Add((i, pending));
            else ask.Add(pending);
        }
        // Owner #111: right after a big queue change (a list played, shuffle on or off, a reshuffle, a True Shuffle mode)
        // nothing ahead is asked for or downloaded until the queue has stayed as it is for a while: a playlist played
        // and shuffled seconds later fetched two queues. The song that plays is fetched at once all the same.
        if (DateTime.UtcNow < _settleUntil) { fetch.Clear(); ask.Clear(); }
        // The download layer owns the authoritative window, including requests left
        // by earlier clicks (which may never have entered _askedAhead).
        if (_streamingSource is { } source)
        {
            var songs = fetch.Where(p => ReferenceEquals(p.Song.Source, source)).Select(p => p.Song.Song).ToList();
            if (_onlinePreparingRequest is { } preparing && ReferenceEquals(preparing.Source, source)) songs.Add(preparing.Song);
            source.SetPlaybackWindow(_onlinePreparingRequest is null ? CurrentTrack?.FilePath : null, songs);
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
        // Live: asking shares the two bot lanes with downloading; a download waited 8 s behind asks, a song neither bot had
        // held a lane for a minute. So asking runs only while the next two need nothing (an ask in flight gives way).
        AskAhead(fetch.Count > 0 ? [] : ask);
        // Owner 2026-10-20: the song clicked to play is asked for first and alone (the bots answer one song at a time; an
        // ahead download sent alongside it was asked first: "Photograph" clicked, "The Cut Off" requested). They follow once it plays.
        if (_preparingStandIn is not null) return;
        foreach (var (_, pending) in fetch)
        {
            var status = pending.Source.StatusOf(pending.Song);
            if (status.State is OnlineDownloadState.Downloading or OnlineDownloadState.Queued)
            { _askedAhead.TryAdd(pending.Song.Id, pending); continue; }
            if (!pending.Source.CanDownload || !_askedAhead.TryAdd(pending.Song.Id, pending)) continue;
            // Part of playing, like Play itself: the fast lanes (two at once), also while bulk downloads are paused (live:
            // a paused queue held the 2nd and 3rd song back).
            _ = Task.Run(async () =>
            {
                try { await pending.Source.PrefetchAsync(pending.Song, pending.Keep, CancellationToken.None); }
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

    // ── Owner #111: a new queue settles before anything ahead is fetched ──
    /// <summary>How long a new queue must stay unchanged before the songs ahead are asked for or downloaded.</summary>
    internal TimeSpan QueueSettle { get; set; } = TimeSpan.FromSeconds(10);
    private DateTime _settleUntil;
    internal bool QueueIsSettling => DateTime.UtcNow < _settleUntil;
    private int _settleGeneration;

    /// <summary>A new queue (played, shuffled, unshuffled, reshuffled): the songs ahead wait <see cref="QueueSettle"/>; each
    /// further big change starts the wait again.</summary>
    private void QueueReplaced()
    {
        if (QueueSettle <= TimeSpan.Zero) return;
        _settleUntil = DateTime.UtcNow + QueueSettle;
        var generation = ++_settleGeneration;
        _ = Task.Delay(QueueSettle).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generation != _settleGeneration) return;
            _settleUntil = default;
            PostStreamAhead();
        }));
    }

    /// <summary>Next, a song ending, Previous or a jump in the queue: the songs ahead move at once (owner #100), also
    /// during a wait.</summary>
    private void QueueMoved()
    {
        if (_settleUntil == default) return;
        _settleUntil = default; _settleGeneration++;
        PostStreamAhead();
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

/// <summary>Owner #100: the page header toggle's words (its tooltip shows at once on hover).</summary>
public static class StreamToggle
{
    public static string Tip(bool on) => on
        ? "Songs you don't have play too · fetched as the queue reaches them. Click: only yours"
        : "Only your songs play. Click: play the songs you don't have too";
}
