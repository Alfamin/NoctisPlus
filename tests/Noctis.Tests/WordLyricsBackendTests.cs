using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Noctis.Features.WordLyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>Real child-process/file/control protocol, fake Python model. No network, audio
/// mutation, models or installed WordLyrics files are used.</summary>
public class WordLyricsBackendTests
{
    private sealed class Rig : IDisposable
    {
        internal readonly TestPersistenceService Data = new();
        internal readonly string App, Music, Song, Work;
        private readonly string? _before = Environment.GetEnvironmentVariable("WORDLYRICS_PYTHON");
        internal Rig(bool hold = false, string outcome = "timed", bool stuck = false, double done = 1, bool earlyWords = false, string? crashOn = null)
        {
            var python = Environment.GetEnvironmentVariable("NOCTIS_TEST_PYTHON") ?? "python";
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(python) { ArgumentList = { "--version" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                if (probe is null || !probe.WaitForExit(5000) || probe.ExitCode != 0) Assert.Skip("Python is unavailable for the synthetic backend-process fixture.");
            }
            catch (System.ComponentModel.Win32Exception) { Assert.Skip("Python is unavailable for the synthetic backend-process fixture."); }
            Environment.SetEnvironmentVariable("WORDLYRICS_PYTHON", python);
            App = Path.Combine(Data.DataDirectory, "backend"); Music = Path.Combine(Data.DataDirectory, "فارسی %TEMP% & (music)"); Work = Path.Combine(Data.DataDirectory, "work");
            Directory.CreateDirectory(Music); Directory.CreateDirectory(Work);
            var module = Directory.CreateDirectory(Path.Combine(App, "wordlyrics")).FullName;
            File.WriteAllText(Path.Combine(module, "__init__.py"), "__version__ = \"1.6.4\"\n");
            File.WriteAllText(Path.Combine(App, "WordLyrics.bat"), "fixture only");
            File.WriteAllText(Path.Combine(module, "__main__.py"), "# --confirm-redo is supported by this synthetic backend\n");
            File.WriteAllText(Path.Combine(App, "fixture.json"), JsonSerializer.Serialize(new { hold, outcome, stuck, done, earlyWords, crashOn }));
            File.WriteAllText(Path.Combine(module, "models.py"), "class ModelError(Exception):pass\ndef missing(folder):return []\ndef download(*a,**k):raise Exception('Original download must never run')\n");
            var run = """
                import json,sys,time,os
                from pathlib import Path
                from urllib.parse import urlsplit
                args=sys.argv[1:]
                def value(key):return args[args.index(key)+1]
                config=json.loads(Path('fixture.json').read_text())
                songs=Path(value('--songs-from')).read_text(encoding='utf-8').splitlines()
                def put(path,data):
                    target=Path(path);tmp=target.with_suffix('.tmp');tmp.write_text(json.dumps(data),encoding='utf-8')
                    # A Windows reader can briefly hold this file during its next progress poll.
                    # The fixture tests model preemption, not atomic-replace sharing violations.
                    for attempt in range(30):
                        try:os.replace(tmp,target);return
                        except PermissionError:
                            if attempt==29:raise
                            time.sleep(.01)
                control=Path(value('--control')) if '--control' in args else None
                if config.get('stuck'): time.sleep(60)  # owner #91: a model call that never returns: no progress, no CPU
                deadline=time.monotonic()+15
                while config['hold'] and time.monotonic()<deadline:
                    word=control.read_text() if control and control.exists() else 'run'
                    if config.get('earlyWords'): Path(songs[0]).with_suffix('.elrc').write_text('[00:01.00]<00:01.00>synthetic words',encoding='utf-8')
                    put(value('--progress'),{'completed':1 if config.get('earlyWords') else 0,'total':len(songs),'paused':word=='pause','stages':[{'name':'Align words','state':'running','done':config.get('done',1),'total':4,'current':'Synthetic song'}]})
                    if word=='stop':break
                    time.sleep(.05)
                # Owner #161: a song that brings the whole program down: no result file at all.
                if config.get('crashOn') and any(config['crashOn'] in Path(p).name for p in songs): os._exit(9)
                proxy=urlsplit(os.environ.get('https_proxy',''))
                put(value('--result'),{'run_folder':'','songs':[{'path':p,'title':Path(p).stem,'status':config['outcome']} for p in songs],
                    'args':args,'authenticated_loopback_proxy':proxy.hostname=='127.0.0.1' and bool(proxy.password)})
                sys.exit(4 if config['outcome']=='network_error' else 0)
                """;
            File.WriteAllText(Path.Combine(module, "runner.py"), "def probe(python):return []\ndef main():\n" +
                "    if not ensure(__import__('os').getcwd()):return 70\n" + string.Join("\n", run.Split('\n').Select(line => "    " + line)) + "\n");
            Song = Path.Combine(Music, "آهنگ %NAME% & song.mp3"); File.WriteAllText(Song, "synthetic audio remains unchanged");
        }
        internal Worker Worker(Action<ProgressState> progress) => new(Work, () => [Music], new(true, "Light", false, App, ""), _ => { }, _ => { }, progress, readiness: _ => true);
        internal string Result => Path.Combine(Work, "result.json");
        public void Dispose() { Environment.SetEnvironmentVariable("WORDLYRICS_PYTHON", _before); Data.Dispose(); }
    }
    private static async Task Until(Func<bool> condition, Func<string>? diagnostic = null)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
        Assert.True(condition(), "Synthetic backend did not reach the expected state. " + diagnostic?.Invoke());
    }
    [Fact]
    public void FinishedWordsSuppressStaleAutomaticQueuedPill_ButExplicitRepairRemainsVisible()
    {
        using var rig = new Rig();
        using var worker = new Worker(rig.Work, () => [rig.Music], new(false, "Light", false, rig.App, ""), _ => { }, _ => { }, _ => { }, startTimer: false, readiness: _ => false);
        worker.Ask(rig.Song);
        Assert.Equal(SongTimingStatus.Queued, worker.StatusFor(rig.Song));
        File.WriteAllText(Path.ChangeExtension(rig.Song, ".elrc"), "[00:01.00]<00:01.00>word");
        Assert.Equal(SongTimingStatus.None, worker.StatusFor(rig.Song));
        worker.Ask(rig.Song, TimingAction.Current);
        Assert.Equal(SongTimingStatus.Queued, worker.StatusFor(rig.Song));
    }

    private const string OnlineTtml = "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"1.0\" end=\"2.0\"><span begin=\"1.0\" end=\"2.0\">word</span></p></div></body></tt>";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WordLyricsChosenByHand_ShowsInsteadOfTheOnlineTiming_TheUsersOwnTtmlStays(bool online)
    {
        // #200: a .ttml out-ranks the .elrc, so Retime on an online-timed song would never show without this.
        using var rig = new Rig(); var states = new ConcurrentQueue<ProgressState>();
        var ttml = Path.ChangeExtension(rig.Song, ".ttml");
        File.WriteAllText(ttml, online ? Noctis.Services.Lyrics.OnlineTimingSidecar.Mark(OnlineTtml, Noctis.Services.Lyrics.WordTimingSource.LrcRed, 0)! : OnlineTtml);
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song, TimingAction.Current);
        await Until(() => states.Any(s => s.Finished));
        Assert.Equal(!online, File.Exists(ttml));
    }

    [Fact]
    public async Task RetimeOnAnOnlineTimedSong_GetsItsWords_AndTheOnlineTimingComesBackWhenNothingWasTimed()
    {
        // #213 (ALL RED): WordLyrics leaves a song with any .ttml alone ("protected"), so Retime never ran.
        using var rig = new Rig(outcome: "no_lyrics"); var states = new ConcurrentQueue<ProgressState>();
        var ttml = Path.ChangeExtension(rig.Song, ".ttml");
        var lrc = Path.ChangeExtension(rig.Song, ".lrc");
        var online = Noctis.Services.Lyrics.OnlineTimingSidecar.Mark(OnlineTtml, Noctis.Services.Lyrics.WordTimingSource.LrcRed, 0)!;
        File.WriteAllText(ttml, online);
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song, TimingAction.Current);
        await Until(() => states.Any(s => s.Finished));
        Assert.Equal(online, File.ReadAllText(ttml));
        Assert.False(File.Exists(lrc));
        Assert.Empty(Directory.EnumerateFiles(rig.Work, "*", SearchOption.AllDirectories).Where(f => f.Contains("held-online")));
    }

    [Fact]
    public void TheOnlineWordsGoToWordLyricsAsLineTimedLyrics()
    {
        var lines = Worker.LinesOf(OnlineTtml)!;
        Assert.Equal("[00:01.00]word\n", lines);
    }

    [Fact]
    public async Task AutomaticTiming_NeverRemovesAnOnlineTiming()
    {
        using var rig = new Rig(); var states = new ConcurrentQueue<ProgressState>();
        var ttml = Path.ChangeExtension(rig.Song, ".ttml");
        File.WriteAllText(ttml, Noctis.Services.Lyrics.OnlineTimingSidecar.Mark(OnlineTtml, Noctis.Services.Lyrics.WordTimingSource.Unison, 0)!);
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song, TimingAction.Fresh, null, out _, automatic: true);
        await Until(() => states.Any(s => s.Finished));
        Assert.True(File.Exists(ttml));
    }
    [Theory]
    [InlineData("no_lyrics")]
    [InlineData("not_timed")]
    [InlineData("rejected")]
    [InlineData("lines_only")]
    public async Task UnavailableLyricsWaitTenDays_AcrossPlaybackAheadNetworkAndRestart(string outcome)
    {
        using var rig = new Rig(outcome: outcome); var states = new ConcurrentQueue<ProgressState>();
        using (var worker = rig.Worker(states.Enqueue))
        {
            worker.Ask(rig.Song);
            await Until(() => states.Any(s => s.Finished));
            Assert.Equal(0, worker.WaitingCount); // dormant retries are not processing/queued songs
            Assert.Equal(SongTimingStatus.None, worker.StatusFor(rig.Song));
            Assert.InRange(worker.NextTryUtc(DateTime.UtcNow)!.Value, DateTime.UtcNow.AddDays(9.99), DateTime.UtcNow.AddDays(10.01));
            worker.AskPlayed(rig.Song, out var accepted); Assert.False(accepted);
            worker.Prioritize(rig.Song); worker.SetAhead([rig.Song], null); worker.RetryNetworkWaits(); worker.Restart();
            Assert.False(worker.EnqueueAutomatic(rig.Song, rig.Music, true));
            Assert.Equal(0, worker.AskMany([rig.Song]));
            Assert.True(worker.AutomaticBlocked(rig.Song));
        }
        File.Delete(rig.Result);
        using var resumed = rig.Worker(_ => { }); resumed.TickNow();
        await Task.Delay(150);
        Assert.False(File.Exists(rig.Result)); Assert.True(resumed.AutomaticBlocked(rig.Song));
        Assert.Equal(0, resumed.WaitingCount);
        // The same recording in a stream/library folder shares the cooldown.
        var copy = Path.Combine(Directory.CreateDirectory(Path.Combine(rig.Music, "cache")).FullName, Path.GetFileName(rig.Song));
        File.Copy(rig.Song, copy);
        resumed.AskPlayed(copy, out var copyAccepted); Assert.False(copyAccepted);
    }

    [Fact]
    public async Task ManualRetryBypassesCooldown_AndFetchesFreshLyrics()
    {
        using var rig = new Rig(outcome: "no_lyrics"); var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue); worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Finished));
        File.Delete(rig.Result); File.WriteAllText(Path.Combine(rig.App, "fixture.json"), "{\"hold\":false,\"outcome\":\"timed\"}");
        worker.Ask(rig.Song);
        await Until(() => File.Exists(rig.Result) && !worker.IsWaiting(rig.Song));
        using var result = JsonDocument.Parse(File.ReadAllText(rig.Result));
        var args = result.RootElement.GetProperty("args").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Contains("--repair-lyrics", args); Assert.Contains("--ignore-lyric-source", args);
        Assert.False(worker.AutomaticBlocked(rig.Song));
    }

    [Fact]
    public async Task ExpiredCooldownAutomaticallyRefetches_WithoutReplayingOldDraft()
    {
        using var rig = new Rig(outcome: "not_timed"); var states = new ConcurrentQueue<ProgressState>();
        using (var worker = rig.Worker(states.Enqueue))
        {
            worker.Ask(rig.Song); await Until(() => states.Any(s => s.Finished));
        }
        var stateFile = Path.Combine(rig.Work, "state.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(stateFile))!;
        state["waiting"]![0]!["notBeforeUtc"] = DateTime.UtcNow.AddSeconds(-1);
        File.WriteAllText(stateFile, state.ToJsonString());
        var rulesFile = Path.Combine(rig.Work, "song-rules.json");
        var rules = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(rulesFile))!;
        foreach (var (_, rule) in rules["Rules"]!.AsObject()) rule!["RetryUtc"] = DateTime.UtcNow.AddSeconds(-1);
        File.WriteAllText(rulesFile, rules.ToJsonString());
        File.Delete(rig.Result); File.WriteAllText(Path.Combine(rig.App, "fixture.json"), "{\"hold\":false,\"outcome\":\"timed\"}");
        using var resumed = rig.Worker(_ => { }); resumed.TickNow();
        await Until(() => File.Exists(rig.Result) && !resumed.IsWaiting(rig.Song));
        using var result = JsonDocument.Parse(File.ReadAllText(rig.Result));
        Assert.Contains("--repair-lyrics", result.RootElement.GetProperty("args").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void IgnoringASongIsDurableReversible_AndPreservesLyricsAndOtherRecordings()
    {
        using var rig = new Rig();
        var copy = Path.Combine(Directory.CreateDirectory(Path.Combine(rig.Music, "cache")).FullName, Path.GetFileName(rig.Song));
        File.Copy(rig.Song, copy);
        var unrelated = Path.Combine(Directory.CreateDirectory(Path.Combine(rig.Music, "unrelated")).FullName, Path.GetFileName(rig.Song));
        File.WriteAllText(unrelated, "different recording");
        File.WriteAllText(Path.ChangeExtension(rig.Song, ".lrc"), "[00:01.00]Keep my lyrics");
        using (var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""), _ => { }, _ => { }, startTimer: false))
        {
            worker.Ask(rig.Song); worker.SetIgnored(rig.Song, true);
            Assert.Equal(0, worker.WaitingCount); Assert.True(worker.IsIgnored(copy)); Assert.False(worker.IsIgnored(unrelated));
            worker.AskPlayed(rig.Song, out var accepted); Assert.False(accepted);
            worker.SetAhead([copy], null); Assert.Equal(0, worker.WaitingCount);
            Assert.False(worker.EnqueueAutomatic(rig.Song, rig.Music)); Assert.Equal(0, worker.AskMany([rig.Song]));
            Assert.Contains("off", worker.Ask(rig.Song, TimingAction.Fresh));
        }
        using var restored = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""), _ => { }, _ => { }, startTimer: false);
        Assert.True(restored.IsIgnored(copy)); restored.SetIgnored(copy, false);
        Assert.False(restored.IsIgnored(rig.Song)); Assert.True(restored.EnqueueAutomatic(rig.Song, rig.Music));
        Assert.Equal("[00:01.00]Keep my lyrics", File.ReadAllText(Path.ChangeExtension(rig.Song, ".lrc")));
    }

    [Fact]
    public void EverySongWaitingForWordLyrics_AsksTheOnlineSourcesFirst_TimeLibraryAndRestartToo()
    {
        // Owner #207/#210: a backlog, a Time library press, Restart: all go through the online sources before WordLyrics.
        using var rig = new Rig();
        var asked = new List<string>();
        using var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""), _ => { }, _ => { }, startTimer: false)
        { WaitsForOnline = p => { lock (asked) asked.Add(p); return true; } };
        Assert.Equal(1, worker.AskMany([rig.Song]));   // Time library: pressed by hand, still online first
        worker.TickNow();
        Assert.Contains(asked, p => string.Equals(p, Path.GetFullPath(rig.Song), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, worker.WaitingCount);                                 // kept in line, not started
        Assert.Equal(SongTimingStatus.None, worker.StatusFor(rig.Song));     // and says nothing while the sources answer
        asked.Clear();
        worker.Restart();
        worker.TickNow();
        Assert.NotEmpty(asked);
        Assert.Equal(1, worker.WaitingCount);
    }

    [Fact]
    public void ASongWithOnlineTimingIsNotTimedAutomatically_ThisRunOnly_AndATimeWordsPressStays()
    {
        // #200 (owner 2026-10-29): "Processing lyrics" showed under lrc.red's word timing of a stream.
        using var rig = new Rig();
        using (var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""), _ => { }, _ => { }, startTimer: false))
        {
            worker.AskPlayed(rig.Song, out var accepted); Assert.True(accepted); Assert.Equal(1, worker.WaitingCount);
            worker.SetCoveredOnline(rig.Song, true);
            Assert.Equal(0, worker.WaitingCount);
            Assert.Equal(SongTimingStatus.None, worker.StatusFor(rig.Song));
            worker.AskPlayed(rig.Song, out accepted); Assert.False(accepted);
            Assert.False(worker.EnqueueAutomatic(rig.Song, rig.Music));
            Assert.False(worker.IsIgnored(rig.Song));               // not the user's Remove
            worker.SetCoveredOnline(rig.Song, false);               // did not fit the finished file
            Assert.True(worker.EnqueueAutomatic(rig.Song, rig.Music));
        }
        using var next = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""), _ => { }, _ => { }, startTimer: false);
        next.SetCoveredOnline(rig.Song, true);
        next.Ask(rig.Song);                                          // a Time words press is the user's
        Assert.Equal(1, next.WaitingCount);
    }

    [Fact]
    public async Task NativeRunnerPreservesPathsGuidesAndPreferredProxy_WithoutShellExpansion()
    {
        using var rig = new Rig(); var tool = new Tool(rig.App);
        var list = Path.Combine(rig.Work, "songs.txt"); File.WriteAllLines(list, [rig.Song]);
        var draft = LyricDrafts.Save(rig.Work, "[00:01.00]custom words\n\n");
        using var process = tool.StartQuietRun(rig.Music, list, rig.Result, Path.Combine(rig.Work, "run.log"), "light", "", true,
            Path.Combine(rig.Work, "progress.json"), Path.Combine(rig.Work, "control.txt"), TimingAction.Custom, draft);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, process.ExitCode);
        using var result = JsonDocument.Parse(File.ReadAllText(rig.Result));
        var args = result.RootElement.GetProperty("args").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Equal(rig.Music, args[0]); Assert.Contains("--confirm-redo", args); Assert.Contains("--repair-lyrics", args);
        Assert.Equal(draft, args[Array.IndexOf(args, "--lyrics-file") + 1]);
        Assert.True(result.RootElement.GetProperty("authenticated_loopback_proxy").GetBoolean());
        Assert.Equal("synthetic audio remains unchanged", File.ReadAllText(rig.Song));
    }
    [Fact]
    public async Task NativePauseResumeStopUseBackendControl_AndKeepMusic()
    {
        using var rig = new Rig(hold: true, outcome: "not_timed"); var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Controllable));
        Assert.Equal(SongTimingStatus.Processing, worker.StatusFor(rig.Song));
        Assert.Equal(SongTimingStatus.None, worker.StatusFor(Path.Combine(rig.Music, "not queued.mp3")));
        worker.Ask(rig.Song); // repeated empty-state click must not replace the active item
        Assert.Equal(1, worker.WaitingCount);
        worker.SetPaused(true); Assert.Equal("pause", File.ReadAllText(Path.Combine(rig.Work, "control.txt")));
        Assert.Equal(SongTimingStatus.Paused, worker.StatusFor(rig.Song));
        worker.SetPaused(false); Assert.Equal("run", File.ReadAllText(Path.Combine(rig.Work, "control.txt")));
        Assert.Equal(SongTimingStatus.Processing, worker.StatusFor(rig.Song));
        worker.Cancel();
        await Until(() => states.Any(s => s.Message == "Cancelled"));
        Assert.Equal(0, worker.WaitingCount); Assert.True(File.Exists(rig.Song));
        Assert.Equal(SongTimingStatus.None, worker.StatusFor(rig.Song));
        Assert.StartsWith("0 saved", states.Last(s => s.Message == "Cancelled").Detail);
    }
    [Fact]
    public async Task TurningAutomaticModeOff_DoesNotHideAnAlreadyProcessingDownload()
    {
        using var rig = new Rig(hold: true); var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue);
        Assert.True(worker.EnqueueAutomatic(rig.Song, rig.Music, complete: true));
        await Until(() => worker.StatusFor(rig.Song) == SongTimingStatus.Processing);
        worker.Options = worker.Options with { Auto = false };
        Assert.Equal(SongTimingStatus.Processing, worker.StatusFor(rig.Song));
        worker.Cancel();
        await Until(() => states.Any(s => s.Message == "Cancelled"));
        Assert.Equal(SongTimingStatus.None, worker.StatusFor(rig.Song));
    }
    [Fact]
    public async Task NetworkFailureKeepsCheckpointAndRetryTimeAcrossRestart()
    {
        using var rig = new Rig(outcome: "network_error"); var states = new ConcurrentQueue<ProgressState>();
        using (var worker = rig.Worker(states.Enqueue))
        {
            worker.Ask(rig.Song);
            await Until(() => states.Any(s => s.Finished));
            Assert.Equal(1, worker.WaitingCount);
            Assert.Equal(SongTimingStatus.Retry, worker.StatusFor(rig.Song));
        }
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Work, "state.json")));
        var row = Assert.Single(checkpoint.RootElement.GetProperty("waiting").EnumerateArray());
        Assert.Equal(1, row.GetProperty("waits").GetInt32());
        Assert.InRange(row.GetProperty("notBeforeUtc").GetDateTime(), DateTime.UtcNow.AddSeconds(15), DateTime.UtcNow.AddSeconds(35));
        Assert.True(row.GetProperty("networkWait").GetBoolean());
        File.Delete(rig.Result);
        using var resumed = rig.Worker(_ => { }); resumed.TickNow();
        await Task.Delay(150); Assert.Equal(1, resumed.WaitingCount); Assert.False(File.Exists(rig.Result));
    }
    /// <summary>Owner #162: a song put off for days (no lyrics found once the network worked) starts its network waits afresh;
    /// on the TEST profile one kept 100 and would have gone straight to hourly tries.</summary>
    [Fact]
    public async Task ASongPutOffForDays_StartsItsNetworkWaitsAfresh()
    {
        using var rig = new Rig(outcome: "network_error");
        using (var worker = rig.Worker(_ => { }))
        {
            worker.Ask(rig.Song);
            await Until(() => worker.StatusFor(rig.Song) == SongTimingStatus.Retry && File.Exists(rig.Result));
            File.Delete(rig.Result);
            File.WriteAllText(Path.Combine(rig.App, "fixture.json"), "{\"hold\":false,\"outcome\":\"no_lyrics\"}");
            worker.RetryNetworkWaits();
            await Until(() => worker.AutomaticBlocked(rig.Song), () => $"status={worker.StatusFor(rig.Song)}");
        }
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Work, "state.json")));
        var row = Assert.Single(checkpoint.RootElement.GetProperty("waiting").EnumerateArray());
        Assert.True(row.GetProperty("deferred").GetBoolean());
        Assert.Equal(0, row.GetProperty("waits").GetInt32());
        Assert.False(row.GetProperty("networkWait").GetBoolean());
    }
    /// <summary>Owner #161: a song that crashes the whole WordLyrics run takes only itself down; the songs beside it are timed
    /// (a crash used to charge every song of the batch a try, and two crashes put all of them off for ten days).</summary>
    [Fact]
    public async Task ASongThatCrashesTheRun_IsFoundAlone_AndTheOthersAreTimed()
    {
        using var rig = new Rig(crashOn: "poison");
        string Song(string name) { var path = Path.Combine(rig.Music, name + ".mp3"); File.WriteAllText(path, "synthetic audio"); return path; }
        var first = Song("a first"); var poison = Song("b poison"); var last = Song("c last");
        var log = new ConcurrentQueue<string>();
        using var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""), l => log.Enqueue($"{DateTime.Now:ss.fff} LOG {l}"), _ => { }, s => log.Enqueue($"{DateTime.Now:ss.fff} {s.Message} / {s.Detail}"), readiness: _ => true);
        Assert.Equal(3, worker.AskMany([first, poison, last]));
        var clock = Stopwatch.StartNew(); // each run starts Python; the first crash waits 2 s before the songs go one by one
        while (!(worker.StatusFor(first) == SongTimingStatus.None && worker.StatusFor(last) == SongTimingStatus.None) && clock.Elapsed < TimeSpan.FromSeconds(40))
            await Task.Delay(50);
        Assert.True(worker.StatusFor(first) == SongTimingStatus.None && worker.StatusFor(last) == SongTimingStatus.None,
            $"first={worker.StatusFor(first)} poison={worker.StatusFor(poison)} last={worker.StatusFor(last)} after {clock.Elapsed.TotalSeconds:0.0} s; " + string.Join(" | ", log));
        Assert.Equal(SongTimingStatus.Retry, worker.StatusFor(poison)); // charged one try, tried again later
        Assert.False(worker.AutomaticBlocked(first)); Assert.False(worker.AutomaticBlocked(last));
        Assert.Equal(1, worker.WaitingCount);
    }

    /// <summary>Owner #162: a song whose lyric source keeps refusing (reported as a network error) is not retried around the
    /// clock: every working request elsewhere counted as "back online" and retried it at once (99 tries seen on the TEST
    /// profile). Its wait grows to an hour at most and it is never given up (owner: no ten-day wait for network errors).</summary>
    [Fact]
    public async Task ANetworkErrorThatNeverClears_BacksOffToAnHour_AndIsNeverGivenUp()
    {
        using var rig = new Rig(outcome: "network_error");
        using var worker = rig.Worker(_ => { });
        worker.Ask(rig.Song);
        for (var run = 1; run < Worker.RecoveryRetryWaits; run++)
        {
            await Until(() => worker.StatusFor(rig.Song) == SongTimingStatus.Retry && File.Exists(rig.Result));
            File.Delete(rig.Result);
            worker.RetryNetworkWaits(); // "back online": a song that only began failing is tried again at once
        }
        await Until(() => worker.StatusFor(rig.Song) == SongTimingStatus.Retry && File.Exists(rig.Result));
        File.Delete(rig.Result);
        worker.RetryNetworkWaits(); await Task.Delay(400);
        Assert.False(File.Exists(rig.Result)); // kept failing: "back online" no longer retries it at once
        Assert.False(worker.AutomaticBlocked(rig.Song)); // never put off for ten days
        Assert.Equal(SongTimingStatus.Retry, worker.StatusFor(rig.Song));
        Assert.InRange(worker.NextTryUtc(DateTime.UtcNow)!.Value, DateTime.UtcNow, DateTime.UtcNow.AddHours(1).AddMinutes(1));
        Assert.Equal(TimeSpan.FromSeconds(30), Worker.NetworkBackoff(0));
        Assert.True(Worker.NetworkBackoff(3) > Worker.NetworkBackoff(1) && Worker.NetworkBackoff(50) == TimeSpan.FromHours(1));
        worker.Restart(); // owner: Restart retries everything that waits
        await Until(() => File.Exists(rig.Result));
    }

    [Fact]
    public async Task StoppingAnOldWorkerCannotOverwriteItsReplacementsCheckpoint()
    {
        using var rig = new Rig(hold: true); var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue); worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Controllable)); worker.Dispose();
        var checkpoint = Path.Combine(rig.Work, "state.json"); File.WriteAllText(checkpoint, "replacement checkpoint");
        await Task.Delay(300); Assert.Equal("replacement checkpoint", File.ReadAllText(checkpoint));
    }
    [Fact]
    public async Task MissingModelsCannotTriggerAutomaticSetupEvenAfterTheReadyCheck()
    {
        using var rig = new Rig();
        var models = Path.Combine(rig.App, "wordlyrics", "models.py");
        File.WriteAllText(models, "class ModelError(Exception):pass\ndef missing(folder):return ['missing model']\ndef download(*a,**k):raise Exception('Download was called')\n");
        using var worker = rig.Worker(_ => { }); worker.Ask(rig.Song);
        await Until(() => File.Exists(Path.Combine(rig.Work, "last run.log")) && File.ReadAllText(Path.Combine(rig.Work, "last run.log")).Contains("SETUP_CONSENT_REQUIRED"));
        Assert.Equal(1, worker.WaitingCount); Assert.False(File.Exists(rig.Result));
    }
    [Fact]
    public async Task CorruptModelRecoveryCannotDownloadWithoutSetupConsent()
    {
        using var rig = new Rig();
        File.WriteAllText(Path.Combine(rig.App, "wordlyrics", "runner.py"), "from . import models\ndef probe(python):return []\ndef main():\n    try:models.download('corrupt model')\n    except models.ModelError as e:print(str(e));return 70\n    raise Exception('Model download was allowed')\n");
        var list = Path.Combine(rig.Work, "songs.txt"); File.WriteAllLines(list, [rig.Song]);
        var log = Path.Combine(rig.Work, "run.log");
        using var child = new Tool(rig.App).StartQuietRun(rig.Music, list, rig.Result, log, "light", "", true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(70, child.ExitCode); Assert.Contains("SETUP_CONSENT_REQUIRED", File.ReadAllText(log));
    }

    [Fact] // Owner #183: an installed 1.6.4 gets one voice-model piece per call (four held ~7 GB more on an 8 GB card).
    public async Task BackgroundRuns_GiveTheVoiceModelOnePiecePerCall()
    {
        using var rig = new Rig();
        var module = Path.Combine(rig.App, "wordlyrics");
        File.WriteAllText(Path.Combine(module, "separate.py"), "BATCH = 4\ndef count_batches(n, batch=BATCH):return batch\ndef spec_batches(mix, batch=BATCH):return batch\n");
        File.WriteAllText(Path.Combine(module, "runner.py"), "from . import separate as S\ndef probe(python):return []\ndef main():\n    print('pieces', S.BATCH, S.count_batches(0), S.spec_batches(0), flush=True);return 0\n");
        var list = Path.Combine(rig.Work, "songs.txt"); File.WriteAllLines(list, [rig.Song]);
        var log = Path.Combine(rig.Work, "run.log");
        using var child = new Tool(rig.App).StartQuietRun(rig.Music, list, rig.Result, log, "light", "", true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, child.ExitCode); Assert.Contains("pieces 1 1 1", File.ReadAllText(log));
    }
    [Fact] // Owner #187: processor runs skip the memory pool; a card that keeps crashing the start is left out.
    public async Task BackgroundRuns_KeepProcessorMemoryLow_AndLeaveOutACardThatCrashesTheStart()
    {
        using var rig = new Rig();
        var module = Path.Combine(rig.App, "wordlyrics");
        File.WriteAllText(Path.Combine(module, "engine.py"), string.Join('\n',
            "ALIGNER,SEPARATOR='a','s'",
            "class Opts:",
            "    enable_cpu_mem_arena=True",
            "class FakeOrt:",
            "    __version__='t'",
            "    SessionOptions=Opts",
            "    @staticmethod",
            "    def InferenceSession(path,sess_options=None,providers=None):return ('cpu',sess_options.enable_cpu_mem_arena)",
            "class Engine:",
            "    def __init__(self,settings):",
            "        self.ort,self.gpu,self.dir,self.sessions,self.note,self.say=FakeOrt,'card','m',{},'',lambda m:None",
            "        self._auto(settings)",
            "    def _session(self,model,where):return self.sessions.setdefault((model,where),('gpu',True))",
            "    def _auto(self,settings_path):self.where={ALIGNER:'gpu',SEPARATOR:'gpu'}",
            "    def _run(self,model,outs,feed):return 'ran'", ""));
        File.WriteAllText(Path.Combine(module, "runner.py"), string.Join('\n',
            "import os",
            "from . import engine as E",
            "def probe(python):return []",
            "def main():",
            "    s=os.path.join(os.environ['WORDLYRICS_HOME'],'speed test.json')",
            "    for i in range(3):E.Engine(s)",
            "    e=E.Engine(s);print('fourth',e.where[E.ALIGNER],e.note[:11],flush=True)",
            "    os.remove(s+'.unfinished')",
            "    e=E.Engine(s);e._run('a',None,None);print('proved',os.path.exists(s+'.unfinished'),flush=True)",
            "    print('pool',e._session('a','cpu')[1],flush=True)",
            "    return 0", ""));
        var list = Path.Combine(rig.Work, "songs.txt"); File.WriteAllLines(list, [rig.Song]);
        var log = Path.Combine(rig.Work, "run.log");
        using var child = new Tool(rig.App).StartQuietRun(rig.Music, list, rig.Result, log, "light", "", true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        var text = File.ReadAllText(log);
        Assert.Equal(0, child.ExitCode);
        Assert.Contains("fourth cpu GPU_SKIPPED", text);
        Assert.Contains("proved False", text);
        Assert.Contains("pool False", text);
    }

    [Theory] // Owner #187: "Ease off when the PC is busy" rests more while calls run slow, and only when it is on.
    [InlineData(true, "paces 1.0 1.0 0.7 0.49 0.54 0.59")]
    [InlineData(false, "paces 1.0 1.0 1.0 1.0 1.0 1.0")]
    public async Task EaseOff_RestsMoreWhileModelCallsRunSlow(bool adapt, string expected)
    {
        using var rig = new Rig();
        var module = Path.Combine(rig.App, "wordlyrics");
        File.WriteAllText(Path.Combine(module, "engine.py"), string.Join('\n',
            "ALIGNER,SEPARATOR='a','s'",
            "class Engine:",
            "    stop=None",
            "    def __init__(self):self.pace,self.paces,self.say,self.current_model=1.0,[],lambda m:None,'a'",
            "    def _session(self,model,where):return None",
            "    def _auto(self,settings_path):pass",
            "    def _run(self,model,outs,feed):return 'ran'",
            "    def _rest(self,worked):self.paces.append(round(self.pace,2))", ""));
        File.WriteAllText(Path.Combine(module, "runner.py"), string.Join('\n',
            "from . import engine as E",
            "def probe(python):return []",
            "def main():",
            "    e=E.Engine()",
            "    for worked in (1.0,1.0,2.0,2.0,1.0,1.0):e._rest(worked)",
            "    print('paces',' '.join(str(p) for p in e.paces),flush=True)",
            "    return 0", ""));
        var list = Path.Combine(rig.Work, "songs.txt"); File.WriteAllLines(list, [rig.Song]);
        var log = Path.Combine(rig.Work, "run.log");
        using var child = new Tool(rig.App).StartQuietRun(rig.Music, list, rig.Result, log, "full", "", true, adapt: adapt);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, child.ExitCode);
        Assert.Contains(expected, File.ReadAllText(log));
    }

    // ── Owner #91: Restart, a stuck-run watchdog, a fixed setup and "time all songs without word timing" ──

    [Fact]
    public async Task RestartDuringARun_EndsItAtOnce_KeepsTheSong_AndStartsAgain()
    {
        using var rig = new Rig(hold: true, outcome: "not_timed"); var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Controllable));
        var said = worker.Restart();
        Assert.Contains("start again", said);
        await Until(() => states.Any(s => s.Message == "Restarted"));
        Assert.Equal(1, worker.WaitingCount); // nothing is lost and no try was spent
        await Until(() => worker.StatusFor(rig.Song) == SongTimingStatus.Processing); // and it runs again at once
        worker.Cancel(); await Until(() => states.Any(s => s.Message == "Cancelled"));
        Assert.Equal("synthetic audio remains unchanged", File.ReadAllText(rig.Song));
    }

    [Fact]
    public async Task RestartClearsARetryWait_SoTheSongIsTriedAgainNow()
    {
        using var rig = new Rig(outcome: "network_error"); var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song);
        await Until(() => worker.StatusFor(rig.Song) == SongTimingStatus.Retry); // short network retry, not setup's long backoff
        File.Delete(rig.Result);
        worker.Restart();
        await Until(() => File.Exists(rig.Result)); // tried again right away
    }

    [Fact]
    public async Task ASetupThatWasMissingAndIsNowReady_ClearsTheWaitsSongsCollected_AndSaysSo()
    {
        using var rig = new Rig(outcome: "network_error"); var ready = true; var announced = 0;
        using var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""), _ => { }, _ => { }, _ => { }, readiness: _ => ready)
            { ReadinessEvery = TimeSpan.Zero };
        worker.BecameReady += () => Interlocked.Increment(ref announced);
        worker.Ask(rig.Song);
        await Until(() => worker.StatusFor(rig.Song) == SongTimingStatus.Retry);
        Assert.Equal(1, Volatile.Read(ref announced)); // the first look at a backend that is set up
        ready = false; worker.TickNow();               // setup broken or removed
        File.Delete(rig.Result);
        ready = true; worker.TickNow();                // setup finished again
        await Until(() => File.Exists(rig.Result));    // the retry wait (10 minutes) was cleared
        Assert.Equal(2, Volatile.Read(ref announced));
    }

    [Fact]
    public async Task AStuckRun_IsRestartedByItself_ThenStoppedWithAClearMessage_AndTheSongIsKept()
    {
        using var rig = new Rig(stuck: true); var states = new ConcurrentQueue<ProgressState>();
        using var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""), _ => { }, _ => { }, states.Enqueue, readiness: _ => true)
            { StuckAfter = TimeSpan.FromMilliseconds(400), IdleCpuAfter = TimeSpan.FromMilliseconds(400) };
        worker.Ask(rig.Song);
        var clock = Stopwatch.StartNew();
        while (!states.Any(s => s.Message == "WordLyrics stopped responding") && clock.Elapsed < TimeSpan.FromSeconds(25)) await Task.Delay(50);
        Assert.Contains(states, s => s.Message == "Restarted");
        Assert.Contains(states, s => s.Message == "WordLyrics stopped responding" && s.Error);
        Assert.Equal(1, worker.WaitingCount); // kept, for Restart or the next try
        Assert.Equal(SongTimingStatus.Retry, worker.StatusFor(rig.Song));
    }

    // Owner #108: the song playing now is timed first and alone; a run on other songs less than 75% through steps
    // aside at once, one further on finishes its song first; the other songs are never lost.
    [Fact]
    public async Task APlayedSong_StopsARunNotFarThrough_AndIsTimedFirst_TheOtherSongKept()
    {
        using var rig = new Rig(hold: true, done: 1); // 25% through
        var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Fraction is > 0 and < 0.75));
        var played = Path.Combine(rig.Music, "played now.mp3"); File.WriteAllText(played, "synthetic");
        worker.AskPlayed(played, out var accepted); Assert.True(accepted);
        worker.MakeRoom(played);
        await Until(() => worker.StatusFor(played) == SongTimingStatus.Processing);
        Assert.Equal([played], File.ReadAllLines(Path.Combine(rig.Work, "songs.txt"))); // alone in its run
        Assert.True(worker.IsWaiting(rig.Song)); // kept, no failed try
    }

    /// <summary>Owner #193: the pill goes by who asked. A song asked for by hand is marked asked; the song playing now
    /// (timed automatically) is marked as neither asked nor shown, so its run stays off the screen.</summary>
    [Fact]
    public async Task RunsSayWhoAskedForThem_ByHandOrBecauseASongIsPlaying()
    {
        using var rig = new Rig(hold: true, done: 1);
        var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Fraction is > 0 and < 0.75));
        Assert.Contains(states, s => s.Asked && !s.Listening && !s.Retry && !s.Finished);
        var played = Path.Combine(rig.Music, "played now.mp3"); File.WriteAllText(played, "synthetic");
        worker.AskPlayed(played, out var accepted); Assert.True(accepted);
        worker.MakeRoom(played);
        await Until(() => states.Any(s => !s.Asked && s.Listening && !s.Finished), () => string.Join(" | ", states.Select(s => $"{s.Message} asked={s.Asked} listening={s.Listening}")));
        Assert.Null(new PillPolicy().Decide(states.Last(s => s.Listening), null));
    }

    /// <summary>Owner #168: the run was stopped to make room, and nothing followed (the streamed song it was on left the
    /// cache when it was skipped): the pill sat on "Starting WordLyrics" for good. It goes back to idle.</summary>
    [Fact]
    public async Task ARunStoppedToMakeRoom_WithNothingAfterIt_LeavesThePillIdle()
    {
        using var rig = new Rig(hold: true, done: 1);
        var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Fraction is > 0 and < 0.75));
        File.Delete(rig.Song); // a skipped stream leaves the cache
        worker.MakeRoom(Path.Combine(rig.Music, "played now.mp3"));
        await Until(() => states.LastOrDefault() is { Message: "Ready", Finished: true }, () => string.Join(" | ", states.Select(s => s.Message)));
        Assert.Null(TimingPill.From(states.Last()));
    }

    [Fact]
    public async Task APlayedSong_LetsARunFarThroughFinishItsSong_ThenGoesNext()
    {
        using var rig = new Rig(hold: true, done: 3.5); // 88% through
        var states = new ConcurrentQueue<ProgressState>();
        var reports = new ConcurrentQueue<string>();
        using var worker = rig.Worker(state =>
        {
            states.Enqueue(state);
            if (state.Finished && File.Exists(Path.Combine(rig.Work, "last run.log")))
                reports.Enqueue(File.ReadAllText(Path.Combine(rig.Work, "last run.log")));
        });
        worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Fraction >= 0.75));
        var played = Path.Combine(rig.Music, "played now.mp3"); File.WriteAllText(played, "synthetic");
        worker.AskPlayed(played, out _);
        worker.MakeRoom(played);
        await Until(() => worker.StatusFor(played) == SongTimingStatus.Processing);
        Assert.False(worker.IsWaiting(rig.Song), string.Join("\n", reports)); // it finished (timed), then the played song started
        Assert.Equal([played], File.ReadAllLines(Path.Combine(rig.Work, "songs.txt")));
    }

    [Fact]
    public async Task PlayingCacheCopy_ReportsItsTimedLibraryCopyBeforeRunEnds_AndDoesNotInterruptIt()
    {
        using var rig = new Rig(hold: true, earlyWords: true);
        var written = new ConcurrentQueue<string>();
        using var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""),
            _ => { }, _ => { }, readiness: _ => true, lyricsWritten: paths => { foreach (var path in paths) written.Enqueue(path); });
        var cache = Path.Combine(rig.Data.DataDirectory, "stream-cache", Path.GetFileName(rig.Song));
        worker.Watched = () => cache;
        worker.AskPlayed(rig.Song, out var accepted); Assert.True(accepted);
        await Until(() => worker.StatusFor(rig.Song) == SongTimingStatus.Processing);
        worker.MakeRoom(cache);
        await Until(() => written.Contains(rig.Song));
        Assert.False(File.Exists(rig.Result)); // notified while the backend is still running
        Assert.Equal([rig.Song], File.ReadAllLines(Path.Combine(rig.Work, "songs.txt")));
        Assert.NotEqual("stop", File.Exists(Path.Combine(rig.Work, "control.txt")) ? File.ReadAllText(Path.Combine(rig.Work, "control.txt")) : "");
    }

    [Fact]
    public async Task ManualPlayingRequest_PromotesAnExistingJobAheadOfOlderRequests_WithoutReplacingItsDraft()
    {
        using var rig = new Rig(hold: true);
        var states = new ConcurrentQueue<ProgressState>();
        var logs = new ConcurrentQueue<string>();
        using var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Light", false, rig.App, ""),
            logs.Enqueue, _ => { }, states.Enqueue, readiness: _ => true, startTimer: false);
        worker.SetPaused(true);
        worker.Ask(rig.Song);
        var current = Path.Combine(rig.Music, "playing.mp3"); File.WriteAllText(current, "synthetic");
        var draft = Path.Combine(rig.Work, "custom.txt"); File.WriteAllText(draft, "custom words");
        worker.Ask(current, TimingAction.Custom, draft);
        worker.Prioritize(current);
        worker.SetPaused(false);
        await Until(() => worker.StatusFor(current) == SongTimingStatus.Processing,
            () => $"Status={worker.StatusFor(current)}; logs={string.Join(" | ", logs)}; states={string.Join(" | ", states)}");
        Assert.Equal([current], File.ReadAllLines(Path.Combine(rig.Work, "songs.txt")));
        Assert.True(worker.IsWaiting(rig.Song));
        worker.Cancel();
        await Until(() => File.Exists(rig.Result));
        using var result = JsonDocument.Parse(File.ReadAllText(rig.Result));
        var args = result.RootElement.GetProperty("args").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Equal(draft, args[Array.IndexOf(args, "--lyrics-file") + 1]);
    }

    [Fact]
    public void AheadOnlyJobsAreTransient_AndChangingTheWindowKeepsExplicitRequests()
    {
        using var rig = new Rig();
        using var worker = new Worker(rig.Work, () => [rig.Music], new(true, "Full", false, rig.App, ""),
            _ => { }, _ => { }, readiness: _ => false, startTimer: false);
        worker.SetPaused(true);
        worker.Ask(rig.Song);
        var next = Path.Combine(rig.Music, "ahead.mp3"); File.WriteAllText(next, "synthetic");
        worker.SetAhead([rig.Song, next], rig.Song);
        Assert.Equal(2, worker.WaitingCount);
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Work, "state.json"))))
            Assert.Single(json.RootElement.GetProperty("waiting").EnumerateArray());
        worker.SetAhead([], rig.Song);
        Assert.Equal(1, worker.WaitingCount);
        Assert.True(worker.IsWaiting(rig.Song));
        Assert.False(worker.IsWaiting(next));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void FullscreenBudgetUsesLight_ThenRestoresTheChosenSpeed(int state, bool protect)
    {
        Assert.Equal(protect, GamingBudget.ProtectsFullscreen(state));
        Assert.Equal(protect ? "Light" : "Half", GamingBudget.Speed("Half", GamingBudget.ProtectsFullscreen(state)));
        Assert.Equal("Full", GamingBudget.Speed("Full", false));
    }

    [Fact]
    public void TimeAllWithoutWordTiming_QueuesOnlySongsThatHaveNone_InOneSave()
    {
        using var rig = new Rig(); var timed = Path.Combine(rig.Music, "already timed.mp3");
        File.WriteAllText(timed, "synthetic"); File.WriteAllText(Path.ChangeExtension(timed, ".elrc"), "[00:01.00]<00:01.00>word");
        using var worker = new Worker(rig.Work, () => [rig.Music], new(false, "Light", false, rig.App, ""), _ => { }, _ => { }, _ => { }, startTimer: false, readiness: _ => false);
        Assert.Equal(1, worker.AskMany([rig.Song, timed, Path.Combine(rig.Music, "gone.mp3"), rig.Song]));
        Assert.Equal(1, worker.WaitingCount);
        Assert.Equal(0, worker.AskMany([rig.Song])); // already waiting
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Work, "state.json")));
        Assert.Single(checkpoint.RootElement.GetProperty("waiting").EnumerateArray()); // saved, so it survives until setup
    }
}
