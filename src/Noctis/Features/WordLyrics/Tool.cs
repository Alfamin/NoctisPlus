using System.Diagnostics;
using System.Text;

namespace Noctis.Features.WordLyrics;

/// <summary>
/// The WordLyrics program on this computer: where it is, whether it is set up, and how it is
/// started. The plugin never does any lyric work itself; it only starts this program.
/// </summary>
internal sealed class Tool
{
    internal static bool SuppressModelLaunchForTests { get; set; }
    public const string Launcher = "WordLyrics.bat";

    public Tool(string? configured)
    {
        var folder = configured?.Trim().Trim('"');
        Folder = string.IsNullOrEmpty(folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WordLyrics")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder));
    }

    public string Folder { get; }

    private string Bat => Path.Combine(Folder, Launcher);

    public bool Installed => File.Exists(Bat);

    /// <summary>The first WordLyrics that can be given single songs, reports what it did with them and
    /// can be paused and stopped from the progress window (--control).</summary>
    private static readonly Version Needed = new(1, 6, 4);

    /// <summary>The first WordLyrics that understands --control. Today the same as <see cref="Needed"/>.</summary>
    private static readonly Version ControlNeeded = new(1, 6, 4);

    /// <summary>The installed program's version, or null when it cannot be told.</summary>
    private Version? ProgramVersion()
    {
        try
        {
            var about = File.ReadAllText(Path.Combine(Folder, "wordlyrics", "__init__.py"));
            var match = System.Text.RegularExpressions.Regex.Match(about, "__version__\\s*=\\s*\"(\\d+\\.\\d+\\.\\d+)");
            return match.Success ? Version.Parse(match.Groups[1].Value) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Installed, but from before it could do what this plugin needs.</summary>
    public bool TooOld => ProgramVersion() is not { } version || version < Needed;

    /// <summary>Can this program be paused and stopped through a control file? Without it the buttons stay hidden.</summary>
    public bool SupportsControl => ProgramVersion() is { } version && version >= ControlNeeded;
    public bool SupportsRepair
    {
        get
        {
            try { return File.ReadAllText(Path.Combine(Folder, "wordlyrics", "__main__.py")).Contains("--confirm-redo", StringComparison.Ordinal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>Installed, new enough, its own Python set up and both models fetched: a run starts without downloading anything.</summary>
    public bool Ready
    {
        get
        {
            try
            {
                if (SuppressModelLaunchForTests || !Installed || TooOld) return false;
                var python = Path.Combine(Folder, ".python");
                var models = Path.Combine(Folder, "models");
                return File.Exists(Path.Combine(python, "python.exe")) && Directory.EnumerateFiles(python, "ready-*.txt").Any()
                    && new FileInfo(Path.Combine(models, "mms_fa_300m.onnx")).Length == 1262421764
                    && new FileInfo(Path.Combine(models, "Kim_Vocal_2.onnx")).Length == 66759214;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Owner #91: what keeps <see cref="Ready"/> false, in the user's words (empty when nothing is known to be missing).</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        try
        {
            if (!Installed) { missing.Add("the program"); return missing; }
            if (TooOld) missing.Add($"a newer program (version {Needed} or later)");
            var python = Path.Combine(Folder, ".python");
            if (!File.Exists(Path.Combine(python, "python.exe")) || !Directory.Exists(python) || !Directory.EnumerateFiles(python, "ready-*.txt").Any()) missing.Add("its Python packages");
            foreach (var (file, size, name) in new[] { ("mms_fa_300m.onnx", 1262421764L, "the timing model"), ("Kim_Vocal_2.onnx", 66759214L, "the voice model") })
            {
                var model = new FileInfo(Path.Combine(Folder, "models", file));
                if (!model.Exists) missing.Add(name);
                else if (model.Length != size) missing.Add(name + " (partly downloaded)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return missing;
    }

    /// <summary>
    /// Times the songs listed in <paramref name="listFile"/>, without a window. What happened to
    /// each song is written to <paramref name="resultFile"/>, the program's own output to <paramref name="logFile"/>.
    /// With <paramref name="controlFile"/> the program looks at that small file twice a second: "pause", "run" or "stop".
    /// </summary>
    public Process StartQuietRun(string root, string listFile, string resultFile, string logFile, string speed, string backup, bool retry, string? progressFile = null, string? controlFile = null, TimingAction action = TimingAction.Missing, string? draft = null)
    {
        // Pass paths as real arguments. cmd.exe expands %variables% even inside quotes.
        var python = Environment.GetEnvironmentVariable("WORDLYRICS_PYTHON");
        var info = new ProcessStartInfo(string.IsNullOrWhiteSpace(python) ? Path.Combine(Folder, ".python", "python.exe") : python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Folder,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-X", "utf8", "-c", BackgroundEntry, root, "--songs-from", listFile, "--result", resultFile,
            "--yes", "--no-open", "--plain-output", "--speed", speed }) info.ArgumentList.Add(arg);
        if (retry) info.ArgumentList.Add("--retry");
        if (action != TimingAction.Missing)
        {
            foreach (var arg in new[] { "--redo", "--confirm-redo", "--retime-mode", action == TimingAction.Current ? "current" : "fresh" }) info.ArgumentList.Add(arg);
            if (action is TimingAction.Fresh or TimingAction.Custom)
            {
                info.ArgumentList.Add("--repair-lyrics"); info.ArgumentList.Add("--ignore-lyric-source");
            }
        }
        if (action == TimingAction.Custom)
        {
            if (string.IsNullOrWhiteSpace(draft) || !File.Exists(draft)) throw new InvalidOperationException("The saved lyric draft is missing. Edit the lyrics again; no active lyrics were changed.");
            info.ArgumentList.Add("--lyrics-file"); info.ArgumentList.Add(draft);
        }
        if (progressFile is not null) { info.ArgumentList.Add("--progress"); info.ArgumentList.Add(progressFile); }
        if (controlFile is not null) { info.ArgumentList.Add("--control"); info.ArgumentList.Add(controlFile); }
        backup = backup.Trim().Trim('"');
        if (backup.Length > 0) { info.ArgumentList.Add("--backup-to"); info.ArgumentList.Add(Environment.ExpandEnvironmentVariables(backup)); }
        info.Environment["WORDLYRICS_HOME"] = Folder;
        info.Environment["PYTHONPATH"] = Folder;
        info.Environment["WORDLYRICS_NO_PAUSE"] = "1"; // the launcher waits for a key press otherwise
        File.WriteAllText(logFile, "WordLyrics background run\n", new UTF8Encoding(false));
        var gate = new object();
        void Log(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            try { lock (gate) { if (new FileInfo(logFile).Length < 5_000_000) File.AppendAllText(logFile, e.Data + "\n", new UTF8Encoding(false)); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += Log; process.ErrorDataReceived += Log;
        var proxy = new BackendProxy(() => FreeMusicFinder.FreeMusicPlugin.Current?.MetadataProxy ?? WordLyricsFeature.Current?.MetadataProxy ?? "");
        proxy.Configure(info);
        process.EnableRaisingEvents = true; process.Exited += (_, _) => proxy.Dispose();
        try { if (!process.Start()) throw new InvalidOperationException("WordLyrics could not be started."); }
        catch { proxy.Dispose(); process.Dispose(); throw; }
        // The background work must never compete with the music for the processor; what the
        // program starts in turn inherits this. Not worth failing over when Windows refuses.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return process;
    }

    /// <summary>
    /// Opens a window that installs or updates WordLyrics into <see cref="Folder"/> with the
    /// program's own installer, then fetches its models. <paramref name="scriptFolder"/> is where
    /// the few lines that do this are written.
    /// </summary>
    public void OpenSetup(string scriptFolder, Action? closed = null)
    {
        Directory.CreateDirectory(scriptFolder);
        var script = Path.Combine(scriptFolder, "setup.cmd");
        File.WriteAllText(script, SetupText().Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        var info = new ProcessStartInfo(Cmd)
        {
            Arguments = $"/d /s /c \"\"{script}\"\"",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = scriptFolder,
        };
        info.Environment["WORDLYRICS_DIR"] = Folder;
        info.Environment["WORDLYRICS_NO_START"] = "1";
        info.Environment["WORDLYRICS_NO_PAUSE"] = "1";
        var proxy = new BackendProxy(() => FreeMusicFinder.FreeMusicPlugin.Current?.MetadataProxy ?? WordLyricsFeature.Current?.MetadataProxy ?? "");
        proxy.Configure(info);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        // Owner #91: when the setup window closes, waiting songs start at once instead of after their retry waits.
        process.Exited += (_, _) => { proxy.Dispose(); process.Dispose(); try { closed?.Invoke(); } catch (Exception) { } };
        try { if (!process.Start()) throw new IOException("WordLyrics setup could not start."); }
        catch { proxy.Dispose(); process.Dispose(); throw; }
    }

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    // The standalone runner repairs packages and missing/corrupt models automatically.
    // Background native jobs must instead stop at setup and preserve their checkpoint.
    // Both guards are process-local; explicit, confirmed Setup keeps the normal installer.
    internal const string BackgroundEntry = """
        import os,sys
        import wordlyrics.runner as runner
        from wordlyrics import models
        def no_download(*args,**kwargs):
            raise models.ModelError('SETUP_CONSENT_REQUIRED: Open Set up WordLyrics to approve model downloads.')
        models.download=no_download
        def offline_check(home,**kwargs):
            broken=runner.probe(sys.executable)
            if broken or models.missing(os.path.join(home,'models')):
                print('SETUP_CONSENT_REQUIRED: Packages or models are missing. Open Set up WordLyrics; no setup download was started.',flush=True)
                return False
            return True
        runner.ensure=offline_check
        sys.exit(runner.main())
        """;

    internal string SetupText()
    {
        var complete = Installed && !TooOld && new[] { "__main__.py", "runner.py", "firststart.py", "menu.py", "library.py", "repair.py", "drafts.py", "search.py", "guides.py" }
            .All(name => File.Exists(Path.Combine(Folder, "wordlyrics", name))) && File.Exists(Path.Combine(Folder,"requirements.txt"));
        return complete ? SetupScript.Replace(DownloadInstaller, "echo  Reusing the installed WordLyrics program. Existing Python/packages/models are checked before any download.") : SetupScript;
    }

    private const string DownloadInstaller = "\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -Command \"irm https://raw.githubusercontent.com/Alfamin/WordLyrics/main/install.ps1 | iex\"";

    /// <summary>A folder as a quoted argument: "D:\" would end in an escaped quote, "D:\." does not.</summary>
    private static string FolderArgument(string folder)
        => folder.EndsWith('\\') || folder.EndsWith('/') ? folder + "." : folder;

    private const string SetupScript = """
        @echo off
        rem Written by the WordLyrics plugin for Noctis. Installs or updates the WordLyrics program
        rem into the folder named in WORDLYRICS_DIR, then lets it fetch what it needs.
        title WordLyrics setup
        echo.
        echo  Installing WordLyrics into %WORDLYRICS_DIR%
        echo.
        "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "irm https://raw.githubusercontent.com/Alfamin/WordLyrics/main/install.ps1 | iex"
        if not exist "%WORDLYRICS_DIR%\WordLyrics.bat" goto :failed
        call "%WORDLYRICS_DIR%\WordLyrics.bat" setup
        if errorlevel 1 goto :failed
        echo.
        echo  WordLyrics is ready. New songs in Noctis get word-by-word lyrics from now on.
        echo  Press a key to close this window.
        pause >nul
        exit /b 0
        :failed
        echo.
        echo  That did not finish. Check the internet connection, then flip the switch in Noctis
        echo  again: it continues where it stopped.
        echo  Press a key to close this window.
        pause >nul
        exit /b 1

        """;
}
