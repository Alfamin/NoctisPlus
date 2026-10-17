using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>
/// How long a bot usually takes to answer, learned from its own answers (owner, 2026-10-17:
/// "if it usually answers in about two seconds, don't wait twenty"). Each kind of wait keeps
/// its last answer times; the wait allowed is a few times the usual one, within limits, so a
/// slow or silent bot hands over to the backup quickly while a normal answer is never cut off.
/// Kept next to the login (no message text, only seconds).
/// </summary>
internal sealed class BotTimings
{
    internal const int Keep = 40, Enough = 5;
    private readonly string? _path;
    private readonly Dictionary<string, List<double>> _seconds;
    private readonly object _sync = new();

    public BotTimings(string? path)
    {
        _path = path;
        _seconds = Load(path);
    }

    /// <summary>How many answer times are known for <paramref name="kind"/>.</summary>
    public int Count(string kind) { lock (_sync) return _seconds.TryGetValue(kind, out var list) ? list.Count : 0; }

    public void Record(string kind, TimeSpan took)
    {
        if (took <= TimeSpan.Zero || took > TimeSpan.FromMinutes(10)) return;
        lock (_sync)
        {
            if (!_seconds.TryGetValue(kind, out var list)) _seconds[kind] = list = [];
            list.Add(Math.Round(took.TotalSeconds, 2));
            if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
        }
        Save();
    }

    /// <summary>Learned from history: only fills a kind that knows too little yet.</summary>
    public void Seed(string kind, IEnumerable<TimeSpan> answers)
    {
        lock (_sync)
        {
            if (_seconds.TryGetValue(kind, out var known) && known.Count >= Enough) return;
            var list = answers.Where(t => t > TimeSpan.Zero && t < TimeSpan.FromMinutes(10))
                .Select(t => Math.Round(t.TotalSeconds, 2)).TakeLast(Keep).ToList();
            if (list.Count == 0) return;
            _seconds[kind] = list;
        }
        Save();
    }

    /// <summary>The usual answer time (median), or null while too few are known.</summary>
    public TimeSpan? Usual(string kind)
    {
        lock (_sync)
        {
            if (!_seconds.TryGetValue(kind, out var list) || list.Count < Enough) return null;
            var sorted = list.Order().ToArray();
            return TimeSpan.FromSeconds(sorted[sorted.Length / 2]);
        }
    }

    /// <summary>How long to wait: about three times the usual answer and never less than the
    /// slow answers seen (90th percentile, plus margin), kept between <paramref name="min"/> and
    /// <paramref name="max"/>. <paramref name="fallback"/> until enough answers are known.</summary>
    public TimeSpan Wait(string kind, TimeSpan fallback, TimeSpan min, TimeSpan max)
    {
        double[] sorted;
        lock (_sync)
        {
            if (!_seconds.TryGetValue(kind, out var list) || list.Count < Enough) return fallback;
            sorted = list.Order().ToArray();
        }
        var median = sorted[sorted.Length / 2];
        var p90 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.9) - 1)];
        var wait = TimeSpan.FromSeconds(Math.Max(median * 3, p90 * 1.5));
        return wait < min ? min : wait > max ? max : wait;
    }

    private void Save()
    {
        if (_path is null) return;
        Dictionary<string, List<double>> copy;
        lock (_sync) copy = _seconds.ToDictionary(p => p.Key, p => p.Value.ToList());
        try { AtomicJson.Write(_path, copy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<string, List<double>> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path) && new FileInfo(path).Length < 64 * 1024)
                return JsonSerializer.Deserialize<Dictionary<string, List<double>>>(File.ReadAllText(path))?
                    .Where(p => p.Key.Length <= 40 && p.Value is not null)
                    .ToDictionary(p => p.Key, p => p.Value.Where(v => v > 0 && v < 600).TakeLast(Keep).ToList()) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }
}
