using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The off-Windows visualizer feed: LibVLC plays straight to the device there (no
/// amem callbacks, no NAudio chain, so <see cref="BeatTapProvider"/> never runs and
/// the meters stay dead — Discord "row of dots doing nothing" on the Flatpak build).
/// <see cref="SideDecodeFeedCore"/> pulls the same track from a side decode (ffmpeg
/// in production, a synthetic stream here) and feeds the meters ahead of the
/// player's clock: it opens near the heard position, reads exactly what the lookahead
/// needs, reopens on seeks instead of chewing through the gap, and stops at EOF.
/// </summary>
public class SideDecodeFeedCoreTests
{
    private const int Rate = 44100;
    private const int Bands = 48;

    private sealed class Source
    {
        public readonly List<long> Opens = new();
        public int Disposed;
        public double Hz = 1000;
        public double LengthSeconds = 30;

        public Stream? Open(long startFrame)
        {
            Opens.Add(startFrame);
            var total = (long)(LengthSeconds * Rate);
            var frames = (int)Math.Max(0, total - startFrame);
            var samples = new float[frames];
            for (var i = 0; i < frames; i++)
                samples[i] = (float)(0.9 * Math.Sin(2 * Math.PI * Hz * (startFrame + i) / Rate));
            return new Tracking(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), this);
        }

        private sealed class Tracking : MemoryStream
        {
            private readonly Source _owner;
            public Tracking(byte[] data, Source owner) : base(data, writable: false) => _owner = owner;
            protected override void Dispose(bool disposing) { _owner.Disposed++; base.Dispose(disposing); }
        }
    }

    private static (SideDecodeFeedCore core, Source src, SpectrumMeter spectrum, BeatMeter beat) Make(double lengthSeconds = 30)
    {
        var src = new Source { LengthSeconds = lengthSeconds };
        var spectrum = new SpectrumMeter(() => 0);
        var beat = new BeatMeter(() => 0);
        var core = new SideDecodeFeedCore(src.Open, Rate, spectrum, beat);
        return (core, src, spectrum, beat);
    }

    private static long Frames(double ms) => (long)Math.Round(ms * Rate / 1000.0);

    private static int BandOf(double hz)
    {
        var logMin = Math.Log(SpectrumMeter.MinHz);
        var logSpan = Math.Log(SpectrumMeter.MaxHz) - logMin;
        return (int)Math.Floor((Math.Log(hz) - logMin) / logSpan * Bands);
    }

    private static int ArgMax(float[] bands)
    {
        var best = 0;
        for (var i = 1; i < bands.Length; i++) if (bands[i] > bands[best]) best = i;
        return best;
    }

    [Fact]
    public void Advance_FeedsAheadOfTheClock_AndTheToneLandsInItsBand()
    {
        var (core, src, spectrum, beat) = Make();

        for (var ms = 0; ms <= 600; ms += 20) core.Advance(ms);

        Assert.Equal(new[] { 0L }, src.Opens);
        Assert.Equal(Frames(600 + SideDecodeFeedCore.LookaheadMs), core.DecodedFrames);
        var bands = new float[Bands];
        Assert.True(spectrum.TryRead(0, bands));
        Assert.Equal(BandOf(1000), ArgMax(bands));
        Assert.InRange(bands[BandOf(1000)], 0.85f, 1.0f);
        Assert.True(beat.IsLive(0));
    }

    [Fact]
    public void Advance_SeekBack_ReopensJustBeforeTheNewTarget()
    {
        var (core, src, _, _) = Make();
        for (var ms = 0; ms <= 10_000; ms += 20) core.Advance(ms);

        core.Advance(2000);

        Assert.Equal(2, src.Opens.Count);
        Assert.Equal(1, src.Disposed);
        Assert.Equal(Frames(2000 + SideDecodeFeedCore.LookaheadMs - SideDecodeFeedCore.PrimeMs), src.Opens[1]);
        Assert.Equal(Frames(2000 + SideDecodeFeedCore.LookaheadMs), core.DecodedFrames);
    }

    [Fact]
    public void Advance_BigJumpForward_ReopensInsteadOfCatchingUp()
    {
        var (core, src, _, _) = Make(lengthSeconds: 120);
        for (var ms = 0; ms <= 1000; ms += 20) core.Advance(ms);

        core.Advance(60_000);

        Assert.Equal(2, src.Opens.Count);
        Assert.Equal(Frames(60_000 + SideDecodeFeedCore.LookaheadMs - SideDecodeFeedCore.PrimeMs), src.Opens[1]);
        Assert.Equal(Frames(60_000 + SideDecodeFeedCore.LookaheadMs), core.DecodedFrames);
    }

    [Fact]
    public void Advance_SmallStall_CatchesUpOnTheSameStream()
    {
        var (core, src, _, _) = Make();
        for (var ms = 0; ms <= 1000; ms += 20) core.Advance(ms);

        core.Advance(1000 + SideDecodeFeedCore.MaxCatchUpMs - 100);

        Assert.Single(src.Opens);
        Assert.Equal(Frames(1000 + SideDecodeFeedCore.MaxCatchUpMs - 100 + SideDecodeFeedCore.LookaheadMs), core.DecodedFrames);
    }

    [Fact]
    public void Advance_PastTheEnd_StopsWithoutReopening()
    {
        var (core, src, _, _) = Make(lengthSeconds: 1);
        for (var ms = 0; ms <= 1500; ms += 20) core.Advance(ms);

        Assert.True(core.Ended);
        Assert.Single(src.Opens);
        Assert.Equal(Frames(1000), core.DecodedFrames);

        // A seek back into the file plays again.
        core.Advance(200);
        Assert.False(core.Ended);
        Assert.Equal(2, src.Opens.Count);
    }

    [Fact]
    public void Reset_ClosesTheStream_AndTheNextAdvanceReopens()
    {
        var (core, src, _, _) = Make();
        core.Advance(0);

        core.Reset();

        Assert.Equal(1, src.Disposed);
        Assert.Equal(0, core.DecodedFrames);
        core.Advance(5000);
        Assert.Equal(2, src.Opens.Count);
        Assert.Equal(Frames(5000 + SideDecodeFeedCore.LookaheadMs - SideDecodeFeedCore.PrimeMs), src.Opens[1]);
    }

    [Fact]
    public void Advance_WhenTheSourceCannotOpen_IsQuiet()
    {
        var spectrum = new SpectrumMeter(() => 0);
        var opens = 0;
        var core = new SideDecodeFeedCore(_ => { opens++; return null; }, Rate, spectrum, new BeatMeter(() => 0));

        core.Advance(0);
        core.Advance(20);

        Assert.Equal(0, core.DecodedFrames);
        Assert.False(spectrum.IsLive(0));
        // No reopen storm: one attempt, then wait for a seek/track change.
        Assert.Equal(1, opens);
    }

    [Theory]
    [InlineData(null, true)]   // every platform: the thread decides per tick
    [InlineData("1", true)]
    [InlineData("0", false)]   // escape hatch
    public void ShouldRun_OnEveryPlatformUnlessDisabled(string? env, bool expected)
        => Assert.Equal(expected, SideDecodeMeterFeed.ShouldRun(env));

    [Theory]
    [InlineData(null, false, true)]   // LibVLC's own output (Windows shared without the engine, macOS)
    [InlineData(null, true, false)]   // engine / exclusive sink taps the meters: never a second writer
    [InlineData("1", true, true)]     // dev override
    [InlineData("0", false, false)]   // escape hatch
    public void ShouldFeed_OnlyWhileTheOutputDoesNotTapTheMeters(string? env, bool outputTaps, bool expected)
        => Assert.Equal(expected, SideDecodeMeterFeed.ShouldFeed(env, outputTaps));

    // Discord (1.6.2, Windows): "Exclusive OFF = dead Visualizer". An Exclusive Mode toggle
    // (or a persisted "on" applied at startup) rebuilds the players and retires the gapless
    // engine until restart, so shared mode then plays through LibVLC's own output, with no
    // tap. The old gate kept the side feed off on Windows whatever the output was.
    [Theory]
    [InlineData(false, false, false, false)] // engine retired by a rebuild, exclusive off: no tap
    [InlineData(false, false, true, true)]   // gapless engine live
    [InlineData(false, true, false, true)]   // exclusive callbacks (exclusive or shared-fallback sink)
    [InlineData(true, false, false, true)]   // NOCTIS_WASAPI experimental sink
    public void VlcPlayer_OutputTapsMeters_OnlyOnTheTappedChains(bool wasapiSink, bool exclusiveBuilt, bool engineLive, bool expected)
        => Assert.Equal(expected, VlcAudioPlayer.OutputTapsMetersFor(wasapiSink, exclusiveBuilt, engineLive));

    private sealed class FeedRig : IDisposable
    {
        public readonly FakeAudioPlayer Player = new();
        public readonly Source Src = new() { LengthSeconds = 120 };
        public readonly SpectrumMeter Spectrum = new(() => 0);
        public readonly BeatMeter Beat = new(() => 0);
        public readonly SideDecodeMeterFeed Feed;
        public readonly string File = Path.Combine(Path.GetTempPath(), "noctis-sidefeed-" + Guid.NewGuid().ToString("N") + ".flac");
        public double Now;

        public FeedRig(string? env = null)
        {
            System.IO.File.WriteAllBytes(File, new byte[16]);
            Feed = new SideDecodeMeterFeed(Player, () => "ffmpeg", (_, _, frame) => Src.Open(frame),
                Spectrum, Beat, env, () => Now);
        }

        /// <summary>Plays on for <paramref name="ms"/>, ticking every 20 ms.</summary>
        public void Run(double ms)
        {
            for (double t = 0; t < ms; t += 20)
            {
                Player.Position += TimeSpan.FromMilliseconds(20);
                Now += 20;
                Feed.Tick();
            }
        }

        public void Dispose()
        {
            Feed.Dispose();
            try { System.IO.File.Delete(File); } catch { }
        }
    }

    [Fact]
    public void Tick_SharedOutputWithoutTap_FeedsTheMeters()
    {
        using var rig = new FeedRig();
        rig.Player.OutputTapsMeters = false; // Windows shared after the engine was retired
        rig.Player.Play(rig.File);

        rig.Run(600);

        Assert.Single(rig.Src.Opens);
        var bands = new float[Bands];
        Assert.True(rig.Spectrum.TryRead(0, bands));
        Assert.Equal(BandOf(1000), ArgMax(bands));
        Assert.True(rig.Beat.IsLive(0));
    }

    [Fact]
    public void Tick_WhileTheOutputTapsTheMeters_StandsAside()
    {
        using var rig = new FeedRig();
        rig.Player.OutputTapsMeters = true; // gapless engine / exclusive sink
        rig.Player.Play(rig.File);

        rig.Run(600);

        Assert.Empty(rig.Src.Opens);
        Assert.False(rig.Spectrum.IsLive(0));
        Assert.False(rig.Beat.IsLive(0));
    }

    [Fact]
    public void Tick_TapLostMidTrack_TakesOverAtTheHeardSpot_AndYieldsWhenItReturns()
    {
        using var rig = new FeedRig();
        rig.Player.OutputTapsMeters = true;
        rig.Player.Play(rig.File);
        rig.Player.Position = TimeSpan.FromSeconds(30);
        rig.Run(200);
        Assert.Empty(rig.Src.Opens);

        // Exclusive Mode toggled off: the rebuilt players play through LibVLC's own output.
        rig.Player.OutputTapsMeters = false;
        rig.Run(200);

        Assert.Single(rig.Src.Opens);
        var opened = rig.Src.Opens[0];
        Assert.InRange(opened, Frames(30_000), Frames(30_200 + SideDecodeFeedCore.LookaheadMs));
        Assert.True(rig.Spectrum.IsLive(0));

        // Exclusive back on: the sink's tap owns the meters again; the decode is dropped.
        rig.Player.OutputTapsMeters = true;
        rig.Feed.Tick();
        Assert.Equal(1, rig.Src.Disposed);
        rig.Run(200);
        Assert.Single(rig.Src.Opens);
    }

    [Fact]
    public void Tick_DevOverride_FeedsEvenWithTheTap()
    {
        using var rig = new FeedRig(env: "1");
        rig.Player.OutputTapsMeters = true;
        rig.Player.Play(rig.File);

        rig.Run(200);

        Assert.Single(rig.Src.Opens);
    }
}

/// <summary>
/// The real pipe: ffmpeg decodes a generated WAV (1 kHz for the first second, 3 kHz
/// for the second) from a mid-file start frame; the meter shows the tone at the
/// seek point, and disposing the stream leaves no ffmpeg behind. Skipped (passes
/// vacuously) when no ffmpeg is on this machine.
/// </summary>
public class FfmpegPcmPipeTests
{
    private const int Rate = 44100;

    private static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var c = Path.Combine(dir.Trim(), exe); if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    private static string WriteTwoToneWav()
    {
        var path = Path.Combine(Path.GetTempPath(), "noctis-sidefeed-" + Guid.NewGuid().ToString("N") + ".wav");
        var frames = Rate * 2;
        var data = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var hz = i < Rate ? 1000 : 3000;
            var s = (short)(0.9 * short.MaxValue * Math.Sin(2 * Math.PI * hz * i / Rate));
            data[i * 2] = (byte)s;
            data[i * 2 + 1] = (byte)(s >> 8);
        }
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        return path;
    }

    [Fact]
    public void Pipe_SeeksToTheStartFrame_AndTheToneThereReachesTheMeter()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return;
        var wav = WriteTwoToneWav();
        try
        {
            var spectrum = new SpectrumMeter(() => 0);
            var core = new SideDecodeFeedCore(f => FfmpegPcmPipe.Open(ffmpeg, wav, f, Rate), Rate, spectrum, new BeatMeter(() => 0));

            // Heard position 1.4 s: the primed window sits entirely inside the 3 kHz half.
            for (var ms = 1400; ms <= 1700; ms += 20) core.Advance(ms);

            var bands = new float[48];
            Assert.True(spectrum.TryRead(0, bands));
            var logMin = Math.Log(SpectrumMeter.MinHz);
            var logSpan = Math.Log(SpectrumMeter.MaxHz) - logMin;
            var band3k = (int)Math.Floor((Math.Log(3000) - logMin) / logSpan * 48);
            var band1k = (int)Math.Floor((Math.Log(1000) - logMin) / logSpan * 48);
            Assert.InRange(bands[band3k], 0.85f, 1.0f);
            Assert.InRange(bands[band1k], 0f, 0.2f);

            // EOF at 2 s, then the process is gone once the stream is dropped.
            for (var ms = 1720; ms <= 2400; ms += 20) core.Advance(ms);
            Assert.True(core.Ended);
            core.Reset();
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }
}
