using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

/// <summary>Public artist relations only. No user session, scrobbles, local paths or listening events are sent.
/// The injected route/key keep this usable by the future native app as well as the plugin.</summary>
internal sealed class LastFmSimilar : IDisposable
{
    private sealed record Entry(DateTimeOffset Until, List<SimilarArtist> Artists);
    private readonly HttpClient _http;
    private readonly Func<string> _key;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _pace;
    private readonly string _path;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _slots = new(3, 3), _start = new(1, 1);
    private Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _next, _retry;
    private DateTimeOffset _dataExpires = DateTimeOffset.MaxValue;
    private string _rejectedKey = "", _notice = "";
    public string Notice { get { lock (_sync) return _notice; } }
    public bool Enabled => ValidKey(_key());
    public DateTimeOffset DataExpires { get { lock (_sync) return _dataExpires; } }

    public static bool ValidKey(string? value) => value is not null && Regex.IsMatch(value.Trim(), "\\A[0-9a-fA-F]{32}\\z", RegexOptions.CultureInvariant);

    public LastFmSimilar(string folder, HttpMessageHandler handler, Func<string> key, Func<DateTimeOffset>? now = null, TimeSpan? pace = null)
    {
        _path = Path.Combine(folder, "lastfm.json"); _key = key; _now = now ?? (() => DateTimeOffset.UtcNow);
        _pace = pace ?? TimeSpan.FromMilliseconds(350);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(SimilarArtists.UserAgent);
        try
        {
            if (Safe() && File.Exists(_path) && new FileInfo(_path).Length < 4 * 1024 * 1024 &&
                JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) is { } saved)
                _cache = saved.Where(p => p.Value is { Artists: not null } && p.Value.Until > _now() && p.Value.Until <= _now().AddDays(15))
                    .Take(256).ToDictionary(p => p.Key, p => p.Value with { Artists = Clean(p.Value.Artists) }, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    public async Task<List<SimilarArtist>> SimilarAsync(string artist, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var apiKey = _key().Trim();
        if (!ValidKey(apiKey)) return [];
        if (string.IsNullOrWhiteSpace(artist) || artist.Length > 200 || artist.Any(char.IsControl)) return [];
        var name = BotText.Key(artist);
        lock (_sync)
            if (_cache.TryGetValue(name, out var cached) && cached.Until > _now()) { if (cached.Until < _dataExpires) _dataExpires = cached.Until; return [.. cached.Artists]; }
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _start.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_rejectedKey == apiKey) throw new InvalidOperationException(_notice);
                    if (_retry > _now()) throw new InvalidOperationException(_notice);
                }
                var delay = _next - _now();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_rejectedKey == apiKey || _retry > _now()) throw new InvalidOperationException(_notice);
                }
                _next = _now() + _pace;
            }
            finally { _start.Release(); }

            // Never log this URI, provider error text, or underlying network exception messages: they may echo the key.
            using var response = await _http.GetAsync("https://ws.audioscrobbler.com/2.0/?method=artist.getsimilar&artist=" +
                Uri.EscapeDataString(artist) + "&limit=50&autocorrect=1&api_key=" + apiKey + "&format=json", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw Wait(response);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Last.fm HTTP failure.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Last.fm returned too much data. Using the other catalogs.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var bytes = new byte[8192];
            while (true)
            {
                int n = await stream.ReadAsync(bytes, deadline.Token).ConfigureAwait(false); if (n == 0) break;
                if (buffer.Length + n > 1024 * 1024) throw new InvalidDataException("Last.fm returned too much data. Using the other catalogs.");
                buffer.Write(bytes, 0, n);
            }
            using var doc = JsonDocument.Parse(buffer.ToArray()); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var result = new List<SimilarArtist>();
            if (root.TryGetProperty("error", out var error))
            {
                int code = error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out var i) ? i : 0;
                if (code == 29) throw Wait(response);
                if (code is 10 or 26)
                {
                    lock (_sync)
                    {
                        _rejectedKey = apiKey;
                        _notice = code == 10 ? "Last.fm rejected the API key. Check it in Finder settings; using the other catalogs."
                            : "Last.fm suspended this API key. Change it in Finder settings; using the other catalogs.";
                        throw new InvalidOperationException(_notice);
                    }
                }
                if (code != 6) throw new InvalidDataException("Last.fm could not provide similar artists. Using the other catalogs.");
            }
            else
            {
                if (!root.TryGetProperty("similarartists", out var similar) || similar.ValueKind != JsonValueKind.Object || !similar.TryGetProperty("artist", out var rows) ||
                    rows.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) throw new JsonException();
                foreach (var row in rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().ToArray() : new[] { rows })
                {
                    if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                    var display = n.GetString()?.Trim() ?? "";
                    double score = 0;
                    if (row.TryGetProperty("match", out var m))
                    {
                        if (m.ValueKind == JsonValueKind.Number) m.TryGetDouble(out score);
                        else if (m.ValueKind == JsonValueKind.String) double.TryParse(m.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out score);
                    }
                    if (double.IsFinite(score) && BotText.Key(display) != name) result.Add(new(display, Math.Clamp(score, 0, 1)));
                }
                result = Clean(result).OrderByDescending(a => a.Score).ToList();
            }
            lock (_sync)
            {
                if (_rejectedKey != apiKey && _retry <= _now()) { _rejectedKey = ""; _notice = ""; }
                // Respect a shorter server lifetime; no-store/no-cache data never enters the persistent cache.
                var headers = response.Headers.CacheControl;
                var ttl = result.Count == 0 ? TimeSpan.FromDays(7) : TimeSpan.FromDays(14);
                if (headers?.MaxAge is { } max) ttl = max - (response.Headers.Age ?? TimeSpan.Zero) < ttl ? max - (response.Headers.Age ?? TimeSpan.Zero) : ttl;
                if (response.Content.Headers.Expires is { } expires && expires - _now() < ttl) ttl = expires - _now();
                var dataUntil = headers?.NoStore == true || headers?.NoCache == true ? _now() : _now() + ttl;
                if (dataUntil < _dataExpires) _dataExpires = dataUntil;
                if (headers?.NoStore != true && headers?.NoCache != true && ttl > TimeSpan.Zero)
                {
                    _cache[name] = new(_now() + ttl, result);
                    _cache = _cache.Where(p => p.Value.Until > _now()).OrderByDescending(p => p.Value.Until).Take(256).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    while (_cache.Count > 1 && JsonSerializer.SerializeToUtf8Bytes(_cache).Length > 4 * 1024 * 1024) _cache.Remove(_cache.Keys.Last());
                    try { if (Safe()) AtomicJson.Write(_path, _cache); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            return [.. result];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("Last.fm timed out. Check your VPN/proxy; using the other catalogs."); }
        catch (HttpRequestException ex) { throw Failure(ex.StatusCode is { } status ? $"Last.fm answered HTTP {(int)status}. Using the other catalogs."
            : "Last.fm could not be reached. Check your VPN/proxy; using the other catalogs."); }
        catch (InvalidDataException) { throw Failure("Last.fm returned invalid or oversized data. Using the other catalogs."); }
        catch (IOException) { throw Failure("Last.fm returned an incomplete response. Using the other catalogs."); }
        catch (JsonException) { throw Failure("Last.fm returned an unsupported response. Using the other catalogs."); }
        finally { _slots.Release(); }
    }

    private Exception Failure(string safeMessage) { lock (_sync) { _notice = safeMessage; return new InvalidOperationException(safeMessage); } }

    private Exception Wait(HttpResponseMessage response)
    {
        lock (_sync)
        {
            var requested = response.Headers.RetryAfter?.Date ?? _now().Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            _retry = requested < _now().AddSeconds(1) ? _now().AddSeconds(1) : requested;
            _notice = "Last.fm is rate limited. Using the other catalogs until " + _retry.ToLocalTime().ToString("g") + ".";
            return new InvalidOperationException(_notice);
        }
    }

    internal static List<SimilarArtist> Filter(IEnumerable<SimilarArtist> artists, string seed, IEnumerable<string> known)
    {
        var seedKey = BotText.Key(seed); var keys = known.Select(BotText.Key).Append(seedKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        return Clean(artists).Where(a =>
        {
            var key = BotText.Key(a.Name);
            if (seedKey.Length > 0 && key.Contains(seedKey, StringComparison.Ordinal)) return false;
            var parts = Regex.Split(a.Name, @"\s+(?:&|x|and)\s+|,\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return parts.Length == 1 || !parts.Any(p => keys.Contains(BotText.Key(p)));
        }).ToList();
    }

    private static List<SimilarArtist> Clean(IEnumerable<SimilarArtist> artists) => artists.Where(a => a is not null && a.Name is { Length: > 0 and <= 200 } &&
        !a.Name.Any(char.IsControl) && double.IsFinite(a.Score)).DistinctBy(a => BotText.Key(a.Name)).Take(50).Select(a => a with { Score = Math.Clamp(a.Score, 0, 1) }).ToList();
    private bool Safe()
    {
        // Check ancestors too: a non-link file inside a redirected cache is still redirected.
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(_path))!); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return !File.Exists(_path) || (File.GetAttributes(_path) & FileAttributes.ReparsePoint) == 0;
    }
    public void Dispose() => _http.Dispose();
}
