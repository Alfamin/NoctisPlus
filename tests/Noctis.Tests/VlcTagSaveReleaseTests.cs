using System.Diagnostics;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The real player on the tag save of its playing track (live, 10-13: "Access to the path is
/// denied" saving lyrics of the paused, playing .m4a). Runs LibVLC and the gapless engine
/// with no audio device: only with NOCTIS_AOUT=dummy set for the test run (silent mode is
/// read once per process) and ffmpeg on PATH to make the track; skipped otherwise.
/// </summary>
[Collection("Tag save replace")] // PosixReplaceDisabled is process-wide
public class VlcTagSaveReleaseTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _o;

    public VlcTagSaveReleaseTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var c = Path.Combine(dir.Trim(), exe); if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    private string MakeM4a(string ffmpeg)
    {
        var path = Path.Combine(_dir, "Paste Test.m4a");
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=30",
                     "-c:a", "aac", "-b:a", "96k", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        Assert.True(File.Exists(path), "ffmpeg made no test track");
        return path;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    private static string FileLyrics(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Tag.Lyrics ?? "";
    }

    private sealed class Rig : IDisposable
    {
        public readonly VlcAudioPlayer Player = new();
        private long _heardMs = -1;
        private long _lowestHeardMs = long.MaxValue;
        public string? Error;

        public Rig()
        {
            Player.PositionChanged += (_, p) =>
            {
                var ms = (long)p.TotalMilliseconds;
                Interlocked.Exchange(ref _heardMs, ms);
                long low;
                while (ms < (low = Interlocked.Read(ref _lowestHeardMs)) &&
                       Interlocked.CompareExchange(ref _lowestHeardMs, ms, low) != low) { }
            };
            Player.PlaybackError += (_, e) => Error = e;
        }

        public long HeardMs => Interlocked.Read(ref _heardMs);

        /// <summary>The lowest position reported since the last call (a restart from 0 shows here).</summary>
        public long TakeLowestHeardMs() => Interlocked.Exchange(ref _lowestHeardMs, long.MaxValue);

        public void PlayUntil(string path, long ms)
        {
            Player.Play(path);
            Assert.True(WaitFor(() => HeardMs > ms), $"never played (error={Error})");
        }

        public long Pause()
        {
            Player.Pause();
            Assert.True(WaitFor(() => Player.State == PlaybackState.Paused));
            Thread.Sleep(300);
            return HeardMs;
        }

        public void Dispose()
        {
            Player.Stop();
            Thread.Sleep(300);
            Player.Dispose();
        }
    }

    private static void SkipUnlessRunnable(out string ffmpeg)
    {
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file replace rules");
        ffmpeg = FindFfmpeg()!;
        Assert.SkipWhen(ffmpeg == null, "no ffmpeg on PATH");
    }

    [Fact]
    public void TagSaveOfThePlayingTrack_ReplacesTheFileUnderLibVlc_AndPlaybackNeverStops()
    {
        SkipUnlessRunnable(out var ffmpeg);
        var path = MakeM4a(ffmpeg);
        var metadata = new MetadataService();
        using var rig = new Rig();
        rig.PlayUntil(path, 1500);

        // LibVLC holds the playing file: the plain replace (the old save) is refused, the
        // live "Access to the path is denied" …
        var copy = path + ".copy";
        File.Copy(path, copy);
        var refused = Record.Exception(() => File.Move(copy, path, overwrite: true));
        File.Delete(copy);
        _o.WriteLine($"plain replace under LibVLC: {refused?.GetType().Name}: {refused?.Message}");
        Assert.IsType<UnauthorizedAccessException>(refused);
        // … while its handle shares write and delete.
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }

        var session = rig.Player.CurrentSessionId;
        var before = rig.HeardMs;
        Assert.True(metadata.WriteLyrics(path, "new words", null));
        Assert.Equal("new words", FileLyrics(path));
        Assert.True(WaitFor(() => rig.HeardMs > before + 1000), $"playback stopped (error={rig.Error})");
        Assert.Equal(session, rig.Player.CurrentSessionId); // never reopened

        var pausedAt = rig.Pause();
        Assert.True(metadata.WriteLyrics(path, "paused words", null));
        Assert.Equal("paused words", FileLyrics(path));
        Assert.Equal(PlaybackState.Paused, rig.Player.State);
        rig.Player.Resume();
        Assert.True(WaitFor(() => rig.HeardMs > pausedAt + 1000), $"no playback after resume (error={rig.Error})");
        Assert.Equal(session, rig.Player.CurrentSessionId);
        Assert.Single(Directory.GetFiles(_dir));
        Assert.Null(rig.Error);
    }

    // Where the POSIX rename is refused (FAT, exFAT, network shares) the player lets go of
    // the file for the rename and reopens it where it was.
    [Fact]
    public void TagSaveOfThePlayingTrack_WithoutPosixRename_ThePlayerReleasesAndReopensIt()
    {
        SkipUnlessRunnable(out var ffmpeg);
        var path = MakeM4a(ffmpeg);
        var metadata = new MetadataService();
        using var rig = new Rig();
        MetadataService.PosixReplaceDisabled = true;
        try
        {
            rig.PlayUntil(path, 1500);
            // Nothing registered: the live failure.
            Assert.False(metadata.WriteLyrics(path, "before the fix", null));
            Assert.Equal("", FileLyrics(path));

            OpenFileHolders.Register(rig.Player);
            try
            {
                // Paused, like the live report.
                var pausedAt = rig.Pause();
                var session = rig.Player.CurrentSessionId;
                Assert.True(metadata.WriteLyrics(path, "new words", null));
                Assert.Equal("new words", FileLyrics(path));
                Assert.Single(Directory.GetFiles(_dir));
                Assert.True(WaitFor(() => rig.Player.CurrentSessionId != session)); // reopened
                Thread.Sleep(500);
                Assert.Equal(PlaybackState.Paused, rig.Player.State);
                Assert.Equal(path, rig.Player.CurrentMediaPath);

                rig.TakeLowestHeardMs();
                rig.Player.Resume();
                Assert.True(WaitFor(() => rig.HeardMs > pausedAt + 1000), $"no playback after resume (error={rig.Error})");
                var resumedFrom = rig.TakeLowestHeardMs();
                _o.WriteLine($"paused at {pausedAt} ms, resumed after the reopen from {resumedFrom} ms");
                Assert.InRange(resumedFrom, pausedAt - 300, pausedAt + 300);

                // Playing: the save lands and the music goes on from where it was.
                var before = rig.HeardMs;
                rig.TakeLowestHeardMs();
                var sw = Stopwatch.StartNew();
                Assert.True(metadata.WriteLyrics(path, "other words", null));
                var saveMs = sw.ElapsedMilliseconds;
                Assert.Equal("other words", FileLyrics(path));
                Assert.True(WaitFor(() => rig.HeardMs > before + 1000), $"no playback after the save (error={rig.Error})");
                var lowest = rig.TakeLowestHeardMs();
                _o.WriteLine($"playing save took {saveMs} ms; position {before} -> {rig.HeardMs} ms after {sw.ElapsedMilliseconds} ms, lowest {lowest} ms");
                Assert.InRange(lowest, before - 300, before + 1000);
                Assert.Equal(PlaybackState.Playing, rig.Player.State);
                Assert.True(rig.HeardMs < before + sw.ElapsedMilliseconds + 1000, "jumped ahead");
                Assert.Null(rig.Error);
            }
            finally
            {
                OpenFileHolders.Unregister(rig.Player);
            }
        }
        finally
        {
            MetadataService.PosixReplaceDisabled = false;
        }
    }
}
