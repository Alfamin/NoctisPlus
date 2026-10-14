namespace Noctis.Mobile.Services;

/// <summary>
/// When the audio a player tap hands over will be heard, on the meters' clock, for a tap that is
/// called in bursts. A Media3 audio sink is fed whenever its AudioTrack has room, so buffers can
/// arrive several at once and then not for a while (the emulator: ~250 ms apart); stamping each
/// one "now + latency", as the desktop's evenly pulled WASAPI tap may, squeezed a burst's beats
/// together and stretched the gaps (kicks 500 ms apart logged 550, 550, 300 ms apart). Here the
/// stream runs on its own continuous clock instead, each buffer starting where the last one
/// ended, and is only nudged toward "now + latency" (<see cref="DriftGain"/> a buffer, settled
/// within a second or so of a track starting) so the two cannot wander apart; a jump of more
/// than <see cref="ReanchorMs"/> (a seek, a stall) re-anchors it outright. The latency itself is
/// the tap's to measure (Noctis.Android's MeterTap asks the sink how far it has played).
/// </summary>
internal sealed class TapClock
{
    public const double ReanchorMs = 400;
    public const double DriftGain = 0.05;

    private double _nextMs = double.NaN;

    /// <summary>Forget the stream (a flush, a pause in metering): the next buffer re-anchors.</summary>
    public void Reset() => _nextMs = double.NaN;

    /// <summary>
    /// The latency to stamp a buffer of <paramref name="frames"/> frames fed at
    /// <paramref name="nowMs"/> with: how far ahead of now its first frame is heard. Advances
    /// the stream past the buffer.
    /// </summary>
    public int Next(double nowMs, int latencyMs, int frames, int sampleRate)
    {
        var heard = nowMs + latencyMs;
        if (double.IsNaN(_nextMs) || Math.Abs(heard - _nextMs) > ReanchorMs) _nextMs = heard;
        else _nextMs += (heard - _nextMs) * DriftGain;
        var ahead = (int)Math.Round(_nextMs - nowMs);
        _nextMs += 1000.0 * frames / sampleRate;
        return ahead;
    }
}
