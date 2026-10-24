using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

/// <summary>One song from Spotify's song search. <paramref name="Lyrics"/>: Spotify said its lyrics matched.</summary>
internal sealed record SpotifyTrackHit(string Id, string Title, IReadOnlyList<string> Artists, string Album, double? Seconds, bool Explicit,
    string Artwork, bool Lyrics);

/// <summary>
/// Owner #155: Spotify's own song search, with the same anonymous token as playlist search. It matches lyrics as well
/// as names, forgives typos ("you use to call me on my cellphone late nite" → Drake, Hotline Bling) and ranks by
/// popularity, so a famous song beats a closer copy nobody plays. Metadata only.
/// </summary>
internal sealed partial class PlaylistCatalog
{
    // The web player's stored song search. When Spotify retires it, the answer says PersistedQueryNotFound and the
    // current one is read from the web player once (its search page code), then kept for this run.
    internal const string TracksHashFallback = "b02683192a98dde7966b5e6655a79eeb62713eab703eda9902c932818dd52751";
    private string _tracksHash = TracksHashFallback;
    private readonly SemaphoreSlim _hashGate = new(1, 1);
    private DateTimeOffset _hashCheckedAt = DateTimeOffset.MinValue;
    private const string PlayerPage = "https://open.spotify.com/search";
    private const long PlayerScriptLimit = 16L * 1024 * 1024;

    internal async Task<IReadOnlyList<SpotifyTrackHit>> SearchTracksAsync(string term, int limit, CancellationToken ct)
    {
        term = term.Trim();
        if (term.Length is 0 or > 200) return [];
        var raw = await TracksPageAsync(term, limit, _tracksHash, ct).ConfigureAwait(false);
        if (StaleQuery(raw) && await RefreshTracksHashAsync(ct).ConfigureAwait(false) is { } fresh)
            raw = await TracksPageAsync(term, limit, fresh, ct).ConfigureAwait(false);
        return ParseTracks(raw);
    }

    private async Task<string> TracksPageAsync(string term, int limit, string hash, CancellationToken ct)
    {
        var variables = JsonSerializer.Serialize(new
        {
            searchTerm = term, offset = 0, limit = Math.Clamp(limit, 1, 20), numberOfTopResults = 5, includeAudiobooks = false,
            includePreReleases = false, includeLocalConcertsField = false, includeArtistHasConcertsField = false,
        });
        var url = "https://api-partner.spotify.com/pathfinder/v1/query?operationName=searchTracks&variables=" + Uri.EscapeDataString(variables) +
            "&extensions=" + Uri.EscapeDataString("{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"" + hash + "\"}}");
        try { return await Get(url, await TokenAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("METADATA_HTTP_401", StringComparison.Ordinal))
        {
            _token = null; // expired early: one fresh token
            return await Get(url, await TokenAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }

    internal static bool StaleQuery(string raw) => raw.Contains("PersistedQueryNotFound", StringComparison.Ordinal);

    /// <summary>The web player's current song-search hash, read from its search page code; null when it cannot be found
    /// (then the search simply has no lyric answer). Looked up at most once every ten minutes.</summary>
    private async Task<string?> RefreshTracksHashAsync(CancellationToken ct)
    {
        await _hashGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - _hashCheckedAt < TimeSpan.FromMinutes(10)) return _tracksHash == TracksHashFallback ? null : _tracksHash;
            _hashCheckedAt = DateTimeOffset.UtcNow;
            var page = await Get(PlayerPage, null, ct).ConfigureAwait(false);
            foreach (var script in PlayerScripts(page))
            {
                var code = await GetPlayerScriptAsync(script, ct).ConfigureAwait(false);
                foreach (var chunk in SearchChunks(code))
                {
                    string chunkCode;
                    try { chunkCode = await GetPlayerScriptAsync(chunk, ct).ConfigureAwait(false); }
                    catch (InvalidDataException) { continue; } // the CSS chunk's hash: not a script
                    if (TracksHashIn(chunkCode) is { } hash)
                    {
                        Noctis.Services.DebugLog.Write("Search", "Spotify song search: using the web player's current query");
                        return _tracksHash = hash;
                    }
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Noctis.Services.DebugLog.Write("Search", "Spotify song search query not found: " + ex.Message);
            return null;
        }
        finally { _hashGate.Release(); }
    }

    internal static IEnumerable<string> PlayerScripts(string html) => Regex.Matches(html,
            @"src=""(https://open\.spotifycdn\.com/cdn/build/web-player/web-player\.[0-9a-f]+\.js)""")
        .Select(m => m.Groups[1].Value).Distinct();

    /// <summary>The search page's code: its webpack chunk id by name, then every hash that id has (one is the script's).</summary>
    internal static IEnumerable<string> SearchChunks(string code)
    {
        var id = Regex.Match(code, @"(\d+):""xpui-routes-search""");
        if (!id.Success) yield break;
        foreach (var hash in Regex.Matches(code, @"[{,]" + id.Groups[1].Value + @":""([0-9a-f]{6,16})""").Select(m => m.Groups[1].Value).Distinct())
            yield return "https://open.spotifycdn.com/cdn/build/web-player/xpui-routes-search." + hash + ".js";
    }

    internal static string? TracksHashIn(string code)
        => Regex.Match(code, @"""searchTracks"",""query"",""([0-9a-f]{64})""") is { Success: true } m ? m.Groups[1].Value : null;

    private async Task<string> GetPlayerScriptAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host != "open.spotifycdn.com" || !uri.AbsolutePath.StartsWith("/cdn/build/web-player/", StringComparison.Ordinal))
            throw new InvalidDataException("Unapproved metadata host.");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"METADATA_HTTP_{(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength > PlayerScriptLimit) throw new InvalidDataException("METADATA_TOO_LARGE");
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct); reading.CancelAfter(_http.Timeout);
        await using var body = await response.Content.ReadAsStreamAsync(reading.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[65536];
        while (true)
        {
            var count = await body.ReadAsync(bytes, reading.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > PlayerScriptLimit) throw new InvalidDataException("METADATA_TOO_LARGE");
            buffer.Write(bytes, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static List<SpotifyTrackHit> ParseTracks(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (Path(doc.RootElement, ["data", "searchV2", "tracksV2", "items"]) is not { ValueKind: JsonValueKind.Array } items)
            throw new InvalidDataException("TRACK_SEARCH_UNAVAILABLE: Spotify's song search answered in an unknown form.");
        var hits = new List<SpotifyTrackHit>();
        foreach (var item in items.EnumerateArray())
        {
            if (Path(item, ["item", "data"]) is not { ValueKind: JsonValueKind.Object } data || String(data, "__typename", "") != "Track") continue;
            var title = String(data, "name", "").Trim();
            var artists = Path(data, ["artists", "items"]) is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(a => Path(a, ["profile", "name"]) is { ValueKind: JsonValueKind.String } n ? n.GetString()!.Trim() : "")
                    .Where(n => n.Length > 0).ToList()
                : [];
            if (title.Length == 0 || artists.Count == 0) continue;
            double? seconds = Path(data, ["duration", "totalMilliseconds"]) is { ValueKind: JsonValueKind.Number } ms && ms.TryGetInt64(out var m) && m > 0 ? m / 1000.0 : null;
            var album = Path(data, ["albumOfTrack", "name"]) is { ValueKind: JsonValueKind.String } a ? a.GetString()!.Trim() : "";
            var art = Path(data, ["albumOfTrack", "coverArt", "sources"]) is { ValueKind: JsonValueKind.Array } sources
                ? sources.EnumerateArray().Select(s => String(s, "url", "")).FirstOrDefault(ArtistCatalog.ArtworkUrl) ?? "" : "";
            var lyrics = item.TryGetProperty("matchedFields", out var fields) && fields.ValueKind == JsonValueKind.Array
                && fields.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == "LYRICS");
            var isExplicit = Path(data, ["contentRating", "label"]) is { ValueKind: JsonValueKind.String } label && label.GetString() == "EXPLICIT";
            hits.Add(new(String(data, "id", ""), title, artists, album, seconds, isExplicit, art, lyrics));
        }
        return hits;
    }
}
