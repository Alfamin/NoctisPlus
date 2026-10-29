using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// What the playing row's equalizer (Views.PlayingBars) reads each frame: the beat pulse and
/// five spectrum bands of the audio being heard, from the same <see cref="BeatMeter"/> and
/// <see cref="SpectrumMeter"/> the desktop's row indicator reads (they live in Noctis.Core).
/// On Android the player's PCM tap feeds them (Noctis.Android's MeterTap); where nothing
/// feeds them (tests, a player without a tap) <see cref="TryRead"/> says so and the bars run
/// the desktop's no-tap motion.
/// One read per frame is shared by every bar on screen: the FFT runs once however many
/// indicators ask in that frame. Reads also mark the levels as wanted, and the tap feeds
/// only while they are (<see cref="IsWanted"/>), so with no indicator on screen, or every
/// one paused, the audio thread does no metering at all.
/// </summary>
public sealed class RowEqLevels
{
    /// <summary>The process-wide source the Android tap feeds and the bars read by default.</summary>
    public static RowEqLevels Shared { get; } = new(BeatMeter.Shared, SpectrumMeter.Shared);

    public const int BandCount = 5;

    /// <summary>How long after the last read the tap keeps feeding: a few frames' slack, so a
    /// scroll or a page change never starves the meters, and short enough to stop soon after.</summary>
    public const double DemandWindowMs = 1000;

    /// <summary>Reads closer together than this share one computation (one frame).</summary>
    private const double SameFrameMs = 4;

    private readonly float[] _bands = new float[BandCount];
    private double _readAtMs = double.NegativeInfinity;
    private double _pulse;
    private bool _live;
    private long _demandMsBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);

    public RowEqLevels(BeatMeter beat, SpectrumMeter spectrum)
    {
        Beat = beat;
        Spectrum = spectrum;
    }

    public BeatMeter Beat { get; }
    public SpectrumMeter Spectrum { get; }

    /// <summary>Milliseconds on the meters' clock.</summary>
    public double NowMs => Beat.NowMs;

    /// <summary>An indicator read within <see cref="DemandWindowMs"/>: the tap should feed.
    /// Any thread (the tap asks on the audio thread).</summary>
    public bool IsWanted(double nowMs) =>
        nowMs - BitConverter.Int64BitsToDouble(Volatile.Read(ref _demandMsBits)) <= DemandWindowMs;

    /// <summary>
    /// UI thread: the beat <paramref name="pulse"/> (0..1) and <paramref name="bands"/> (0..1,
    /// bass to treble) heard at <paramref name="nowMs"/>. False, with zeros, when no live audio
    /// is flowing; the caller falls back to its own motion.
    /// </summary>
    public bool TryRead(double nowMs, out double pulse, Span<float> bands)
    {
        Volatile.Write(ref _demandMsBits, BitConverter.DoubleToInt64Bits(nowMs));
        if (Math.Abs(nowMs - _readAtMs) >= SameFrameMs)
        {
            _readAtMs = nowMs;
            _live = Beat.TryRead(nowMs, out _pulse);
            if (!_live || !Spectrum.TryRead(nowMs, _bands)) Array.Clear(_bands);
        }
        pulse = _live ? _pulse : 0;
        _bands.AsSpan(0, Math.Min(bands.Length, BandCount)).CopyTo(bands);
        return _live;
    }
}
