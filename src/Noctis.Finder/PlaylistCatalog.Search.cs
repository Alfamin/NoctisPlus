using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>A public playlist found by name. <paramref name="Saves"/> is null when the service did not say.</summary>
internal sealed record FoundPlaylist(string Url, string Name, string Owner, string Artwork, int Tracks, long? Saves);

/// <summary>
/// Owner #69: public playlists by name ("hype drake", "gym"). Spotify's own web search with the anonymous token
/// every embed page carries, then each result's save and song counts; the most saved rise (owner: saves show
/// what people found relevant). Deezer's public search stands in when Spotify does not answer. Metadata only.
/// </summary>
internal sealed partial class PlaylistCatalog
{
    // The web player's stored search query. When Spotify retires it, the search errors and Deezer answers instead.
    private const string SearchHash = "af1730623dc1248b75a61a18bad1f47f1fc7eff802fb0676683de88815c958d8";
    // Any public embed page carries a token; this is Spotify's own long-lived Today's Top Hits.
    private const string TokenPage = "https://open.spotify.com/embed/playlist/37i9dQZF1DXcBWIGoYBM5M";
    /// <summary>Counts are a nicety: a search never waits longer than this for them.</summary>
    internal static TimeSpan CountsBudget = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, (int Tracks, long? Saves, DateTime At)> _counts = new();
    /// <summary>The last search's count timing, for the session log.</summary>
    internal static string LastCounts = "";
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenUntil;

    /// <summary>Playlists named like <paramref name="query"/>, best first, and which service answered. <paramref name="offset"/>
    /// asks for a later page (owner #71: more as the list is scrolled); each page is ranked on its own, so cards already
    /// on screen never move.</summary>
    public async Task<(IReadOnlyList<FoundPlaylist> Playlists, string Source)> SearchPlaylistsAsync(string query, int limit, CancellationToken ct, int offset = 0)
    {
        query = query.Trim();
        if (query.Length is 0 or > 200) return ([], "");
        try
        {
            var found = await SpotifySearchAsync(query, limit, ct, offset).ConfigureAwait(false);
            if (found.Count > 0) return (found, "Spotify");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Noctis.Services.DebugLog.Write("Search", "Spotify playlists unavailable, using Deezer: " + ex.Message);
        }
        return (await DeezerSearchAsync(query, limit, ct, offset).ConfigureAwait(false), "Deezer");
    }

    internal async Task<IReadOnlyList<FoundPlaylist>> SpotifySearchAsync(string query, int limit, CancellationToken ct, int offset = 0)
    {
        var variables = JsonSerializer.Serialize(new
        {
            searchTerm = query, offset, limit, numberOfTopResults = 5, includeAudiobooks = false,
            includePreReleases = false, includeLocalConcertsField = false, includeArtistHasConcertsField = false,
        });
        var url = "https://api-partner.spotify.com/pathfinder/v1/query?operationName=searchPlaylists&variables=" + Uri.EscapeDataString(variables) +
            "&extensions=" + Uri.EscapeDataString("{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"" + SearchHash + "\"}}");
        string raw;
        try { raw = await Get(url, await TokenAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("METADATA_HTTP_401", StringComparison.Ordinal))
        {
            _token = null; // expired early: one fresh token
            raw = await Get(url, await TokenAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        var found = ParseSpotifySearch(raw);
        await CountAsync(found, ct).ConfigureAwait(false);
        return Rank(query, found.Select(f => _counts.TryGetValue(f.Url, out var c) ? f with { Tracks = c.Tracks, Saves = c.Saves } : f).ToList());
    }

    internal static List<FoundPlaylist> ParseSpotifySearch(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (Path(doc.RootElement, ["data", "searchV2", "playlists", "items"]) is not { ValueKind: JsonValueKind.Array } items)
            throw new InvalidDataException("PLAYLIST_SEARCH_UNAVAILABLE: Spotify's search answered in an unknown form.");
        var found = new List<FoundPlaylist>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("data", out var data) || String(data, "__typename", "") != "Playlist") continue;
            var uri = String(data, "uri", "");
            if (!SpotifyPlaylists.PlaylistLink(uri, out var link) || found.Any(f => f.Url == link!.Url)) continue;
            var name = Clean(String(data, "name", ""));
            if (name.Length == 0) continue;
            var owner = data.TryGetProperty("ownerV2", out var o) && o.TryGetProperty("data", out var od) ? Clean(String(od, "name", "")) : "";
            found.Add(new(link!.Url, name, owner, Cover(data), 0, null));
        }
        return found;
    }

    // The 300 px cover when there is one (a tile is 168 px), else the largest.
    private static string Cover(JsonElement data)
    {
        if (Path(data, ["images", "items"]) is not { ValueKind: JsonValueKind.Array } images) return "";
        var sources = images.EnumerateArray().Take(1)
            .SelectMany(i => i.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array ? s.EnumerateArray().ToArray() : [])
            // Covers people upload come without a size ("width": null).
            .Select(s => (Url: String(s, "url", ""), Width: s.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var px) ? px : 0))
            .Where(s => ArtistCatalog.ArtworkUrl(s.Url)).ToList();
        return sources.FirstOrDefault(s => s.Width == 300).Url ?? sources.OrderByDescending(s => s.Width).FirstOrDefault().Url ?? "";
    }

    // Saves and song count for each result at once, from Spotify's own two small answers; remembered for half an hour.
    private async Task CountAsync(IReadOnlyList<FoundPlaylist> found, CancellationToken ct)
    {
        var wanted = found.Where(f => !(_counts.TryGetValue(f.Url, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(30))).ToList();
        if (wanted.Count == 0) return;
        var token = await TokenAsync(ct).ConfigureAwait(false);
        var clock = System.Diagnostics.Stopwatch.StartNew(); var answered = 0;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CountsBudget);
        try
        {
            await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = budget.Token }, async (f, cancel) =>
            {
                var id = f.Url[(f.Url.LastIndexOf('/') + 1)..];
                var saves = Number(await Try($"https://spclient.wg.spotify.com/popcount/v2/playlist/{id}/count", cancel).ConfigureAwait(false), "count");
                var tracks = Number(await Try($"https://spclient.wg.spotify.com/playlist/v2/playlist/{id}/metadata", cancel).ConfigureAwait(false), "length");
                Interlocked.Increment(ref answered);
                if (saves is not null || tracks is not null) _counts[f.Url] = ((int)Math.Clamp(tracks ?? 0, 0, 10000), saves, DateTime.UtcNow);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // shown without the late ones
        LastCounts = $"{answered} of {wanted.Count} counted in {clock.ElapsedMilliseconds} ms";
        if (_counts.Count > 2000) _counts.Clear();

        async Task<string?> Try(string url, CancellationToken cancel)
        {
            try { return await Get(url, token, cancel).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or TimeoutException or SourcesWaitingException) { return null; }
        }
    }

    // {"count":"18335"} or {"length":106}: a JSON number or a number in a string.
    internal static long? Number(string? json, string key)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(key, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n >= 0 ? n : null;
            return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : null;
        }
        catch (JsonException) { return null; }
    }

    // Words that describe any playlist: "hype drake songs" means a Drake playlist, whatever it is called.
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal) { "song", "songs", "playlist", "playlists", "music", "mix", "the" };

    /// <summary>
    /// Playlists named with every word typed come first, then the rest of Spotify's answers. Within each, the most
    /// saved rise (each tenfold counts as about eight places of Spotify's own order), so a huge generic workout
    /// playlist never buries "Drake Hype Songs" for "hype drake", but the most popular Drake one leads.
    /// </summary>
    internal static IReadOnlyList<FoundPlaylist> Rank(string query, IReadOnlyList<FoundPlaylist> found)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(BotText.Key)
            .Where(w => w.Length > 0 && !FillerWords.Contains(w)).ToArray();
        return found.Select((f, position) =>
        {
            var name = BotText.Key(f.Name);
            var named = words.Length > 0 && words.All(name.Contains);
            var score = (f.Saves is { } s ? Math.Log10(s + 1) : 2) - position * 0.12;
            return (f, named, score, position);
        }).OrderByDescending(r => r.named).ThenByDescending(r => r.score).ThenBy(r => r.position).Select(r => r.f).ToList();
    }

    internal async Task<IReadOnlyList<FoundPlaylist>> DeezerSearchAsync(string query, int limit, CancellationToken ct, int offset = 0)
    {
        var raw = await Get("https://api.deezer.com/search/playlist?q=" + Uri.EscapeDataString(query) + "&limit=" + limit + (offset > 0 ? "&index=" + offset : ""), null, ct).ConfigureAwait(false);
        return ParseDeezerSearch(raw);
    }

    internal static List<FoundPlaylist> ParseDeezerSearch(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("PLAYLIST_SEARCH_UNAVAILABLE: Deezer's search answered in an unknown form.");
        var found = new List<FoundPlaylist>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number <= 0) continue;
            if (!SpotifyPlaylists.PlaylistLink("https://www.deezer.com/playlist/" + number, out var link) || found.Any(f => f.Url == link!.Url)) continue;
            var name = Clean(String(item, "title", ""));
            if (name.Length == 0) continue;
            var owner = item.TryGetProperty("user", out var user) ? Clean(String(user, "name", "")) : "";
            var cover = String(item, "picture_medium", String(item, "picture", ""));
            var tracks = item.TryGetProperty("nb_tracks", out var n) && n.TryGetInt32(out var count) ? Math.Clamp(count, 0, 10000) : 0;
            found.Add(new(link!.Url, name, owner, ArtistCatalog.ArtworkUrl(cover) ? cover : "", tracks, null));
        }
        return found;
    }

    private static string Clean(string text) => new string(text.Where(c => !char.IsControl(c)).Take(200).ToArray()).Trim();

    /// <summary>Search opened: get Spotify's access now, so the first playlist search does not wait for it.</summary>
    internal async Task WarmUpSearchAsync()
    {
        try { await TokenAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Noctis.Services.DebugLog.Write("Search", "Playlist search warm-up: " + ex.Message); }
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is { } fresh && DateTimeOffset.UtcNow < _tokenUntil) return fresh;
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } other && DateTimeOffset.UtcNow < _tokenUntil) return other;
            using var data = Embed(await Get(TokenPage, null, ct).ConfigureAwait(false));
            var token = Token(data.RootElement);
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("PLAYLIST_SEARCH_UNAVAILABLE: Spotify gave no search access.");
            var until = DateTimeOffset.UtcNow.AddMinutes(20);
            foreach (var path in new[] { new[] { "props", "pageProps", "state", "settings", "session" }, new[] { "props", "pageProps", "settings", "session" } })
                if (Path(data.RootElement, path) is { } session && session.TryGetProperty("accessTokenExpirationTimestampMs", out var ms) && ms.TryGetInt64(out var at))
                { until = DateTimeOffset.FromUnixTimeMilliseconds(at).AddMinutes(-1); break; }
            _token = token; _tokenUntil = until;
            return token;
        }
        finally { _tokenGate.Release(); }
    }
}
