namespace FreeMusicFinder;

// Keep the existing numeric values for saved 2.5 queues.
internal enum DownloadState { Queued, Running, Saved, AlreadyThere, Failed, Cancelled, Waiting, Removed }

internal sealed class Download(BotTrack track, string folder)
{
    public BotTrack Track { get; } = track;
    public string Folder { get; } = folder;
    public DownloadState State { get; internal set; }
    public double Progress { get; internal set; }
    public string Error { get; internal set; } = "";
    public DateTimeOffset? RetryAt { get; internal set; }
    public bool Priority { get; internal set; }
    public bool Held { get; internal set; }
    public string? SavedPath { get; internal set; }
    public bool IsActive => State is DownloadState.Queued or DownloadState.Running or DownloadState.Waiting;
}

/// <summary>One Telegram transfer at a time, with a separate priority lane and editable durable backlog.</summary>
internal sealed class Downloads : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<Download> _active = new(), _known = new();
    private readonly HashSet<string> _tidied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;
    private readonly string? _store;
    private bool _paused, _saveFailing;
    private Download? _current, _last;
    private CancellationTokenSource? _currentStop;
    private int _done, _saved;
    private bool _disposed;
    public bool Paused { get { lock (_gate) return _paused; } }
    public IReadOnlyList<Download> History { get { lock (_gate) return _known.ToArray(); } }
    public IReadOnlyList<Download> Active { get { lock (_gate) return _active.ToArray(); } }
    public event Action<Download>? Changed;
    public event Action<string>? Finished;

    public Downloads(Action<string> log, string? store = null, Func<MusicRequest, BotTrack>? restore = null)
    {
        _log = log; _store = store;
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
                var download = new Download(restore(item.Request), item.Folder)
                    { State = item.State, Error = item.Error, RetryAt = item.RetryAt, Priority = item.Priority, Held = item.Held };
                _known.Add(download);
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
        _ = Task.Run(RunAsync);
    }

    private sealed record QueueItem(MusicRequest Request, string Folder, DownloadState State, string Error, DateTimeOffset? RetryAt, bool Priority = false, bool Held = false);
    private sealed record QueueFile(int Version, bool Paused, QueueItem[] Items);
    private void Save()
    {
        // Dispose checkpoints synchronously before relinquishing ownership of the file.
        // A cancelled worker may finish much later; it must never replace a newer queue.
        if (_disposed) return;
        var active = _active.ToHashSet();
        if (_store is not null) AtomicJson.Write(_store, new QueueFile(1, _paused,
            _active.Concat(_known.Where(d => !active.Contains(d))).Where(d => d.Track.Request is not null)
                .Select(d => new QueueItem(d.Track.Request!, d.Folder, d.State, d.Error, d.RetryAt, d.Priority, d.Held)).ToArray()));
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
    private bool Runnable(Download d) => !d.Held && (!_paused || d.Priority);
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
        int index = item.Priority ? _active.FindIndex(d => !d.Priority && d != _current) : -1;
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
            cancel = _current == item ? _currentStop : null;
        }
        Cancel(cancel); Wake(); Raise(item); return true;
    }
    public void Clear()
    {
        Download[] old; Download[] active; CancellationTokenSource? cancel;
        lock (_gate)
        {
            old = _known.ToArray(); active = _active.ToArray(); _known.Clear(); _active.Clear();
            try { Save(); } catch { _known.AddRange(old); _active.AddRange(active); throw; }
            foreach (var item in old.Where(d => d.IsActive || d.State is DownloadState.Failed or DownloadState.Cancelled)) item.State = DownloadState.Removed;
            _done = _saved = 0; _last = null; cancel = _currentStop;
        }
        Cancel(cancel); Wake(); foreach (var item in old) Raise(item);
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
                Download? download; CancellationTokenSource? transfer = null;
                TimeSpan delay = TimeSpan.FromMinutes(1);
                lock (_gate)
                {
                    if (_disposed) break;
                    var eligible = _active.Where(d => d != _current && Runnable(d)).ToArray();
                    download = eligible.FirstOrDefault(d => d.RetryAt is null || d.RetryAt <= DateTimeOffset.UtcNow);
                    if (download is not null)
                    {
                        transfer = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        _current = download; _currentStop = transfer;
                        download.State = DownloadState.Running; download.Error = ""; download.RetryAt = null; SaveOrLog();
                    }
                    else if (eligible.Length > 0)
                        delay = TimeSpan.FromMilliseconds(Math.Clamp((eligible.Min(d => d.RetryAt)!.Value - DateTimeOffset.UtcNow).TotalMilliseconds, 10, 60000));
                }
                if (download is null) { await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false); continue; }
                try { await RunOneAsync(download, transfer!.Token).ConfigureAwait(false); }
                finally { lock (_gate) { _current = null; _currentStop = null; } transfer!.Dispose(); }
                string? summary = null;
                lock (_gate)
                {
                    if (_known.Contains(download))
                    {
                        if (download.State != DownloadState.Waiting)
                        {
                            _active.Remove(download); _done++;
                            if (download.State == DownloadState.Saved) _saved++;
                            if (download.State != DownloadState.Cancelled) _last = download;
                            // Songs waiting for Start or behind a pause do not keep the finished ones from being reported.
                            if (!_active.Any(Runnable)) { summary = Summary(); _done = _saved = 0; _last = null; }
                        }
                        SaveOrLog();
                    }
                }
                Raise(download);
                if (summary is not null && !_stop.IsCancellationRequested) RaiseFinished(summary);
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
    private async Task RunOneAsync(Download download, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            bool tidy; lock (_gate) tidy = _tidied.Add(download.Folder);
            if (tidy && Downloader.CleanLeftovers(download.Folder) is > 0 and var removed)
                _log($"removed {removed} unfinished downloads left in {download.Folder}");
            Raise(download);
            var outcome = await Downloader.DownloadAsync(download.Track, download.Folder, new Report(p =>
            { download.Progress = p; Raise(download); }), ct).ConfigureAwait(false);
            var savedPath = Downloader.FindExisting(download.Folder, download.Track.FileName);
            lock (_gate)
            {
                if (download.State != DownloadState.Removed)
                {
                    download.SavedPath = savedPath;
                    download.State = outcome == DownloadOutcome.Saved ? DownloadState.Saved : DownloadState.AlreadyThere;
                }
            }
        }
        catch (SourcesWaitingException ex) { SetOutcome(download, DownloadState.Waiting, ex.Message, ex.Until); }
        catch (IOException ex) { SetOutcome(download, DownloadState.Waiting, "Connection or storage interrupted: " + ex.Message, DateTimeOffset.UtcNow.AddMinutes(10)); }
        catch (System.Net.Sockets.SocketException ex) { SetOutcome(download, DownloadState.Waiting, "TELEGRAM_CONNECTION_FAILED: " + ex.Message, DateTimeOffset.UtcNow.AddMinutes(10)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetOutcome(download, DownloadState.Cancelled, ""); }
        catch (Exception ex)
        {
            SetOutcome(download, DownloadState.Failed, ex is OperationCanceledException ? "The download was interrupted." : ex.Message);
            if (download.State == DownloadState.Failed) _log($"download of \"{download.Track.Title}\" failed: {download.Error}");
        }
    }
    private void SetOutcome(Download d, DownloadState state, string error, DateTimeOffset? retry = null)
    { lock (_gate) { if (d.State == DownloadState.Removed) return; d.State = state; d.Error = error; d.RetryAt = retry; } }
    private string? Summary()
    {
        if (_last is null) return null;
        if (_done > 1) return $"Saved {_saved} of {_done} tracks to {_last.Folder}";
        return _last.State switch
        {
            DownloadState.Saved => $"Saved \"{_last.Track.Title}\" to {_last.Folder}",
            DownloadState.Failed => $"Could not download \"{_last.Track.Title}\": {_last.Error}", _ => null,
        };
    }
    private void Raise(Download d) { try { Changed?.Invoke(d); } catch (Exception ex) { _log("download listener failed: " + ex.Message); } }
    private void RaiseFinished(string text) { try { Finished?.Invoke(text); } catch (Exception ex) { _log("download listener failed: " + ex.Message); } }
    private sealed class Report(Action<double> report) : IProgress<double> { void IProgress<double>.Report(double value) => report(value); }
}
