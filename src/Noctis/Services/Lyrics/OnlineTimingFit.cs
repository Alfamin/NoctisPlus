using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Noctis.Services.AudioAnalysis;

namespace Noctis.Services.Lyrics;

/// <summary>
/// Whether an online word-timed TTML (lrc.red, Unison) fits this exact recording (#200). The same length is not proof:
/// lrc.red's Kiss Me More was 0.49 s late for the owner's file. Model-free and cheap: the song's vocal-band spectral
/// flux (bundled ffmpeg, 16 kHz mono) is correlated with the TTML's word onsets over ±1 s. A clear peak near zero is
/// a fit; a real shift is applied only when a second, independent measure (band energy against the sung spans) agrees.
/// Anything less clear is "no fit", and the song keeps the existing route (LRCLIB → WordLyrics).
/// Measured on 59 library songs with a WordLyrics reference (scratch fit200.py; thresholds chosen on one half, checked on
/// the other): 32 trusted, none more than 80 ms off except PLUTOSKI (+0.06 s vs WordLyrics +0.34 s, unresolved); 0 of 58
/// wrong-song pairs (another song's words on this audio) accepted. Kiss Me More +0.48 s caught and shifted; BIRDS OF A
/// FEATHER (+0.32 s, significance 1.9) falls back.
/// </summary>
internal static class OnlineTimingFit
{
    public sealed record Word(double Start, double End, bool First);

    /// <summary>Shift = seconds to add to every TTML time (0 when the fit is already right); null shift = no fit.</summary>
    public sealed record Result(double? Shift, double Estimate, double Sharpness, double EnergyEstimate, double Significance, int Words);

    internal const int Rate = 16000, Hop = 160, Size = 1024, MaxLag = 100;
    internal const double MinSharpness = 0.10, BigShift = 0.12, Agree = 0.08, Ignore = 0.05, Edge = 0.95;
    internal const int MinWords = 30, Rolls = 16;
    internal const double MinSignificance = 2.0;
    const int MaxSeconds = 900;
    static readonly int LowBin = (int)Math.Ceiling(300.0 * Size / Rate), HighBin = (int)Math.Floor(3400.0 * Size / Rate);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Main-voice word onsets (split syllables joined, background vocals left out); empty when untimed.</summary>
    public static IReadOnlyList<Word> WordsOf(string? ttml)
    {
        var (lines, _) = TtmlParser.Parse(ttml, joinSplitWords: true);
        var words = new List<Word>();
        foreach (var line in lines ?? [])
        {
            if (line.IsBackgroundOnly || line.Words is not { Count: > 0 } row) continue;
            var first = true;
            foreach (var w in row)
            {
                if (string.IsNullOrWhiteSpace(w.Text)) continue;
                words.Add(new Word(w.Start.TotalSeconds, (w.End ?? w.Start).TotalSeconds, first));
                first = false;
            }
        }
        return words;
    }

    /// <summary>Decodes the file with ffmpeg and measures; null when ffmpeg is missing or fails (= no fit).</summary>
    public static async Task<Result?> CheckAsync(string ffmpeg, string audioPath, string ttml, CancellationToken ct)
    {
        var words = WordsOf(ttml);
        if (words.Count < MinWords) return new Result(null, 0, 0, 0, 0, words.Count);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-nostats", "-hide_banner", "-v", "error", "-t", MaxSeconds.ToString(CultureInfo.InvariantCulture), "-i", audioPath,
                     "-map", "0:a:0", "-ac", "1", "-ar", Rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var kill = timeout.Token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
            var errors = p.StandardError.ReadToEndAsync(timeout.Token);
            var features = new Features();
            var buffer = new byte[64 * 1024];
            int carry = 0, n;
            while ((n = await p.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(carry), timeout.Token)) > 0)
            {
                n += carry;
                int whole = n / 4 * 4;
                for (int i = 0; i < whole; i += 4) features.Add(BitConverter.ToSingle(buffer, i));
                carry = n - whole;
                if (carry > 0) Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
            }
            await errors;
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0 ? Measure(features.Flux, features.Energy, words) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    /// <summary>The decision on 100-per-second frame signals: vocal-band flux and band energy (raw, not detrended).</summary>
    public static Result Measure(IReadOnlyList<double> flux, IReadOnlyList<double> energy, IReadOnlyList<Word> words)
    {
        int n = Math.Min(flux.Count, energy.Count);
        int inside = words.Count(w => w.Start * 100 < n);
        if (inside < MinWords || n < 4 * MaxLag) return new Result(null, 0, 0, 0, 0, inside);
        double[] sig = Detrend(flux, n, 101), onsets = Train(words, n, onsets: true);
        var (est, sharp) = Scan(sig, onsets);
        var significance = Significance(sig, onsets);
        var (energyEst, _) = Scan(Detrend(energy, n, 301), Train(words, n, onsets: false));
        double? shift = null;
        if (significance >= MinSignificance && sharp >= MinSharpness && Math.Abs(est) < Edge && (Math.Abs(est) <= BigShift || Math.Abs(est - energyEst) <= Agree))
            shift = Math.Abs(est) > Ignore ? est : 0;
        return new Result(shift, est, sharp, energyEst, significance, inside);
    }

    /// <summary>Every begin/end in the document moved by <paramref name="seconds"/>; everything else byte for byte.</summary>
    public static string Shift(string ttml, double seconds)
    {
        if (seconds == 0) return ttml;
        return TimeAttribute.Replace(ttml, m =>
        {
            var t = TtmlParser.ParseTime(m.Groups[3].Value);
            if (t == null) return m.Value;
            var s = Math.Max(0, t.Value.TotalSeconds + seconds);
            var text = m.Groups[3].Value.Contains(':')
                ? (s >= 3600 ? $"{(int)(s / 3600)}:{(int)(s % 3600 / 60):00}:" : $"{(int)(s / 60)}:") + (s % 60).ToString("00.000", CultureInfo.InvariantCulture)
                : s.ToString("0.000", CultureInfo.InvariantCulture) + (m.Groups[3].Value.EndsWith('s') ? "s" : "");
            return m.Groups[1].Value + m.Groups[2].Value + text + m.Groups[2].Value;
        });
    }

    static readonly Regex TimeAttribute = new(@"(\s(?:begin|end)\s*=\s*)([""'])([^""']*)\2", RegexOptions.CultureInvariant);

    static double[] Detrend(IReadOnlyList<double> v, int n, int width)
    {
        // v minus its centred moving average, zero beyond the ends (numpy convolve "same").
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + v[i];
        var o = new double[n];
        int h = width / 2;
        for (int i = 0; i < n; i++)
            o[i] = v[i] - (prefix[Math.Min(n, i + h + 1)] - prefix[Math.Max(0, i - h)]) / width;
        return o;
    }

    static double[] Train(IReadOnlyList<Word> words, int n, bool onsets)
    {
        var t = new double[n];
        foreach (var w in words)
        {
            int a = (int)Math.Round(w.Start * 100, MidpointRounding.ToEven), b = (int)Math.Round(w.End * 100, MidpointRounding.ToEven);
            if (a < 0 || a >= n) continue;
            if (onsets) t[a] += w.First ? 2 : 1;
            else for (int i = a; i < Math.Min(b, n); i++) t[i] = 1;
        }
        if (onsets)
        {
            var g = new double[13];
            for (int k = 0; k < 13; k++) g[k] = Math.Exp(-0.5 * Math.Pow((k - 6) / 2.0, 2));
            var s = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (t[i] == 0) continue;
                for (int k = 0; k < 13; k++) if (i + k - 6 is var j && j >= 0 && j < n) s[j] += t[i] * g[k];
            }
            t = s;
        }
        var mean = t.Average();
        for (int i = 0; i < n; i++) t[i] -= mean;
        return t;
    }

    static double[] Correlate(double[] sig, double[] tr)
    {
        int n = Math.Min(sig.Length, tr.Length);
        var c = new double[2 * MaxLag + 1];
        for (int L = -MaxLag; L <= MaxLag; L++)
        {
            double sum = 0;
            for (int i = Math.Max(0, -L), end = n - Math.Max(0, L); i < end; i++) sum += sig[i + L] * tr[i];
            c[L + MaxLag] = sum;
        }
        return c;
    }

    /// <summary>How far the real ±1 s peak stands above the peaks of the same words moved far along the song
    /// (z against <see cref="Rolls"/> circular moves). Repetitive audio that fits any words scores low.</summary>
    static double Significance(double[] sig, double[] tr)
    {
        int n = Math.Min(sig.Length, tr.Length);
        double real = Correlate(sig, tr).Max();
        var nulls = new double[Rolls];
        var moved = new double[n];
        for (int j = 0; j < Rolls; j++)
        {
            int k = (int)(n * (j + 1L) / (Rolls + 1));
            for (int i = 0; i < n; i++) moved[(i + k) % n] = tr[i];
            nulls[j] = Correlate(sig, moved).Max();
        }
        double mean = nulls.Average(), sd = Math.Sqrt(nulls.Sum(x => (x - mean) * (x - mean)) / Rolls);
        return (real - mean) / (sd + 1e-9);
    }

    static (double Shift, double Sharpness) Scan(double[] sig, double[] tr)
    {
        var c = Correlate(sig, tr);
        int best = 0;
        for (int i = 1; i < c.Length; i++) if (c[i] > c[best]) best = i;
        var sorted = c.OrderBy(x => x).ToArray();
        double median = sorted[sorted.Length / 2], second = double.MinValue;
        for (int i = 0; i < c.Length; i++) if (Math.Abs(i - best) > 15) second = Math.Max(second, c[i]);
        return ((best - MaxLag) / 100.0, (c[best] - second) / (c[best] - median + 1e-9));
    }

    /// <summary>Streaming short-time spectrum: one 10 ms frame per <see cref="Hop"/> samples, Hann window.</summary>
    internal sealed class Features
    {
        readonly float[] _ring = new float[Size];
        readonly double[] _window = new double[Size], _re = new double[Size], _im = new double[Size];
        double[]? _last;
        int _count, _sinceFrame;
        public List<double> Flux { get; } = new();
        public List<double> Energy { get; } = new();

        public Features()
        {
            for (int i = 0; i < Size; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1));
        }

        public void Add(float sample)
        {
            _ring[_count % Size] = sample;
            _count++;
            if (_count < Size) return;
            if (_count > Size && ++_sinceFrame < Hop) return;
            _sinceFrame = 0;
            int start = _count - Size;
            for (int i = 0; i < Size; i++) { _re[i] = _ring[(start + i) % Size] * _window[i]; _im[i] = 0; }
            Fft.Forward(_re, _im);
            var bands = new double[HighBin - LowBin + 1];
            double flux = 0, total = 0;
            for (int b = LowBin; b <= HighBin; b++)
            {
                var v = Math.Log(1 + 100 * Math.Sqrt(_re[b] * _re[b] + _im[b] * _im[b]));
                bands[b - LowBin] = v;
                total += v;
                if (_last != null) flux += Math.Max(0, v - _last[b - LowBin]);
            }
            _last = bands;
            Flux.Add(flux);
            Energy.Add(total / bands.Length);
        }
    }
}
