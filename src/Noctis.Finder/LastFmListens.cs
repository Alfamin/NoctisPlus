using System.Globalization;
using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>Owner #283: For you on the phone learns from what the listener logs everywhere: the connected Last.fm account's
/// scrobbles (the computer's and the phone's), read by the account NAME with the app's key (<c>user.getRecentTracks</c>, a
/// public read; no session key, nothing written). Kept on disk; a later read asks only for scrobbles newer than the newest
/// kept one. Any failure leaves the kept scrobbles in use: For you never waits on or fails because of Last.fm.</summary>
internal sealed class LastFmListens : IDisposable
{
    /// <summary>Scrobbles kept (the newest): about four months at the owner's ~44 plays a day.</summary>
    public const int Kept = 5_000;
    /// <summary>A first read takes this many pages of 200; later reads at most this many pages of new scrobbles.</summary>
    public const int MaxPages = 5;
    /// <summary>How long a read is fresh: a For you build within it asks Last.fm nothing.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(20);

    private readonly string _path;
    private readonly Func<string> _user, _key;
    private readonly Func<DateTimeOffset> _now;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly object _sync = new();
    private Saved? _saved;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    internal sealed record Listen(string Artist, string Title, long Uts);
    private sealed record Saved(string User, List<Listen> Listens);

    public LastFmListens(string folder, HttpMessageHandler handler, Func<string> user, Func<string> key, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(folder, "lastfm-listens.json"); _user = user; _key = key; _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(SimilarArtists.UserAgent);
    }

    /// <summary>The account's scrobbles as plays For you learns from (each one counted, so heard), newest first. Empty with no
    /// account connected. Reads new scrobbles when the kept ones are older than <see cref="Fresh"/>.</summary>
    public async Task<List<TastePlay>> PlaysAsync(CancellationToken ct)
    {
        var user = _user()?.Trim() ?? "";
        if (user.Length == 0) return [];
        await _reading.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = Load(user);
            if (_now() - _readAt >= Fresh)
            {
                try
                {
                    var newer = await ReadNewerAsync(user, saved.Listens.Count > 0 ? saved.Listens[0].Uts : 0, ct).ConfigureAwait(false);
                    _readAt = _now();
                    if (newer.Count > 0)
                    {
                        saved = new(user, [.. newer.Concat(saved.Listens).DistinctBy(l => (l.Uts, l.Artist, l.Title)).OrderByDescending(l => l.Uts).Take(Kept)]);
                        lock (_sync) _saved = saved;
                        Write(saved);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    _readAt = _now() - Fresh + TimeSpan.FromMinutes(2); // offline or refused: try again in a couple of minutes
                }
            }
            return [.. saved.Listens.Select(l => new TastePlay(l.Artist, DateTimeOffset.FromUnixTimeSeconds(l.Uts).UtcDateTime, 1.0, l.Title, Heard: true))];
        }
        finally { _reading.Release(); }
    }

    private Saved Load(string user)
    {
        lock (_sync)
        {
            if (_saved is { } s && string.Equals(s.User, user, StringComparison.OrdinalIgnoreCase)) return s;
            Saved? read = null;
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length < 8 * 1024 * 1024)
                    read = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            // Another account's scrobbles are not this listener's taste.
            _saved = read is { Listens: not null } && string.Equals(read.User, user, StringComparison.OrdinalIgnoreCase)
                ? read with { Listens = [.. read.Listens.Where(l => l is { Artist.Length: > 0 }).OrderByDescending(l => l.Uts)] }
                : new Saved(user, []);
            if (!ReferenceEquals(_saved, read)) _readAt = DateTimeOffset.MinValue;
            return _saved;
        }
    }

    private void Write(Saved saved)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Scrobbles after <paramref name="afterUts"/> (all of the newest pages on a first read), newest first.</summary>
    private async Task<List<Listen>> ReadNewerAsync(string user, long afterUts, CancellationToken ct)
    {
        var result = new List<Listen>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var query = "method=user.getrecenttracks&user=" + Uri.EscapeDataString(user) + "&limit=200&page=" + page
                + (afterUts > 0 ? "&from=" + (afterUts + 1).ToString(CultureInfo.InvariantCulture) : "");
            // Never log this URI: it carries the key.
            using var response = await _http.GetAsync("https://ws.audioscrobbler.com/2.0/?" + query + "&api_key=" + _key().Trim() + "&format=json",
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Last.fm HTTP failure.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Too much data.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Too much data.");
            var (listens, pages) = Parse(bytes);
            result.AddRange(listens);
            if (page >= pages || listens.Count == 0) break;
        }
        return result;
    }

    /// <summary>One page of <c>user.getRecentTracks</c>: the scrobbles (the song playing now has no date and is left out) and the page count.</summary>
    internal static (List<Listen> Listens, int Pages) Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException("Last.fm could not read the account.");
        if (!root.TryGetProperty("recenttracks", out var recent) || recent.ValueKind != JsonValueKind.Object) return ([], 0);
        var pages = recent.TryGetProperty("@attr", out var attr) && attr.TryGetProperty("totalPages", out var tp)
            && int.TryParse(tp.ValueKind == JsonValueKind.String ? tp.GetString() : tp.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
        var result = new List<Listen>();
        if (!recent.TryGetProperty("track", out var tracks)) return (result, pages);
        // One scrobble comes back as an object, not an array.
        var items = tracks.ValueKind == JsonValueKind.Array ? tracks.EnumerateArray().ToList() : tracks.ValueKind == JsonValueKind.Object ? [tracks] : [];
        foreach (var t in items)
        {
            if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.Object) continue;
            if (!date.TryGetProperty("uts", out var u) || !long.TryParse(u.ValueKind == JsonValueKind.String ? u.GetString() : u.GetRawText(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var uts) || uts <= 0) continue;
            var artist = t.TryGetProperty("artist", out var a) ? a.ValueKind == JsonValueKind.Object
                ? Text(a, "#text") is { Length: > 0 } x ? x : Text(a, "name") : a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "" : "";
            var title = Text(t, "name");
            if (artist.Length == 0 || title.Length == 0) continue;
            result.Add(new(artist, title, uts));
        }
        return (result, pages);
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    public void Dispose() { _http.Dispose(); _reading.Dispose(); }
}
