namespace FreeMusicFinder;

// Keep the existing numeric values for saved 2.5 queues.
internal enum DownloadState { Queued, Running, Saved, AlreadyThere, Failed, Cancelled, Waiting, Removed }

/// <summary>A transfer has stages as well as bytes: a completed MP3 may still await a FLAC answer.</summary>
internal interface IDownloadProgress : IProgress<double>
{
    void Stage(string text, Noctis.Services.Search.OnlineDownloadStep step);
}

internal sealed class Download(BotTrack track, string folder)
{
    public BotTrack Track { get; } = track;
    public string Folder { get; } = folder;
    public DownloadState State { get; internal set; }
    public double Progress { get; internal set; }
    public string Stage { get; internal set; } = "";
    /// <summary>What the running transfer is doing, for the step icons (owner #54).</summary>
    public Noctis.Services.Search.OnlineDownloadStep Step { get; internal set; }
    public string Error { get; internal set; } = "";
    public DateTimeOffset? RetryAt { get; internal set; }
    public bool Priority { get; internal set; }
    public bool Held { get; internal set; }
    public string? SavedPath { get; internal set; }
    /// <summary>When this run failed (this session only): a quick repeat click shows the error instead of asking the bots again.</summary>
    internal DateTimeOffset? FailedAt { get; set; }
    internal Noctis.Features.ProgressivePlayback.ProgressiveTransfer? Playback { get; set; }
    /// <summary>Held back for the song clicked to play after it (owner #83); it goes back to the queue, not to Failed.</summary>
    internal bool Preempted { get; set; }
    /// <summary>Owner #156: when the running transfer last showed a sign of life (a stage or bytes).</summary>
    internal DateTimeOffset LastActivity { get; set; }
    /// <summary>Stopped by the stall watchdog; it goes back to the queue. <see cref="Stalls"/> counts this session's stops.</summary>
    internal bool Stalled { get; set; }
    internal int Stalls { get; set; }
    /// <summary>Cancelled by the watchdog alone: only then is a transfer that ignores its cancel left behind.</summary>
    internal CancellationTokenSource? StallStop { get; set; }
    internal bool FinishIntoCache { get; set; }
    /// <summary>Owner #87: a streamed song the user added to Songs: its library folder (copied there once saved).</summary>
    public string? KeepIn { get; internal set; }
    public bool IsActive => State is DownloadState.Queued or DownloadState.Running or DownloadState.Waiting;
}

/// <summary>
/// One Telegram transfer at a time, plus a second lane for Play / download-now requests (owner #79: SICKO MODE waited
/// a whole other song's 40 s transfer before any bot was asked), with an editable durable backlog.
/// </summary>
internal sealed class Downloads : IDisposable
{
    internal const int Lanes = MusicSources.FetchLanes;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<Download> _active = new(), _known = new();
    private readonly HashSet<string> _tidied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;
    private readonly string? _store;
    private bool _paused, _saveFailing;
    private Download? _last, _now;
    private readonly Dictionary<Download, CancellationTokenSource> _running = new();
    private int _done, _saved;
    private bool _disposed;
    public bool Paused { get { lock (_gate) return _paused; } }
    public IReadOnlyList<Download> History { get { lock (_gate) return _known.ToArray(); } }
    public IReadOnlyList<Download> Active { get { lock (_gate) return _active.ToArray(); } }
    public event Action<Download>? Changed;
    public event Action<string>? Finished;
    /// <summary>Owner #156: a transfer with no sign of life this long (no stage, no bytes) is stopped and queued again, so one
    /// stuck song cannot hold the queue; the bots' own waits (a busy bot, a short flood wait) stay well inside it.</summary>
    internal static TimeSpan StallLimit = TimeSpan.FromMinutes(5);
    /// <summary>A song that stalls this often in a session is marked failed instead of being queued again.</summary>
    internal const int MaxStalls = 3;
    /// <summary>How soon a stalled song is tried again.</summary>
    internal static TimeSpan StallRetry = TimeSpan.FromSeconds(10);
    /// <summary>Drops the Telegram connection (the login is kept), so a request that hangs on it ends and frees its lane.</summary>
    internal Func<Task>? ResetConnection { get; set; }
    /// <summary>Owner #160: while the backlog downloads one song, the bots are asked for the next ones (as for streaming): their
    /// files wait in the chat, so each later song only has to be collected. One ask at a time; a Play or download-now request
    /// stops the ask in flight, and songs fetched for playback are left to the player's own asking ahead.</summary>
    internal const int AheadSongs = 2;
    /// <summary>Asks the bots for a song without transferring it (<see cref="AskOnlyStream"/>).</summary>
    internal Func<Download, CancellationToken, Task>? AskAhead { get; set; }
    private readonly HashSet<Download> _askedAhead = new();
    private Task _askingAhead = Task.CompletedTask;
    private CancellationTokenSource? _askStop;

    public Downloads(Action<string> log, string? store = null, Func<MusicRequest, BotTrack>? restore = null, string? cacheFolder = null)
    {
        _log = log; _store = store; CacheFolder = cacheFolder;
        if (store is not null && File.Exists(store))
        {
            if (new FileInfo(store).Length > 16 * 1024 * 1024 || (File.GetAttributes(store) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("QUEUE_UNREADABLE: saved queue is too large or redirected.");
            QueueFile file;
            try
            {
                // Atomic checkpoint replacement can overlap this read while an old worker exits. Read one stable
                // handle, allowing its writer/rename to finish instead of failing Noctis plugin startup.
                using var input = new FileStream(store, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(input); // preserve the previous BOM/legacy encoding handling
                file = System.Text.Json.JsonSerializer.Deserialize<QueueFile>(reader.ReadToEnd()) ?? throw new InvalidDataException();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            { throw new InvalidDataException("QUEUE_UNREADABLE: keep the saved queue for diagnosis; it was not replaced.", ex); }
            if (file.Version != 1 || file.Items is null || file.Items.Length > 10000 || restore is null)
                throw new InvalidDataException("QUEUE_UNREADABLE: unsupported saved queue.");
            _paused = file.Paused;
            foreach (var item in file.Items)
            {
                if (!Path.IsPathFullyQualified(item.Folder) || item.Request is null || string.IsNullOrWhiteSpace(item.Request.Title) ||
                    string.IsNullOrWhiteSpace(item.Request.Artist) || item.Request.Title.Length > 1000 || item.Request.Artist.Length > 1000 ||
                    !Enum.IsDefined(item.State) || item.RetryAt > DateTimeOffset.UtcNow.AddDays(8))
                    throw new InvalidDataException("QUEUE_UNREADABLE: invalid track or folder.");
                if (item.State == DownloadState.Removed) continue;
                // Resume explicit saves after restart, not the previous session's
                // speculative audio window. Its partial cache remains reusable.
                bool parked = cacheFolder is not null && item.KeepIn is null && StreamCache.Contains(cacheFolder, Path.Combine(item.Folder, "x")) &&
                    item.State is not (DownloadState.Saved or DownloadState.AlreadyThere or DownloadState.Failed);
                var download = new Download(restore(item.Request), item.Folder)
                    { State = item.State, Error = item.Error, RetryAt = item.RetryAt, Priority = item.Priority, Held = item.Held, KeepIn = item.KeepIn };
                _known.Add(download);
                if (parked) { download.State = DownloadState.Cancelled; download.Priority = false; continue; }
                if (item.State is DownloadState.Saved or DownloadState.AlreadyThere)
                {
                    download.SavedPath = Downloader.FindExisting(download.Folder, download.Track.FileName);
                    continue;
                }
                if (item.State == DownloadState.Failed) continue;
                download.State = item.RetryAt > DateTimeOffset.UtcNow ? DownloadState.Waiting : DownloadState.Queued;
                _active.Add(download);
            }
            var ordered=_active.OrderByDescending(d=>d.Priority).ToArray();
            _active.Clear();_active.AddRange(ordered);
        }
        Noctis.Services.OnlineAccess.Shared.Changed += NetworkModeChanged;
        Noctis.Services.OnlineAccess.Shared.Recovered += NetworkRecovered;
        Noctis.Services.OnlineAccess.Shared.RetryHint += NetworkRecovered;
        _ = Task.Run(RunAsync);
    }

    private sealed record QueueItem(MusicRequest Request, string Folder, DownloadState State, string Error, DateTimeOffset? RetryAt, bool Priority = false, bool Held = false, string? KeepIn = null);
    private sealed record QueueFile(int Version, bool Paused, QueueItem[] Items);
    private void Save()
    {
        // Dispose checkpoints synchronously before relinquishing ownership of the file.
        // A cancelled worker may finish much later; it must never replace a newer queue.
        if (_disposed) return;
        var active = _active.ToHashSet();
        if (_store is not null) AtomicJson.Write(_store, new QueueFile(1, _paused,
            _active.Concat(_known.Where(d => !active.Contains(d))).Where(d => d.Track.Request is not null)
                .Select(d => new QueueItem(d.Track.Request!, d.Folder, d.State, d.Error, d.RetryAt, d.Priority, d.Held, d.KeepIn)).ToArray()));
    }
    /// <summary>
    /// The worker's own saves. One that fails (the file held open a moment by another program) must not stop the
    /// worker: the queue in memory stays as it is and the next save writes it again.
    /// </summary>
    private void SaveOrLog()
    {
        try { Save(); _saveFailing = false; }
        catch (Exception ex)
        {
            if (!_saveFailing) _log("QUEUE_SAVE_FAILED: " + ex.Message + " Downloads go on; the queue is saved again after the next change.");
            _saveFailing = true;
        }
    }
    // Whether a request would be started now: songs staged from Playlists wait for Start, and a pause holds the batch.
    private bool Runnable(Download d) => !Noctis.Services.OnlineAccess.Shared.IsOffline && !d.Held && (!_paused || d.Priority);
    private void NetworkModeChanged(bool offline)
    {
        CancellationTokenSource[] cancel = [];
        lock (_gate)
            if (offline)
            {
                foreach (var d in _running.Keys) d.Preempted = true;
                cancel = _running.Values.ToArray();
            }
        foreach (var source in cancel) Cancel(source);
        if (!offline) NetworkRecovered();
        Wake();
    }
    private void NetworkRecovered()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (Noctis.Services.OnlineAccess.Shared.IsOffline) return;
            foreach (var d in _active.Where(d => d.State == DownloadState.Waiting &&
                (d.Error.StartsWith("TELEGRAM_CONNECTION_FAILED", StringComparison.Ordinal) || d.Error.StartsWith("Connection interrupted", StringComparison.Ordinal))))
            { d.RetryAt = null; d.State = DownloadState.Queued; }
            SaveOrLog();
        }
        Wake();
    }
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    public void SetPaused(bool paused)
    {
        lock (_gate) { var old = _paused; _paused = paused; try { Save(); } catch { _paused = old; throw; } }
        Wake();
    }
    public void RetryFailed()
    {
        Download[] retry;
        lock (_gate)
        {
            retry = _known.Where(d => d.State == DownloadState.Failed).ToArray();
            foreach (var item in retry) { item.State = DownloadState.Queued; Insert(item); }
            try { Save(); } catch { foreach (var item in retry) { item.State = DownloadState.Failed; _active.Remove(item); } throw; }
        }
        Wake(); foreach (var item in retry) Raise(item);
    }
    public Download? Find(string fileName, string folder)
    { folder = Path.GetFullPath(folder); lock (_gate) return _active.FirstOrDefault(d => Same(d, fileName, folder)); }

    public Download Start(BotTrack track, string folder, bool priority = false, bool held = false)
    {
        folder = Path.GetFullPath(folder);
        Download download;
        lock (_gate)
        {
            if (CacheFolder is { } cache && StreamCache.Contains(cache, Path.Combine(folder, "x")) && !Wanted(track.FileName))
                return new Download(track, folder) { State = DownloadState.Removed };
            download = _active.FirstOrDefault(d => Same(d, track.FileName, folder))!;
            if (download is not null)
            {
                if (priority && !download.Priority) PromoteLocked(download);
            }
            else
            {
                download = new Download(track, folder) { Priority = priority, Held = held && !priority };
                if (_disposed || _stop.IsCancellationRequested) { download.State = DownloadState.Cancelled; return download; }
                if (_known.Count >= 10000) throw new InvalidOperationException("QUEUE_FULL: clear finished history or remove requests before adding more than 10,000 tracks.");
                _known.Add(download); Insert(download);
                try { Save(); } catch { _active.Remove(download); _known.Remove(download); throw; }
            }
        }
        Wake(); Raise(download); return download;
    }
    private void Insert(Download item)
    {
        int index = item.Priority ? _active.FindIndex(d => !d.Priority && !_running.ContainsKey(d)) : -1;
        if (index < 0) _active.Add(item); else _active.Insert(index, item);
    }
    private void PromoteLocked(Download item)
    {
        int index = _active.IndexOf(item); var held = item.Held; item.Priority = true; item.Held = false;
        _active.Remove(item); Insert(item);
        try { Save(); } catch { item.Priority = false; item.Held = held; _active.Remove(item); _active.Insert(index, item); throw; }
    }
    public int ReleaseHeld()
    {
        Download[] held;
        lock (_gate)
        {
            held = _active.Where(d => d.Held).ToArray();
            foreach (var item in held) item.Held = false;
            try { Save(); } catch { foreach (var item in held) item.Held = true; throw; }
        }
        Wake(); foreach (var item in held) Raise(item); return held.Length;
    }
    public void DownloadNext(Download item)
    {
        lock (_gate)
        {
            if (_known.Contains(item) && item.State==DownloadState.Failed)
            {
                var priority=item.Priority;item.Priority=true;item.State=DownloadState.Queued;Insert(item);
                try {Save();}catch{item.State=DownloadState.Failed;item.Priority=priority;_active.Remove(item);throw;}
            }
            if (!_active.Contains(item)) return;
            if (!item.Priority) PromoteLocked(item);
        }
        Wake(); Raise(item);
    }
    /// <summary>Owner #87: where played-only songs are saved (outside the library). Null: every song goes where asked.</summary>
    public string? CacheFolder { get; set; }
    internal bool IsCached(Download d) => CacheFolder is { } cache && StreamCache.Contains(cache, Path.Combine(d.Folder, "x"));

    private HashSet<string>? _playbackWindow;
    private static string AudioName(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".flac"
        ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
    private bool Wanted(string fileName) => _playbackWindow is null || _playbackWindow.Contains(AudioName(fileName));

    // A playback request is disposable; an explicit Download/Add is not. This runs under
    // the same lock as Start so late prefetch completions cannot resurrect an old window.
    internal void SetPlaybackWindow(IEnumerable<string> names)
    {
        Download[] gone;
        var cancel = new List<CancellationTokenSource>();
        lock (_gate)
        {
            var window = names.Select(AudioName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_playbackWindow?.SetEquals(window) == true) return;
            _playbackWindow = window;
            foreach (var d in _active.Where(d => Wanted(d.Track.FileName))) d.FinishIntoCache = false;
            gone = _active.Where(d => IsCached(d) && d.KeepIn is null && !Wanted(d.Track.FileName)).ToArray();
            foreach (var d in gone)
            {
                if (d.State == DownloadState.Running && d.Progress >= .85)
                { d.FinishIntoCache = true; continue; }
                _active.Remove(d); d.State = DownloadState.Cancelled; d.Preempted = false;
                if (_running.TryGetValue(d, out var running)) cancel.Add(running);
            }
            SaveOrLog();
        }
        foreach (var running in cancel) Cancel(running);
        Wake(); foreach (var d in gone) Raise(d);
    }

    /// <summary>
    /// Owner #87: "Add to songs" for a streamed song. A saved one is copied into <paramref name="libraryFolder"/> now
    /// (returns its library path); one still downloading is copied as soon as it is saved (returns Pending).
    /// </summary>
    public (string? Path, bool Pending) Keep(Download item, string libraryFolder)
    {
        libraryFolder = Path.GetFullPath(libraryFolder);
        string? saved = null;
        lock (_gate)
        {
            if (item.State is DownloadState.Saved or DownloadState.AlreadyThere) saved = item.SavedPath;
            else if (item.IsActive)
            {
                item.KeepIn = libraryFolder;
                SaveOrLog();
                return (null, true);
            }
        }
        if (saved is null || !File.Exists(saved)) return (null, false);
        // A failed copy (disk full, no permission) leaves the song streamed, so Add can be pressed again.
        var copy = CopyKept(saved, libraryFolder) ?? throw new IOException("The song could not be copied into " + libraryFolder + ". Check free space and the download folder; it is still in the stream cache.");
        lock (_gate) { item.KeepIn = libraryFolder; SaveOrLog(); }
        Raise(item);
        return (copy, false);
    }

    private string? CopyKept(string saved, string libraryFolder)
    {
        try
        {
            var copy = StreamCache.CopyToLibrary(saved, libraryFolder);
            Noctis.Services.Search.OnlineMusicSearch.PublishSavedFile(copy);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"could not add \"{Path.GetFileName(saved)}\" to the library: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Owner #83: the song clicked to play last is wanted now. It goes first and starts at once; the other transfers are
    /// held back to the queue until it is done (they resume from the bot's chat, the bot is not asked again), except
    /// the one still being heard, so it does not stop while the new one arrives.
    /// </summary>
    public void PlayNow(Download item)
    {
        CancellationTokenSource[] hold;
        lock (_gate)
        {
            if (!_active.Contains(item)) return;
            item.Priority = true; item.Held = false;
            _active.Remove(item); _active.Insert(0, item); _now = item;
            var others = _running.Where(r => r.Key != item && !r.Key.FinishIntoCache && r.Key.Playback?.HasReaders != true).ToArray();
            foreach (var (other, _) in others) other.Preempted = true;
            hold = others.Select(r => r.Value).ToArray();
            SaveOrLog();
        }
        foreach (var source in hold) Cancel(source);
        Wake(); Raise(item);
    }
    public bool Remove(Download item)
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            int known = _known.IndexOf(item), active = _active.IndexOf(item);
            if (known < 0) return false;
            _known.Remove(item); _active.Remove(item);
            try { Save(); } catch { _known.Insert(known, item); if (active >= 0) _active.Insert(active, item); throw; }
            if (item.IsActive || item.State is DownloadState.Failed or DownloadState.Cancelled) item.State = DownloadState.Removed;
            cancel = _running.GetValueOrDefault(item);
        }
        Cancel(cancel); Wake(); Raise(item); return true;
    }
    public void Clear()
    {
        Download[] old; Download[] active; CancellationTokenSource[] cancel;
        lock (_gate)
        {
            old = _known.ToArray(); active = _active.ToArray(); _known.Clear(); _active.Clear();
            try { Save(); } catch { _known.AddRange(old); _active.AddRange(active); throw; }
            foreach (var item in old.Where(d => d.IsActive || d.State is DownloadState.Failed or DownloadState.Cancelled)) item.State = DownloadState.Removed;
            _done = _saved = 0; _last = null; cancel = _running.Values.ToArray();
        }
        foreach (var source in cancel) Cancel(source);
        Wake(); foreach (var item in old) Raise(item);
    }
    private static void Cancel(CancellationTokenSource? source) { try { source?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            SaveOrLog(); // Running/queued requests are already resumable on restore.
            _disposed = true;
        }
        _stop.Cancel();
        Noctis.Services.OnlineAccess.Shared.Changed -= NetworkModeChanged;
        Noctis.Services.OnlineAccess.Shared.Recovered -= NetworkRecovered;
        Noctis.Services.OnlineAccess.Shared.RetryHint -= NetworkRecovered;
        Wake();
    }
    private static bool Same(Download d, string name, string folder) =>
        string.Equals(d.Track.FileName, name, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Folder, folder, StringComparison.OrdinalIgnoreCase);

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Download? download; CancellationTokenSource? transfer = null; var tidy = false;
                TimeSpan delay = TimeSpan.FromMinutes(1);
                lock (_gate)
                {
                    if (_disposed) break;
                    var eligible = _active.Where(d => !_running.ContainsKey(d) && Runnable(d)).ToArray();
                    // A song that has to wait (a bot limit) or was removed does not hold the others back.
                    if (_now is { } now && (!_active.Contains(now) || !Runnable(now) || now.RetryAt > DateTimeOffset.UtcNow)) _now = null;
                    // The song clicked to play last starts at once, even while held-back transfers are still stopping;
                    // nothing else starts until it is done. Otherwise the second lane takes only Play / download-now
                    // requests and the backlog keeps to one.
                    download = _now is { } wanted
                        ? eligible.FirstOrDefault(d => d == wanted && (d.RetryAt is null || d.RetryAt <= DateTimeOffset.UtcNow))
                        : eligible.FirstOrDefault(d => (d.RetryAt is null || d.RetryAt <= DateTimeOffset.UtcNow) &&
                            (_running.Count == 0 || _running.Count < Lanes && d.Priority));
                    if (download is not null)
                    {
                        transfer = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        _running[download] = transfer; tidy = _tidied.Add(download.Folder);
                        download.State = DownloadState.Running; download.Error = ""; download.Stage = ""; download.Step = Noctis.Services.Search.OnlineDownloadStep.Asking; download.Progress = 0; download.RetryAt = null;
                        download.LastActivity = DateTimeOffset.UtcNow; download.Stalled = false; download.StallStop = new(); SaveOrLog();
                    }
                    else if (eligible.Any(d => d.RetryAt > DateTimeOffset.UtcNow))
                        delay = TimeSpan.FromMilliseconds(Math.Clamp((eligible.Where(d => d.RetryAt is not null).Min(d => d.RetryAt)!.Value - DateTimeOffset.UtcNow).TotalMilliseconds, 10, 60000));
                    if (_running.Count > 0) { StopStalled(); delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, Math.Clamp(StallLimit.TotalMilliseconds / 4, 50, 30_000))); }
                }
                if (download is null) { await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false); continue; }
                // A Play or download-now request takes the bots first; otherwise the next songs are asked for meanwhile.
                if (download.Priority) Cancel(_askStop); else PostAskAhead();
                // Before this folder's first transfer starts, so the other lane's partial file is never taken for a leftover.
                if (tidy && Downloader.CleanLeftovers(download.Folder) is > 0 and var removed)
                    _log($"removed {removed} unfinished downloads left in {download.Folder}");
                _ = RunLaneAsync(download, transfer!);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _log("QUEUE_SAVE_FAILED: " + ex.Message + " Pending requests remain in the last saved queue."); }
        lock (_gate)
        {
            // Shutdown is resumable. User removals are omitted from the saved file entirely.
            foreach (var item in _active) item.State = DownloadState.Cancelled;
            try { Save(); } catch (Exception ex) { _log("QUEUE_SAVE_FAILED: " + ex.Message); }
            _active.Clear();
        }
    }
    private void PostAskAhead()
    {
        if (AskAhead is not { } ask) return;
        lock (_gate)
        {
            if (!_askingAhead.IsCompleted || _disposed) return;
            _askingAhead = Task.Run(() => AskAheadAsync(ask));
        }
    }

    /// <summary>Owner #160: the next <see cref="AheadSongs"/> backlog songs not yet asked for, in queue order, one at a time.</summary>
    private async Task AskAheadAsync(Func<Download, CancellationToken, Task> ask)
    {
        for (var turn = 0; turn < AheadSongs && !_stop.IsCancellationRequested; turn++)
        {
            Download? next; CancellationTokenSource stop;
            lock (_gate)
            {
                if (_disposed || _active.Any(d => d.Priority && (d.State == DownloadState.Running || Runnable(d)))) return;
                _askedAhead.RemoveWhere(d => !_active.Contains(d));
                next = _active.Where(d => !_running.ContainsKey(d) && Runnable(d) && !d.Priority && !IsCached(d)
                        && (d.RetryAt is null || d.RetryAt <= DateTimeOffset.UtcNow))
                    .Take(AheadSongs).FirstOrDefault(d => !_askedAhead.Contains(d));
                if (next is null) return;
                _askedAhead.Add(next);
                stop = _askStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                stop.CancelAfter(TimeSpan.FromSeconds(45));
            }
            try { await ask(next, stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (!_stop.IsCancellationRequested)
            {
                // Nothing lost: the song is asked for again when its turn comes.
                _log($"asking ahead for \"{next.Track.Title}\" stopped: {(ex is OperationCanceledException ? "a played song went first" : ex.Message)}");
                if (ex is OperationCanceledException) return;
            }
            finally { lock (_gate) { if (ReferenceEquals(_askStop, stop)) _askStop = null; } stop.Dispose(); }
        }
    }

    /// <summary>Owner #156: under the queue lock. Each stalled transfer is cancelled (it requeues itself) and the connection is
    /// reset once, so a request hanging on it ends.</summary>
    private void StopStalled()
    {
        var reset = false;
        foreach (var (download, transfer) in _running)
        {
            if (download.Stalled || DateTimeOffset.UtcNow - download.LastActivity < StallLimit) continue;
            download.Stalled = true; reset = true;
            _log($"\"{download.Track.Title}\" showed no progress for {StallLimit.TotalMinutes:0} minutes; restarting it and the Telegram connection.");
            Cancel(transfer); Cancel(download.StallStop);
        }
        if (reset && ResetConnection is { } resetConnection)
            _ = Task.Run(async () => { try { await resetConnection().ConfigureAwait(false); } catch (Exception ex) { _log("Telegram connection reset failed: " + ex.Message); } });
    }

    private async Task RunLaneAsync(Download download, CancellationTokenSource transfer)
    {
        try
        {
            try { await RunOneAsync(download, transfer.Token).ConfigureAwait(false); }
            finally { lock (_gate) { _running.Remove(download); download.StallStop?.Dispose(); download.StallStop = null; } transfer.Dispose(); }
            string? summary = null;
            lock (_gate)
            {
                if (_now == download && download.State != DownloadState.Queued) _now = null;
                if (_known.Contains(download))
                {
                    if (download.FinishIntoCache && download.State is DownloadState.Waiting or DownloadState.Queued)
                    {
                        _known.Remove(download); _active.Remove(download); download.State = DownloadState.Removed;
                    }
                    if (download.State is not (DownloadState.Waiting or DownloadState.Queued))
                    {
                        _active.Remove(download);
                        // Playing fills the cache, not Songs: it is not a library-save notice.
                        if (!IsCached(download) || download.KeepIn is not null)
                        {
                            _done++;
                            if (download.State == DownloadState.Saved) _saved++;
                            if (download.State != DownloadState.Cancelled) _last = download;
                        }
                        // Songs waiting for Start or behind a pause do not keep the finished ones from being reported.
                        if (!_active.Any(Runnable)) { summary = Summary(); _done = _saved = 0; _last = null; }
                    }
                    SaveOrLog();
                }
            }
            Raise(download);
            if (summary is not null && !_stop.IsCancellationRequested) RaiseFinished(summary);
        }
        catch (Exception ex) { _log("QUEUE_SAVE_FAILED: " + ex.Message + " Pending requests remain in the last saved queue."); }
        finally { Wake(); }
    }
    private async Task RunOneAsync(Download download, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            Raise(download);
            download.Playback = new(download.Folder, download.Track.FileName, download.Track.Duration);
            // Only a stall lets go of a transfer that ignores its cancel (a request hanging on a dead connection), so the lane is
            // freed; other cancels (a song held back for Play) still wait for the transfer to stop, as before.
            var outcome = await Downloader.DownloadAsync(download.Track, download.Folder, new Report(p =>
            { download.LastActivity = DateTimeOffset.UtcNow; download.Progress = double.IsFinite(p) ? Math.Clamp(p, 0, .99) : 0; download.Stage = ""; if (p > 0) download.Step = Noctis.Services.Search.OnlineDownloadStep.Receiving; Raise(download); },
                (stage, step) => { download.LastActivity = DateTimeOffset.UtcNow; download.Stage = stage; download.Step = step; download.Progress = 0; Raise(download); }), ct, download.Playback)
                .WaitAsync(download.StallStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            var savedPath = Downloader.FindExisting(download.Folder, download.Track.FileName);
            lock (_gate)
            {
                if (download.State != DownloadState.Removed)
                {
                    download.SavedPath = savedPath;
                    download.Stage = "";
                    download.Step = Noctis.Services.Search.OnlineDownloadStep.None;
                    download.Progress = 1;
                    download.State = outcome == DownloadOutcome.Saved ? DownloadState.Saved : DownloadState.AlreadyThere;
                }
            }
            // Owner #87: a streamed song stays in the cache, out of the library, unless the user added it meanwhile.
            // Owner #93: only a close match supplied it: the song is kept and marked for the user to check.
            var unsure = download.Track.Request is { } wanted && CheckThese.TakeFetched(wanted);
            if (savedPath is not null && IsCached(download))
            {
                if (download.KeepIn is { } library)
                {
                    if (CopyKept(savedPath, library) is not { } kept) lock (_gate) download.KeepIn = null;
                    else if (unsure) CheckThese.Add(kept, download.Track.Request!);
                }
            }
            else if (outcome == DownloadOutcome.Saved && savedPath is not null)
            {
                if (unsure) CheckThese.Add(savedPath, download.Track.Request!);
                Noctis.Services.Search.OnlineMusicSearch.PublishSavedFile(savedPath);
            }
        }
        // Whatever the stopped transfer threw: a held-back song goes back to the queue.
        catch (Exception) when (ct.IsCancellationRequested && download.Preempted && !_stop.IsCancellationRequested)
        { SetOutcome(download, DownloadState.Queued, ""); lock (_gate) { download.Preempted = false; download.Progress = 0; } }
        // Owner #156: stopped by the stall watchdog: queued again in a moment, or failed after MaxStalls.
        catch (Exception) when (ct.IsCancellationRequested && download.Stalled && !_stop.IsCancellationRequested)
        {
            int stalls; lock (_gate) { stalls = ++download.Stalls; download.Stalled = false; download.Progress = 0; }
            if (stalls >= MaxStalls) SetOutcome(download, DownloadState.Failed, $"No progress for {StallLimit.TotalMinutes:0} minutes, {stalls} times. Try it again later.");
            else SetOutcome(download, DownloadState.Queued, "", DateTimeOffset.UtcNow + StallRetry);
        }
        catch (SourcesWaitingException ex) { SetOutcome(download, DownloadState.Waiting, ex.Message, ex.Until); }
        catch (IOException ex) { SetOutcome(download, DownloadState.Waiting, "Connection or storage interrupted: " + ex.Message, DateTimeOffset.UtcNow.AddMinutes(10)); }
        catch (System.Net.Sockets.SocketException ex) { Noctis.Services.OnlineAccess.Shared.Failed(); SetOutcome(download, DownloadState.Waiting, "TELEGRAM_CONNECTION_FAILED: " + ex.Message, DateTimeOffset.UtcNow.AddSeconds(5)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetOutcome(download, DownloadState.Cancelled, ""); }
        catch (Exception ex)
        {
            SetOutcome(download, DownloadState.Failed, ex is OperationCanceledException ? "The download was interrupted." : ex.Message);
            if (download.State == DownloadState.Failed) _log($"download of \"{download.Track.Title}\" failed: {download.Error}");
        }
    }
    private void SetOutcome(Download d, DownloadState state, string error, DateTimeOffset? retry = null)
    { lock (_gate) { if (d.State == DownloadState.Removed || d.State == DownloadState.Cancelled && !_active.Contains(d)) return; d.State = state; d.Error = error; d.RetryAt = retry; d.Step = Noctis.Services.Search.OnlineDownloadStep.None; d.FailedAt = state == DownloadState.Failed ? DateTimeOffset.UtcNow : null; } }
    private string? Summary()
    {
        if (_last is null) return null;
        if (_done > 1) return _saved == _done ? "Saved to library" : $"Saved {_saved} of {_done} songs";
        return _last.State switch
        {
            DownloadState.Saved => "Saved to library",
            DownloadState.Failed => $"Could not download \"{_last.Track.Title}\": {_last.Error}", _ => null,
        };
    }
    private void Raise(Download d) { try { Changed?.Invoke(d); } catch (Exception ex) { _log("download listener failed: " + ex.Message); } }
    private void RaiseFinished(string text) { try { Finished?.Invoke(text); } catch (Exception ex) { _log("download listener failed: " + ex.Message); } }
    private sealed class Report(Action<double> report, Action<string, Noctis.Services.Search.OnlineDownloadStep> stage) : IDownloadProgress
    {
        void IProgress<double>.Report(double value) => report(value);
        public void Stage(string text, Noctis.Services.Search.OnlineDownloadStep step) => stage(text, step);
    }
}
