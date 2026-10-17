using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Models;
using Noctis.Services.Lyrics;

namespace Noctis.Services;

/// <summary>
/// Fetches lyrics from lrc.red (Discord request, nutf!xx). Its API page says "The lrc.red API is
/// free for the public to use" (checked 2026-10-17); no key, no account. Two requests: the track
/// lookup <c>/api/v1?track=&amp;artist=&amp;duration=</c> (the BiniLyrics API, which
/// lyrics-api.binimum.org now redirects to) lists recordings with their ISRC and timing level
/// ("word", "line", "none"), then <c>/s/{isrc}.ttml</c> is the lyrics. The TTML is converted by
/// <see cref="OnlineTtml"/>, so duet voices and background vocals survive.
/// Returns results as <see cref="LrcLibResult"/> for compatibility with existing lyrics infrastructure.
/// </summary>
public partial class LrcRedLyricsService : ILrcRedLyricsService
{
    private const string LookupUrl = "https://lrc.red/api/v1";
    private const string FileUrl = "https://lrc.red/s/";
    // LRCLIB's: names the app to a free community service. lrc.red's Cloudflare refuses some
    // stock client agents (Python-urllib answered 403 on 2026-10-17).
    private const string UserAgent = "Noctis (https://github.com/heartached/Noctis)";
    private readonly HttpClient _http;

    // Same bounded memo as KugouLyricsService: found lyrics are written to a sidecar, so this
    // only saves repeat lookups within a short window. Cleared when full.
    internal const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    public LrcRedLyricsService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
    {
        var cacheKey = $"lrcred:{artist.Length}:{artist}|{trackName}|{Math.Round(durationSeconds)}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var lookup = await GetStringAsync(LookupQuery(artist, trackName, durationSeconds), ct);
            var song = PickSong(lookup!, artist, trackName, durationSeconds);
            if (song == null)
            {
                Remember(cacheKey, null);
                return null;
            }

            // Missing file: a definitive miss, like an empty lookup.
            var ttml = await GetStringAsync(FileUrl + song.Value.Isrc + ".ttml", ct, notFoundIsNull: true);
            LrcLibResult? result = null;
            if (ttml != null)
            {
                var lyrics = OnlineTtml.Convert(ttml)
                    ?? throw new InvalidDataException("lrc.red sent TTML that does not parse.");
                result = new LrcLibResult
                {
                    TrackName = song.Value.Title,
                    ArtistName = song.Value.Artist,
                    AlbumName = song.Value.Album,
                    Duration = song.Value.DurationSeconds,
                    SyncedLyrics = lyrics.Synced,
                    PlainLyrics = lyrics.Plain,
                };
            }
            Remember(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidDataException or InvalidOperationException)
        {
            // Network failure, non-success status, timeout, malformed JSON / TTML.
            // Uncached so a later attempt can succeed.
            throw new LyricsProviderException("lrc.red", ex);
        }
    }

    /// <summary>The lookup URL: by track and artist (and duration when known), or a title search
    /// when the artist is the library's placeholder — the lookup needs both names.</summary>
    internal static string LookupQuery(string artist, string trackName, double durationSeconds)
    {
        if (LyricsSearchSelector.IsUnknownArtist(artist))
            return $"{LookupUrl}?q={Uri.EscapeDataString(trackName.Trim())}";
        var url = $"{LookupUrl}?track={Uri.EscapeDataString(trackName.Trim())}&artist={Uri.EscapeDataString(artist.Trim())}";
        if (durationSeconds > 0)
            url += "&duration=" + Math.Round(durationSeconds).ToString(CultureInfo.InvariantCulture);
        return url;
    }

    private void Remember(string key, LrcLibResult? value)
    {
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken ct, bool notFoundIsNull = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, ct);
        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
    }

    /// <summary>
    /// The lookup result that is the local track, or null. Every result must match by title,
    /// artist and duration (±5 s); among those an exact title beats a "(Live)" / "(Acoustic)"
    /// cut that merely contains it, then the richer timing, then the closer duration, then the
    /// lookup's own order.
    /// </summary>
    internal static (string Isrc, string Title, string Artist, string? Album, double DurationSeconds)? PickSong(
        string json, string artist, string trackName, double durationSeconds)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        (string Isrc, string Title, string Artist, string? Album, double DurationSeconds)? best = null;
        (int Exact, int Timing, double Delta) bestKey = default;
        foreach (var r in results.EnumerateArray())
        {
            var isrc = Str(r, "isrc") ?? Str(r, "id");
            if (isrc == null || !IsrcPattern().IsMatch(isrc)) continue;
            var candidate = new LrcLibResult
            {
                TrackName = Str(r, "track_name"),
                ArtistName = Str(r, "artist_name"),
                Duration = Num(r, "duration"),
            };
            if (!LyricsSearchSelector.MatchesTrack(candidate, artist, trackName, durationSeconds)) continue;

            var key = (
                Exact: string.Equals(candidate.TrackName!.Trim(), trackName.Trim(), StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                Timing: Str(r, "timing_type") switch { "word" => 2, "line" => 1, _ => 0 },
                Delta: durationSeconds > 0 && candidate.Duration > 0 ? Math.Abs(candidate.Duration - durationSeconds) : 0);
            if (best != null && !IsBetter(key, bestKey)) continue;
            best = (isrc, candidate.TrackName!, candidate.ArtistName!, Str(r, "album_name"), candidate.Duration);
            bestKey = key;
        }
        return best;
    }

    private static bool IsBetter((int Exact, int Timing, double Delta) a, (int Exact, int Timing, double Delta) b) =>
        a.Exact != b.Exact ? a.Exact > b.Exact
        : a.Timing != b.Timing ? a.Timing > b.Timing
        : a.Delta < b.Delta;

    // An ISRC is 12 letters and digits; the file URL is built from it, never taken from the response.
    [GeneratedRegex("^[A-Za-z0-9]{12}$")]
    private static partial Regex IsrcPattern();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
}
