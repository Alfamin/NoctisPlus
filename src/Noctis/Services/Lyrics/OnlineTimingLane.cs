using Noctis.Models;

namespace Noctis.Services.Lyrics;

/// <summary>
/// #200 automatic online word timing for the songs on disk: the song playing first, then the next ones in the queue,
/// then the library songs without word timing in the background (a song that has it is left as it is). One lookup per
/// song, paced (<see cref="Pace"/> between songs), definitive misses remembered for days, a failure (offline, busy,
/// error) never. Found → the song's .ttml, marked with its source (<see cref="LyricsWriter.WriteOnlineTiming"/>), moved
/// first when <see cref="OnlineTimingFit"/> is sure the file is shifted; nothing found → the current route. Needs no
/// WordLyrics install. Never blocks playback: it only writes sidecars and says so.
/// </summary>
internal sealed class OnlineTimingLane : IDisposable
{
    public enum Outcome { Written, AlreadyTimed, Skipped, Miss, Kept, Transient }

    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FailureRest = TimeSpan.FromMinutes(5);
    public const int Ahead = 3;

    readonly OnlineWordTiming _timing;
    readonly OnlineTimingMisses _misses;
    readonly LyricsWriter _writer;
    readonly Func<string?> _ffmpeg;
    readonly Func<bool> _offline;
    readonly Func<string, bool> _userTimed;
    readonly object _gate = new();
    readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    readonly CancellationTokenSource _stop = new();
    Track? _current;
    List<Track> _upNext = [];
    List<Track> _library = [];
    DateTime _restUntil;
    Task? _loop;

    readonly HashSet<string> _streamAsked = new(StringComparer.Ordinal);
    readonly Dictionary<string, OnlineWordTiming.Lookup> _provisional = new(StringComparer.Ordinal);

    /// <summary>Lyrics files written for these songs: the lyrics page reloads the one shown in place.</summary>
    public event Action<IReadOnlyList<string>>? Written;
    /// <summary>A stream's online word timing to show now (the song, its marked TTML, the source name); not on disk yet.</summary>
    public event Action<Track, string, string>? Provisional;

    /// <summary>The fit check; a seam for tests (the real one decodes with ffmpeg).</summary>
    internal Func<string, string, string, CancellationToken, Task<OnlineTimingFit.Result?>> Check { get; set; } = OnlineTimingFit.CheckAsync;
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    internal TimeSpan Spacing { get; set; } = Pace;
    /// <summary>A fullscreen game (the owner games): the library waits; the song being heard is still looked up.</summary>
    internal Func<bool> Busy { get; set; } = Features.WordLyrics.GamingBudget.IsActive;

    /// <param name="userTimed">True for a song whose word timing the user made or chose (Lyrics Studio's .elrc, a song
    /// taken out of word timing): never given an online .ttml that would out-rank it.</param>
    public OnlineTimingLane(OnlineWordTiming timing, OnlineTimingMisses misses, LyricsWriter writer, Func<string?> ffmpeg,
        Func<bool> offline, Func<string, bool> userTimed)
    {
        _timing = timing;
        _misses = misses;
        _writer = writer;
        _ffmpeg = ffmpeg;
        _offline = offline;
        _userTimed = userTimed;
    }

    /// <summary>A word-timed .elrc the app itself wrote (Lyrics Studio, a saved online word sync): the user's choice.
    /// WordLyrics' own .elrc is not in that registry, so those songs still get the online timing.</summary>
    public static bool StudioTimed(string path)
    {
        var elrc = LyricsWriter.SidecarsOnDisk(path, ".elrc");
        return elrc.Count > 0 && elrc.Any(Noctis.Helpers.AppWrittenSidecarRegistry.Default.Contains);
    }

    public void Start(TimeSpan firstLibraryDelay) => _loop ??= Task.Run(() => RunAsync(firstLibraryDelay, _stop.Token));

    /// <summary>The song playing and the queue after it (UI thread); replaces the earlier priorities.</summary>
    public void SetPlaying(Track? current, IEnumerable<Track> upNext)
    {
        lock (_gate) { _current = current; _upNext = upNext.Take(Ahead).ToList(); }
        Wake();
    }

    /// <summary>The library's songs (any thread); replaces the earlier list. Songs done this run are not asked again.</summary>
    public void SetLibrary(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        lock (_gate) _library = list;
        Wake();
    }

    void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }

    /// <summary>The next song to look at: playing, then the queue, then the library (after the first delay); null = idle.</summary>
    internal Track? Next(bool libraryOpen)
    {
        lock (_gate)
        {
            foreach (var t in Candidates(libraryOpen))
                if (Local(t) is { } path && !_done.Contains(path)) return t;
            return null;
        }
    }

    IEnumerable<Track> Candidates(bool libraryOpen)
    {
        if (_current != null) yield return _current;
        foreach (var t in _upNext) yield return t;
        // #207: songs WordLyrics is about to time (a download, a song fetched ahead, a new library song): before the library.
        foreach (var (path, since) in _wanted.OrderBy(w => w.Value).ToList())
        {
            if (Find(path) is { } wanted) { yield return wanted; continue; }
            if (Now() - since < FindGrace) continue;   // a new download may reach the library list a moment later
            _wanted.Remove(path);   // nothing to look it up by: WordLyrics goes on
            _done.Add(path);
        }
        if (!libraryOpen) yield break;
        foreach (var t in _library) yield return t;
    }

    Track? Find(string path)
    {
        bool Same(string? other) => string.Equals(other, path, StringComparison.OrdinalIgnoreCase);
        if (_current is { } c && Same(Local(c))) return c;
        return _upNext.FirstOrDefault(t => Same(Local(t))) ?? _library.FirstOrDefault(t => Same(t.FilePath)) ?? FromTags(path);
    }

    /// <summary>Owner #210: a song WordLyrics has waiting that no list knows (a stream-cache copy, an old queue entry):
    /// looked up by its own tags.</summary>
    static Track? FromTags(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = TagLib.File.Create(path);
            var title = file.Tag.Title;
            var artist = file.Tag.JoinedPerformers is { Length: > 0 } joined ? joined : file.Tag.FirstAlbumArtist;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
            return new Track { FilePath = path, Title = title, Artist = artist, Duration = file.Properties.Duration };
        }
        catch (Exception) { return null; }   // unreadable tags: WordLyrics goes on
    }

    /// <summary>#207: songs WordLyrics asked about, and since when. </summary>
    readonly Dictionary<string, DateTime> _wanted = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The longest WordLyrics waits once the lane is on its song (a source that hangs must not hold it back for
    /// good). Owner #210: a backlog of hundreds waits its turn; only the song being looked at counts against this.</summary>
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(90);
    string? _working;
    DateTime _workingSince;
    internal void StartedOn(string path) { lock (_gate) { _working = path; _workingSince = Now(); } }
    internal static readonly TimeSpan FindGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Owner #207, hard rule: lrc.red, Unison and Kugou come first for every song, wherever it comes from. WordLyrics asks
    /// before it times a song automatically: true = wait, the lane looks at it next (before the library); false = go on
    /// (looked at already, offline, resting after a failure, or its look took <see cref="WaitLimit"/>).
    /// </summary>
    public bool WantsFirst(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _offline()) return false;
        var now = Now();
        if (now < _restUntil) return false;
        lock (_gate)
        {
            if (_done.Contains(path)) { _wanted.Remove(path); return false; }
            if (!_wanted.ContainsKey(path)) _wanted[path] = now;
            if (string.Equals(_working, path, StringComparison.OrdinalIgnoreCase) && now - _workingSince >= WaitLimit)
            { _wanted.Remove(path); _done.Add(path); return false; }
        }
        Wake();
        return true;
    }

    /// <summary>A song is playing that is not looked at yet only because its file is still arriving.</summary>
    bool CurrentPending()
    {
        lock (_gate) return _current is { IsRemoteStream: false } t && Local(t) == null && (t.PendingStream != null || !string.IsNullOrWhiteSpace(t.FilePath));
    }

    /// <summary>The complete file to look at: a song on disk, or a stream once its file is fully saved; null while it is
    /// still arriving (the fit check needs the whole recording).</summary>
    static string? Local(Track? t)
    {
        if (t is null || t.IsRemoteStream) return null;
        var path = t.PendingStream is Noctis.ViewModels.PendingStream p ? p.Source.CompletedPlaybackPath(p.Song) : t.IsPendingStream ? null : t.FilePath;
        return string.IsNullOrWhiteSpace(path) || Features.ProgressivePlayback.ProgressiveTransfer.IsDownloading(path) ? null : path;
    }

    async Task RunAsync(TimeSpan firstLibraryDelay, CancellationToken ct)
    {
        var libraryAt = Now() + firstLibraryDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = Now();
                // A stream that just started: its word timing first, no file needed (owner 2026-10-29).
                if (!_offline() && StreamToShow() is { } streaming)
                {
                    await ShowWhileStreamingAsync(streaming, ct);
                    continue;
                }
                var track = _offline() || now < _restUntil ? null : Next(now >= libraryAt && !Busy());
                if (track == null)
                {
                    // The song playing may still be arriving: look again soon, so its lyrics come online before WordLyrics starts.
                    var wait = now < _restUntil ? _restUntil - now : CurrentPending() ? TimeSpan.FromSeconds(2) : now < libraryAt ? libraryAt - now : TimeSpan.FromMinutes(1);
                    await _wake.WaitAsync(wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait, ct);
                    continue;
                }
                var outcome = await ProcessAsync(track, ct);
                if (outcome == Outcome.Transient) _restUntil = Now() + FailureRest;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "Lyrics.OnlineTimingLane", ex.Message); await Task.Delay(Spacing, ct).ConfigureAwait(false); }
        }
    }

    /// <summary>One song, start to end. Everything but a failure marks it done for this run.</summary>
    internal async Task<Outcome> ProcessAsync(Track track, CancellationToken ct)
    {
        var path = Local(track)!;
        StartedOn(path);
        Outcome outcome;
        try { outcome = await ProcessCoreAsync(track, path, ct); }
        finally { lock (_gate) if (string.Equals(_working, path, StringComparison.OrdinalIgnoreCase)) _working = null; }
        if (outcome != Outcome.Transient) lock (_gate) { _done.Add(path); _wanted.Remove(path); }
        if (outcome == Outcome.Written) Written?.Invoke(Paths(path, track));
        return outcome;
    }

    /// <summary>lrc.red, Unison, Kugou (owner #209), paced: lrc.red is asked about one song every <see cref="Spacing"/>,
    /// counted from the last ask, so the fit check of one song overlaps the wait before the next.</summary>
    async Task<OnlineWordTiming.Lookup> FindAsync(Track track, double seconds, CancellationToken ct)
    {
        if (Interlocked.Read(ref _lastAskTicks) is var last && last != 0
            && last + (long)Spacing.TotalMilliseconds - Environment.TickCount64 is var waitMs && waitMs > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct);
        Interlocked.Exchange(ref _lastAskTicks, Environment.TickCount64);
        return await _timing.FindAsync(track.Artist ?? "", track.Title, seconds, ct);
    }
    long _lastAskTicks;   // Environment.TickCount64 of the last ask; 0: none yet

    static List<string> Paths(string path, Track track) =>
        new[] { path, track.FilePath }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Owner 2026-10-29: a song being streamed shows its online word timing at once, before its file has arrived (only
    /// the length is compared then). The same answer is written as the .ttml when the file is complete (moved first if
    /// the fit check is sure it is shifted) and the lyrics update in place; it is never taken back.
    /// </summary>
    internal async Task<bool> ShowWhileStreamingAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return false;
        var seconds = track.Duration.TotalSeconds;
        var key = OnlineTimingMisses.Key(track.Artist ?? "", track.Title, seconds);
        lock (_gate) if (!_streamAsked.Add(key)) return false;
        if (_misses.IsMiss(key, Now())) return false;
        var found = await FindAsync(track, seconds, ct);
        if (!found.Found)
        {
            // A source that could not answer is asked again with the finished file (never in a loop here).
            if (!found.Transient) _misses.Remember(key, Now());
            return false;
        }
        lock (_gate) _provisional[key] = found;
        var marked = OnlineTimingSidecar.Mark(found.Ttml!, found.Source!.Value, 0) ?? found.Ttml!;
        Provisional?.Invoke(track, marked, OnlineTimingSidecar.Name(found.Source!.Value));
        return true;
    }

    /// <summary>The song playing is a stream still arriving that was not looked up yet.</summary>
    Track? StreamToShow()
    {
        lock (_gate)
        {
            if (_current is not { IsRemoteStream: false } t || Local(t) != null || string.IsNullOrWhiteSpace(t.Title)) return null;
            if (t.PendingStream == null && string.IsNullOrWhiteSpace(t.FilePath)) return null;
            return _streamAsked.Contains(OnlineTimingMisses.Key(t.Artist ?? "", t.Title, t.Duration.TotalSeconds)) ? null : t;
        }
    }

    async Task<Outcome> ProcessCoreAsync(Track track, string path, CancellationToken ct)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(track.Title)) return Outcome.Skipped;
        if (LyricsWriter.SidecarsOnDisk(path, ".lyricsfile").Count > 0) return Outcome.Skipped;
        var ttmls = LyricsWriter.SidecarsOnDisk(path, ".ttml");
        if (ttmls.Count > 0)
            return OnlineTimingSidecar.SourceOf(SafeRead(ttmls[0])) != null ? Outcome.AlreadyTimed : Outcome.Skipped;
        var seconds = track.Duration.TotalSeconds;
        var key = OnlineTimingMisses.Key(track.Artist ?? "", track.Title, seconds);
        bool streamedOnline;
        lock (_gate) streamedOnline = _provisional.ContainsKey(key);
        // Owner 2026-10-29: a song that already has word timing (WordLyrics, Lyrics Studio, anything) is fine as it is;
        // except a stream already showing its online timing, whose WordLyrics file may have landed meanwhile.
        if (!streamedOnline && LyricsWriter.SidecarsOnDisk(path, ".elrc").Count > 0) return Outcome.AlreadyTimed;
        if (_userTimed(path)) return Outcome.Skipped;

        if (_misses.IsMiss(key, Now())) return Outcome.Miss;
        OnlineWordTiming.Lookup? shown;
        lock (_gate) _provisional.Remove(key, out shown);
        var found = shown ?? await FindAsync(track, seconds, ct);
        if (!found.Found)
        {
            if (found.Transient) return Outcome.Transient;
            _misses.Remember(key, Now());
            return Outcome.Miss;
        }
        // Owner 2026-10-29 (Circo Loco: shown at Play, then taken back for WordLyrics): the online timing of the same
        // recording stays; the fit check only moves it when it is sure the file is shifted (Kiss Me More +0.48 s).
        // Measured: 55 of 59 songs within 80 ms this way, none taken away; unsure (25 of 59) = left as it is.
        var shift = _ffmpeg() is { } ffmpeg && await Check(ffmpeg, path, found.Ttml!, ct) is { Shift: { } sure } ? sure : 0;
        var marked = OnlineTimingSidecar.Mark(OnlineTimingFit.Shift(found.Ttml!, shift), found.Source!.Value, shift);
        return marked != null && _writer.WriteOnlineTiming(path, marked) ? Outcome.Written : Outcome.Kept;
    }

    static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Wake();
    }
}
