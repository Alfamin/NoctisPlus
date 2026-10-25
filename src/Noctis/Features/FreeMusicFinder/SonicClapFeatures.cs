using Noctis.Services.AudioAnalysis;

namespace FreeMusicFinder;

/// <summary>CLAP's unfused 48 kHz / 10 s log-mel frontend. Slaney frequency scale and area-normalized filters,
/// centered reflect padding, periodic Hann, power 2, 10 log10, floor 1e-10. No random crops or extra loudness transform.
/// Checked against the model's Transformers frontend; changing this requires a new embedding-space version.</summary>
internal static class SonicClapFeatures
{
    public const int Rate = 48000, Samples = 480000, Frames = 1001, Bins = 64;
    private const int FftSize = 1024, Hop = 480;
    private static readonly double[] Window = Enumerable.Range(0, FftSize).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / FftSize)).ToArray();
    private static readonly (int Bin, double Weight)[][] Filters = MakeFilters();

    private static (int Bin, double Weight)[][] MakeFilters()
    {
        static double Mel(double hz) => hz >= 1000 ? 15 + Math.Log(hz / 1000) / (Math.Log(6.4) / 27) : hz / (200.0 / 3);
        static double Hz(double mel) => mel >= 15 ? 1000 * Math.Exp((mel - 15) * (Math.Log(6.4) / 27)) : mel * (200.0 / 3);
        double first = Mel(50), last = Mel(14000);
        var points = Enumerable.Range(0, Bins + 2).Select(i => Hz(first + (last - first) * i / (Bins + 1))).ToArray();
        var filters = new (int Bin, double Weight)[Bins][];
        for (int m = 0; m < Bins; m++)
        {
            var band = new List<(int, double)>();
            for (int f = 0; f <= FftSize / 2; f++)
            {
                double hz = f * Rate / (double)FftSize;
                double weight = Math.Max(0, Math.Min((hz - points[m]) / (points[m + 1] - points[m]),
                    (points[m + 2] - hz) / (points[m + 2] - points[m + 1]))) * 2 / (points[m + 2] - points[m]);
                if (weight > 0) band.Add((f, weight));
            }
            filters[m] = band.ToArray();
        }
        return filters;
    }

    public static float[] Extract(ReadOnlySpan<float> input, CancellationToken ct = default)
    {
        if (input.Length == 0) throw new InvalidDataException("Empty audio");
        var wave = new float[Samples];
        int take = Math.Min(Samples, input.Length);
        int repeats = Samples / take;
        for (int n = 0; n < repeats; n++) input[..take].CopyTo(wave.AsSpan(n * take));
        var result = new float[Frames * Bins];
        var re = new double[FftSize]; var im = new double[FftSize]; var power = new double[FftSize / 2 + 1];
        for (int frame = 0; frame < Frames; frame++)
        {
            if (frame % 16 == 0) ct.ThrowIfCancellationRequested();
            for (int i = 0; i < FftSize; i++)
            {
                int at = frame * Hop + i - FftSize / 2;
                if (at < 0) at = -at;
                else if (at >= Samples) at = 2 * Samples - 2 - at;
                re[i] = wave[at] * Window[i]; im[i] = 0;
            }
            Fft.Forward(re, im);
            for (int f = 0; f < power.Length; f++) power[f] = re[f] * re[f] + im[f] * im[f];
            for (int m = 0; m < Bins; m++)
            {
                double energy = 0;
                // Each FFT bin overlaps at most two triangular filters. Skip exact zeros, preserving sum order.
                foreach (var filter in Filters[m]) energy += power[filter.Bin] * filter.Weight;
                result[frame * Bins + m] = (float)(10 * Math.Log10(Math.Max(1e-10, energy)));
            }
        }
        return result;
    }
}
