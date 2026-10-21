using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Noctis.Features.WordLyrics;

/// <summary>What the user chose in Settings → Features. Read on the UI thread, used everywhere.</summary>
internal sealed record Options(bool Auto, string Speed, bool Notify, string Folder, string Backup, bool LightWhileGaming = true);

/// <summary>
/// Processes typed library/download requests, one background run at a time. Retains the
/// backend checkpoint, retry and process-control protocol, without directory watchers.
///
/// A song is handed over only after its file has been left alone for a while (a download or a
/// copy is finished, and Noctis has written what it writes), and never when it already has
/// word-by-word lyrics. The plugin itself reads and writes nothing in the music folders: the
/// program does the work, and it only ever adds new lyric files next to a song.
/// </summary>
internal sealed class Worker : IDisposable
{
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".ogg", ".oga", ".opus", ".wav", ".aiff", ".aif", ".aifc", ".wma", ".aac", ".ape", ".wv", ".alac", ".asf", ".dsf", ".dff",
    };
    // A song with one of these next to it already has word-by-word lyrics.
    private static readonly string[] WordLyrics = { ".elrc", ".ttml", ".lyricsfile" };

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    // How long a new file has to stay unchanged. A file that got its name by a rename (a download
    // saved under a temporary name, a song renamed by hand) was whole before it got that name; one
    // that was created under its name may still be written in pieces. Anyone who still has the file
    // open for writing holds it back anyway (see Ripe).
    private static readonly TimeSpan QuietAfterRename = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuietAfterCreate = TimeSpan.FromSeconds(10);
    private const int MaxSongsPerRun = 200;
    private const int MaxTries = 2;
    private const int ExitAlreadyRunning = 3;
    private const int ExitSetupFailed = 70;

    private readonly string _data;
    private readonly SongRules _rules;
    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly Action<string> _log;
    private readonly Action<string> _notify;
    private readonly Action<ProgressState> _progress;
    private readonly Func<Tool, bool> _readiness;
    private readonly Action<IReadOnlyList<string>> _lyricsWritten;
    private readonly object _gate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private Options _options;
    private Process? _run;
    private Job? _job;
    private readonly HashSet<string> _activePaths = new(StringComparer.OrdinalIgnoreCase);
    internal event Action? StatusChanged;
    private DateTime _stateSaved = DateTime.MinValue;
    private DateTime _lastSeen;
    private bool _saidNotReady;
    private bool _saidBadFolder;
    private bool _ticking;
    private bool _disposed;
    private bool _paused;               // the user paused: no new batch starts until Resume; the waiting list is kept
    private bool _cancelling;           // the user cancelled the run that is going; it is finishing the song it is on
    private bool _controlled;           // the run that is going looks at the control file (so the buttons are shown)
    private bool _restarting;           // owner #91: the run that is going was ended by Restart (or the watchdog)
    private string? _restartNotice;     // what the panel says once a restarted run has ended (null: the watchdog says it)
    private bool? _wasReady;            // whether WordLyrics was set up at the last look (null: not looked yet)
    private bool _yielding;             // owner #108: the run finishes the song it is on and stops for the song playing now
    private double _runProgress;        // how far the run is (0..1), from its stages
    private int _lastCompleted;         // songs the run has finished, at the last look
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    internal Func<bool> FullscreenWorkload { get; set; } = GamingBudget.IsActive;
    private DateTime _budgetChecked;
    private bool _lightBudget;
    /// <summary>Owner #110: the song playing now; its word timing is reported the moment it is written, not when the run ends.</summary>
    internal Func<string?>? Watched { get; set; }
    // Owner #91 watchdog: a run whose progress file and CPU time both stop moving is stuck (a model call that never returns).
    internal TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(15);
    internal TimeSpan IdleCpuAfter { get; set; } = TimeSpan.FromMinutes(5);
    internal TimeSpan ReadinessEvery { get; set; } = TimeSpan.FromSeconds(5);
    private const int MaxAutoRestarts = 2;
    private int _autoRestarts;
    private DateTime _progressStamp, _progressMoved, _cpuMoved;
    private TimeSpan _cpuSeen;
    private View _view = new("Starting WordLyrics", "", 0, 0, null, false);

    /// <summary>What the last progress update of the program said (before pause and cancel are added to it).</summary>
    private sealed record View(string Message, string Detail, int Completed, int Total, double? Fraction, bool EnginePaused);

    private sealed class Item
    {
        public string Path = "";
        public string Root = "";
        public TimingAction Action;
        public string? Draft;
        public bool Asked;              // the user asked for this song by hand
        public DateTime Urgent;         // owner #108: the song playing now (newest first, alone in its run; not saved)
        public bool Ahead, AheadOnly;
        public int AheadOrder;
        public int Tries;               // runs that failed because of the song
        public int Waits;               // runs that could not do anything (no internet, broken setup): see Backoff
        public DateTime Seen;
        public DateTime QuietSince;
        public TimeSpan Quiet = QuietAfterCreate;
        public DateTime NotBefore;
        public long Length = -1;
        public DateTime Stamp;
        public bool AccessReported;
        public bool NetworkWait;
        public bool Deferred;
    }

    /// <param name="dataDirectory">The plugin's own folder (list of waiting songs, last result, last output).</param>
    /// <param name="folders">An immutable snapshot of Noctis's configured folders.</param>
    /// <param name="log">Called from any thread.</param>
    /// <param name="notify">Called from any thread.</param>
    public Worker(string dataDirectory, Func<IReadOnlyList<string>> folders, Options options, Action<string> log, Action<string> notify, Action<ProgressState>? progress = null, bool startTimer = true, Func<Tool, bool>? readiness = null, Action<IReadOnlyList<string>>? lyricsWritten = null)
    {
        _data = dataDirectory;
        _folders = folders;
        _options = options;
        _log = log;
        _notify = notify;
        _progress = progress ?? (_ => { });
        _readiness = readiness ?? (tool => tool.Ready);
        _lyricsWritten = lyricsWritten ?? (_ => { });
        Directory.CreateDirectory(_data);

        _rules = new SongRules(_data, _log);
        _rules.Load();
        HasCheckpoint = File.Exists(StateFile);
        LoadState();

        _timer = new Timer(_ => OnTick(), null, startTimer ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan, startTimer ? Tick : Timeout.InfiniteTimeSpan);
    }

    public Options Options
    {
        get { lock (_gate) return _options; }
        set { lock (_gate) { _options = value; _saidBadFolder = false; } } // a changed setting may be the repair
    }

    public bool HasCheckpoint { get; }
    public DateTime LastSeenUtc => _lastSeen;
    internal int WaitingCount { get { lock (_gate) return _items.Values.Count(i => !i.Deferred); } }
    internal bool IsWaiting(string path) { lock (_gate) return _items.TryGetValue(path, out var item) && !item.Deferred; }
    /// <summary>When every waiting song is held for a later try (a provider error, a wait after failures), the
    /// soonest of those times; null while something can go now or nothing waits.</summary>
    internal DateTime? NextTryUtc(DateTime now)
    {
        lock (_gate)
        {
            if (_run is not null || _items.Count == 0) return null;
            var soonest = _items.Values.Min(item => item.NotBefore);
            return soonest > now + TimeSpan.FromMinutes(1) ? soonest : null;
        }
    }
    internal SongTimingStatus StatusFor(string path)
    {
        lock (_gate)
        {
            if (_disposed || IsIgnored(path) || !_items.TryGetValue(path, out var item) || item.Deferred) return SongTimingStatus.None;
            if (item.Action == TimingAction.Missing && !_activePaths.Contains(path) && HasWordLyrics(path)) return SongTimingStatus.None;
            if (!_activePaths.Contains(path) && !item.Asked && !_options.Auto) return SongTimingStatus.None;
            if (_paused) return SongTimingStatus.Paused;
            if (_activePaths.Contains(path)) return SongTimingStatus.Processing;
            return item.NotBefore > DateTime.UtcNow ? SongTimingStatus.Retry : SongTimingStatus.Queued;
        }
    }
    public bool EnqueueAutomatic(string path, string root, bool complete = false)
        => Consider(Path.GetFullPath(path), Path.GetFullPath(root), complete ? QuietAfterRename : QuietAfterCreate);
    internal void TickNow() => OnTick();

    internal void RetryNetworkWaits()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var item in _items.Values.Where(i => i.NetworkWait))
            { item.NetworkWait = false; item.NotBefore = DateTime.UtcNow; }
            SaveState();
        }
        _timer.Change(TimeSpan.Zero, Tick);
    }

    /// <summary>How long the program gets to stop after Cancel before it is stopped by force.</summary>
    internal TimeSpan CancelGrace { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Pause (true) or Resume (false), from the progress window. The program rests between two steps of
    /// the models; meanwhile no new batch is started and the waiting songs stay on the list.
    /// </summary>
    public void SetPaused(bool paused)
    {
        var resumed = false;
        lock (_gate)
        {
            if (_disposed || _paused == paused) return;
            _paused = paused;
            StatusChanged?.Invoke();
            if (_run is not null && _controlled && !_cancelling) WriteControl(paused ? "pause" : "run");
            if (_run is null && !paused)
            {
                resumed = true;
                _progress(new ProgressState("Resumed", _items.Count == 0 ? "Nothing is waiting." : $"{_items.Count} song{(_items.Count == 1 ? " is" : "s are")} waiting and start in a moment."));
            }
            else Emit();
        }
        if (resumed) _timer.Change(TimeSpan.Zero, Tick);
    }

    /// <summary>
    /// Cancel, from the progress window: the program finishes the song it is timing and stops. When it
    /// has ended, the songs of this batch that it did not reach and every song still waiting are taken
    /// off the list (see <see cref="Finished"/>); a stop by the user is no failed try for any song.
    /// </summary>
    public void Cancel()
    {
        Process? active;
        lock (_gate)
        {
            active = _run;
            if (_disposed || _cancelling) return;
            if (active is null)
            {
                var count = _items.Count; _items.Clear(); _paused = false; SaveState();
                _progress(new ProgressState("Cancelled", CancelNotice(0, count), Finished: true));
                return;
            }
            if (!_controlled) return;
            _cancelling = true;
            _paused = false;
            WriteControl("stop");
            Emit();
        }
        // A program that does not react (stuck in a model call, say) is stopped by force after a while.
        Task.Run(async () =>
        {
            try { await Task.Delay(CancelGrace).ConfigureAwait(false); }
            catch (Exception) { return; }
            Job? job;
            lock (_gate)
            {
                if (!ReferenceEquals(_run, active)) return;
                job = _job;
            }
            Safe(() => _log("WordLyrics did not stop by itself in time; it is stopped now"));
            Safe(() =>
            {
                if (job is not null) job.Dispose();
                else if (!active.HasExited) active.Kill(entireProcessTree: true);
            });
        });
    }

    /// <summary>
    /// Owner #91: Restart. Whatever WordLyrics is doing stops at once (no grace: it may be stuck); the songs stay on the
    /// list without a failed try, every retry wait is cleared and the next run starts right away. With
    /// <paramref name="automatic"/> (the stuck-run watchdog) the songs keep their tries.
    /// </summary>
    public string Restart(bool automatic = false)
    {
        Process? active; Job? job; int waiting;
        lock (_gate)
        {
            if (_disposed) return "";
            _paused = false; _cancelling = false;
            ClearWaits(resetTries: !automatic);
            active = _run; job = _job;
            waiting = _items.Count;
            if (active is not null) { _restarting = true; _restartNotice = waiting == 0 ? "Nothing is waiting." : $"{waiting} song{(waiting == 1 ? "" : "s")} start again."; }
        }
        // A Restart by the user closes every WordLyrics run of this installation: one left by the old Noctis plugin or a
        // command window holds the one-run-at-a-time lock and would keep this one waiting.
        if (!automatic) Safe(() => { var stopped = StopOtherRuns(new Tool(Options.Folder).Folder, active); if (stopped > 0) _log($"stopped {stopped} other WordLyrics run{(stopped == 1 ? "" : "s")}"); });
        if (active is not null)
            Safe(() => { if (job is not null) job.Dispose(); else if (!active.HasExited) active.Kill(entireProcessTree: true); });
        else
        {
            _timer.Change(TimeSpan.Zero, Tick);
            _progress(new ProgressState("Restarted", waiting == 0 ? "Nothing is waiting." : $"{waiting} song{(waiting == 1 ? "" : "s")} start again.", Finished: waiting == 0));
        }
        return waiting == 0 ? "WordLyrics restarted. Nothing is waiting." : $"WordLyrics restarted: {waiting} song{(waiting == 1 ? "" : "s")} start again.";
    }

    /// <summary>Owner #108: the song playing now is timed next, before everything else waiting.</summary>
    internal string? AskPlayed(string path, out bool accepted)
    {
        accepted = false;
        if (AutomaticBlocked(path)) return null;
        var message = Ask(path, TimingAction.Missing, null, out accepted, automatic: true);
        if (!accepted) return message;
        Prioritize(path);
        return message;
    }

    /// <summary>Promote an existing request without replacing its repair action or custom draft.</summary>
    internal void Prioritize(string path)
    {
        lock (_gate)
            if (_items.TryGetValue(Path.GetFullPath(path), out var item) && !item.Deferred && !IsIgnored(path)) { item.Urgent = DateTime.UtcNow; item.NotBefore = DateTime.UtcNow; }
        _timer.Change(TimeSpan.Zero, Tick);
    }

    /// <summary>Transient priorities for complete files in the next two queue positions.</summary>
    internal void SetAhead(IReadOnlyList<string> paths, string? current)
    {
        var wanted = paths.Take(2).Where(p => !AutomaticBlocked(p) && File.Exists(p) && !ProgressivePlayback.ProgressiveTransfer.IsDownloading(p)
            && !HasWordLyrics(p) && !EmbeddedWordLyrics.HasWords(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool displaced = false;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var item in _items.Values.ToArray())
            {
                if (!WordLyricsService.SameSong(item.Path, current)) item.Urgent = default;
                if (!item.Ahead || wanted.Contains(item.Path, StringComparer.OrdinalIgnoreCase)) continue;
                item.Ahead = false;
                if (item.AheadOnly) { displaced |= _activePaths.Contains(item.Path); _items.Remove(item.Path); }
            }
            for (var i = 0; i < wanted.Length; i++)
            {
                var path = wanted[i];
                if (!_items.TryGetValue(path, out var item))
                {
                    var now = DateTime.UtcNow;
                    _items[path] = item = new Item { Path = path, Root = Path.GetDirectoryName(path)!, Asked = true,
                        AheadOnly = true, Action = _rules.Get(path).RetryUtc != default ? TimingAction.Fresh : TimingAction.Missing, Seen = now, QuietSince = now, NotBefore = now };
                }
                item.Ahead = true; item.AheadOrder = i;
            }
            SaveState();
        }
        if (displaced) MakeRoom(current ?? "");
        StatusChanged?.Invoke();
        _timer.Change(TimeSpan.Zero, Tick);
    }

    /// <summary>
    /// Owner #108: another song plays. A run on other songs that is at least 75% through finishes the song it is on and
    /// stops; one less far is stopped now. Either way its songs stay on the list (no failed try) and the song playing now
    /// goes first.
    /// </summary>
    internal void MakeRoom(string path)
    {
        Process active; Job? job;
        lock (_gate)
        {
            if (_disposed || _run is not { } run || _cancelling || _restarting ||
                _activePaths.Any(p => string.Equals(Path.GetFileName(p), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))) return;
            if (_runProgress >= 0.75)
            {
                if (!_controlled) return; // an old program without the stop file simply finishes
                _yielding = true; _cancelling = true;
                WriteControl("stop");
                Emit();
                return;
            }
            active = run; job = _job;
            _restarting = true; _restartNotice = null;
        }
        Safe(() => { if (job is not null) job.Dispose(); else if (!active.HasExited) active.Kill(entireProcessTree: true); });
    }

    /// <summary>WordLyrics processes of this installation (its own Python) other than <paramref name="ours"/>.</summary>
    internal static List<Process> OtherRuns(string toolFolder, Process? ours)
    {
        var python = Path.GetFullPath(Path.Combine(toolFolder, ".python"));
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName("python"))
        {
            try
            {
                if (ours is not null && process.Id == ours.Id) { process.Dispose(); continue; }
                var exe = process.MainModule?.FileName;
                if (exe is not null && exe.StartsWith(python + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { found.Add(process); continue; }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            process.Dispose();
        }
        return found;
    }

    private static int StopOtherRuns(string toolFolder, Process? ours)
    {
        var stopped = 0;
        foreach (var other in OtherRuns(toolFolder, ours))
            using (other)
                try { other.Kill(entireProcessTree: true); stopped++; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        return stopped;
    }

    /// <summary>What else may hold WordLyrics' one-run lock, for the "another run" message.</summary>
    private string OtherHolder()
    {
        try
        {
            var others = OtherRuns(new Tool(Options.Folder).Folder, null);
            foreach (var o in others) o.Dispose();
            var current = Environment.ProcessPath ?? "";
            var otherNoctis = Process.GetProcessesByName("Noctis").Any(p => { try { return !string.Equals(p.MainModule?.FileName, current, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; } finally { p.Dispose(); } });
            return otherNoctis ? "Another Noctis app is open and may be timing songs (close it), or press Restart to stop every WordLyrics run."
                : others.Count > 0 ? "A WordLyrics window or an earlier run is still going. Press Restart to stop it."
                : "Another window is using the model. This batch will retry in a minute.";
        }
        catch (Exception) { return "Another window is using the model. This batch will retry in a minute."; }
    }

    /// <summary>Every waiting song may start now (a fixed setup, a restart). Called under the lock.</summary>
    private void ClearWaits(bool resetTries)
    {
        var now = DateTime.UtcNow;
        foreach (var item in _items.Values.Where(i => !i.Deferred)) { item.NotBefore = now; item.Waits = 0; item.AccessReported = false; if (resetTries) item.Tries = 0; }
        _saidNotReady = false; _saidBadFolder = false;
        SaveState();
    }

    /// <summary>Owner #91: many songs asked for at once ("Time all songs without word timing"): one save, not one per song.
    /// Returns how many were added; songs that already have word timing or are already waiting are skipped.</summary>
    internal int AskMany(IEnumerable<string> paths)
    {
        var now = DateTime.UtcNow; var added = 0;
        var fresh = new List<(string Path, string Root)>();
        foreach (var raw in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw) || !Audio.Contains(Path.GetExtension(raw)) || !File.Exists(raw)) continue;
                var path = Path.GetFullPath(raw);
                if (HasWordLyrics(path) || EmbeddedWordLyrics.HasWords(path)) continue;
                fresh.Add((path, LibraryFolders().FirstOrDefault(f => Inside(path, f)) ?? Path.GetDirectoryName(path)!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        lock (_gate)
        {
            if (_disposed) return 0;
            foreach (var (path, root) in fresh)
            {
                if (AutomaticBlocked(path)) continue;
                if (_items.TryGetValue(path, out var existing)) { existing.Asked = true; existing.NotBefore = now; continue; }
                _items[path] = new Item { Path = path, Root = root, Asked = true, Action = _rules.Get(path).RetryUtc != default ? TimingAction.Fresh : TimingAction.Missing, Seen = now, QuietSince = now, NotBefore = now };
                added++;
            }
            SaveState();
        }
        _timer.Change(TimeSpan.Zero, Tick);
        return added;
    }

    /// <summary>Raised (worker thread) the first time WordLyrics is found set up after it was not (a finished setup, or the
    /// first look at a backend that was already installed).</summary>
    internal event Action? BecameReady;

    /// <summary>The Noctis library folders that exist right now.</summary>
    public IReadOnlyList<string> LibraryFolders() => _folders();

    /// <summary>The user asked for this song. Returns what to tell them right away, or null.</summary>
    public string? Ask(string path, TimingAction action = TimingAction.Missing, string? draft = null)
        => Ask(path, action, draft, out _);

    internal string? Ask(string path, TimingAction action, string? draft, out bool accepted, bool automatic = false)
    {
        accepted = false;
        if (!string.IsNullOrWhiteSpace(path) && ProgressivePlayback.ProgressiveTransfer.IsDownloading(path))
            return "Wait for the song to finish downloading.";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "The song's file is not there.";
        if (!Audio.Contains(Path.GetExtension(path))) return "WordLyrics cannot read this kind of file.";
        if (action == TimingAction.Missing && (HasWordLyrics(path) || EmbeddedWordLyrics.HasWords(path))) return "Word-timed lyrics already exist. To fix wrong words or timing, right-click this song and choose Wrong lyrics / redo with fresh lyrics (WordLyrics), or Edit / paste custom lyrics.";
        var tool = new Tool(Options.Folder);

        path = Path.GetFullPath(path);
        var root = LibraryFolders().FirstOrDefault(f => Inside(path, f)) ?? Path.GetDirectoryName(path)!;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return null;
            if (IsIgnored(path)) return "WordLyrics is off for this song.";
            if (automatic && AutomaticBlocked(path)) return null;
            var rule = _rules.Get(path);
            if (rule.RetryUtc != default)
            {
                // A manual retry bypasses the cooldown and fetches a new source. An expired
                // automatic retry also ignores the lyrics that could not be aligned last time.
                if (action == TimingAction.Missing) action = TimingAction.Fresh;
                _rules.Set(path, rule with { RetryUtc = default });
                if (_items.TryGetValue(path, out var deferred) && deferred.Deferred) _items.Remove(path);
            }
            // The empty-lyrics action is idempotent, including a download racing
            // its automatic request. Do not replace a live item's identity.
            if (action == TimingAction.Missing && _items.TryGetValue(path, out var existing))
            {
                existing.Asked = true;
                existing.AheadOnly = false;
                SaveState();
                accepted = true;
                return "Already queued for word timing.";
            }
            _items[path] = new Item { Path = path, Root = root, Asked = true, Action = action, Draft = draft, Seen = now, QuietSince = now, NotBefore = now };
            SaveState();
            accepted = true;
        }
        _timer.Change(TimeSpan.Zero, Tick);
        return !tool.Ready ? "Queued. WordLyrics setup is needed; the request is saved." : _run is null ? "Queued for word timing…" : "Queued: WordLyrics is busy with another song.";
    }

    public void Dispose()
    {
        Process? run;
        Job? job;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            run = _run;
            job = _job;
            _run = null;
            _job = null;
            _lastSeen = DateTime.UtcNow;
            SaveState();
        }
        Quietly(_timer.Dispose);
        // The program only ever adds complete files, so it can be stopped at any moment; the
        // songs it had not reached are still on the list and are done next time.
        if (run is not null)
        {
            try
            {
                if (job is not null) job.Dispose();
                else if (!run.HasExited) run.Kill(entireProcessTree: true);
            }
            catch (Exception ex) { Safe(() => _log($"WordLyrics could not be stopped: {ex.GetType().Name}: {ex.Message}")); }
            Quietly(() => File.Delete(ControlFile));
        }
    }

    // ── noticing new songs ──

    private void OnTick()
    {
        lock (_gate)
        {
            if (_disposed || _ticking) return;
            _ticking = true;
        }
        try { Safe(Work); }
        finally { lock (_gate) _ticking = false; }
    }

    private void Work()
    {
        var now = DateTime.UtcNow;
        if (now - _budgetChecked >= TimeSpan.FromSeconds(5))
        {
            _budgetChecked = now;
            var protect = Options.LightWhileGaming && FullscreenWorkload();
            if (protect != _lightBudget)
            {
                _lightBudget = protect;
                _log(protect ? "WordLyrics: fullscreen workload detected; new timing runs use Light." : "WordLyrics: normal timing speed restored for new runs.");
            }
        }
        if (_run is not null) { ReadProgress(); Watch(now); }

        List<Item> waiting;
        lock (_gate)
        {
            if (_disposed) return;
            if (now - _stateSaved >= TimeSpan.FromMinutes(5))
            {
                _lastSeen = now;
                SaveState();
            }
            if (_run is not null) return;
        }
        CheckReadiness(now);
        lock (_gate)
        {
            if (_disposed || _run is not null || _items.Count == 0) return;
            if (_paused) return;            // paused by the user: the waiting songs stay on the list until Resume
            waiting = _items.Values.Where(i => i.Asked || _options.Auto).ToList();
        }

        // Looking at the files happens outside the lock: a slow drive must never make Noctis
        // wait (it calls into the plugin from its own thread). Only this timer changes these
        // entries while no run is going.
        var ripe = waiting.Where(i => (!Services.OnlineAccess.Shared.IsOffline || i.Action == TimingAction.Custom && i.Draft is not null || i.Action is TimingAction.Missing or TimingAction.Current && File.Exists(Path.ChangeExtension(i.Path, ".lrc"))) && Ripe(i, now)).OrderByDescending(i => i.Urgent).ThenByDescending(i => i.Ahead)
            .ThenBy(i => i.Ahead ? i.AheadOrder : 0).ThenByDescending(i => i.Asked).ThenBy(i => i.Seen).ToList();
        var gone = waiting.Where(i => i.Length == -2).ToList();
        if (gone.Count > 0)
        {
            lock (_gate)
            {
                foreach (var item in gone)
                    if (_items.TryGetValue(item.Path, out var current) && ReferenceEquals(current, item)) _items.Remove(item.Path);
                SaveState();
            }
        }
        if (ripe.Count == 0) return;
        // Owner #108: the song playing now runs alone, so it is done (and the next one played can go first) at once.
        var batch = ripe[0].Urgent != default || ripe[0].Ahead ? [ripe[0]]
            : ripe.Where(i => i.Urgent == default && !i.Ahead && i.Asked == ripe[0].Asked && i.Action == ripe[0].Action && i.Draft == ripe[0].Draft && SamePath(i.Root, ripe[0].Root)).Take(MaxSongsPerRun).ToList();

        var options = Options;
        Tool tool;
        try { tool = new Tool(options.Folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // Said once, not at every tick; the next change of a setting lets it be said again.
            if (!_saidBadFolder)
            {
                _saidBadFolder = true;
                var message = $"The WordLyrics folder in the feature settings is not a usable folder name ({ex.GetType().Name}). Correct it in Settings → Features → WordLyrics; the waiting songs are kept.";
                _log(message);
                _progress(new ProgressState("Setup is needed", message, Error: true, Finished: true));
            }
            return;
        }
        _saidBadFolder = false;
        var ready = _readiness(tool);

        var repairReady = batch[0].Action == TimingAction.Missing || tool.SupportsRepair;
        if (!ready || !repairReady)
        {
            if (!_saidNotReady)
            {
                _saidNotReady = true;
                _log("songs are waiting, but WordLyrics is not set up in " + tool.Folder);
                var problem = !repairReady ? "WORDLYRICS_REPAIR_UPDATE_REQUIRED: this backend lacks backed-up rerun support. Use Set up WordLyrics to update it; the request is saved." : NotReady(tool);
                _progress(new ProgressState("Setup is needed", problem, Error: true, Finished: true));
            }
            return;
        }
        _saidNotReady = false;
        try { Start(tool, batch, options); }
        catch (Exception ex)
        {
            foreach (var item in batch) item.NotBefore = now + TimeSpan.FromMinutes(2);
            var message = $"WORDLYRICS_START_FAILED ({ex.GetType().Name}): the background program could not start. Check the WordLyrics folder and installation. Retrying in two minutes.";
            _log(message);
            _progress(new ProgressState("WordLyrics could not start", message, Total: batch.Count, Finished: true, Error: true));
        }
    }

    private DateTime _readyChecked;
    /// <summary>
    /// Owner #91: is WordLyrics set up now? Looked at every few seconds while no run is going, even when every song is still
    /// waiting out a retry or nothing is waiting at all. A setup that was just finished (or repaired) clears the retry waits
    /// songs collected while it was missing or broken (up to 3 hours each), and is announced once (BecameReady).
    /// </summary>
    private void CheckReadiness(DateTime now)
    {
        if (now - _readyChecked < ReadinessEvery) return;
        _readyChecked = now;
        bool ready;
        try { ready = _readiness(new Tool(Options.Folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { ready = false; }
        if (!ready) { _wasReady = false; return; }
        if (_wasReady == true) return;
        var wasMissing = _wasReady == false;
        _wasReady = true;
        if (wasMissing)
        {
            lock (_gate) if (!_disposed && _items.Count > 0) ClearWaits(resetTries: false);
            _log("WordLyrics is set up; songs waiting for it start now");
        }
        Safe(() => BecameReady?.Invoke());
    }
    /// <summary>Has the file been left alone long enough, and does it still need lyrics? Called under the lock.</summary>
    private bool Ripe(Item item, DateTime now)
    {
        if (now < item.NotBefore) return false;
        if (IsIgnored(item.Path)) { item.Length = -2; return false; }
        var rule = _rules.Get(item.Path);
        if (rule.RetryUtc > now)
        {
            item.Deferred = true; item.NotBefore = rule.RetryUtc;
            item.Action = TimingAction.Fresh; item.Draft = null; item.AheadOnly = false;
        }
        if (now < item.NotBefore) return false;
        item.Deferred = false;
        try
        {
            var file = new FileInfo(item.Path);
            if (!file.Exists)
            {
                if (!Directory.Exists(item.Root)) { item.NotBefore=now+TimeSpan.FromMinutes(1);return false; }
                if (!file.Exists && now - item.Seen < TimeSpan.FromMinutes(1)) return false; // may be on its way
                item.Length = -2; // taken off the list
                return false;
            }
            if (file.Length != item.Length || file.LastWriteTimeUtc != item.Stamp)
            {
                item.Length = file.Length;
                item.Stamp = file.LastWriteTimeUtc;
                if (!item.Asked) item.QuietSince = now;
            }
            if (file.Length == 0 || (!item.Asked && now - item.QuietSince < item.Quiet)) return false;
            // Still being written by whoever brought it?
            using (new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
            if (item.Action == TimingAction.Missing && (HasWordLyrics(item.Path) || EmbeddedWordLyrics.HasWords(item.Path)))
            {
                item.Length = -2;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.QuietSince = now;
            item.NotBefore = now + TimeSpan.FromSeconds(ex is UnauthorizedAccessException ? 60 : 5);
            if (!item.AccessReported)
            {
                item.AccessReported = true;
                _progress(new ProgressState("Waiting for file access", $"WORDLYRICS_FILE_ACCESS: {Path.GetFileName(item.Path)} could not be read ({ex.GetType().Name}). Close any app writing to this file or check its permissions. The request is saved and retries automatically.", Error: true, Finished: true));
            }
            return false;
        }
        catch (Exception)
        {
            item.Length = -2; // a path nothing can be done with
            return false;
        }
    }

    /// <summary>A file appeared (or got its final name) in a library folder. True when it was added to the list.</summary>
    private bool Consider(string path, string root, TimeSpan quiet, bool save = true)
    {
        if (!Audio.Contains(Path.GetExtension(path))) return false;
        // Taken now, so that the quiet time counts from this moment and not from the first look.
        long length = -1;
        var stamp = default(DateTime);
        try
        {
            var file = new FileInfo(path);
            if (file.Exists) (length, stamp) = (file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed || !_options.Auto || AutomaticBlocked(path) || _items.ContainsKey(path)) return false;
            _items[path] = new Item { Path = path, Root = root, Action = _rules.Get(path).RetryUtc != default ? TimingAction.Fresh : TimingAction.Missing, Seen = now, QuietSince = now, Quiet = quiet, NotBefore = now, Length = length, Stamp = stamp };
            if (save) SaveState();
            return true;
        }
    }

    // ── running the program ──

    private string ListFile => Path.Combine(_data, "songs.txt");
    private string ResultFile => Path.Combine(_data, "result.json");
    private string OutputFile => Path.Combine(_data, "last run.log");
    private string ProgressFile => Path.Combine(_data, "progress.json");
    private string StateFile => Path.Combine(_data, "state.json");
    private string ControlFile => Path.Combine(_data, "control.txt");

    private void Start(Tool tool, List<Item> batch, Options options)
    {
        File.WriteAllLines(ListFile, batch.Select(i => i.Path), new UTF8Encoding(false));
        File.Delete(ResultFile);
        File.Delete(ProgressFile);
        var selectedSpeed = GamingBudget.Speed(options.Speed, _lightBudget);
        var speed = selectedSpeed.ToLowerInvariant() is "full" or "half" or "light" ? selectedSpeed.ToLowerInvariant() : "half";
        // Pause and Cancel need a program that looks at a control file (1.6.4 on); an older one runs without the buttons.
        var controlled = tool.SupportsControl;
        Process run;
        lock (_gate)
        {
            if (_disposed || _run is not null) return;
            // A user can ignore a song after Ripe inspected the batch, before process launch.
            batch = batch.Where(i => !IsIgnored(i.Path) && _items.TryGetValue(i.Path, out var current) && ReferenceEquals(current, i)).ToList();
            if (batch.Count == 0) return;
            File.WriteAllLines(ListFile, batch.Select(i => i.Path), new UTF8Encoding(false));
            if (controlled) WriteControl(_paused ? "pause" : "run");
            run = _run = tool.StartQuietRun(batch[0].Root, ListFile, ResultFile, OutputFile, speed, options.Backup, retry: batch[0].Asked, ProgressFile,
                controlled ? ControlFile : null, batch[0].Action, batch[0].Draft);
            _job = Job.Around(run);
            _activePaths.Clear();
            _activePaths.UnionWith(batch.Select(i => i.Path));
            _reported.Clear(); _lastCompleted = 0; _runProgress = 0; _yielding = false;
            _controlled = controlled;
            _cancelling = false;
            _restarting = false;
            _progressStamp = DateTime.MinValue; _progressMoved = _cpuMoved = DateTime.UtcNow; _cpuSeen = TimeSpan.Zero;
            _view = new View("Starting WordLyrics", $"{batch.Count} songs queued. Preparing backup and lyric search...", 0, batch.Count, null, false);
        }
        _log($"started WordLyrics on {batch.Count} song{(batch.Count == 1 ? "" : "s")} ({speed} speed)");
        lock (_gate) Emit();
        Task.Run(async () =>
        {
            try { await run.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception) { /* killed while we waited */ }
            Safe(() => Finished(run, batch));
        });
    }

    /// <summary>
    /// Owner #91: the friend's run sat at "Time the words 0/1" for good. A run whose progress file has not changed for
    /// <see cref="StuckAfter"/> and that has used no CPU for <see cref="IdleCpuAfter"/> is stuck: it is restarted (the songs
    /// stay, keeping their tries), twice at most for one batch; then it is stopped and the panel says so, with Restart.
    /// A paused run and one that is stopping are left alone.
    /// </summary>
    private void Watch(DateTime now)
    {
        Process? run;
        lock (_gate) { run = _run; if (run is null || _paused || _cancelling || _restarting) return; }
        try
        {
            var stamp = File.Exists(ProgressFile) ? File.GetLastWriteTimeUtc(ProgressFile) : DateTime.MinValue;
            if (stamp != _progressStamp) { _progressStamp = stamp; _progressMoved = now; }
            var cpu = run.TotalProcessorTime;
            if (cpu - _cpuSeen > TimeSpan.FromSeconds(2)) { _cpuSeen = cpu; _cpuMoved = now; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or NotSupportedException) { return; }
        if (now - _progressMoved < StuckAfter || now - _cpuMoved < IdleCpuAfter) return;
        if (++_autoRestarts <= MaxAutoRestarts)
        {
            _log($"WordLyrics made no progress for {StuckAfter.TotalMinutes:0} minutes and used no CPU; restarting it (the songs are kept)");
            Restart(automatic: true);
            return;
        }
        _log("WordLyrics stopped responding again; it is stopped. Press Restart in the word timing panel to try again");
        Restart(automatic: true);
        lock (_gate) { _restartNotice = null; foreach (var item in _items.Values.Where(i => !i.Deferred)) item.NotBefore = now + TimeSpan.FromMinutes(30); SaveState(); }
        _progress(new ProgressState("WordLyrics stopped responding", "It made no progress twice. The songs are kept and tried again in 30 minutes; press Restart to try now. If it keeps happening, run Set up WordLyrics again.", Finished: true, Error: true));
    }

    private void ReadProgress()
    {
        Process? active;
        lock (_gate) active = _run;
        if (active is null) return;
        try
        {
            var file = new FileInfo(ProgressFile);
            if (!file.Exists || file.Length > 64 * 1024) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(ProgressFile));
            var root = doc.RootElement;
            var completed = root.GetProperty("completed").GetInt32();
            var total = root.GetProperty("total").GetInt32();
            var detail = Text(root, "notice");
            string message = "Preparing WordLyrics";
            double? fraction = null;
            int stageCount = 0, stagesDone = 0;
            if (root.TryGetProperty("stages", out var stages))
                foreach (var stage in stages.EnumerateArray())
                {
                    stageCount++;
                    if (Text(stage, "state") == "done") stagesDone++;
                    if (Text(stage, "state") == "running")
                    {
                        message = Text(stage, "name");
                        detail = string.Join("\n", new[] { Text(stage, "count"), Text(stage, "current"), Text(stage, "note"), Text(root, "notice") }.Where(s => s.Length > 0));
                        detail += "\nWorking - last update " + file.LastWriteTime.ToString("HH:mm:ss");
                        var count = stage.GetProperty("total").GetDouble();
                        fraction = count > 0 ? Math.Clamp(stage.GetProperty("done").GetDouble() / count, 0, 1) : null;
                    }
                }
            var enginePaused = root.TryGetProperty("paused", out var rests) && rests.ValueKind == JsonValueKind.True;
            lock (_gate)
                if (ReferenceEquals(_run, active))
                {
                    _view = new View(message, detail, completed, total, fraction, enginePaused);
                    _runProgress = stageCount > 0 ? Math.Clamp((stagesDone + (fraction ?? 0)) / stageCount, 0, 1) : 0;
                    Emit();
                }
            // Owner #110: the song playing now, timed in this run: its lyrics are shown as soon as they are written.
            if (completed > _lastCompleted)
            {
                _lastCompleted = completed;
                string? playing = null;
                lock (_gate)
                    if (Watched?.Invoke() is { Length: > 0 } current)
                        // The song heard may be another copy of the one timed (a streamed song added to Songs): same file name.
                        playing = _activePaths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), Path.GetFileName(current), StringComparison.OrdinalIgnoreCase) && !_reported.Contains(p));
                if (playing is not null && (HasWordLyrics(playing) || EmbeddedWordLyrics.HasWords(playing)))
                {
                    lock (_gate) _reported.Add(playing);
                    _lyricsWritten([playing]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
    }

    private void Finished(Process run, List<Item> batch)
    {
        int code;
        try { code = run.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        Quietly(run.Dispose);
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var outcome = new Dictionary<string, (string Status, string Title)>(StringComparer.OrdinalIgnoreCase);
        var error = "";
        var reason = "";
        var report = "";
        if (code != ExitAlreadyRunning && File.Exists(ResultFile))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(ResultFile));
                var root = document.RootElement;
                error = Text(root, "error");
                report = Path.Combine(Text(root, "run_folder"), "report.html");
                // Owner #96: the run is over; its song copies that match their song exactly are duplicates now.
                if (Text(root, "run_folder") is { Length: > 0 } runFolder) BackupCleaner.CleanLater(runFolder, _log);
                if (root.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array)
                    foreach (var song in songs.EnumerateArray())
                    {
                        outcome[Usual(Text(song, "path"))] = (Text(song, "status"), Text(song, "title"));
                        if (reason.Length == 0 && Text(song, "status") is not "timed" and not "skipped") reason = Text(song, "reason");
                    }
                if (root.TryGetProperty("left_out", out var left) && left.ValueKind == JsonValueKind.Array)
                    foreach (var song in left.EnumerateArray())
                        outcome[Usual(Text(song, "path"))] = ("left_out", "");
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                error = "the result could not be read: " + ex.Message;
            }
        }

        var counts = new Dictionary<string, int>();
        var titles = new Dictionary<string, string>();
        bool asked = batch[0].Asked, stopped, cancelled, held, restarted; string? restartNotice;
        var firstDelays = 0;
        var dropped = 0;
        lock (_gate)
        {
            if (_disposed) return; // an old worker may not overwrite its replacement's checkpoint
            stopped = _disposed;
            held = _paused;                 // paused too late for this run to notice: the songs still waiting stay held
            cancelled = _cancelling && ReferenceEquals(_run, run);
            restarted = _restarting && ReferenceEquals(_run, run); restartNotice = _restartNotice; _restarting = false; _restartNotice = null;
            // Owner #108: stopped for the song playing now: like a restart, nothing is lost and nothing is the song's fault.
            if (_yielding && ReferenceEquals(_run, run)) { cancelled = false; restarted = true; }
            _yielding = false;
            if (!restarted && code != ExitAlreadyRunning) _autoRestarts = 0; // a run that ended by itself: the watchdog starts counting again
            if (ReferenceEquals(_run, run))
            {
                _run = null;
                _activePaths.Clear();
                _job?.Dispose();
                _job = null;
                _cancelling = false;
                _controlled = false;
                Quietly(() => File.Delete(ControlFile));
            }
            foreach (var item in batch)
            {
                if (_items.TryGetValue(item.Path, out var latest) && !ReferenceEquals(latest, item)) continue;
                var status = outcome.TryGetValue(item.Path, out var o) ? o.Status : "";
                var title = string.IsNullOrWhiteSpace(o.Title) ? Path.GetFileNameWithoutExtension(item.Path) : o.Title;
                if (code == ExitAlreadyRunning && !cancelled)
                {
                    item.NotBefore = now + TimeSpan.FromMinutes(1); // a run in another window: wait for it
                    continue;
                }
                if (stopped) continue; // Noctis is closing: the song stays on the list
                // Owner #91: ended by Restart or the watchdog: nothing is the song's fault; what was finished stays finished.
                if (restarted && status is not ("timed" or "lines_only" or "no_lyrics" or "rejected" or "not_timed" or "skipped")) continue;
                if (cancelled)
                {
                    // A stop by the user says nothing about a song: no failed try, no wait. What was finished
                    // stays finished (its lyrics are written); everything else is taken off the list below.
                    if (status is not ("timed" or "lines_only" or "no_lyrics" or "rejected" or "not_timed" or "skipped" or "left_out")) continue;
                }
                else
                {
                    if (status == "network_error" || (code == ExitSetupFailed && status is "" or "not_reached"))
                    {
                        item.NetworkWait = status == "network_error";
                        if (item.NetworkWait) Services.OnlineAccess.Shared.Failed();
                        // Not the song's fault, and it can last for hours (no internet, a broken setup):
                        // it keeps its tries and waits longer each time instead of being given up.
                        if (item.Waits == 0) firstDelays++;
                        item.NotBefore = now + (item.NetworkWait ? TimeSpan.FromSeconds(30) : Backoff(item.Waits));
                        item.Waits++;
                        continue;
                    }
                    if (status is "" or "failed" or "not_reached")
                    {
                        // Noctis may have been writing to the file, or the run broke off: once more, later
                        if (++item.Tries < MaxTries)
                        {
                            item.NotBefore = now + TimeSpan.FromMinutes(2);
                            continue;
                        }
                        status = status.Length == 0 ? "failed" : status;
                    }
                }
                if (IsIgnored(item.Path)) { _items.Remove(item.Path); continue; }
                if (!cancelled && status is "no_lyrics" or "rejected" or "not_timed" or "lines_only" or "failed")
                {
                    _rules.Set(item.Path, new(RetryUtc: now + SongRules.RetryAfter));
                    item.Deferred = true; item.NotBefore = now + SongRules.RetryAfter;
                    item.Action = TimingAction.Fresh; item.Draft = null; item.Urgent = default;
                    item.Ahead = false; item.AheadOnly = false; item.Tries = 0; item.NetworkWait = false;
                }
                else
                {
                    _items.Remove(item.Path);
                    if (status == "timed") _rules.Set(item.Path, new());
                }
                counts[status] = counts.GetValueOrDefault(status) + 1;
                titles[status] = title;
            }
            if (cancelled && !stopped)
            {
                // The songs of this batch that were not reached and all that were still waiting.
                dropped = _items.Count;
                _items.Clear();
            }
            SaveState();
        }
        if (stopped) return;

        var written = batch.Where(i => outcome.TryGetValue(i.Path, out var o) && o.Status is "timed" or "lines_only").Select(i => i.Path).ToArray();
        if (written.Length > 0) _lyricsWritten(written);

        if (restarted)
        {
            _log(restartNotice is null ? "WordLyrics made room for the song playing now; the others wait" : "WordLyrics was restarted; the waiting songs start again");
            if (restartNotice is not null) _progress(new ProgressState("Restarted", restartNotice));
            _timer.Change(TimeSpan.FromSeconds(1), Tick);
            return;
        }
        if (cancelled)
        {
            var finished = counts.GetValueOrDefault("timed") + counts.GetValueOrDefault("lines_only");
            _log($"WordLyrics was cancelled: {finished} finished, {dropped} taken off the waiting list");
            _progress(new ProgressState("Cancelled", CancelNotice(finished, dropped) + $"\nDetails: {ResultFile}", finished, batch.Count, Finished: true, Report: report));
            return;
        }

        if (code == ExitAlreadyRunning)
        {
            _log("WordLyrics is running in another window; trying again in a minute");
            _progress(new ProgressState("Waiting for another WordLyrics run", OtherHolder(), Total: batch.Count, Paused: held));
            return;
        }
        if (code == ExitSetupFailed)
        {
            _progress(new ProgressState("Setup is needed", "ENVIRONMENT_REPAIR_FAILED: private packages could not be repaired. Open Set up WordLyrics; the queued songs are saved and tried again later. Startup log: " + OutputFile, Finished: true, Error: true, Report: report, Paused: held));
            return;
        }
        _log($"WordLyrics finished (exit code {code}): " +
             (counts.Count == 0 ? "to be tried again" : string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}"))) +
             (error.Length > 0 ? " - " + error : ""));
        var message = Summary(counts, titles, asked);
        var timed = counts.GetValueOrDefault("timed");
        var failed = counts.Where(c => c.Key is not "timed" and not "skipped").Sum(c => c.Value);
        var detail = string.Join("\n", new[] { error, reason, $"Details: {ResultFile}", $"Startup log: {OutputFile}" }.Where(s => s.Length > 0));
        if (counts.Keys.Any(s => s is "no_lyrics" or "rejected" or "not_timed" or "lines_only" or "failed")) detail = "Next automatic try in 10 days.\n" + detail;
        var retrying = counts.Count == 0 && outcome.Count != 0 || (counts.Count == 0 && code != 0);
        _progress(new ProgressState(retrying ? "Retry scheduled" : timed > 0 ? $"{timed} songs received word timestamps" : "No new word timestamps were written",
            detail + (failed > 0 ? $"\n{failed} songs need attention." : "") + (held ? "\nPaused: the songs still waiting start when you press Resume." : ""),
            counts.Values.Sum(), batch.Count, Finished: true, Error: failed > 0 || code != 0, Report: report, Paused: held));
        // The result itself is shown by the status pill from the progress above; the summary stays in the log.
        if (message is not null) _log(message);
        // Said when a song first has to wait, not at every later try.
        if (firstDelays > 0 && (asked || Options.Notify))
            _notify($"Lyrics provider offline · {firstDelays} song{(firstDelays == 1 ? "" : "s")} retry later");
        _timer.Change(TimeSpan.FromSeconds(1), Tick); // anything else waiting?
    }

    /// <summary>One short result line; only successfully written lyrics count as saved.</summary>
    internal static string CancelNotice(int finished, int dropped)
        => $"{finished} saved · {dropped} removed from queue";

    /// <summary>Tells the progress window how things stand, with pause and cancel added to what the program last said. Called under the lock.</summary>
    private void Emit()
    {
        StatusChanged?.Invoke();
        if (_run is null)
        {
            if (_paused)
                _progress(new ProgressState("Paused", $"{_items.Count} song{(_items.Count == 1 ? " is" : "s are")} waiting. Press Resume to continue.", Paused: true));
            return;
        }
        var view = _view;
        var message = view.Message;
        var detail = view.Detail;
        if (_cancelling)
        {
            message = "Stopping WordLyrics";
            detail = "The song being timed is finished first. Songs that were not reached, and the songs still waiting, are taken off the list.\n" + detail;
        }
        else if (_paused)
        {
            message = "Paused";
            detail = (view.EnginePaused ? "WordLyrics is paused. Press Resume to continue." : "Pausing: the step in progress is finishing. Press Resume to continue.") + "\n" + detail;
        }
        _progress(new ProgressState(message, detail, view.Completed, view.Total, view.Fraction,
            Controllable: _controlled, Paused: _paused && !_cancelling, Cancelling: _cancelling));
    }

    /// <summary>Writes "pause", "run" or "stop" for the program to find. Called under the lock; the file is replaced whole.</summary>
    private void WriteControl(string word)
    {
        try
        {
            var temporary = ControlFile + ".tmp";
            File.WriteAllText(temporary, word, new UTF8Encoding(false));
            File.Move(temporary, ControlFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("the control file could not be written: " + ex.Message);
        }
    }

    /// <summary>How long a song waits after a run that could not do anything: 10 minutes, 30 minutes, an hour, then every 3 hours.</summary>
    internal static TimeSpan Backoff(int waits) => waits switch
    {
        <= 0 => TimeSpan.FromMinutes(10),
        1 => TimeSpan.FromMinutes(30),
        2 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(3),
    };

    /// <summary>One sentence for the notice pill, or null when there is nothing worth saying.</summary>
    private static string? Summary(Dictionary<string, int> counts, Dictionary<string, string> titles, bool asked)
    {
        int Count(string status) => counts.GetValueOrDefault(status);
        var total = counts.Values.Sum();
        if (total == 0) return null;
        if (total == 1)
        {
            var (status, title) = (counts.Keys.First(), titles.Values.First());
            return status switch
            {
                "timed" => $"Word-by-word lyrics are ready: {title}",
                "lines_only" => $"Line-by-line lyrics written (words could not be timed): {title}",
                "no_lyrics" => $"No lyrics were found for: {title}",
                "network_error" => $"Lyric provider unavailable. Open WordLyrics progress for the connection error: {title}",
                "rejected" => $"The model could not confirm the online lyrics for this recording: {title}",
                "not_timed" => $"The lyrics could not be timed well enough: {title}",
                "skipped" => asked ? $"Nothing to do (it already has word-by-word lyrics, or a twin file gets them): {title}" : null,
                "left_out" => asked ? $"WordLyrics could not use this file: {title}" : null,
                _ => $"WordLyrics could not read the song: {title}",
            };
        }
        var parts = new List<string>();
        if (Count("timed") > 0) parts.Add($"{Count("timed")} got word-by-word lyrics");
        if (Count("lines_only") > 0) parts.Add($"{Count("lines_only")} got line-by-line lyrics");
        if (Count("no_lyrics") > 0) parts.Add($"{Count("no_lyrics")} without lyrics");
        var other = total - Count("timed") - Count("lines_only") - Count("no_lyrics") - Count("skipped") - Count("left_out");
        if (other > 0) parts.Add($"{other} could not be timed");
        return parts.Count == 0 ? null : $"WordLyrics, {total} new songs: " + string.Join(", ", parts);
    }

    private static string NotReady(Tool tool)
    {
        const string how = " Use Set up WordLyrics; queued songs are saved.";
        if (!tool.Installed) return "The WordLyrics program is not installed." + how;
        // Owner #91: say what is missing, and that setup keeps what is there (a model downloaded for the old plugin is reused).
        var missing = tool.Missing();
        var what = missing.Count == 0 ? "" : " Missing: " + string.Join(", ", missing) + ".";
        return (tool.TooOld ? "The WordLyrics program needs an update." : "WordLyrics is not set up yet.") + what +
            " Setup reuses everything already downloaded." + how;
    }

    // ── the list of waiting songs, kept across restarts ──

    private void LoadState()
    {
        _lastSeen = DateTime.UtcNow;
        try
        {
            if (!File.Exists(StateFile)) return;
            if (new FileInfo(StateFile).Length > 16 * 1024 * 1024 || (File.GetAttributes(StateFile) & FileAttributes.ReparsePoint) != 0)
                throw new JsonException("Checkpoint is too large or redirected.");
            using var document = JsonDocument.Parse(File.ReadAllText(StateFile));
            var root = document.RootElement;
            if (root.TryGetProperty("lastSeenUtc", out var seen) && seen.TryGetDateTime(out var when)) _lastSeen = when.ToUniversalTime();
            if (!root.TryGetProperty("waiting", out var waiting) || waiting.ValueKind != JsonValueKind.Array) return;
            var now = DateTime.UtcNow;
            foreach (var entry in waiting.EnumerateArray())
            {
                string path = Text(entry, "path"), folder = Text(entry, "root");
                if (path.Length == 0 || folder.Length == 0 || (!File.Exists(path) && Directory.Exists(folder))) continue;
                _items[path] = new Item
                {
                    Path = path, Root = folder, Seen = now, QuietSince = now, NotBefore = now,
                    Asked = entry.TryGetProperty("asked", out var asked) && asked.ValueKind == JsonValueKind.True,
                    Action = entry.TryGetProperty("action", out var act) && Enum.TryParse<TimingAction>(act.GetString(), out var parsed) && Enum.IsDefined(parsed) ? parsed : TimingAction.Missing,
                    Draft = Text(entry, "draft") is { Length: > 0 } draft ? draft : null,
                    Tries = entry.TryGetProperty("tries", out var tries) && tries.TryGetInt32(out var tr) ? Math.Clamp(tr, 0, MaxTries) : 0,
                    Waits = entry.TryGetProperty("waits", out var waits) && waits.TryGetInt32(out var wa) ? Math.Clamp(wa, 0, 100) : 0,
                    NetworkWait = entry.TryGetProperty("networkWait", out var nw) && nw.ValueKind == JsonValueKind.True,
                    Deferred = entry.TryGetProperty("deferred", out var de) && de.ValueKind == JsonValueKind.True,
                };
                if (entry.TryGetProperty("notBeforeUtc", out var retry) && retry.TryGetDateTime(out var until))
                    _items[path].NotBefore = until.ToUniversalTime() > now.AddDays(31) ? now.Add(SongRules.RetryAfter) : until.ToUniversalTime();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("the list of waiting songs could not be read: " + ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Not in a form this plugin wrote: Noctis must still start. The file is kept for a look
            // (the next save replaces state.json), and the list starts empty.
            _items.Clear();
            try { File.Copy(StateFile, StateFile + ".broken", overwrite: true); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            _log("the list of waiting songs was not readable (" + ex.Message + "); a copy was kept as state.json.broken and the list starts empty");
        }
    }

    /// <summary>Called under the lock.</summary>
    private void SaveState()
    {
        _stateSaved = DateTime.UtcNow;
        try
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("lastSeenUtc", _lastSeen);
                json.WriteStartArray("waiting");
                foreach (var item in _items.Values.Where(i => i.Length != -2 && !i.AheadOnly))
                {
                    json.WriteStartObject();
                    json.WriteString("path", item.Path);
                    json.WriteString("root", item.Root);
                    json.WriteBoolean("asked", item.Asked);
                    json.WriteString("action", item.Action.ToString());
                    if (item.Draft is not null) json.WriteString("draft", item.Draft);
                    json.WriteNumber("tries", item.Tries); json.WriteNumber("waits", item.Waits);
                    json.WriteBoolean("networkWait", item.NetworkWait);
                    json.WriteBoolean("deferred", item.Deferred);
                    json.WriteString("notBeforeUtc", item.NotBefore);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            var temporary = StateFile + ".tmp";
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, StateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log("the list of waiting songs could not be saved: " + ex.Message);
        }
        StatusChanged?.Invoke();
    }

    internal void ShareRules(string source, string target)
    {
        try { _rules.Share(source, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _log("WordLyrics copy preferences could not be shared: " + ex.Message); }
    }
    internal bool IsIgnored(string path) => _rules.Get(path).Ignored;
    internal bool AutomaticBlocked(string path)
    {
        var rule = _rules.Get(path);
        return rule.Ignored || rule.RetryUtc > DateTime.UtcNow;
    }
    internal void SetIgnored(string path, bool ignored)
    {
        bool stopRun;
        lock (_gate)
        {
            _rules.Set(path, new(Ignored: ignored));
            stopRun = ignored && _activePaths.Any(IsIgnored);
            foreach (var item in _items.Values.Where(i => _rules.Get(i.Path).Ignored).ToArray()) _items.Remove(item.Path);
            SaveState();
        }
        // Stop any run containing this song safely; other songs keep their checkpoint.
        if (stopRun) MakeRoom("");
        StatusChanged?.Invoke();
    }

    // ── small helpers ──

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    internal static bool HasWordLyrics(string song)
    {
        var stem = Path.Combine(Path.GetDirectoryName(song) ?? "", Path.GetFileNameWithoutExtension(song));
        return WordLyrics.Any(extension => File.Exists(stem + extension));
    }

    /// <summary>The program names very long paths in Windows' long form; the list uses the usual one.</summary>
    private static string Usual(string path)
    {
        const string longForm = @"\\?\";
        if (path.StartsWith(longForm + @"UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[(longForm.Length + 4)..];
        return path.StartsWith(longForm, StringComparison.Ordinal) ? path[longForm.Length..] : path;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string path, string folder)
        => path.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nothing that goes wrong on one of our own threads may reach Noctis.</summary>
    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            try { _log($"error: {ex.GetType().Name}: {ex.Message}"); } catch { /* nothing more to do */ }
            try { _progress(new ProgressState("WordLyrics encountered an error", $"WORKER_ERROR ({ex.GetType().Name}): {ex.Message}", Finished: true, Error: true)); } catch { }
        }
    }

    private static void Quietly(Action action)
    {
        try { action(); } catch { /* shutting down */ }
    }
}
