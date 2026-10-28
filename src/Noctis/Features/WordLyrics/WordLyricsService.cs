using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Search;
using Noctis.ViewModels;

namespace Noctis.Features.WordLyrics;

/// <summary>Noctis owns discovery, UI and lifetime. The MIT WordLyrics worker owns only
/// the persisted backend job protocol. No plugin menu registration or filesystem watching.</summary>
internal sealed class WordLyricsService(ILibraryService library, Func<AppSettings> settings) : IDisposable
{
    private Worker? _worker;
    private IPluginHost? _host;
    private string[] _folders = [];
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _baseline, _disposed = true, _dialogOpen;
    private DateTime _startedUtc;
    private Services.Plugins.PluginHost? _preferences;
    private volatile string _metadataProxy = "";
    internal string MetadataProxy => _metadataProxy;
    internal void AttachPreferences(Services.Plugins.PluginHost host) => _preferences = host;
    private void PreferenceChanged(string feature, string key)
    {
        if (!_disposed && feature.Equals("dev.moshi.freemusicfinder", StringComparison.OrdinalIgnoreCase) && key == "proxy") UpdateProxy();
    }
    private void UpdateProxy()
        => _metadataProxy = settings().PluginSettingValues?.FirstOrDefault(p => p.Key.Equals("dev.moshi.freemusicfinder", StringComparison.OrdinalIgnoreCase)).Value?.GetValueOrDefault("proxy") ?? "";
    internal string DataDirectory => _host?.DataDirectory ?? "";
    internal bool Enabled => !_disposed;
    internal ProgressState State { get; private set; } = new("Ready", "New downloads use the timing mode in Settings → Features.", Finished: true);
    internal event Action? Changed;
    /// <summary>The compact status pill (null: hidden). Replaces the long top notices for word timing.</summary>
    internal TimingPill? Pill { get; private set; }
    private readonly PillPolicy _pills = new();
    internal event Action? PillChanged;
    internal event Action<IReadOnlyList<string>>? LyricsWritten;
    internal int WaitingCount => _worker?.WaitingCount ?? 0;
    internal DateTime? NextTryUtc => _worker?.NextTryUtc(DateTime.UtcNow);
    internal SongTimingStatus StatusFor(string path)
    {
        var timingPath = Volatile.Read(ref _playingTimingPath);
        var state = _worker?.StatusFor(timingPath is not null && SameSong(path, Volatile.Read(ref _playingPath)) ? timingPath : path) ?? SongTimingStatus.None;
        return state is SongTimingStatus.Queued or SongTimingStatus.Retry && !BackendReady ? SongTimingStatus.SetupNeeded : state;
    }
    private void SongStatusChanged() => Dispatcher.UIThread.Post(() => { if (!_disposed) Changed?.Invoke(); });
    private void NetworkRecovered() { if (!OnlineAccess.Shared.IsOffline) _worker?.RetryNetworkWaits(); }
    private void NetworkModeChanged(bool offline) { if (!offline) _worker?.RetryNetworkWaits(); }
    internal void SetPreviewState(ProgressState state) => Progress(state);
    internal bool IsIgnored(string path) => _worker?.IsIgnored(path) == true;
    internal void SetIgnored(string path, bool ignored)
    {
        if (_worker is { } worker)
        {
            var playing = Volatile.Read(ref _playingPath);
            var saved = Volatile.Read(ref _playingLibraryPath);
            if (SameSong(path, playing) && playing is not null) worker.ShareRules(path, playing);
            if (SameSong(path, saved) && saved is not null) worker.ShareRules(path, saved);
            worker.SetIgnored(path, ignored);
        }
        _playingAsked = false;
        Changed?.Invoke();
    }
    internal string[] Folders => _folders;
    internal IReadOnlyList<Track> Tracks => library.AllTracks;

    internal void Start(IPluginHost host)
    {
        _host = host; _disposed = false; _known.Clear(); _baseline = false; _startedUtc = DateTime.UtcNow;
        _playingAsked = false; _aheadPaths = []; _aheadCurrent = null;
        UpdateProxy(); if (_preferences is not null) _preferences.NativePreferenceChanged += PreferenceChanged;
        _folders = settings().MusicFolders.ToArray();
        _worker = new Worker(host.DataDirectory, () => _folders, ReadOptions(), Log, Notify, Progress,
            lyricsWritten: PublishWrittenLyrics);
        _worker.StatusChanged += SongStatusChanged;
        OnlineAccess.Shared.Recovered += NetworkRecovered;
        OnlineAccess.Shared.RetryHint += NetworkRecovered;
        OnlineAccess.Shared.Changed += NetworkModeChanged;
        _worker.Watched = () => Volatile.Read(ref _playingPath);
        // Owner #96: song copies earlier runs left that are exact duplicates of their song go (in the background, after
        // startup has settled, at low priority).
        BackupCleaner.Temporary = source => FreeMusicFinder.FreeMusicPlugin.Current?.StreamCacheFolder is { Length: > 0 } cache
            && string.Equals(Path.GetFullPath(cache).TrimEnd('\\', '/'), Path.GetFullPath(source).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        try { BackupCleaner.CleanEarlierLater(new Tool(_worker.Options.Folder).Folder, Log, TimeSpan.FromMinutes(2)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        library.LibraryUpdated += LibraryChanged;
        library.MusicFoldersChanged += FoldersChanged;
        OnlineMusicSearch.FileSaved += DownloadSaved;
        LibraryChanged(null, EventArgs.Empty);
        Changed?.Invoke();
    }

    /// <summary>Owner #187: running but not set up, so the first-run card offers its setup.</summary>
    internal bool NeedsSetup => _worker is not null && _host is not null && !BackendReady;

    private Options ReadOptions()
    {
        var config = _host!.Settings;
        var mode = Enum.TryParse<WordTimingMode>(config.GetString(WordTimingSettings.ModeKey), out var value) && Enum.IsDefined(value) ? value : WordTimingMode.Medium;
        var oldValues = settings().PluginSettingValues?.FirstOrDefault(p => p.Key.Equals("dev.moshi.wordlyrics", StringComparison.OrdinalIgnoreCase)).Value;
        var oldSpeed = oldValues?.GetValueOrDefault("speed") ?? "Half";
        return new(mode != WordTimingMode.Off, WordTimingSettings.BackendSpeed(mode) ?? oldSpeed, config.GetBool("notify", true), config.GetString("folder") ?? "", config.GetString("backup") ?? "", config.GetBool("lightWhileGaming", true), config.GetBool("adaptToPc", true));
    }
    internal void SettingChanged(string key)
    {
        if (_worker is null) return;
        _worker.Options = ReadOptions();
        Changed?.Invoke();
        if (key == "setup") _ = SetupAsync();
        else if (key is "progress" or "library") Show(key == "library");
    }

    private void FoldersChanged(object? sender, List<string> folders) => _folders = folders.ToArray();
    private void LibraryChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => LibraryChanged(sender, e)); return; }
        if (_disposed || _worker is not { } worker) return;
        _folders = settings().MusicFolders.ToArray();
        // The first library publish is a baseline, not an instruction to time every old song.
        // Partial snapshots never erase IDs and cannot produce duplicate jobs on a rescan.
        var paths = library.AllTracks.Where(t => !t.IsRemoteStream && !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => t.FilePath).Where(_known.Add).ToArray();
        bool catchingUp = !_baseline && worker.HasCheckpoint;
        bool newSongs = _baseline;
        if (!library.IsPublishingPartial && (sender is not null || library.AllTracks.Count > 0)) _baseline = true;
        if (!newSongs && !catchingUp && sender is null) return;
        // Filesystem creation timestamps can be coarser than the system clock. Allow the
        // first file created during startup through without backfilling an old library.
        EnqueueBackground(paths, worker, catchingUp ? worker.LastSeenUtc.AddSeconds(-2) : newSongs ? null : _startedUtc.AddSeconds(-2), false);
    }
    private void DownloadSaved(object? sender, string path)
    {
        if (_disposed || _worker is not { } worker) return;
        // Owner #108: a song only played (the stream cache, also the songs fetched ahead) is not one of your songs: it is
        // timed only when it plays and "Time every song you play" is on. Added to Songs, its library copy comes here.
        if (OnlineMusicSearch.Current?.IsCachedFile(path) == true) return;
        // Owner 10-20: the song playing now, just added to Songs (or downloaded): timed first, like a played song, and
        // its lyrics switch on the copy being heard (see WithPlayingCopy).
        var playingCopy = Volatile.Read(ref _playingPath);
        if (SameSong(path, playingCopy) && playingCopy is not null) worker.ShareRules(playingCopy, path);
        if (worker.Options.Auto && SameSong(path, playingCopy))
        {
            Volatile.Write(ref _playingLibraryPath, path);
            Volatile.Write(ref _playingTimingPath, path);
            QueueTimingWork(() =>
            {
                if (Worker.HasWordLyrics(path))
                {
                    PublishWrittenLyrics([path]);
                    worker.TickNow(); // an earlier cache-only request can now see the shared timing and leave the queue
                    return;
                }
                var playing = Volatile.Read(ref _playingPath);
                if (playing is not null && (worker.IsWaiting(playing) || Worker.HasWordLyrics(playing)))
                {
                    Volatile.Write(ref _playingTimingPath, playing);
                    if (worker.IsWaiting(playing)) { worker.Prioritize(playing); worker.MakeRoom(playing); }
                    else PublishWrittenLyrics([playing]);
                    return;
                }
                worker.AskPlayed(path, out var accepted);
                if (accepted) worker.MakeRoom(path);
                else if (Worker.HasWordLyrics(path)) PublishWrittenLyrics([path]);
            });
            return;
        }
        EnqueueBackground([path], worker, null, true);
    }
    private void EnqueueBackground(string[] paths, Worker worker, DateTime? since, bool complete)
        => _ = Task.Run(() =>
        {
            foreach (var path in paths)
            {
                if (_disposed || !ReferenceEquals(worker, _worker)) return;
                try
                {
                    if (since is { } last && File.GetCreationTimeUtc(path) <= last) continue;
                    var root = _folders.FirstOrDefault(f => Inside(path, f)) ?? Path.GetDirectoryName(path)!;
                    worker.EnqueueAutomatic(path, root, complete);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Log("WORDLYRICS_ENQUEUE_FAILED: " + ex.Message); }
            }
        });
    // ── Owner #108: songs you play ──
    internal const string PlayedKey = "timePlayed";
    internal static readonly TimeSpan PlayedAfter = TimeSpan.FromSeconds(5);
    private string? _playingPath;
    private string? _playingTimingPath;
    private string? _playingLibraryPath;
    private Track? _playing;
    private bool _playingAsked;
    /// <summary>On (the default): a song you stream is timed after 5 s of play, before anything waiting. Off:
    /// only the songs in your library (a download is queued the moment it arrives).</summary>
    internal bool TimePlayed => _host?.Settings.GetBool(PlayedKey, true) == true;

    /// <summary>Called by the player (UI thread) as the song, its position or its state changes.</summary>
    internal void PlayerMoved(Track? track, TimeSpan position, bool playing)
    {
        if (!ReferenceEquals(track, _playing))
        {
            if (!SameSong(track?.FilePath, _playingPath))
            {
                Volatile.Write(ref _playingTimingPath, null);
                Volatile.Write(ref _playingLibraryPath, null);
            }
            _playing = track; _playingAsked = false;
            Volatile.Write(ref _playingPath, track is { IsRemoteStream: false, FilePath.Length: > 0 } ? track.FilePath : null);
        }
        if (_playingAsked || !playing || _disposed || _worker is not { } worker || !TimePlayed) return;
        if (track is null || track.IsRemoteStream || string.IsNullOrWhiteSpace(track.FilePath)) return;
        // Owned songs start now; streamed songs retain the five-second listening rule.
        if (OnlineMusicSearch.Current?.IsCachedFile(track.FilePath) == true && position < PlayedAfter) return;
        // A song still arriving is asked once its file is whole (the next position change after that).
        if (ProgressivePlayback.ProgressiveTransfer.IsDownloading(track.FilePath) || !File.Exists(track.FilePath)) return;
        _playingAsked = true;
        var path = track.FilePath;
        QueueTimingWork(() =>
        {
            var message = worker.AskPlayed(path, out var accepted);
            if (!accepted) return;
            worker.MakeRoom(path);
            if (message is { Length: > 0 } && !message.StartsWith("Already", StringComparison.Ordinal)) Log("played song: " + message);
        });
    }

    private readonly object _timingWorkGate = new();
    private Task _timingWork = Task.CompletedTask;
    private void QueueTimingWork(Action work)
    {
        lock (_timingWorkGate)
            _timingWork = _timingWork.ContinueWith(_ =>
            {
                if (_disposed) return;
                try { work(); } catch (Exception ex) { Log("WORDLYRICS_QUEUE: " + ex.Message); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private string[] _aheadPaths = [];
    private string? _aheadCurrent;
    private long _aheadGeneration;
    internal void UpdateAhead(IEnumerable<Track> queue, bool playing)
    {
        if (_disposed || _worker is not { } worker) return;
        var current = Volatile.Read(ref _playingPath);
        var paths = TimePlayed && playing && _playingAsked
            ? queue.Take(2).Select(t => t.PendingStream is PendingStream p ? p.Source.CompletedPlaybackPath(p.Song)
                : !t.IsRemoteStream ? t.FilePath : null).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToArray()
            : [];
        if (string.Equals(current, _aheadCurrent, StringComparison.OrdinalIgnoreCase) && paths.SequenceEqual(_aheadPaths, StringComparer.OrdinalIgnoreCase)) return;
        _aheadCurrent = current; _aheadPaths = paths;
        var generation = Interlocked.Increment(ref _aheadGeneration);
        QueueTimingWork(() => { if (generation == Interlocked.Read(ref _aheadGeneration)) worker.SetAhead(paths, current); });
    }

    /// <summary>The same song file in another folder (a streamed song's library copy keeps its file name).</summary>
    internal static bool SameSong(string? one, string? other)
        => one is { Length: > 0 } && other is { Length: > 0 } && string.Equals(Path.GetFileName(one), Path.GetFileName(other), StringComparison.OrdinalIgnoreCase);

    private void PublishWrittenLyrics(IReadOnlyList<string> paths)
    {
        var shown = WithPlayingCopy(paths);
        Dispatcher.UIThread.Post(() => { if (!_disposed) LyricsWritten?.Invoke(shown); });
    }

    // Owner 10-20: a song heard from the stream cache whose library copy was just timed: the timing files go next to the
    // copy being heard too, so the lyrics on screen switch now (the cache copy leaves with the cache).
    private IReadOnlyList<string> WithPlayingCopy(IReadOnlyList<string> paths)
    {
        var playing = Volatile.Read(ref _playingPath);
        var timed = paths.FirstOrDefault(p => SameSong(p, playing));
        if (timed is null) return paths;
        var shown = paths.ToList();
        foreach (var target in new[] { playing, Volatile.Read(ref _playingLibraryPath) }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFullPath(timed), Path.GetFullPath(target!), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var from = Path.Combine(Path.GetDirectoryName(timed)!, Path.GetFileNameWithoutExtension(timed));
                var to = Path.Combine(Path.GetDirectoryName(target!)!, Path.GetFileNameWithoutExtension(target!));
                foreach (var extension in new[] { ".elrc", ".ttml", ".lyricsfile", ".lrc" })
                    if (File.Exists(from + extension)) File.Copy(from + extension, to + extension, overwrite: true);
                shown.Add(target!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Log("Could not share the playing song's timing: " + ex.Message); }
        }
        return shown;
    }

    internal static bool Inside(string path, string folder) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal async Task<string> QueueAsync(IEnumerable<string> paths, TimingAction action = TimingAction.Missing, string? words = null)
    {
        if (_worker is not { } worker) return "Enable WordLyrics in Settings → Features.";
        var selected = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (action == TimingAction.Custom && selected.Length != 1) return "Custom lyrics apply to one selected song.";
        var data = DataDirectory;
        return await Task.Run(() =>
        {
            var draft = action == TimingAction.Custom ? LyricDrafts.Save(data, words ?? "") : null;
            int queued = 0; string? last = null;
            foreach (var path in selected)
            {
                var before = worker.WaitingCount;
                last = worker.Ask(path, action, draft, out var accepted);
                if (accepted && SameSong(path, Volatile.Read(ref _playingPath)))
                {
                    worker.Prioritize(path);
                    worker.MakeRoom(path);
                }
                if (worker.WaitingCount > before) queued++;
            }
            return selected.Length == 1 ? last ?? "Queued." : $"{queued} songs queued. Existing word timing is kept when timing missing words.";
        });
    }

    /// <summary>Explicit lyrics-page request for one complete local song. Setup remains opt-in.</summary>
    internal async Task TimeSongAsync(Track track)
    {
        if (!Enabled) { OpenSettings?.Invoke(); return; }
        if (track.IsRemoteStream || ProgressivePlayback.ProgressiveTransfer.IsDownloading(track.FilePath)) return;
        if (!File.Exists(track.FilePath))
        {
            Progress(new("Song unavailable", "The local file is missing.", Error: true, Finished: true));
            return;
        }
        try
        {
            // The worker retains the request across setup/restarts; never call OpenSetup here.
            if (_worker is not { } worker) return;
            var result = await Task.Run(() =>
            {
                var playing = SameSong(track.FilePath, Volatile.Read(ref _playingPath));
                var saved = Volatile.Read(ref _playingTimingPath);
                var path = playing && saved is not null && File.Exists(saved) ? saved : track.FilePath;
                if (playing) worker.ShareRules(track.FilePath, path);
                var message = worker.Ask(path, TimingAction.Missing, null, out var accepted, enableIgnored: true);
                if (accepted && playing)
                {
                    worker.Prioritize(path);
                    worker.MakeRoom(path);
                }
                else if (playing && Worker.HasWordLyrics(path)) PublishWrittenLyrics([path]);
                return (Message: message, Accepted: accepted);
            });
            if (_disposed || !ReferenceEquals(worker, _worker)) return;
            Log(result.Message ?? "Queued for word timing");
            // Ready jobs report through the existing worker/pill. Setup/rejected
            // requests open the panel; there is no false "running" status.
            if (!result.Accepted || !BackendReady) Show(track: track);
        }
        catch (Exception ex) { Progress(new("Could not queue song", ex.Message, Error: true, Finished: true)); }
    }

    private void Progress(ProgressState state) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        State = state; Changed?.Invoke();
        // A small native pill (icon + a few words), never a focus-stealing window over playback or a game.
        // "Show timing status" covers work in progress, "Say when a song is done" the result; problems always show.
        var pill = _pills.Decide(state, Pill);
        var settings = _host?.Settings;
        if (pill is { Kind: TimingPillKind.Working or TimingPillKind.Paused } && settings?.GetBool("showProgress", true) == false) pill = null;
        if (pill is { Kind: TimingPillKind.Done } && settings?.GetBool("notify", true) == false && settings.GetBool("showProgress", true) == false) pill = null;
        if (pill == Pill) return;
        Pill = pill; PillChanged?.Invoke();
    });
    internal Task PauseAsync(bool paused) => Task.Run(() => _worker?.SetPaused(paused));
    internal Task CancelAsync() => Task.Run(() => _worker?.Cancel());

    /// <summary>Owner #91: Restart. Every WordLyrics run is closed and the waiting songs start again at once.</summary>
    internal Task<string> RestartAsync() => Task.Run(() => _worker?.Restart() ?? "Enable WordLyrics in Settings → Features.");

    /// <summary>Owner #91: the library songs (local files) that have no word timing and are not already waiting. Reads only
    /// lyric files and tags, off the UI thread.</summary>
    internal Task<IReadOnlyList<string>> MissingAsync(CancellationToken ct = default)
    {
        var worker = _worker;
        var paths = library.AllTracks.Where(t => !t.IsRemoteStream && !t.IsExternal && !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => t.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var missing = new List<string>();
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (worker?.IsWaiting(Path.GetFullPath(path)) == true || worker?.AutomaticBlocked(path) == true || !File.Exists(path)) continue;
                    if (!Worker.HasWordLyrics(path) && !EmbeddedWordLyrics.HasWords(path)) missing.Add(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            return missing;
        }, ct);
    }

    /// <summary>Owner #91: one click for every library song without word timing. Existing word timing is never redone.</summary>
    internal async Task<string> TimeAllMissingAsync(CancellationToken ct = default)
    {
        if (_worker is not { } worker) return "Enable WordLyrics in Settings → Features.";
        var missing = await MissingAsync(ct);
        if (missing.Count == 0) return "Every song already has word timing.";
        var added = await Task.Run(() => worker.AskMany(missing), ct);
        return BackendReady ? $"{added:N0} song{(added == 1 ? "" : "s")} queued for word timing."
            : $"{added:N0} song{(added == 1 ? "" : "s")} saved for word timing; they start once WordLyrics is set up.";
    }

    /// <summary>
    /// Owner #91: once WordLyrics is set up (or found set up for the first time, e.g. a model downloaded for the old plugin),
    /// it offers to time the library songs that have none, once for each installation. Never while nothing is missing.
    /// </summary>
    private async Task OfferMissingAsync()
    {
        if (_disposed || _worker is not { } worker || _host is null) return;
        var flag = Path.Combine(DataDirectory, "offered-missing.txt");
        var key = new Tool(worker.Options.Folder).Folder;
        try { if (File.Exists(flag) && string.Equals(File.ReadAllText(flag).Trim(), key, StringComparison.OrdinalIgnoreCase)) return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        IReadOnlyList<string> missing;
        try { missing = await MissingAsync(); }
        catch (Exception ex) { Log("WORDLYRICS_MISSING_COUNT_FAILED: " + ex.Message); return; }
        if (missing.Count == 0 || _disposed) return;
        try { File.WriteAllText(flag, key); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_disposed || (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } owner) return;
            var text = $"WordLyrics is ready.\n{missing.Count:N0} song{(missing.Count == 1 ? " has" : "s have")} no word timing. Time {(missing.Count == 1 ? "it" : "them")} now?";
            if (!await Views.ConfirmationDialog.ShowAsync(owner, text) || _disposed) return;
            var result = await TimeAllMissingAsync();
            Progress(new ProgressState("Queued", result));
        });
    }
    internal void OpenReport()
    {
        if (_worker is not { } worker || string.IsNullOrWhiteSpace(State.Report)) return;
        var path = Path.GetFullPath(State.Report);
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || !Inside(path, new Tool(worker.Options.Folder).Folder) || !File.Exists(path))
            throw new IOException("The result report is not available in the WordLyrics folder. The queue and current lyrics are kept.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
    internal const string SetupConsent = "Download WordLyrics?\n~1.33 GB models + Python/packages (optional).";
    internal async Task SetupAsync(Func<string, Task<bool>>? confirm = null)
    {
        if (_worker is not { } worker || _host is not { } host) return;
        try
        {
            if (confirm is null)
            {
                if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } owner) return;
                confirm = text => Views.ConfirmationDialog.ShowAsync(owner, text);
            }
            if (!await confirm(SetupConsent) || _disposed) return;
            await Task.Run(() => new Tool(worker.Options.Folder).OpenSetup(host.DataDirectory, closed: () => { if (!_disposed) _worker?.Restart(); }));
        }
        catch (Exception ex) { Progress(new("Setup could not open", ex.Message, Error: true, Finished: true)); }
    }
    /// <summary>Shows the progress panel inside the main window (set by the shell); the separate dialog is only the
    /// fallback when nothing is attached.</summary>
    internal Action<bool, Track?>? Presenter { get; set; }

    /// <summary>Opens Settings → Features at WordLyrics (set by the shell).</summary>
    internal Action? OpenSettings { get; set; }

    /// <summary>Installed, new enough, Python and both models present: nothing would be downloaded.</summary>
    internal bool BackendReady
    {
        get
        {
            try { return _worker is { } worker && new Tool(worker.Options.Folder).Ready; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
    }

    internal void Show(bool library = false, Track? track = null)
    {
        if (_disposed) return;
        if (Presenter is { } present) { present(library, track); return; }
        if (_dialogOpen || (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } owner) return;
        _ = Open();
        async Task Open()
        {
            _dialogOpen = true;
            try { await Views.WordLyricsDialog.ShowAsync(owner, this, library, track); }
            finally { _dialogOpen = false; }
        }
    }
    private void Log(string text) => Dispatcher.UIThread.Post(() => { if (!_disposed) _host?.Log(text); });
    private void Notify(string text) => Dispatcher.UIThread.Post(() => { if (!_disposed) _host?.Notify(text); });
    public void Dispose()
    {
        _disposed = true; library.LibraryUpdated -= LibraryChanged; library.MusicFoldersChanged -= FoldersChanged;
        if (_preferences is not null) _preferences.NativePreferenceChanged -= PreferenceChanged;
        OnlineMusicSearch.FileSaved -= DownloadSaved;
        OnlineAccess.Shared.Recovered -= NetworkRecovered;
        OnlineAccess.Shared.RetryHint -= NetworkRecovered;
        OnlineAccess.Shared.Changed -= NetworkModeChanged;
        _worker?.Dispose(); _worker = null; _host = null; _known.Clear(); Changed?.Invoke();
    }
}
