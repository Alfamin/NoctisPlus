using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.ViewModels;

namespace Noctis.Features.TrueShuffle;

/// <summary>App-owned shuffle sessions. Queue intent is reported by the player directly; no
/// reflected queue adapter, visual-tree scan, menu injection or collection watcher.</summary>
internal sealed class TrueShuffleService : IDisposable
{
    private readonly ShuffleQueue _queue;
    private readonly string _data;
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly PlayTracker _tracker = new();
    private readonly List<(string Id, DateTime At)> _started = [];
    private readonly List<PlayRecord> _plays = [];
    private IPluginSettings? _settings;
    private CycleStore? _cycles;
    private ListenStore? _listens;
    private Task _load = Task.CompletedTask, _listenLoad = Task.CompletedTask, _save = Task.CompletedTask;
    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private string? _last, _lastPath, _historyError;
    private RollingQueue? _roll;
    private CancellationTokenSource? _operation;
    private int _revision;
    private bool _enabled, _ownChange, _changingList, _refilling, _saveFailing;
    private Track? _playing;
    private List<Track> _before = [], _manualBefore = [];
    private (RepeatMode Repeat, bool Autoplay)? _preferences;
    internal int WindowSize { get; set; } = 200;
    internal int RefillAt { get; set; } = 20;
    internal Task Pending { get; private set; } = Task.CompletedTask;
    internal Action<string>? Timing { get; set; }
    internal Func<CancellationToken, Task>? BeforePrepare { get; set; }
    private PlayerViewModel Player => _queue.Player;
    internal ShuffleScope ActiveScope => _roll?.Scope ?? DefaultScope();
    internal PlayerShuffleMode DefaultMode => _settings?.GetString("mode") switch
    {
        "Equal chance" => PlayerShuffleMode.EqualChance, "Discovery" => PlayerShuffleMode.Discovery,
        "Never played only" => PlayerShuffleMode.NeverPlayed, "For you" => PlayerShuffleMode.ForYou,
        _ => PlayerShuffleMode.NoRepeats,
    };
    internal bool FollowShortcut => string.IsNullOrWhiteSpace(_settings?.GetString("shortcut")) || _settings?.GetString("shortcut") == "Follow Noctis";
    internal bool CustomShortcut(Avalonia.Input.KeyEventArgs e)
    {
        var value = _settings?.GetString("shortcut");
        if (!_enabled || FollowShortcut || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var gesture = Avalonia.Input.KeyGesture.Parse(value!);
            return gesture.KeyModifiers != Avalonia.Input.KeyModifiers.None && gesture.Matches(e);
        }
        catch (FormatException) { return false; }
    }
    internal void SettingChanged(string key) { if (key == "open") Player.RequestShuffleModes(); }

    public TrueShuffleService(ShuffleQueue queue, string dataDirectory)
    {
        _queue = queue; _data = dataDirectory;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += Tick;
    }

    public void Start(IPluginSettings? settings, string featureDirectory)
    {
        if (_enabled) return;
        _enabled = true; _settings = settings;
        _plays.Clear(); _started.Clear(); _playing = null; _historyError = null;
        Player.NativeShuffle = this; Player.TrueShuffleAvailable = true;
        Player.ShuffleMode = Player.IsShuffleEnabled ? PlayerShuffleMode.Noctis : PlayerShuffleMode.Off;
        Player.PropertyChanged += PlayerChanged;
        Player.TrackStarted += TrackStarted;
        Player.ShuffleListStarting += ListStarting;
        Player.ShuffleListStarted += ListStarted;
        Player.ShuffleQueueCleared += Cleared;
        Player.TracksQueued += Queued;
        _cycles = new(featureDirectory); _listens = new(featureDirectory);
        var cycles = _cycles; var listens = _listens;
        _load = LoadCyclesAsync(cycles);
        // Only For you awaits listening-history import. Uniform/no-repeat modes do not need it.
        _listenLoad = LoadListensAsync(listens);
        _tick.Start();
    }

    private async Task LoadCyclesAsync(CycleStore store)
    {
        try
        {
            await _save;
            var loaded = await Task.Run(store.LoadAsync);
            if (!_enabled || !ReferenceEquals(_cycles, store)) return;
            _seen = new(loaded.Seen, StringComparer.Ordinal); _last = loaded.Last; _lastPath = loaded.LastPath; _historyError = null;
        }
        catch (Exception ex) { _historyError = ex.GetBaseException().Message; }
    }

    private async Task LoadListensAsync(ListenStore store)
    {
        try
        {
            var loaded = await Task.Run(() =>
            {
                var own = store.Load();
                var since = own.Count > 0 ? own.Min(p => p.StartUtc) : DateTime.UtcNow;
                return NoctisHistory.Read(Path.Combine(_data, "play_history.json"), since).Concat(own).OrderBy(p => p.StartUtc).ToList();
            });
            if (_enabled && ReferenceEquals(_listens, store)) _plays.InsertRange(0, loaded);
        }
        catch (Exception ex) { if (_enabled) Player.ShuffleStatus = "Listening history unavailable; For you uses library tags. " + ex.GetBaseException().Message; }
    }

    public Task SelectAsync(PlayerShuffleMode mode, ShuffleScope? source = null)
    {
        _operation?.Cancel(); _operation?.Dispose();
        _operation = new(); var ct = _operation.Token; var revision = ++_revision;
        Player.ShuffleStatus = "";
        Player.ShuffleMode = mode; // selection and lit state are immediate, including while preparing
        if (mode is PlayerShuffleMode.Off or PlayerShuffleMode.Noctis)
        {
            EndSession();
            _ownChange = true;
            try
            {
                if (Player.IsShuffleEnabled != (mode == PlayerShuffleMode.Noctis)) Player.ToggleShuffleCommand.Execute(null);
                Player.ShuffleMode = mode;
            }
            finally { _ownChange = false; Player.IsShuffleBusy = false; }
            return Pending = Task.CompletedTask;
        }
        if (!_enabled) { Player.ShuffleMode = PlayerShuffleMode.Off; Player.ShuffleStatus = "Enable True Shuffle in Settings → Features."; return Task.CompletedTask; }
        Player.IsShuffleBusy = true;
        return Pending = ApplyAsync((ShuffleMode)((int)mode - 2), source ?? DefaultScope(), revision, ct);
    }

    private ShuffleScope DefaultScope() => _settings?.GetString("scope") switch
    {
        "Whole library" => ShuffleScope.WholeLibrary,
        "Open playlist" => ShuffleScope.OpenPlaylist,
        _ => ShuffleScope.CurrentQueue,
    };

    private bool Current(int revision, CancellationToken ct) => _enabled && !ct.IsCancellationRequested && revision == _revision;

    private async Task ApplyAsync(ShuffleMode mode, ShuffleScope scope, int revision, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        void Stamp(string stage) => Timing?.Invoke($"{stage}: {clock.Elapsed.TotalMilliseconds:F2} ms");
        try
        {
            await _load.WaitAsync(ct); Stamp("cycle");
            if (_historyError is not null && mode is not (ShuffleMode.EqualChance or ShuffleMode.ForYou))
                throw new IOException("Remembered shuffle progress is unavailable; Equal chance still works. " + _historyError);
            if (mode == ShuffleMode.ForYou) await _listenLoad.WaitAsync(ct);
            Stamp("listens (For you only)");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var library = _queue.Snapshot(ShuffleScope.WholeLibrary);
                var source = _queue.Snapshot(scope);
                var continuing = scope == ShuffleScope.CurrentQueue ? _roll : null;
                if (continuing is not null) source = ResolveMembers(continuing, library);
                // A restored song with no upcoming rows is a usable player, not an empty
                // shuffle source. Seed from the library without replaying the current song.
                else if (scope == ShuffleScope.CurrentQueue && source.Count <= 1) { scope = ShuffleScope.WholeLibrary; source = library; }
                var effectiveScope = continuing?.Scope ?? scope;
                var playing = _queue.Current;
                var manual = Manual().Where(t => t.Id != playing?.Id).ToList();
                var known = _seen.ToArray(); var last = _last; var lastPath = _lastPath;
                var explicitAllowed = _queue.ExplicitAllowed;
                var skipDisliked = _settings?.GetBool("skipDisliked", false) ?? false;
                var inputs = mode == ShuffleMode.ForYou ? GatherTaste() : null;
                if (BeforePrepare is { } prepare) await prepare(ct);
                Stamp("snapshot");
                var prepared = await Task.Run(() => Prepare(source, library, manual, playing, mode, known, last, lastPath,
                    explicitAllowed, skipDisliked, inputs, ct), ct);
                Stamp("plan");
                if (!Current(revision, ct)) return;
                // Metadata/play-count writes cannot invalidate a click. A real membership or
                // current-song change is retried from a fresh snapshot without another click.
                var fresh = _queue.Snapshot(ShuffleScope.WholeLibrary);
                if (playing?.Id != _queue.Current?.Id || explicitAllowed != _queue.ExplicitAllowed ||
                    !ShuffleQueue.SameSongs(library, fresh) || continuing is null && scope != ShuffleScope.WholeLibrary && !ShuffleQueue.SameSongs(source, _queue.Snapshot(scope))) continue;
                if (prepared.Order.Count == 0)
                    throw new InvalidOperationException(mode == ShuffleMode.NeverPlayed ? "No eligible never-played songs remain. Try Discovery or another source."
                        : "No other eligible songs in this source. Try Whole library or another playlist.");
                // User additions made DURING preparation win too, and are not duplicated by the plan.
                manual = Manual().Where(t => t.Id != playing?.Id).ToList();
                var manualIds = manual.Select(t => t.Id).ToHashSet();
                var manualKeys = manual.Select(t => ShuffleQueue.SongOf(t, prepared.Identities).Key).ToHashSet(StringComparer.Ordinal);
                var ordered = prepared.Order.Where(e => !manualIds.Contains(e.Track.Id) && !manualKeys.Contains(e.Song.Key) && ShuffleEngine.IsEligible(ShuffleQueue.SongOf(e.Track, prepared.Identities), explicitAllowed, skipDisliked, DateTime.UtcNow)).ToList();
                var size = mode == ShuffleMode.ForYou ? Math.Min(WindowSize, 25) : WindowSize;
                var first = ordered.Take(size).Select(e => e.Track).ToList();
                _ownChange = true;
                try
                {
                    EndSession();
                    _preferences = (Player.RepeatMode, Player.AutoplayEnabled);
                    Player.RepeatMode = RepeatMode.Off; Player.AutoplayEnabled = false;
                    var order = manual.Concat(first).ToList();
                    // Owner #95: nothing playing yet starts on a song you have (no wait), as the playlist's Shuffle does.
                    if (playing is null && order.Count > 0 && order[0].IsPendingStream && order.FindIndex(t => !t.IsPendingStream) is > 0 and var owned)
                        (order[0], order[owned]) = (order[owned], order[0]);
                    _queue.Apply(order, keepCurrent: playing is not null);
                    _queue.BeginSession(Player.UpNext.Where(t => !manualIds.Contains(t.Id)));
                    _roll = new(mode, effectiveScope, skipDisliked, effectiveScope == ShuffleScope.WholeLibrary ? null : source,
                        ordered, first.Count, prepared.Identities) { Playlist = continuing?.Playlist ?? (effectiveScope == ShuffleScope.OpenPlaylist ? _queue.OpenPlaylistId : null) };
                    _seen = new(prepared.Plan.Seen, StringComparer.Ordinal); _last = prepared.History.Last; _lastPath = prepared.History.LastPath;
                    MarkCurrent();
                }
                finally { _ownChange = false; }
                Stamp("queue applied");
                SaveCycle();
                return;
            }
            throw new InvalidOperationException("The library is still changing. Wait for its scan, then choose a shuffle mode.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (Current(revision, ct))
            {
                Player.ShuffleMode = _roll is { } roll ? ToPlayer(roll.Mode) : Player.IsShuffleEnabled ? PlayerShuffleMode.Noctis : PlayerShuffleMode.Off;
                Player.ShuffleStatus = ex.GetBaseException().Message;
            }
        }
        finally { if (revision == _revision) { Player.IsShuffleBusy = false; ScheduleRefill(); } }
    }

    private sealed record Prepared(CycleData History, ShufflePlan Plan, List<ShuffleEntry> Order, Dictionary<string, string> Identities);
    private static Prepared Prepare(IReadOnlyList<Track> source, IReadOnlyList<Track> library, IReadOnlyList<Track> manual,
        Track? playing, ShuffleMode mode, string[] known, string? last, string? lastPath, bool explicitAllowed, bool skipDisliked,
        TasteInputs? taste, CancellationToken ct)
    {
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        var songs = new List<ShuffleEntry>(source.Count);
        foreach (var t in source) { ct.ThrowIfCancellationRequested(); songs.Add(new(t, ShuffleQueue.SongOf(t, identities))); }
        var history = ShuffleEngine.UpgradeHistory(library.Select(t => ShuffleQueue.SongOf(t, identities)), known, last, lastPath);
        var current = playing is null ? null : ShuffleQueue.SongOf(playing, identities).Key;
        var waiting = manual.Select(t => ShuffleQueue.SongOf(t, identities).Key).ToHashSet(StringComparer.Ordinal);
        var built = ShuffleEngine.Build(songs.Select(e => e.Song).Where(s => s.Remote || File.Exists(s.Path)), mode,
            history.Seen, current, history.Last, explicitAllowed, skipDisliked, upcoming: waiting, rank: taste is null ? null : Ranker(taste));
        ct.ThrowIfCancellationRequested();
        var byKey = songs.DistinctBy(e => e.Song.Key).ToDictionary(e => e.Song.Key);
        return new(history, built, built.Songs.Select(s => byKey[s.Key]).ToList(), identities);
    }

    private List<Track> Manual() => Player.ShuffleGenerated is { } generated
        ? Player.UpNext.Where(t => !generated.Contains(t)).ToList() : [];
    private static PlayerShuffleMode ToPlayer(ShuffleMode mode) => (PlayerShuffleMode)((int)mode + 2);
    private List<Track> ResolveMembers(RollingQueue roll, List<Track> library)
    {
        if (roll.Members is null) return library;
        var byId = library.DistinctBy(t => t.Id).ToDictionary(t => t.Id);
        return roll.Members.Select(t => byId.TryGetValue(t.Id, out var fresh) ? fresh : Keeps(t) ? t : null).OfType<Track>().ToList();
    }

    /// <summary>A song not in the library that still belongs in the shuffle: a server stream, or (owner #95) a playlist song
    /// you don't have, while "Play songs you don't have" is on (the queue fetches it as it comes up).</summary>
    private bool Keeps(Track t) => t.IsRemoteStream || t.IsPendingStream && Player.StreamMissingSongs;

    internal void ScheduleRefill()
    {
        if (_roll is null || _refilling || _changingList || Player.IsShuffleBusy) return;
        Dispatcher.UIThread.Post(() => { if (_roll is not null && !_refilling && !_changingList && !Player.IsShuffleBusy) Pending = RefillAsync(); }, DispatcherPriority.Background);
    }

    private async Task RefillAsync()
    {
        var roll = _roll; if (roll is null || roll.Finished || DateTime.UtcNow < roll.NotBefore) return;
        var threshold = roll.Mode == ShuffleMode.ForYou ? Math.Min(RefillAt, 6) : RefillAt;
        if (Player.UpNext.Count > threshold) return;
        _refilling = true; var revision = _revision;
        try
        {
            for (var round = 0; round < 3 && ReferenceEquals(roll, _roll) && revision == _revision; round++)
            {
                var library = _queue.Snapshot(ShuffleScope.WholeLibrary).DistinctBy(t => t.Id).ToDictionary(t => t.Id);
                var queued = Player.UpNext.Select(t => t.Id).ToHashSet();
                if (_queue.Current is { } current) queued.Add(current.Id);
                var batch = new List<Track>(); var size = roll.Mode == ShuffleMode.ForYou ? Math.Min(WindowSize, 25) : WindowSize;
                while (batch.Count < size && roll.Remaining > 0)
                {
                    var entry = roll.Plan[roll.Next++];
                    var t = library.GetValueOrDefault(entry.Track.Id) ?? (Keeps(entry.Track) ? entry.Track : null);
                    if (t is null || !queued.Add(t.Id) || !ShuffleQueue.NoFile(t) && !File.Exists(t.FilePath)) continue;
                    if (!ShuffleEngine.StillWanted(ShuffleQueue.SongOf(t, roll.Identities), roll.Mode, _seen, _queue.ExplicitAllowed, roll.SkipDisliked, DateTime.UtcNow)) continue;
                    batch.Add(t);
                }
                if (batch.Count > 0) { _ownChange = true; try { _queue.AppendUpcoming(batch); } finally { _ownChange = false; } }
                if (Player.UpNext.Count > threshold || roll.Remaining > 0) return;
                var known = _seen.ToArray();
                var source = ResolveMembers(roll, library.Values.ToList()); var waiting = Player.UpNext.ToList(); var playing = _queue.Current;
                var taste = roll.Mode == ShuffleMode.ForYou ? GatherTaste() : null;
                var explicitAllowed = _queue.ExplicitAllowed;
                var prepared = await Task.Run(() => Prepare(source, library.Values.ToList(), waiting, playing, roll.Mode, known, _last, _lastPath, explicitAllowed, roll.SkipDisliked, taste, CancellationToken.None));
                if (!_enabled || !ReferenceEquals(roll, _roll) || revision != _revision) return;
                if (prepared.Order.Count == 0)
                {
                    if (roll.Mode == ShuffleMode.NeverPlayed) { roll.Finished = true; Player.ShuffleStatus = "No never-played songs remain; the queued songs will finish."; }
                    else roll.NotBefore = DateTime.UtcNow.AddSeconds(10);
                    return;
                }
                _seen.ExceptWith(known.Except(prepared.Plan.Seen)); _seen.UnionWith(prepared.Plan.Seen);
                roll.Plan = prepared.Order; roll.Next = 0;
                if (prepared.Plan.NewCycle) SaveCycle();
            }
        }
        catch (Exception ex) { if (_enabled) { Player.ShuffleStatus = "Shuffle continuation stopped: " + ex.GetBaseException().Message; EndSession(); Player.ShuffleMode = PlayerShuffleMode.Off; } }
        finally { _refilling = false; }
    }

    private void ListStarting(IReadOnlyList<Track> source)
    {
        if (_ownChange || _roll is null) return;
        _changingList = true; _operation?.Cancel(); ++_revision;
        _before = Player.UpNext.ToList(); _manualBefore = Manual();
    }

    private void ListStarted(IReadOnlyList<Track> source)
    {
        if (!_changingList || _roll is not { } roll) return;
        _changingList = false;
        var playing = _queue.Current;
        var ids = source.Select(t => t.Id).ToHashSet();
        var beforeIds = _before.Select(t => t.Id).ToHashSet(); if (playing is not null) beforeIds.Add(playing.Id);
        var members = (roll.Members ?? _queue.Snapshot(ShuffleScope.WholeLibrary)).Select(t => t.Id).ToHashSet();
        bool same = ids.Count <= 1 || ids.All(beforeIds.Contains) ||
            ids.Count(id => members.Contains(id)) >= .9 * ids.Count && members.Count(id => ids.Contains(id)) >= .9 * members.Count;
        _ownChange = true;
        try
        {
            if (Player.IsShuffleEnabled) Player.ToggleShuffleCommand.Execute(null);
            var manual = _manualBefore.Where(t => t.Id != playing?.Id).ToList();
            var rest = (same ? _before : Player.UpNext.ToList()).Where(t => t.Id != playing?.Id && manual.All(m => m.Id != t.Id)).ToList();
            _queue.ReplaceUpcoming(manual.Concat(rest).ToList());
            _queue.BeginSession(rest.Where(t => !manual.Contains(t)));
            Player.ShuffleMode = ToPlayer(roll.Mode);
            MarkCurrent();
        }
        finally { _ownChange = false; }
        if (same) { Player.IsShuffleBusy = false; ScheduleRefill(); }
        else
        {
            // The list the user selected is now explicit native context, including rows above it.
            _roll = new(roll.Mode, ShuffleScope.CurrentQueue, roll.SkipDisliked, source.ToList(), [], 0, new());
            Pending = SelectAsync(ToPlayer(roll.Mode), ShuffleScope.CurrentQueue);
        }
    }

    private void Cleared()
    {
        if (_ownChange) return;
        _operation?.Cancel(); ++_revision; EndSession();
        Player.IsShuffleBusy = false; Player.ShuffleMode = Player.IsShuffleEnabled ? PlayerShuffleMode.Noctis : PlayerShuffleMode.Off;
    }

    private void Queued(object? sender, QueueAddedEventArgs e) => ScheduleRefill();
    private void TrackStarted(object? sender, Track track)
    {
        SampleListening();
        if (_ownChange || _changingList || _roll is null) return;
        MarkCurrent(); ScheduleRefill();
    }

    private void MarkCurrent()
    {
        if (_roll is null || _queue.Current is not { } t) return;
        var song = ShuffleQueue.SongOf(t, _roll.Identities);
        var changed = _seen.Add(song.Key); changed |= _seen.Add(song.LegacyKey);
        changed |= _last != song.Key; _last = song.Key; _lastPath = song.LegacyKey;
        if (changed && !_ownChange) SaveCycle();
    }

    private void PlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_ownChange || _changingList || _roll is null) return;
        if (e.PropertyName == nameof(Player.RepeatMode) && Player.RepeatMode != RepeatMode.Off ||
            e.PropertyName == nameof(Player.AutoplayEnabled) && Player.AutoplayEnabled ||
            e.PropertyName == nameof(Player.IsRadioActive) && Player.IsRadioActive ||
            e.PropertyName == nameof(Player.IsShuffleEnabled) && Player.IsShuffleEnabled)
        {
            // User-edited preferences must not be overwritten when the session releases control.
            if (e.PropertyName == nameof(Player.RepeatMode) && _preferences is { } p) _preferences = (Player.RepeatMode, p.Autoplay);
            if (e.PropertyName == nameof(Player.AutoplayEnabled) && _preferences is { } a) _preferences = (a.Repeat, Player.AutoplayEnabled);
            Cleared();
        }
    }

    private void EndSession()
    {
        if (_roll is not null) _roll.Finished = true;
        _roll = null; _queue.EndSession(); _changingList = false;
        if (_preferences is { } previous)
        {
            _preferences = null; var own = _ownChange; _ownChange = true;
            try { Player.RepeatMode = previous.Repeat; Player.AutoplayEnabled = previous.Autoplay; }
            finally { _ownChange = own; }
        }
    }

    private void SaveCycle()
    {
        if (_cycles is not { } store || _historyError is not null) return;
        var snapshot = new CycleData(1, _seen.ToArray(), _last, _lastPath);
        _save = _save.ContinueWith(async _ =>
        {
            try { await store.SaveAsync(snapshot); _saveFailing = false; }
            catch (Exception ex)
            {
                if (!_saveFailing) Dispatcher.UIThread.Post(() => { if (_enabled) Player.ShuffleStatus = "Shuffle progress could not be saved; retrying with the next song. " + ex.GetBaseException().Message; });
                _saveFailing = true;
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private void Tick(object? sender, EventArgs e) { SampleListening(); ScheduleRefill(); }
    private void SampleListening()
    {
        var track = _queue.Current;
        if (track != _playing)
        {
            if (_tracker.Running)
            {
                var peek = _tracker.Peek()!.Value;
                bool back = Player.UpNext.FirstOrDefault()?.Id == _playing?.Id;
                bool advanced = Player.History.FirstOrDefault()?.Id == _playing?.Id;
                EndListening(PlayTracker.Classify(_changingList, back, advanced,
                    TimeSpan.FromMilliseconds(peek.DurationMs) - peek.Position, peek.Coverage), peek.Position);
            }
            _playing = track;
            if (track is null) return;
            _tracker.Start(track.Id.ToString(), ShuffleQueue.LegacyKeyOf(track), track.Title, track.Artist, track.Duration,
                _roll?.Mode == ShuffleMode.ForYou ? "for-you" : _roll is null ? "noctis" : "true-shuffle", DateTime.UtcNow, _clock.Elapsed, Player.Position, Player.IsPlaying);
            _started.Add((track.Id.ToString(), DateTime.UtcNow));
            if (_started.Count > 2000) _started.RemoveRange(0, 1000);
        }
        _tracker.Sample(Player.Position, Player.IsPlaying, _clock.Elapsed);
    }

    private void EndListening(PlayEnd end, TimeSpan position)
    {
        var record = _tracker.End(end, position, DateTime.UtcNow, _clock.Elapsed);
        if (record is null || record.ListenedMs == 0 && record.End != PlayEnd.Skipped) return;
        _plays.Add(record);
        _listens?.Append(record, ex => Dispatcher.UIThread.Post(() => { if (_enabled) Player.ShuffleStatus = "Listening history could not be saved: " + ex.GetBaseException().Message; }));
    }

    private sealed record TasteInputs(List<TrackFacts> Facts, List<PlayRecord> Plays, List<string> Recent, List<string> Skipped,
        double Exploration, int Count, int Seed, DateTime Now, string Playlists);
    private TasteInputs GatherTaste()
    {
        SampleListening(); var now = DateTime.UtcNow; var sitting = now; var recent = new List<string>();
        if (_queue.Current is { } current) recent.Add(current.Id.ToString());
        for (var i = _started.Count - 1; i >= 0; i--) { if (sitting - _started[i].At > TimeSpan.FromMinutes(30)) break; sitting = _started[i].At; if (!recent.Contains(_started[i].Id)) recent.Add(_started[i].Id); }
        var skipped = _plays.Where(p => !p.Imported && p.StartUtc >= sitting && p.Outcome == PlayOutcome.StrongSkip).Select(p => p.TrackId).Distinct().ToList();
        var exploration = _settings?.GetString("forYouDiscovery") switch { "Familiar" => .1, "Adventurous" => .35, _ => .2 };
        return new(_queue.LibraryFacts(), _plays.ToList(), recent, skipped, exploration, Math.Min(WindowSize, 25), Environment.TickCount, now, Path.Combine(_data, "playlists.json"));
    }

    private static Func<List<Song>, List<Song>> Ranker(TasteInputs input) => songs =>
    {
        var model = TasteModel.Build(input.Plays, input.Facts, ReadPlaylists(input.Playlists), input.Now);
        var byId = songs.DistinctBy(s => s.Id).ToDictionary(s => s.Id);
        return ForYouPlanner.Plan(byId.Keys.ToList(), model, new(input.Recent, input.Skipped, input.Exploration, input.Count, input.Seed, input.Now)).Select(id => byId[id]).ToList();
    };

    internal static List<IReadOnlyCollection<string>> ReadPlaylists(string? path)
    {
        var result = new List<IReadOnlyCollection<string>>();
        try
        {
            if (path is null || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 64L * 1024 * 1024) return result;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path)); var lists = doc.RootElement;
            if (lists.ValueKind == JsonValueKind.Object && (lists.TryGetProperty("playlists", out var inner) || lists.TryGetProperty("Playlists", out inner))) lists = inner;
            if (lists.ValueKind != JsonValueKind.Array) return result;
            foreach (var list in lists.EnumerateArray())
            {
                if (list.ValueKind != JsonValueKind.Object) continue;
                if ((list.TryGetProperty("isSmartPlaylist", out var smart) || list.TryGetProperty("IsSmartPlaylist", out smart)) && smart.ValueKind == JsonValueKind.True) continue;
                if (!(list.TryGetProperty("trackIds", out var ids) || list.TryGetProperty("TrackIds", out ids)) || ids.ValueKind != JsonValueKind.Array) continue;
                result.Add(ids.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
    }

    public void Dispose()
    {
        if (!_enabled) return;
        _enabled = false; _operation?.Cancel(); ++_revision; _tick.Stop();
        if (_tracker.Running) EndListening(PlayEnd.Stopped, _tracker.Peek()?.Position ?? TimeSpan.Zero);
        Player.PropertyChanged -= PlayerChanged; Player.TrackStarted -= TrackStarted;
        Player.ShuffleListStarting -= ListStarting; Player.ShuffleListStarted -= ListStarted;
        Player.ShuffleQueueCleared -= Cleared; Player.TracksQueued -= Queued;
        EndSession(); Player.NativeShuffle = null; Player.TrueShuffleAvailable = false; Player.IsShuffleBusy = false;
        Player.ShuffleMode = Player.IsShuffleEnabled ? PlayerShuffleMode.Noctis : PlayerShuffleMode.Off;
        try { Task.WhenAll(_save, _listens?.Flushed ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
