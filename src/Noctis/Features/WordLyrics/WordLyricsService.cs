using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Search;

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
    internal event Action? PillChanged;
    internal event Action<IReadOnlyList<string>>? LyricsWritten;
    internal int WaitingCount => _worker?.WaitingCount ?? 0;
    internal void SetPreviewState(ProgressState state) => Progress(state);
    internal string[] Folders => _folders;
    internal IReadOnlyList<Track> Tracks => library.AllTracks;

    internal void Start(IPluginHost host)
    {
        _host = host; _disposed = false; _known.Clear(); _baseline = false; _startedUtc = DateTime.UtcNow;
        UpdateProxy(); if (_preferences is not null) _preferences.NativePreferenceChanged += PreferenceChanged;
        _folders = settings().MusicFolders.ToArray();
        _worker = new Worker(host.DataDirectory, () => _folders, ReadOptions(), Log, Notify, Progress,
            lyricsWritten: paths => Dispatcher.UIThread.Post(() => { if (!_disposed) LyricsWritten?.Invoke(paths); }));
        library.LibraryUpdated += LibraryChanged;
        library.MusicFoldersChanged += FoldersChanged;
        OnlineMusicSearch.FileSaved += DownloadSaved;
        LibraryChanged(null, EventArgs.Empty);
    }

    private Options ReadOptions()
    {
        var config = _host!.Settings;
        var mode = Enum.TryParse<WordTimingMode>(config.GetString(WordTimingSettings.ModeKey), out var value) && Enum.IsDefined(value) ? value : WordTimingMode.Medium;
        var oldValues = settings().PluginSettingValues?.FirstOrDefault(p => p.Key.Equals("dev.moshi.wordlyrics", StringComparison.OrdinalIgnoreCase)).Value;
        var oldSpeed = oldValues?.GetValueOrDefault("speed") ?? "Half";
        return new(mode != WordTimingMode.Off, WordTimingSettings.BackendSpeed(mode) ?? oldSpeed, config.GetBool("notify", true), config.GetString("folder") ?? "", config.GetString("backup") ?? "");
    }
    internal void SettingChanged(string key)
    {
        if (_worker is null) return;
        _worker.Options = ReadOptions();
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
                last = worker.Ask(path, action, draft);
                if (worker.WaitingCount > before) queued++;
            }
            return selected.Length == 1 ? last ?? "Queued." : $"{queued} songs queued. Existing word timing is kept when timing missing words.";
        });
    }

    private void Progress(ProgressState state) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        State = state; Changed?.Invoke();
        // A small native pill (icon + a few words), never a focus-stealing window over playback or a game.
        // "Show timing status" covers work in progress, "Say when a song is done" the result; problems always show.
        var pill = TimingPill.From(state);
        var settings = _host?.Settings;
        if (pill is { Kind: TimingPillKind.Working or TimingPillKind.Paused } && settings?.GetBool("showProgress", true) == false) pill = null;
        if (pill is { Kind: TimingPillKind.Done } && settings?.GetBool("notify", true) == false && settings.GetBool("showProgress", true) == false) pill = null;
        if (pill == Pill) return;
        Pill = pill; PillChanged?.Invoke();
    });
    internal Task PauseAsync(bool paused) => Task.Run(() => _worker?.SetPaused(paused));
    internal Task CancelAsync() => Task.Run(() => _worker?.Cancel());
    internal void OpenReport()
    {
        if (_worker is not { } worker || string.IsNullOrWhiteSpace(State.Report)) return;
        var path = Path.GetFullPath(State.Report);
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || !Inside(path, new Tool(worker.Options.Folder).Folder) || !File.Exists(path))
            throw new IOException("The result report is not available in the WordLyrics folder. The queue and current lyrics are kept.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
    internal const string SetupConsent = "Download and set up WordLyrics? The two AI models need about 1.33 GB, plus Python and required packages. Existing downloads are reused. This is optional; queued songs and existing lyrics are kept if you cancel.";
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
            await Task.Run(() => new Tool(worker.Options.Folder).OpenSetup(host.DataDirectory));
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
        _worker?.Dispose(); _worker = null; _host = null; _known.Clear(); Changed?.Invoke();
    }
}
