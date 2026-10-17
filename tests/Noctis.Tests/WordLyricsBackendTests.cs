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
        internal Rig(bool hold = false, string outcome = "timed")
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
            File.WriteAllText(Path.Combine(App, "fixture.json"), JsonSerializer.Serialize(new { hold, outcome }));
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
                    target=Path(path);tmp=target.with_suffix('.tmp');tmp.write_text(json.dumps(data),encoding='utf-8');os.replace(tmp,target)
                control=Path(value('--control')) if '--control' in args else None
                deadline=time.monotonic()+15
                while config['hold'] and time.monotonic()<deadline:
                    word=control.read_text() if control and control.exists() else 'run'
                    put(value('--progress'),{'completed':0,'total':len(songs),'paused':word=='pause','stages':[{'name':'Align words','state':'running','done':1,'total':4,'current':'Synthetic song'}]})
                    if word=='stop':break
                    time.sleep(.05)
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
    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
        Assert.True(condition(), "Synthetic backend did not reach the expected state.");
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
        using var rig = new Rig(hold: true); var states = new ConcurrentQueue<ProgressState>();
        using var worker = rig.Worker(states.Enqueue);
        worker.Ask(rig.Song);
        await Until(() => states.Any(s => s.Controllable));
        worker.SetPaused(true); Assert.Equal("pause", File.ReadAllText(Path.Combine(rig.Work, "control.txt")));
        worker.SetPaused(false); Assert.Equal("run", File.ReadAllText(Path.Combine(rig.Work, "control.txt")));
        worker.Cancel();
        await Until(() => states.Any(s => s.Message == "Cancelled"));
        Assert.Equal(0, worker.WaitingCount); Assert.True(File.Exists(rig.Song));
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
        }
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Work, "state.json")));
        var row = Assert.Single(checkpoint.RootElement.GetProperty("waiting").EnumerateArray());
        Assert.Equal(1, row.GetProperty("waits").GetInt32());
        Assert.True(row.GetProperty("notBeforeUtc").GetDateTime() > DateTime.UtcNow.AddMinutes(8));
        File.Delete(rig.Result);
        using var resumed = rig.Worker(_ => { }); resumed.TickNow();
        await Task.Delay(150); Assert.Equal(1, resumed.WaitingCount); Assert.False(File.Exists(rig.Result));
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
}
