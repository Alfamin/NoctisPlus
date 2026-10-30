using Noctis.Models;

namespace Noctis.Services.Lyrics;

internal enum OnlineTimingSearchState { Idle, Searching, Found, Miss, Unavailable }

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
    public enum Outcome { Written, AlreadyTimed, Skipped, Miss, Kept, Transient, NotYet }

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
    readonly SemaphoreSlim _lrcRedPace = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    Track? _current;
    List<Track> _upNext = [];
    List<Track> _library = [];
    DateTime _restUntil;
    Task? _loop;
    CancellationTokenSource? _activeWork;

    readonly HashSet<string> _streamAsked = new(StringComparer.Ordinal);
    Track? _shownTrack;   // the stream whose online timing was last put on screen (each play of it is its own Track)
    readonly Dictionary<string, OnlineWordTiming.Lookup> _provisional = new(StringComparer.Ordinal);

    /// <summary>Lyrics files written for these songs: the lyrics page reloads the one shown in place.</summary>
    public event Action<IReadOnlyList<string>>? Written;
    /// <summary>A stream's online word timing to show now (the song, its marked TTML, the source name); not on disk yet.</summary>
    public event Action<Track, string, string>? Provisional;
    public event Action<Track, OnlineTimingSearchState>? SearchChanged;

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
        lock (_gate)
        {
            if (!ReferenceEquals(_current, current)) _activeWork?.Cancel();
            _current = current;
            _upNext = upNext.Take(Ahead).ToList();
        }
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
                // Owner #217: a song whose file is not there (yet, or any more: a stream cut off by a skip) waits; it was
                // marked done before and never looked at again when it came back.
                if (Local(t) is { } path && !_done.Contains(path) && File.Exists(path)) return t;
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
            if (!_wanted.ContainsKey(path)) { _wanted[path] = now; Log("WordLyrics waits: online first for " + Path.GetFileName(path)); }
            if (string.Equals(_working, path, StringComparison.OrdinalIgnoreCase) && now - _workingSince >= WaitLimit)
            {
                _wanted.Remove(path); _done.Add(path);
                Log($"WordLyrics goes on: the online look took over {WaitLimit.TotalSeconds:0} s for " + Path.GetFileName(path));
                return false;
            }
        }
        Wake();
        return true;
    }

    /// <summary>A song is playing that is not looked at yet only because its file is still arriving.</summary>
    bool CurrentPending()
    {
        lock (_gate)
            return _current is { IsRemoteStream: false } t && (Local(t) is { } path ? !File.Exists(path) : t.PendingStream != null || !string.IsNullOrWhiteSpace(t.FilePath));
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
            using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate) _activeWork = work;
            try
            {
                var now = Now();
                // A stream that just started: its word timing first, no file needed (owner 2026-10-29).
                if (!_offline() && StreamToShow() is { } streaming)
                {
                    await ShowWhileStreamingAsync(streaming, work.Token);
                    continue;
                }
                var track = _offline() || now < _restUntil ? null : Next(now >= libraryAt && !Busy());
                if (track == null)
                {
                    // The song playing may still be arriving: look again soon, so its lyrics come online before WordLyrics starts.
                    var wait = now < _restUntil ? _restUntil - now : CurrentPending() ? TimeSpan.FromSeconds(2) : now < libraryAt ? libraryAt - now : TimeSpan.FromMinutes(1);
                    await _wake.WaitAsync(wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait, work.Token);
                    continue;
                }
                var outcome = await ProcessAsync(track, work.Token);
                if (outcome == Outcome.Transient)
                {
                    _restUntil = Now() + FailureRest;
                    Log($"a source could not answer: the lane rests {FailureRest.TotalMinutes:0} min (WordLyrics does not wait meanwhile)");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { /* New playing song goes first. */ }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "Lyrics.OnlineTimingLane", ex.Message);
                Log("lane error: " + ex);
                await Task.Delay(Spacing, ct).ConfigureAwait(false);
            }
            finally { lock (_gate) if (ReferenceEquals(_activeWork, work)) _activeWork = null; }
        }
    }

    /// <summary>One song, start to end. Everything but a failure marks it done for this run.</summary>
    internal async Task<Outcome> ProcessAsync(Track track, CancellationToken ct)
    {
        var path = Local(track)!;
        StartedOn(path);
        SearchChanged?.Invoke(track, OnlineTimingSearchState.Searching);
        string why;
        lock (_gate) why = ReferenceEquals(track, _current) ? "playing" : _upNext.Contains(track) ? "up next" : _wanted.ContainsKey(path) ? "WordLyrics asked" : "library";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _detail = "";
        Outcome outcome;
        try { outcome = await ProcessCoreAsync(track, path, ct); }
        catch (OperationCanceledException) { SearchChanged?.Invoke(track, OnlineTimingSearchState.Idle); throw; }
        catch { SearchChanged?.Invoke(track, OnlineTimingSearchState.Unavailable); throw; }
        finally { lock (_gate) if (string.Equals(_working, path, StringComparison.OrdinalIgnoreCase)) _working = null; }
        if (outcome is not (Outcome.Transient or Outcome.NotYet)) lock (_gate) { _done.Add(path); _wanted.Remove(path); }
        if (outcome == Outcome.NotYet) { SearchChanged?.Invoke(track, OnlineTimingSearchState.Idle); return outcome; }
        SearchChanged?.Invoke(track, outcome switch
        {
            Outcome.Written or Outcome.AlreadyTimed => OnlineTimingSearchState.Found,
            Outcome.Miss => OnlineTimingSearchState.Miss,
            Outcome.Transient => OnlineTimingSearchState.Unavailable,
            _ => OnlineTimingSearchState.Idle
        });
        // The library pass would flood the log with songs that need nothing: those are told only for songs in use.
        if (why != "library" || outcome is Outcome.Written or Outcome.Kept or Outcome.Transient)
            Log($"{outcome} ({why}) {Describe(track)} in {clock.ElapsedMilliseconds} ms{_detail}");
        if (outcome == Outcome.Written) Written?.Invoke(Paths(path, track));
        return outcome;
    }


    /// <summary>Why the last song came out as it did (the loop's own thread only).</summary>
    string _detail = "";

    static void Log(string message) => DebugLog.Write("Online timing", message);
    static string Describe(Track t) => $"\"{t.Artist} - {t.Title}\" ({t.Duration.TotalSeconds:0} s)";

    /// <summary>lrc.red, Unison, Kugou (owner #209), paced: lrc.red is asked about one song every <see cref="Spacing"/>,
    /// counted from the last ask, so the fit check of one song overlaps the wait before the next.</summary>
    Task<OnlineWordTiming.Lookup> FindAsync(Track track, double seconds, CancellationToken ct) =>
        _timing.FindAsync(track.Artist ?? "", track.Title, seconds, ct, WaitForLrcRedAsync);

    async Task WaitForLrcRedAsync(CancellationToken ct)
    {
        await _lrcRedPace.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _lastAskTicks) is var last && last != 0
                && last + (long)Spacing.TotalMilliseconds - Environment.TickCount64 is var waitMs && waitMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct);
            Interlocked.Exchange(ref _lastAskTicks, Environment.TickCount64);
        }
        finally { _lrcRedPace.Release(); }
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
        var seconds = OnlineTimingDuration.Seconds(track);
        var key = OnlineTimingMisses.Key(track.Artist ?? "", track.Title, seconds);
        OnlineWordTiming.Lookup? known;
        lock (_gate)
        {
            if (ReferenceEquals(_shownTrack, track)) return false;
            _shownTrack = track;
            _provisional.TryGetValue(key, out known);
            if (known == null && !_streamAsked.Add(key)) return false;
        }
        // Owner #217: the same song played again before its file was whole (Previous, then back): the answer found the
        // first time is shown again, without asking again.
        if (known != null)
        {
            SearchChanged?.Invoke(track, OnlineTimingSearchState.Found);
            Log($"streaming {Describe(track)}: shown again from {OnlineTimingSidecar.Name(known.Source!.Value)} (found earlier)");
            Provisional?.Invoke(track, OnlineTimingSidecar.Mark(known.Ttml!, known.Source!.Value, 0) ?? known.Ttml!, OnlineTimingSidecar.Name(known.Source!.Value));
            return true;
        }
        if (_misses.IsMiss(key, Now())) { SearchChanged?.Invoke(track, OnlineTimingSearchState.Miss); return false; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SearchChanged?.Invoke(track, OnlineTimingSearchState.Searching);
        OnlineWordTiming.Lookup found;
        try { found = await FindAsync(track, seconds, ct); ct.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                _streamAsked.Remove(key);
                if (ReferenceEquals(_shownTrack, track)) _shownTrack = null;
            }
            SearchChanged?.Invoke(track, OnlineTimingSearchState.Idle);
            throw;
        }
        Log($"streaming {Describe(track)}: {(found.Found ? "shown from " + OnlineTimingSidecar.Name(found.Source!.Value) : "nothing to show")} in {clock.ElapsedMilliseconds} ms; {found.Trace}");
        if (!found.Found)
        {
            // A source that could not answer is asked again with the finished file (never in a loop here).
            // Catalog duration is not proof of a miss while bytes are arriving. Recheck the
            // finished recording with its actual duration instead of caching a bad estimate.
            SearchChanged?.Invoke(track, found.Transient ? OnlineTimingSearchState.Unavailable : OnlineTimingSearchState.Miss);
            return false;
        }
        SearchChanged?.Invoke(track, OnlineTimingSearchState.Found);
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
            if (ReferenceEquals(t, _shownTrack)) return null;
            var key = OnlineTimingMisses.Key(t.Artist ?? "", t.Title, OnlineTimingDuration.Seconds(t));
            return !_streamAsked.Contains(key) || _provisional.ContainsKey(key) ? t : null;
        }
    }

    async Task<Outcome> ProcessCoreAsync(Track track, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return Outcome.NotYet;   // owner #217: looked at when its file is there
        if (string.IsNullOrWhiteSpace(track.Title)) { _detail = ": no title"; return Outcome.Skipped; }
        if (LyricsWriter.SidecarsOnDisk(path, ".lyricsfile").Count > 0) { _detail = ": has a .lyricsfile"; return Outcome.Skipped; }
        var ttmls = LyricsWriter.SidecarsOnDisk(path, ".ttml");
        if (ttmls.Count > 0)
        {
            var saved = SafeRead(ttmls[0]);
            var source = OnlineTimingSidecar.SourceOf(saved);
            _detail = source != null ? ": has online timing from " + OnlineTimingSidecar.Name(source.Value) : ": has the user's own .ttml";
            // Owner #220: an online timing saved with masked words ("****") gets them filled once, from LRCLIB.
            if (source != null && OnlineTimingUncensor.Needs(saved) && !saved!.Contains(UncensorTried, StringComparison.Ordinal))
            {
                var (clean, note) = await _timing.UncensorAsync(saved, track.Artist ?? "", track.Title, track.Duration.TotalSeconds, ct);
                _detail += note;
                try { File.WriteAllText(ttmls[0], clean.Replace("plus:source=", UncensorTried + " plus:source="), new System.Text.UTF8Encoding(false)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Outcome.AlreadyTimed; }
                return clean == saved ? Outcome.Kept : Outcome.Written;
            }
            return source != null ? Outcome.AlreadyTimed : Outcome.Skipped;
        }
        var seconds = OnlineTimingDuration.Seconds(track, path);
        var key = OnlineTimingMisses.Key(track.Artist ?? "", track.Title, seconds);
        bool streamedOnline;
        lock (_gate) streamedOnline = _provisional.ContainsKey(key);
        // Repair earlier false misses caused by catalog length (#224), without replacing
        // a user's own timing or retiming every already-timed song.
        var correctedMiss = Math.Abs(seconds - track.Duration.TotalSeconds) > OnlineWordTiming.LengthSlack
            && _misses.IsMiss(OnlineTimingMisses.Key(track.Artist ?? "", track.Title, track.Duration.TotalSeconds), Now());
        // Owner 2026-10-29: a song that already has word timing (WordLyrics, Lyrics Studio, anything) is fine as it is;
        // except a stream already showing its online timing, whose WordLyrics file may have landed meanwhile.
        if (!streamedOnline && !correctedMiss && LyricsWriter.SidecarsOnDisk(path, ".elrc").Count > 0) { _detail = ": has an .elrc"; return Outcome.AlreadyTimed; }
        if (_userTimed(path)) { _detail = ": timed by the user"; return Outcome.Skipped; }

        if (_misses.IsMiss(key, Now())) { _detail = $": no source had it (remembered {OnlineTimingMisses.Keep.TotalDays:0} days)"; return Outcome.Miss; }
        OnlineWordTiming.Lookup? shown;
        lock (_gate) _provisional.TryGetValue(key, out shown);
        var found = shown ?? await FindAsync(track, seconds, ct);
        ct.ThrowIfCancellationRequested();
        _detail = shown != null ? "; the answer shown while streaming" : "; " + found.Trace;
        if (!found.Found)
        {
            if (found.Transient) return Outcome.Transient;
            _misses.Remember(key, Now());
            return Outcome.Miss;
        }
        // #222: display the playing song's answer before decoding/checking its complete file too.
        // Fit and quiet WordLyrics replacement still run; the raw answer survives a priority switch.
        bool playing;
        lock (_gate)
        {
            _provisional[key] = found;
            playing = ReferenceEquals(track, _current);
        }
        if (playing)
        {
            Log($"playing {Describe(track)}: shown before fit from {OnlineTimingSidecar.Name(found.Source!.Value)}; {found.Trace}");
            Provisional?.Invoke(track, OnlineTimingSidecar.Mark(found.Ttml!, found.Source!.Value, 0) ?? found.Ttml!, OnlineTimingSidecar.Name(found.Source!.Value));
        }
        // Owner 2026-10-29 (Circo Loco: shown at Play, then taken back for WordLyrics): the online timing of the same
        // recording stays; the fit check only moves it when it is sure the file is shifted (Kiss Me More +0.48 s).
        // Measured: 55 of 59 songs within 80 ms this way, none taken away; unsure (25 of 59) = left as it is.
        var ffmpegPath = _ffmpeg();
        var fit = ffmpegPath is { } ffmpeg ? await Check(ffmpeg, path, found.Ttml!, ct) : null;
        var shift = fit is { Shift: { } sure } ? sure : 0;
        _detail += $"; from {OnlineTimingSidecar.Name(found.Source!.Value)}, " + (ffmpegPath == null ? "no ffmpeg: not fit-checked" : fit == null ? "fit check could not read the audio"
            : (fit.Shift is { } moved ? $"fit sure: moved {moved:+0.00;-0.00} s" : "fit unsure: left as found")
              + $" (estimate {fit.Estimate:+0.00;-0.00} s, sharpness {fit.Sharpness:0.00}, significance {fit.Significance:0.0}, energy {fit.EnergyEstimate:+0.00;-0.00} s)");
        // #226 supersedes #215: uncertainty records fit confidence, never permission to replace
        // usable online timing automatically. WordLyrics repair is the user's explicit choice.
        var recheck = found.Source == WordTimingSource.Kugou && fit?.Shift == null;
        if (recheck) _detail += "; kept online (no automatic model replacement)";
        var marked = OnlineTimingSidecar.Mark(OnlineTimingFit.Shift(found.Ttml!, shift), found.Source!.Value, shift, unverified: recheck);
        var written = marked != null && _writer.WriteOnlineTiming(path, marked);
        if (written) lock (_gate) _provisional.Remove(key);
        if (!written) _detail += "; not written (a .ttml of the user's, read-only or unwritable)";
        return written ? Outcome.Written : Outcome.Kept;
    }

    /// <summary>Owner #220: a saved online timing whose masked words were looked up once (not again every start).</summary>
    const string UncensorTried = "plus:uncensor=\"tried\"";

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
