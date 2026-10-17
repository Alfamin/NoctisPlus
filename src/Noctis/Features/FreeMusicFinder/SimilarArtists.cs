using System.Net;
using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>An artist another artist's listeners also play, most similar first.</summary>
internal sealed record SimilarArtist(string Name, double Score);

/// <summary>
/// "Listeners of this artist also play…" from ListenBrainz, the open listening database (no key). Artists are found in
/// MusicBrainz through one of the listener's own songs by them, so a namesake is never taken for the artist. Public
/// metadata only (artist names and song titles are sent); MusicBrainz is asked at most once a second, as it asks;
/// everything is cached.
/// </summary>
internal sealed class SimilarArtists : IDisposable
{
    public const string UserAgent = "NoctisFreeMusicFinder/2.11 ( https://github.com/Alfamin/noctic-download-plugin )";
    private const string Algorithm = "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30";
    private static readonly TimeSpan KeepFound = TimeSpan.FromDays(180), KeepMissing = TimeSpan.FromDays(7), KeepSimilar = TimeSpan.FromDays(14);
    private readonly HttpClient _http;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _pace;
    private readonly SemaphoreSlim _musicBrainz = new(1, 1);
    private DateTimeOffset _nextMusicBrainz;
    private readonly object _lock = new();
    private Saved _saved;

    private sealed record Entry<T>(DateTimeOffset At, T? Value);
    private sealed record Saved(Dictionary<string, Entry<string>> Mbids, Dictionary<string, Entry<List<SimilarArtist>>> Similar);

    public SimilarArtists(string folder, HttpMessageHandler handler, Func<DateTimeOffset>? now = null, TimeSpan? musicBrainzPace = null)
    {
        _path = Path.Combine(folder, "listenbrainz.json"); _now = now ?? (() => DateTimeOffset.UtcNow); _pace = musicBrainzPace ?? TimeSpan.FromSeconds(1.1);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _saved = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        try
        {
            if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(_path).Length < 16 * 1024 * 1024
                && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path)) is { Mbids: not null, Similar: not null } saved)
                _saved = new(new(saved.Mbids, StringComparer.Ordinal), new(saved.Similar, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>The MusicBrainz id of an artist, through one of their songs the listener has; null when it cannot be told apart.</summary>
    public async Task<string?> ArtistIdAsync(string artist, IReadOnlyList<string> titles, CancellationToken ct)
    {
        var key = "artist:" + BotText.Key(artist);
        lock (_lock)
            if (_saved.Mbids.TryGetValue(key, out var known) && _now() - known.At < (known.Value is null ? KeepMissing : KeepFound)) return known.Value;
        string? found = null;
        bool busy = false; // MusicBrainz asked us to slow down: not knowing is not the same as not found
        foreach (var title in titles.Where(t => t.Trim().Length > 0).Distinct().Take(3))
        {
            using var doc = await MusicBrainzAsync("/ws/2/recording/?fmt=json&limit=5&query=" + Uri.EscapeDataString($"recording:\"{Quote(title)}\" AND artist:\"{Quote(artist)}\""), ct);
            if (doc is null) { busy = true; break; }
            found = Credits(doc.RootElement, "recordings").FirstOrDefault(c => BotText.Key(c.Name) == BotText.Key(artist)).Id;
            if (found is not null) break;
        }
        if (found is null && !busy)
        {
            // No song of theirs to go by: only an artist name that MusicBrainz knows exactly once.
            using var doc = await MusicBrainzAsync("/ws/2/artist/?fmt=json&limit=5&query=" + Uri.EscapeDataString($"artist:\"{Quote(artist)}\""), ct);
            busy = doc is null;
            if (doc is not null && doc.RootElement.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
            {
                var exact = artists.EnumerateArray().Where(a => BotText.Key(Text(a, "name")) == BotText.Key(artist)).Select(a => Text(a, "id")).Where(id => Guid.TryParse(id, out _)).ToList();
                if (exact.Count == 1) found = exact[0];
            }
        }
        if (found is not null || !busy) Remember(() => _saved.Mbids[key] = new(_now(), found));
        return found;
    }

    /// <summary>Artists whose listeners overlap with this one's, most similar first (scores scaled to 0..1).</summary>
    public async Task<List<SimilarArtist>> SimilarAsync(string mbid, CancellationToken ct)
    {
        if (!Guid.TryParse(mbid, out _)) return [];
        lock (_lock)
            if (_saved.Similar.TryGetValue(mbid, out var known) && _now() - known.At < KeepSimilar && known.Value is { } value) return value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync("https://labs.api.listenbrainz.org/similar-artists/json?artist_mbids=" + mbid + "&algorithm=" + Algorithm, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ListenBrainz answered {(int)response.StatusCode} for similar artists.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [];
        // Some answers wrap the list in another list.
        if (rows.Count > 0 && rows[0].ValueKind == JsonValueKind.Array) rows = rows.SelectMany(r => r.EnumerateArray()).ToList();
        var found = rows.Where(r => r.ValueKind == JsonValueKind.Object && Text(r, "artist_mbid") != mbid && Text(r, "name").Length > 0)
            .Select(r => (Name: Text(r, "name"), Score: r.TryGetProperty("score", out var s) && s.TryGetDouble(out var d) ? d : 0)).ToList();
        double top = found.Count == 0 ? 1 : Math.Max(1, found.Max(f => f.Score));
        var result = found.DistinctBy(f => BotText.Key(f.Name)).Take(60).Select(f => new SimilarArtist(f.Name, Math.Round(f.Score / top, 4))).ToList();
        Remember(() => _saved.Similar[mbid] = new(_now(), result));
        return result;
    }

    private async Task<JsonDocument?> MusicBrainzAsync(string path, CancellationToken ct)
    {
        await _musicBrainz.WaitAsync(ct);
        try
        {
            var wait = _nextMusicBrainz - _now();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _nextMusicBrainz = _now() + _pace;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var response = await _http.GetAsync("https://musicbrainz.org" + path, timeout.Token);
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                _nextMusicBrainz = _now() + TimeSpan.FromSeconds(5); // asked too fast: slow down, this artist is looked up next time
                return null;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"MusicBrainz answered {(int)response.StatusCode}.");
            return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
        }
        finally { _musicBrainz.Release(); }
    }

    private static IEnumerable<(string Id, string Name)> Credits(JsonElement root, string list)
    {
        if (!root.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("score", out var score) && score.TryGetInt32(out var s) && s < 90) continue;
            if (!item.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) continue;
            foreach (var credit in credits.EnumerateArray())
                if (credit.TryGetProperty("artist", out var artist) && Text(artist, "id") is var id && Guid.TryParse(id, out _))
                    yield return (id, Text(artist, "name"));
        }
    }

    private static string Quote(string value) => new(value.Where(c => c is not ('"' or '\\') && !char.IsControl(c)).ToArray());
    private static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void Remember(Action change)
    {
        // Parallel seed queries can finish in any order. Keep mutation and atomic publication together so an older
        // snapshot cannot overwrite a newer one after restart.
        lock (_lock)
        {
            change();
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(_path)!);
                for (var ancestor = dir; ancestor is not null; ancestor = ancestor.Parent)
                    if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return;
                AtomicJson.Write(_path, _saved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => _http.Dispose(); // in-flight calls still release their gate when a proxy changes
}
