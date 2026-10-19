using NAudio.Wave;

namespace Noctis.Services;

/// <summary>
/// Pass-through <see cref="ISampleProvider"/> that feeds <see cref="BeatMeter"/> and
/// <see cref="SpectrumMeter"/> with whatever flows through it. Sits at the end of an
/// output chain, after gain and mute. (The meters live in Noctis.Core, which the phone
/// shares; this NAudio tap is the desktop's feed.)
/// </summary>
public sealed class BeatTapProvider : ISampleProvider
{
    private readonly ISampleProvider _inner;
    private readonly BeatMeter _meter;
    private readonly SpectrumMeter _spectrum;
    private readonly int _latencyMs;

    public BeatTapProvider(ISampleProvider inner, int latencyMs, BeatMeter? meter = null, SpectrumMeter? spectrum = null)
    {
        _inner = inner;
        _latencyMs = latencyMs;
        _meter = meter ?? BeatMeter.Shared;
        _spectrum = spectrum ?? SpectrumMeter.Shared;
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            try
            {
                _meter.Feed(buffer, offset, read, WaveFormat.Channels, WaveFormat.SampleRate, _latencyMs);
                _spectrum.Feed(buffer, offset, read, WaveFormat.Channels, WaveFormat.SampleRate, _latencyMs);
            }
            catch
            {
                // A visual nicety must never break the render thread.
            }
        }
        return read;
    }
}
