using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings → Audio → Headphone surround: 5.1/7.1 files render binaurally (VLC's stereo-mode
/// Headphones) instead of a flat stereo downmix. It is a LibVLC instance option, read from
/// settings.json before the player is built and applied at the next launch.
/// </summary>
[Collection("Tag save replace")] // the real-player test below must not share the process with another LibVLC player
public class HeadphoneSurroundTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "hpsurround-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _o;

    public HeadphoneSurroundTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SettingsViewModel NewVm() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    [Fact]
    public void On_IsStereoModeHeadphones_Off_AddsNoOption()
    {
        Assert.Equal("--stereo-mode=6", VlcAudioPlayer.StereoModeArg(true));
        Assert.Null(VlcAudioPlayer.StereoModeArg(false));
    }

    [Fact]
    public void FreshInstall_IsOff()
    {
        Assert.False(new AppSettings().HeadphoneSurround);
        Assert.False(PersistenceService.ReadBoolSettingAtLaunch(_root, nameof(AppSettings.HeadphoneSurround)));
    }

    [Theory]
    [InlineData("{ \"headphoneSurround\": true }", true)]
    [InlineData("{ \"headphoneSurround\": false }", false)]
    [InlineData("{ \"upmixMode\": \"Off\" }", false)]
    [InlineData("{ \"headphoneSurround\": \"yes\" }", false)]
    [InlineData("{ \"headphoneSurround\": tru", false)]
    [InlineData("[]", false)]
    public void LaunchRead_TakesOnlyAJsonTrue(string json, bool expected)
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), json);
        Assert.Equal(expected, PersistenceService.ReadBoolSettingAtLaunch(_root, nameof(AppSettings.HeadphoneSurround)));
    }

    [Fact]
    public async Task LaunchRead_SeesWhatTheSettingsSaveWrote()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { HeadphoneSurround = true });
        Assert.True(PersistenceService.ReadBoolSettingAtLaunch(_root, nameof(AppSettings.HeadphoneSurround)));
        await persistence.SaveSettingsAsync(new AppSettings { HeadphoneSurround = false });
        Assert.False(PersistenceService.ReadBoolSettingAtLaunch(_root, nameof(AppSettings.HeadphoneSurround)));
    }

    [AvaloniaFact]
    public async Task Toggle_IsSaved_AndSaysARestartIsNeededUntilThePlayerMatches()
    {
        var player = new FakeAudioPlayer { HeadphoneSurroundActive = false };
        var vm = NewVm();
        vm.SetAudioPlayer(player);
        await vm.LoadAsync();
        Assert.False(vm.HeadphoneSurround);
        Assert.False(vm.HeadphoneSurroundNeedsRestart);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.HeadphoneSurround = true;
        Assert.Contains(nameof(SettingsViewModel.HeadphoneSurroundNeedsRestart), raised);
        Assert.True(vm.HeadphoneSurroundNeedsRestart);
        await vm.SaveAsync();
        Assert.True(PersistenceService.ReadBoolSettingAtLaunch(_root, nameof(AppSettings.HeadphoneSurround)));

        // The next launch: the player starts with it on, nothing left to restart for.
        var reloaded = NewVm();
        reloaded.SetAudioPlayer(new FakeAudioPlayer { HeadphoneSurroundActive = true });
        await reloaded.LoadAsync();
        Assert.True(reloaded.HeadphoneSurround);
        Assert.False(reloaded.HeadphoneSurroundNeedsRestart);
        Assert.True(reloaded.GetSettings().HeadphoneSurround);

        // Turning it back off is pending again until the next launch.
        reloaded.HeadphoneSurround = false;
        Assert.True(reloaded.HeadphoneSurroundNeedsRestart);
    }

    // ── The real player (LibVLC + gapless engine, no audio device) ──

    private static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var c = Path.Combine(dir.Trim(), exe); if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    private static string MakeRearLeftNoise(string ffmpeg, string dir)
    {
        var path = Path.Combine(dir, "rear-left.flac");
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anoisesrc=d=6:r=48000:a=0.25:seed=7",
                     "-af", "pan=5.1|BL=c0", "-c:a", "flac", "-sample_fmt", "s16", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        Assert.True(File.Exists(path), "ffmpeg made no test track");
        return path;
    }

    /// <summary>Plays the file through a fresh player and returns the left/right RMS of what VLC
    /// delivered to the engine (the NOCTIS_ENGINE_TAP capture, s16 stereo).</summary>
    private (double Left, double Right) PlayAndMeasure(string path, bool headphoneSurround)
    {
        var tap = Path.Combine(Path.GetTempPath(), "noctis-engine-in-tap.raw");
        var previousTap = Environment.GetEnvironmentVariable("NOCTIS_ENGINE_TAP");
        var previousFlag = VlcAudioPlayer.HeadphoneSurroundAtLaunch;
        Environment.SetEnvironmentVariable("NOCTIS_ENGINE_TAP", "1");
        VlcAudioPlayer.HeadphoneSurroundAtLaunch = headphoneSurround;
        try
        {
            using (var player = new VlcAudioPlayer())
            {
                Assert.Equal(headphoneSurround, player.HeadphoneSurroundActive);
                long heard = 0;
                string? error = null;
                player.PositionChanged += (_, p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
                player.PlaybackError += (_, e) => error = e;
                player.Play(path);
                var sw = Stopwatch.StartNew();
                while (Interlocked.Read(ref heard) < 3000 && sw.ElapsedMilliseconds < 15000)
                    Thread.Sleep(20);
                Assert.True(Interlocked.Read(ref heard) >= 3000, $"never played (error={error})");
                player.Stop();
                Thread.Sleep(300);
            }

            byte[] bytes;
            using (var fs = new FileStream(tap, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            var frames = bytes.Length / 4;
            Assert.True(frames > 48000 * 2, $"tap holds only {frames} frames");
            double l = 0, r = 0;
            int n = 0;
            for (var f = 24000; f < frames - 4800; f++, n++) // skip the head, like the harness
            {
                double a = BitConverter.ToInt16(bytes, f * 4) / 32768.0, b = BitConverter.ToInt16(bytes, f * 4 + 2) / 32768.0;
                l += a * a;
                r += b * b;
            }
            return (20 * Math.Log10(Math.Sqrt(l / n) + 1e-12), 20 * Math.Log10(Math.Sqrt(r / n) + 1e-12));
        }
        finally
        {
            VlcAudioPlayer.HeadphoneSurroundAtLaunch = previousFlag;
            Environment.SetEnvironmentVariable("NOCTIS_ENGINE_TAP", previousTap);
        }
    }

    [Fact]
    public void RealPlayer_RearLeftOnly51_IsLeftOnlyOff_AndReachesBothEarsOn()
    {
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows libvlc ships the HRTF file the binauralizer needs");
        var ffmpeg = FindFfmpeg();
        Assert.SkipWhen(ffmpeg == null, "no ffmpeg on PATH");
        var path = MakeRearLeftNoise(ffmpeg!, _root);

        var off = PlayAndMeasure(path, headphoneSurround: false);
        var on = PlayAndMeasure(path, headphoneSurround: true);
        _o.WriteLine($"off: L={off.Left:0.0} dBFS R={off.Right:0.0} dBFS; on: L={on.Left:0.0} dBFS R={on.Right:0.0} dBFS");

        // Off: the plain downmix puts the rear-left channel in the left ear only.
        Assert.True(off.Left > -25, $"off left {off.Left:0.0}");
        Assert.True(off.Right < -90, $"off right {off.Right:0.0}");
        // On: the binauralizer reaches the far ear too, quieter (head shadow).
        Assert.True(on.Right > -45, $"on right {on.Right:0.0}");
        Assert.True(on.Left - on.Right > 6, $"on ILD {on.Left - on.Right:0.0}");
    }
}
