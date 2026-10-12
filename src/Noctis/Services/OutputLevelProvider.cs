using System;
using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// The user's volume on the gapless engine's Linux output (GitHub #127). On Windows the
/// engine's stream lives in the process audio session and the level is written there
/// (WindowsSessionVolume); a PulseAudio stream has no such handle through
/// libpulse-simple, and LibVLC's own volume would scale the PCM BEFORE the seconds of
/// staged ring (the reason mute moved to <see cref="MuteGateProvider"/>). So the level is
/// applied here, in the render chain after the ring, and is heard within the output
/// buffer. At level 1 it is a pure pass-through, so the bit-exact splice stays
/// bit-exact; a change slews per frame over a few ms so slider moves never click.
/// Runs on the render thread; the target is the only cross-thread state.
/// </summary>
internal sealed class OutputLevelProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _stepPerFrame;
    private volatile float _target = 1f;
    private float _gain = 1f; // render thread only

    public OutputLevelProvider(ISampleProvider source, int slewMs = 15)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = Math.Max(1, source.WaveFormat.Channels);
        var slewFrames = Math.Max(1, source.WaveFormat.SampleRate * Math.Max(1, slewMs) / 1000);
        _stepPerFrame = 1f / slewFrames;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Amplitude 0..1, set from any thread; the next render pass slews toward it.</summary>
    public float Level
    {
        get => _target;
        set => _target = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Gain applied to the last frame read; for diagnostics and tests.</summary>
    public float CurrentGain => _gain;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        var target = _target;
        if (_gain == target)
        {
            if (target != 1f)
            {
                for (var i = 0; i < read; i++)
                    buffer[offset + i] *= target;
            }
            return read;
        }

        // Slewing: one gain value per frame so all channels move together.
        for (var i = 0; i < read; i += _channels)
        {
            if (_gain < target) _gain = Math.Min(target, _gain + _stepPerFrame);
            else if (_gain > target) _gain = Math.Max(target, _gain - _stepPerFrame);

            var end = Math.Min(i + _channels, read);
            for (var s = i; s < end; s++)
                buffer[offset + s] *= _gain;
        }
        return read;
    }
}
