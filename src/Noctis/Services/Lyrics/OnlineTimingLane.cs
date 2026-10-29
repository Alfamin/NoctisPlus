using Noctis.Models;

namespace Noctis.Services.Lyrics;

/// <summary>
/// #200 automatic online word timing for the songs on disk: the song playing first, then the next ones in the queue,
/// then every library song in the background (songs WordLyrics already timed included). One lookup per song, paced
/// (<see cref="Pace"/> between songs), definitive misses and "does not fit this recording" remembered for days, a
/// failure (offline, busy, error) never. Found and fitting (<see cref="OnlineTimingFit"/>) → the song's .ttml, marked
/// with its source, beside WordLyrics' .elrc (<see cref="LyricsWriter.WriteOnlineTiming"/>); anything else leaves the
/// song on its current route. Needs no WordLyrics install. Never blocks playback: it only writes sidecars and says so.
/// </summary>
internal sealed class OnlineTimingLane : IDisposable
{
    public enum Outcome { Written, AlreadyTimed, Skipped, Miss, NoFit, Kept, Transient, NoDecoder }

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

    /// <summary>Lyrics files written for these songs: the lyrics page reloads the one shown in place.</summary>
    public event Action<IReadOnlyList<string>>? Written;

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
        if (!libraryOpen) yield break;
        foreach (var t in _library) yield return t;
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
                if (outcome is Outcome.Written or Outcome.Miss or Outcome.NoFit or Outcome.Kept or Outcome.Transient)
                    await Task.Delay(Spacing, ct);   // the sources were asked: be gentle
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "Lyrics.OnlineTimingLane", ex.Message); await Task.Delay(Spacing, ct).ConfigureAwait(false); }
        }
    }

    /// <summary>One song, start to end. Everything but a failure marks it done for this run.</summary>
    internal async Task<Outcome> ProcessAsync(Track track, CancellationToken ct)
    {
        var path = Local(track)!;
        var outcome = await ProcessCoreAsync(track, path, ct);
        if (outcome != Outcome.Transient) lock (_gate) _done.Add(path);
        if (outcome == Outcome.Written) Written?.Invoke(new[] { path, track.FilePath }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return outcome;
    }

    async Task<Outcome> ProcessCoreAsync(Track track, string path, CancellationToken ct)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(track.Title)) return Outcome.Skipped;
        if (LyricsWriter.SidecarsOnDisk(path, ".lyricsfile").Count > 0) return Outcome.Skipped;
        var ttmls = LyricsWriter.SidecarsOnDisk(path, ".ttml");
        if (ttmls.Count > 0)
            return OnlineTimingSidecar.SourceOf(SafeRead(ttmls[0])) != null ? Outcome.AlreadyTimed : Outcome.Skipped;
        // Owner 2026-10-29: a song that already has word timing (WordLyrics, Lyrics Studio, anything) is fine as it is.
        if (LyricsWriter.SidecarsOnDisk(path, ".elrc").Count > 0) return Outcome.AlreadyTimed;
        if (_userTimed(path)) return Outcome.Skipped;

        var seconds = track.Duration.TotalSeconds;
        var key = OnlineTimingMisses.Key(track.Artist ?? "", track.Title, seconds);
        if (_misses.IsMiss(key, Now())) return Outcome.Miss;
        var found = await _timing.FindAsync(track.Artist ?? "", track.Title, seconds, ct);
        if (!found.Found)
        {
            if (found.Transient) return Outcome.Transient;
            _misses.Remember(key, Now());
            return Outcome.Miss;
        }
        if (_ffmpeg() is not { } ffmpeg) return Outcome.NoDecoder;
        var fit = await Check(ffmpeg, path, found.Ttml!, ct);
        if (fit == null) return Outcome.NoDecoder;   // the file would not decode: not a verdict on the timing
        if (fit.Shift is not { } shift)
        {
            _misses.Remember(key, Now());   // the same recording file, the same verdict next start
            return Outcome.NoFit;
        }
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
