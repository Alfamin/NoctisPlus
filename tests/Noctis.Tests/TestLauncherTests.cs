using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Noctis.Tests;

public class TestLauncherTests
{
    [Fact]
    public async Task MissingBuild_ReportsFailureAndLeavesDiagnosticOutsideProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            var (code, output) = await Launch(root, standalone: true);
            Assert.Equal(1, code);
            Assert.Contains("Test app missing", output);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "launch-logs"), "*.log"));
            Assert.False(Directory.Exists(Path.Combine(root, "profile")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NativeExitBeforeManagedStartup_IsNotReportedAsOpened()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "app"));
            // A native executable with no GUI and no CLR/session.log. Its early
            // exit must be caught even when it beats the first polling tick.
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(root, "app", "Noctis.exe"));
            var (code, output) = await Launch(root);
            Assert.Equal(1, code);
            Assert.Contains("exited before opening a window", output);
            Assert.DoesNotContain("Already open", output);
            Assert.DoesNotContain("Opened (PID", output);
            Assert.False(File.Exists(Path.Combine(root, "profile", "session.log")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "launch-logs"), "*.stderr.log"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "Noctis launcher space " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<(int, string)> Launch(string root, bool standalone = false)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "scripts", "Start-NoctisPlus-Test.ps1"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        var script = Path.Combine(repo!.FullName, "scripts", "Start-NoctisPlus-Test.ps1");
        if (standalone)
        {
            var installed = Path.Combine(root, "Start-NoctisPlus-Test.ps1");
            File.Copy(script, installed);
            script = installed;
        }
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-StartupTimeoutSeconds", "3" })
            start.ArgumentList.Add(arg);
        if (!standalone) { start.ArgumentList.Add("-TestRoot"); start.ArgumentList.Add(root); }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // A busy CI runner took over 25 s to start PowerShell and the launcher (2026-10-19).
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}
