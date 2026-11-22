using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// Android's ordered word-timing lookup. It uses the same Core provider clients as desktop,
/// asks lrc.red → Unison → Kugou (word timing first, then their line timing), and only then the
/// existing cache-first LRCLIB source (owner #290: LRCLIB last, as on the PC).
/// Provider word timings are deliberately labeled provisional until a phone audio-fit check
/// exists; metadata identity and duration alone are not proof of audio fit.
/// </summary>
public sealed class PlusOnlineLyricsSource : IOnlineLyricsSource
{
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    /// <summary>Word timing that was found stays (the PC writes it beside the song for good): asking again would only repeat it (#312).</summary>
    private static readonly TimeSpan WordCacheLifetime = TimeSpan.FromDays(180);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<Provider> _providers;
    private readonly IOnlineLyricsSource _lrclib;
    private readonly string _cacheDirectory;
    private readonly OnlineAccess _access;

    /// <summary>One ordered source. The delegate is injected so provider order/failure policy is testable offline.</summary>
    public sealed record Provider(string Name, Func<Track, CancellationToken, Task<LrcLibResult?>> Find);

    public PlusOnlineLyricsSource(IEnumerable<Provider> providers, IOnlineLyricsSource lrclib,
        string cacheDirectory, OnlineAccess? access = null)
    {
        _providers = providers.ToArray();
        _lrclib = lrclib;
        _cacheDirectory = cacheDirectory;
        _access = access ?? OnlineAccess.Shared;
    }

    /// <summary>Creates the production Android chain through the app's system-proxy-aware HTTP handler.</summary>
    public static PlusOnlineLyricsSource Create(string dataDirectory, HttpClient http)
    {
        // Retain each Core client's bounded per-instance memo across song changes.
        var lrcRed = new LrcRedLyricsService(http);
        var unison = new UnisonLyricsService(http);
        var kugou = new KugouLyricsService(http);
        return new PlusOnlineLyricsSource(new[]
        {
            new Provider("lrc.red", (t, ct) => lrcRed.SearchLyricsAsync(t.Artist ?? "", t.Title, t.Duration.TotalSeconds, ct)),
            new Provider("Unison", (t, ct) => unison.SearchLyricsAsync(t.Artist ?? "", t.Title, t.Duration.TotalSeconds, ct)),
            new Provider("Kugou", (t, ct) => kugou.SearchLyricsAsync(t.Artist ?? "", t.Title, t.Duration.TotalSeconds, ct)),
        }, LrcLibOnlineLyricsSource.Create(dataDirectory, http), Path.Combine(dataDirectory, "phone_timing_cache"));
    }

    public async Task<OnlineLyrics?> FindAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return null;

        var queryKey = QueryKey(track);
        var cachePath = Path.Combine(_cacheDirectory, track.Id.ToString("N") + ".json");
        var cached = await ReadCacheAsync(cachePath, queryKey, ct).ConfigureAwait(false);
        if (cached != null) return cached;
        if (_access.IsOffline) return null;

        // Owner #290: LRCLIB is the last resort, as on the PC. A provider with line timing only is kept and used
        // before LRCLIB when no provider has word timing (no second request).
        var (word, lineTimed, hadTransientFailure) = await AskProvidersAsync(track, ct).ConfigureAwait(false);
        if (word is { } timed)
        {
            await WriteCacheAsync(cachePath, new CacheEntry(queryKey, timed.Text, timed.Provider!, DateTimeOffset.UtcNow, true), ct).ConfigureAwait(false);
            return timed;
        }
        if (lineTimed is { } line)
        {
            var found = new OnlineLyrics(line.Text, false) { Provider = line.Provider, AudioFitVerified = false };
            await WriteCacheAsync(cachePath, new CacheEntry(queryKey, found.Text, line.Provider, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            return found;
        }
        var fallback = await _lrclib.FindAsync(track, ct).ConfigureAwait(false);
        if (fallback is { Failed: true } || hadTransientFailure && fallback == null)
            return OnlineLyrics.Unreachable;
        return fallback is null ? null : fallback with { Provider = "LRCLIB" };
    }

    /// <summary>How long a song that no source has word timing for is left alone before it is asked again (#312).</summary>
    public static readonly TimeSpan MissLifetime = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task<OnlineLyrics?> FindWordTimedAsync(Track track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return null;
        var queryKey = QueryKey(track);
        var id = track.Id.ToString("N");
        var cachePath = Path.Combine(_cacheDirectory, id + ".json");
        var cached = await ReadCacheAsync(cachePath, queryKey, ct).ConfigureAwait(false);
        if (cached != null && HasWordLevelTiming(cached.Text)) return cached;

        var missPath = Path.Combine(_cacheDirectory, id + ".miss");
        if (RecentMiss(missPath, queryKey)) return null;
        if (_access.IsOffline) return null;

        var (word, _, hadTransientFailure) = await AskProvidersAsync(track, ct).ConfigureAwait(false);
        if (word is { } timed)
        {
            await WriteCacheAsync(cachePath, new CacheEntry(queryKey, timed.Text, timed.Provider!, DateTimeOffset.UtcNow, true), ct).ConfigureAwait(false);
            return timed;
        }
        // A real "no": remembered for a week. A failure (offline, busy, timed out) is not, so the next play asks again.
        if (!hadTransientFailure) WriteMiss(missPath, queryKey);
        return null;
    }

    private async Task<(OnlineLyrics? Word, (string Provider, string Text)? LineTimed, bool Transient)> AskProvidersAsync(Track track, CancellationToken ct)
    {
        var hadTransientFailure = false;
        (string Provider, string Text)? lineTimed = null;
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProviderTimeout);
            try
            {
                var result = await provider.Find(track, timeout.Token).ConfigureAwait(false);
                if (result == null || result.Instrumental || !IsMatchingRecording(result, track)) continue;
                // Core providers expose TTML as provenance while SyncedLyrics is their
                // converted ELRC/LRC payload. LyricsLoader parses the converted form.
                var text = result.SyncedLyrics;
                if (!HasWordLevelTiming(text))
                {
                    if (lineTimed is null && LyricsLoader.FromOnlineText(text ?? "")?.IsSynced == true) lineTimed = (provider.Name, text!);
                    continue;
                }
                return (new OnlineLyrics(text!, false) { Provider = provider.Name, AudioFitVerified = false }, lineTimed, hadTransientFailure);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                hadTransientFailure = true;
                DebugLog.Write("Lyrics", $"{provider.Name} phone timing lookup timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hadTransientFailure = true;
                DebugLog.Write("Lyrics", $"{provider.Name} phone timing lookup failed: {ex.GetType().Name}");
            }
        }
        ct.ThrowIfCancellationRequested();
        return (null, lineTimed, hadTransientFailure);
    }

    private static bool RecentMiss(string path, string queryKey)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var parts = File.ReadAllText(path).Split('\n', 2);
            return parts.Length == 2 && parts[1] == queryKey && DateTimeOffset.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var at) && DateTimeOffset.UtcNow - at < MissLifetime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static void WriteMiss(string path, string queryKey)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n" + queryKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DebugLog.Write("Lyrics", $"Phone timing miss write failed: {ex.GetType().Name}");
        }
    }
    private static bool IsMatchingRecording(LrcLibResult result, Track track)
    {
        if (!LyricsSearchSelector.MatchesTrack(result, track.Artist, track.Title, track.Duration.TotalSeconds)) return false;
        // Provider word timing must be close to the local track length. This is an identity guard,
        // not an audio fit check; results remain explicitly provisional until PCM is compared.
        return result.Duration <= 0 || track.Duration <= TimeSpan.Zero ||
               Math.Abs(result.Duration - track.Duration.TotalSeconds) <= 1.0;
    }

    private static bool HasWordLevelTiming(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var loaded = LyricsLoader.FromOnlineText(text);
        if (loaded?.IsSynced != true) return false;
        var wordCount = loaded.Lines.Sum(line => line.Words?.Count ?? 0);
        // A line-synced LRC becomes one word object per line. Require real intra-line word spans.
        return wordCount >= 30 && loaded.Lines.Any(line => line.Words?.Count >= 2);
    }

    private static string QueryKey(Track track) => string.Join("|",
        track.Title.Trim().ToLowerInvariant(), track.Artist?.Trim().ToLowerInvariant() ?? "",
        Math.Round(track.Duration.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<OnlineLyrics?> ReadCacheAsync(string path, string queryKey, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), JsonOptions);
            if (entry == null || entry.QueryKey != queryKey || DateTimeOffset.UtcNow - entry.SavedAtUtc >= (entry.Words ? WordCacheLifetime : CacheLifetime) ||
                string.IsNullOrWhiteSpace(entry.Text)) return null;
            return new OnlineLyrics(entry.Text, false) { Provider = entry.Provider, AudioFitVerified = false };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            DebugLog.Write("Lyrics", $"Phone timing cache read failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static async Task WriteCacheAsync(string path, CacheEntry entry, CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(entry, JsonOptions), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DebugLog.Write("Lyrics", $"Phone timing cache write failed: {ex.GetType().Name}");
        }
    }

    private sealed record CacheEntry(string QueryKey, string Text, string Provider, DateTimeOffset SavedAtUtc, bool Words = false);
}
