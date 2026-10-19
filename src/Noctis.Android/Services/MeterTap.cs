using System.Runtime.InteropServices;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer.Audio;
using Java.Nio;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// The phone's equivalent of the desktop's BeatTapProvider: Media3's TeeAudioProcessor hands
/// every PCM buffer the player renders (after decoding, before the AudioTrack) to this sink on
/// ExoPlayer's playback thread, and it feeds <see cref="RowEqLevels.Shared"/>'s
/// <see cref="BeatMeter"/> and <see cref="SpectrumMeter"/>, which the playing row's bars read.
/// No microphone, no android.media.audiofx.Visualizer and so no RECORD_AUDIO: these are the
/// app's own samples.
/// Cheap by construction: nothing is metered unless a bar has read the levels in the last
/// second (<see cref="RowEqLevels.IsWanted"/>), the samples are copied out of the direct buffer
/// into scratch arrays that grow once and are reused, and the meters' own feed is a few flops a
/// sample (the FFT runs on the reader's side, once a frame). A paused player renders nothing, so
/// nothing runs here then.
/// Timing: the processed audio sits in the AudioTrack's buffer before it is heard, so blocks are
/// stamped that far ahead, the way the desktop stamps WASAPI's. How far is measured, not guessed:
/// the frames tapped since the last flush, against the sink's own heard position (what ExoPlayer
/// reports as the playback position), <see cref="AheadMs"/>; on a continuous stream clock
/// (<see cref="TapClock"/>), because the sink is fed in bursts.
/// </summary>
internal sealed class MeterTap : Java.Lang.Object, TeeAudioProcessor.IAudioBufferSink
{
    /// <summary>The ahead-of-the-ear time assumed until the sink can say (Media3's minimum PCM
    /// AudioTrack buffer).</summary>
    internal const int FallbackBufferMs = 250;

    private readonly RowEqLevels _levels;
    private int _rate, _channels, _encoding;
    private int _latencyMs = -1;
    private readonly TapClock _clock = new();
    /// <summary>The sink's heard position when the first frame after a flush was tapped, and the
    /// frames tapped since (counted whether or not they were metered).</summary>
    private long _baseUs = long.MinValue, _framesTapped;
    private float[] _samples = Array.Empty<float>();
    private short[] _shorts = Array.Empty<short>();
    private byte[] _bytes = Array.Empty<byte>();
    private bool _failed;

    public MeterTap(RowEqLevels levels)
    {
        _levels = levels;
    }

    /// <summary>The sink the tee sits in, asked for its heard position and AudioTrack buffer length.</summary>
    public IAudioSink? Sink { get; set; }

    /// <summary>The output path's own latency below the AudioTrack (Media3AudioPlayer.OutputLatency).</summary>
    public int HardwareLatencyMs { get; set; }

    /// <summary>The fallback for <see cref="AheadMs"/>: the AudioTrack buffer plus the output path,
    /// once the sink has an AudioTrack; until then <see cref="FallbackBufferMs"/>.</summary>
    internal int LatencyMs
    {
        get
        {
            if (_latencyMs >= 0) return _latencyMs;
            var us = Sink?.AudioTrackBufferSizeUs ?? C.TimeUnset;
            if (us <= 0 || us == C.TimeUnset) return FallbackBufferMs + HardwareLatencyMs;
            _latencyMs = (int)(us / 1000) + HardwareLatencyMs;
            return _latencyMs;
        }
    }

    /// <summary>
    /// How far ahead of the ear the next tapped frame is: everything tapped since the flush, less
    /// what the sink has played (its position counts from where the stream started, which is
    /// where it stood when the first frame was tapped). Falls back to <see cref="LatencyMs"/>
    /// while the sink has no position, or if the two ever disagree wildly (a discontinuity).
    /// </summary>
    private int AheadMs()
    {
        var heardUs = Sink?.GetCurrentPositionUs(false) ?? long.MinValue;
        if (heardUs == long.MinValue) return LatencyMs;   // AudioSink.CURRENT_POSITION_NOT_SET
        var tappedUs = _framesTapped * 1_000_000L / _rate;
        if (_baseUs == long.MinValue)
        {
            _baseUs = heardUs - tappedUs + (_framesTapped == 0 ? 0 : LatencyMs * 1000L);
            DebugLog.Write("Audio", $"Meter tap: {_rate} Hz x{_channels} enc {_encoding}, AudioTrack {LatencyMs} ms");
        }
        var ahead = (_baseUs + tappedUs - heardUs) / 1000;
        if (ahead is >= -100 and <= 3000) return (int)ahead;
        _baseUs = heardUs - tappedUs + LatencyMs * 1000L;
        return LatencyMs;
    }

    /// <summary>Format of the buffers that follow (a new track, a seek, a format change).</summary>
    public void Flush(int sampleRateHz, int channelCount, int encoding)
    {
        _rate = sampleRateHz;
        _channels = channelCount;
        _encoding = encoding;
        _latencyMs = -1;   // a new AudioTrack may have another buffer size
        _baseUs = long.MinValue;
        _framesTapped = 0;
        _clock.Reset();
    }

    public void HandleBuffer(ByteBuffer? buffer)
    {
        if (buffer == null) return;
        try
        {
            if (_failed || _rate <= 0 || _channels <= 0) return;
            var width = BytesPerSample(_encoding);
            if (width == 0) return;
            var count = buffer.Remaining() / width;
            count -= count % _channels;
            if (count <= 0) return;
            var frames = count / _channels;
            // Pin where the stream starts on its first buffer even if nothing is metered yet.
            if (_baseUs == long.MinValue && _framesTapped == 0) AheadMs();
            var now = _levels.NowMs;
            if (!_levels.IsWanted(now))
            {
                _framesTapped += frames;
                _clock.Reset();
                return;
            }
            Read(buffer, count, width);
            var latency = _clock.Next(now, AheadMs(), frames, _rate);
            _framesTapped += frames;
            // The beat meter stamps a buffer by its first frame, the spectrum meter by its last.
            _levels.Beat.Feed(_samples, 0, count, _channels, _rate, latency);
            _levels.Spectrum.Feed(_samples, 0, count, _channels, _rate, latency + (int)Math.Round(1000.0 * frames / _rate));
        }
        catch (Exception ex)
        {
            // A visual nicety must never break playback: stop metering for this session.
            _failed = true;
            DebugLog.Write("Audio", $"Meter tap off: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // The tee hands a fresh read-only view per buffer; drop its peer now, not at a GC.
            buffer.Dispose();
        }
    }

    internal static int BytesPerSample(int encoding) => encoding switch
    {
        C.EncodingPcm16bit => 2,
        C.EncodingPcmFloat => 4,
        C.EncodingPcm24bit => 3,
        C.EncodingPcm32bit => 4,
        _ => 0,
    };

    /// <summary><paramref name="count"/> samples from the buffer's position into
    /// <see cref="_samples"/> as -1..1 floats.</summary>
    private void Read(ByteBuffer buffer, int count, int width)
    {
        if (_samples.Length < count) _samples = new float[count];
        var address = JNIEnv.GetDirectBufferAddress(buffer.Handle);
        if (address != IntPtr.Zero)
        {
            address += buffer.Position();
            switch (_encoding)
            {
                case C.EncodingPcmFloat:
                    Marshal.Copy(address, _samples, 0, count);
                    return;
                case C.EncodingPcm16bit:
                    if (_shorts.Length < count) _shorts = new short[count];
                    Marshal.Copy(address, _shorts, 0, count);
                    for (var i = 0; i < count; i++) _samples[i] = _shorts[i] * (1f / 32768f);
                    return;
            }
            EnsureBytes(count * width);
            Marshal.Copy(address, _bytes, 0, count * width);
        }
        else
        {
            // A heap buffer (not seen from Media3's decoders, which hand direct ones): copy it out.
            EnsureBytes(count * width);
            buffer.Get(_bytes, 0, count * width);
        }
        FromBytes(_bytes, _encoding, _samples, count);
    }

    private void EnsureBytes(int length)
    {
        if (_bytes.Length < length) _bytes = new byte[length];
    }

    /// <summary>Little-endian PCM bytes to -1..1 floats.</summary>
    internal static void FromBytes(byte[] bytes, int encoding, float[] samples, int count)
    {
        switch (encoding)
        {
            case C.EncodingPcm16bit:
                for (var i = 0; i < count; i++) samples[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8) * (1f / 32768f);
                break;
            case C.EncodingPcm24bit:
                for (var i = 0; i < count; i++)
                    samples[i] = (bytes[3 * i] << 8 | bytes[3 * i + 1] << 16 | bytes[3 * i + 2] << 24) * (1f / 2147483648f);
                break;
            case C.EncodingPcm32bit:
                for (var i = 0; i < count; i++) samples[i] = BitConverter.ToInt32(bytes, 4 * i) * (1f / 2147483648f);
                break;
            case C.EncodingPcmFloat:
                for (var i = 0; i < count; i++) samples[i] = BitConverter.ToSingle(bytes, 4 * i);
                break;
        }
    }
}
