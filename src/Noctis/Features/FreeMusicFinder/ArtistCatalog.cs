using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreeMusicFinder;

internal sealed record CatalogArtist(string Id, string Name, string Genre = "", string Picture = "");
internal sealed record CatalogRelease(string Id, string Title, CatalogArtist Artist, string Date, string Kind, int Count, bool? Explicit = null, string Artwork = "");
internal sealed record CatalogSong(string Id, string Title, List<CatalogArtist> Artists, double? Seconds, bool? Explicit, int Disc = 1, int Number = 0)
{
    // Apple IDs identify metadata, not a playable/downloadable URL for either bot.
    public MusicRequest Request => new(Title, string.Join(", ", Artists.Select(a => a.Name)), Seconds,
        Id.StartsWith("deezer:") ? "https://www.deezer.com/track/" + Id[7..] : null,
        Explicit, OriginUri: Id.Replace(":", ":track:"));
}
internal sealed record CatalogProfile(CatalogArtist Artist, List<CatalogSong> Popular, List<CatalogRelease> Releases, string Notice = "");
internal sealed record CatalogAlbum(CatalogRelease Release, List<CatalogSong> Songs, string Notice = "");
internal sealed record CatalogResult<T>(T Value, DateTimeOffset SavedAt, bool Cached, string Warning = "");

/// <summary>Public metadata only. No Telegram, audio, preview URLs, credentials or library writes.
/// Cache hits work offline; refresh is explicit. Provider IDs never cross catalogs.</summary>
internal sealed partial class ArtistCatalog : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly CatalogLocks _locks = new();
    private readonly SemaphoreSlim _metadataSlots = new(3, 3), _appleStart = new(1, 1);
    private readonly object _limitsLock = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _applePace;
    private DateTimeOffset _nextApple;
    private readonly Dictionary<string, DateTimeOffset> _wait = new();
    // Waits the catalog did not ask for (no Retry-After; a 403 is often a block on the way, not a
    // limit): the way out they were set on. A VPN switched on since then ends them.
    private readonly Dictionary<string, string> _guessedOn = new();
    private readonly MetadataRoute? _route;
    // Waits the service asked for itself (Retry-After), this run: Retry keeps those.
    private readonly HashSet<string> _askedBy = new();
    // Six covers at a time: Search shows a screenful at once and covers are small (~20 KB).
    private readonly SemaphoreSlim _artGate = new(6, 6);
    private readonly SemaphoreSlim[] _artLocks = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly Dictionary<string, object> _memory = new();
    private readonly Dictionary<string, (CatalogProfile Raw, CatalogProfile View)> _profileViews = new();
    private sealed record Cache<T>(int Version, DateTimeOffset SavedAt, T Value, string Warning = "");
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 64 };

    public ArtistCatalog(string folder, HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null, TimeSpan? applePace = null)
    {
        _folder = Path.GetFullPath(folder); _now = now ?? (() => DateTimeOffset.UtcNow);
        _applePace = applePace ?? TimeSpan.FromSeconds(3.1);
        // HTTP/2 where the catalog offers it: the parallel searches share one connection (one
        // TLS handshake through the proxy) instead of opening three.
        handler ??= PlaylistCatalog.MetadataHandler("");
        _route = handler as MetadataRoute;
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctis-FreeMusicFinder/2.9");
        try
        {
            if (SafeFile("limits.json") is {} path && File.Exists(path) && new FileInfo(path).Length < 16384)
                foreach (var (provider, until) in JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) ?? [])
                    if (provider is "apple" or "deezer" && until > _now() && until < _now().AddDays(8)) _wait[provider] = until;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
    }

    /// <summary>The user asked to try again (Search's Retry): waits the catalog itself did not ask
    /// for end now, including ones saved by an earlier run. A Retry-After it sent is kept.</summary>
    public void ForgetGuessedWaits()
    {
        lock (_limitsLock)
        {
            foreach (var provider in _wait.Keys.Where(p => !_askedBy.Contains(p)).ToList()) _wait.Remove(provider);
            _guessedOn.Clear();
        }
        _warmedAt = default;
    }

    /// <summary>The way out to the song catalog right now (proxy and network generation).</summary>
    public string RouteKey => _route?.KeyFor("api.deezer.com") ?? "";

    private DateTimeOffset _warmedAt;

    /// <summary>Opens the connection to the song catalog ahead of the first search: DNS, TCP and
    /// TLS through the user's proxy happen now instead of on the first keystroke. At most once a
    /// minute; failures are ignored (the real search reports them).</summary>
    public void WarmUp()
    {
        var now = _now();
        if (now - _warmedAt < TimeSpan.FromMinutes(1)) return;
        _warmedAt = now;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await _http.GetAsync("https://api.deezer.com/infos", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { }
        });
    }

    public Task<CatalogResult<List<CatalogArtist>>> SearchAsync(string name, bool refresh, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl)) throw new ArgumentException("Enter an artist name (up to 200 characters).");
        return Cached("search:" + name.ToUpperInvariant(), TimeSpan.FromDays(1), refresh, async () =>
        {
            try
            {
                using var data = await Get("deezer", "/search/artist?q=" + Uri.EscapeDataString(name) + "&limit=25", ct);
                var artists = Data(data.RootElement).Select(DeezerArtist).Where(a => a is not null).Cast<CatalogArtist>().DistinctBy(a => a.Id).ToList();
                if (artists.Count == 0) throw Invalid("Deezer returned no artist matches; trying Apple.");
                return artists;
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                using var data = await Get("apple", "/search?entity=musicArtist&media=music&country=US&limit=25&term=" + Uri.EscapeDataString(name), ct);
                return Results(data.RootElement).Where(a => Text(a, "wrapperType") == "artist").Select(AppleArtist).Where(a => a is not null).Cast<CatalogArtist>().DistinctBy(a => a.Id).ToList();
            }
        }, ct);
    }

    public async Task<CatalogResult<CatalogProfile>> ArtistAsync(CatalogArtist artist, bool refresh, CancellationToken ct)
    {
        var (provider, id) = Split(artist.Id);
        var result = await Cached("artist:" + artist.Id, TimeSpan.FromDays(1), refresh, async () =>
        {
            CatalogProfile value;
            if (provider == "deezer")
            {
                using var detail = await Get(provider, "/artist/" + id, ct);
                var actual = DeezerArtist(detail.RootElement);
                if (actual?.Id != artist.Id) throw Invalid("Artist identity did not match the requested profile.");
                var releases = (await Pages("/artist/" + id + "/albums?limit=100", ct)).Select(a => DeezerRelease(a, actual)).Where(a => a is not null).Cast<CatalogRelease>().ToList();
                value = new(actual, [], releases);
            }
            else
            {
                using var releases = await Get(provider, "/lookup?id=" + id + "&entity=album&limit=200&country=US", ct);
                var all = Results(releases.RootElement).ToArray();
                var actual = all.Where(a => Text(a, "wrapperType") == "artist").Select(AppleArtist).FirstOrDefault(a => a?.Id == artist.Id);
                if (actual is null) throw Invalid("The artist is unavailable in Apple's US catalog.");
                var albums = all.Where(a => Text(a, "wrapperType") == "collection").Select(AppleRelease).Where(a => a is not null).Cast<CatalogRelease>().ToList();
                value = new(actual, [], albums,
                    "Apple US catalog. Regional availability and provider limits mean this is not a guaranteed complete discography." + (albums.Count >= 200 ? " The 200-release limit was reached." : ""));
            }
            // Refresh adds/updates metadata without deleting previously seen releases, even if a store removes one.
            var old = Read<CatalogProfile>("artist:" + artist.Id);
            value = value with { Popular = old?.Value.Popular ?? [], Releases = value.Releases.Concat(old?.Value.Releases ?? []).DistinctBy(a => a.Id).OrderByDescending(a => a.Date, StringComparer.Ordinal).ToList() };
            return value;
        }, ct).ConfigureAwait(false);
        // Normalize at the read boundary as well: existing 2.8.0 caches must improve
        // immediately, without requiring a refresh or deleting their saved metadata.
        if (result.Value.Artist.Id != artist.Id) throw Invalid("The saved artist identity does not match this profile. Refresh the catalog.");
        return Normalize(result);
    }
    public async Task<CatalogResult<CatalogProfile>?> SavedArtistAsync(CatalogArtist artist, CancellationToken ct)
    {
        Split(artist.Id); var saved = await ReadAsync<CatalogProfile>("artist:" + artist.Id, ct).ConfigureAwait(false);
        return saved is null || saved.Value.Artist.Id != artist.Id ? null : Normalize(new(saved.Value, saved.SavedAt, true, saved.Warning));
    }
    private CatalogResult<CatalogProfile> Normalize(CatalogResult<CatalogProfile> result)
    {
        lock (_profileViews)
        {
            var raw = result.Value;
            if (!_profileViews.TryGetValue(raw.Artist.Id, out var found) || !ReferenceEquals(found.Raw, raw))
            {
                if (_profileViews.Count >= 48) _profileViews.Remove(_profileViews.Keys.First());
                // A release credited to another same-name ID must never leak into this
                // artist's catalog through an old cache/provider association.
                var ownName = BotText.Key(raw.Artist.Name);
                found = (raw, raw with { Releases = CatalogEditions.Consolidate(raw.Releases.Where(r => r.Artist.Id == raw.Artist.Id || BotText.Key(r.Artist.Name) != ownName)) }); _profileViews[raw.Artist.Id] = found;
            }
            return result with { Value = found.View };
        }
    }
    public async Task<ArtistIdentity> IdentifyAsync(string name, IEnumerable<string> localAlbums, CatalogArtist? saved, CancellationToken ct, IReadOnlyList<CatalogArtist>? results = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl)) throw new ArgumentException("Enter an artist name (up to 200 characters).");
        var albums = ArtistEvidence.Albums(localAlbums);
        // Saved identities remain useful after the search TTL. Confirm them against
        // saved albums first, so a cached artist page never waits for a new search.
        var cachedSearch = results is null ? await ReadAsync<List<CatalogArtist>>("search:" + name.ToUpperInvariant(), ct).ConfigureAwait(false) : null;
        var all = results ?? cachedSearch?.Value ?? (await SearchAsync(name, false, ct).ConfigureAwait(false)).Value;
        var candidates = all
            .Where(a => BotText.Key(a.Name) == BotText.Key(name)).Take(8).ToList();
        if (candidates.Count == 0) return new(null, all.Take(8).ToList(), [], false, "No exact artist match. Choose the correct spelling/catalog.");
        if (saved is not null && BotText.Key(saved.Name) == BotText.Key(name) && !candidates.Any(a => a.Id == saved.Id)) candidates.Add(saved);
        if (candidates.Count == 1) return new(candidates[0], candidates, [], false);
        var scores = new Dictionary<string, List<string>>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var profile = await SavedArtistAsync(candidate, ct).ConfigureAwait(false);
            scores[candidate.Id] = profile is null ? [] : ArtistEvidence.Matches(albums, profile.Value.Releases.Where(r => r.Artist.Id == candidate.Id));
        }
        ArtistIdentity? Winner()
        {
            var ranked = candidates.OrderByDescending(a => scores.GetValueOrDefault(a.Id)?.Count ?? 0).ToArray();
            if (ranked.Length == 0) return null;
            var first = scores.GetValueOrDefault(ranked[0].Id) ?? [];
            if (!ArtistEvidence.Strong(first) || ranked.Length > 1 && first.Count == (scores.GetValueOrDefault(ranked[1].Id)?.Count ?? 0)) return null;
            return new(ranked[0], candidates, first, true);
        }
        if (Winner() is {} known) return known;
        // A stored name-only choice is not proof. On first disambiguation, one targeted
        // album search can link the library's recording to a stable catalog artist ID.
        foreach (var album in albums.Take(2))
        {
            try
            {
                var provider = candidates.FirstOrDefault()?.Id.Split(':')[0]; if (provider is null) break;
                var matches = await IdentityAlbumsAsync(provider, name, album, ct).ConfigureAwait(false);
                foreach (var candidate in candidates)
                    if (matches.Value.Any(r => r.Artist.Id == candidate.Id && CatalogEditions.Same(r.Title, album)) && !scores[candidate.Id].Contains(album)) scores[candidate.Id].Add(album);
                if (Winner() is {} found) return found;
            }
            catch (Exception ex) when (Recoverable(ex)) { return new(null, candidates, [], false, "Could not verify this namesake against your library. " + Explain(ex)); }
        }
        return new(null, candidates, [], false, "Several artists share this name. The saved selection could not be confirmed from your albums.");
    }
    private Task<CatalogResult<List<CatalogRelease>>> IdentityAlbumsAsync(string provider, string name, string title, CancellationToken ct) =>
        Cached("identity-album:" + provider + ":" + name.ToUpperInvariant() + ":" + CatalogEditions.Key(title), TimeSpan.FromDays(7), false, async () =>
        {
            if (provider == "apple")
            {
                using var data = await Get(provider, "/search?entity=album&media=music&country=US&limit=50&term=" + Uri.EscapeDataString(name + " " + CatalogEditions.Title(title)), ct);
                return Results(data.RootElement).Where(a => Text(a, "wrapperType") == "collection").Select(AppleRelease).Where(a => a is not null).Cast<CatalogRelease>().Where(r => BotText.Key(r.Artist.Name) == BotText.Key(name)).ToList();
            }
            var query = "artist:\"" + name.Replace("\"", "") + "\" album:\"" + CatalogEditions.Title(title).Replace("\"", "") + "\"";
            using var result = await Get("deezer", "/search/album?limit=50&q=" + Uri.EscapeDataString(query), ct);
            return Data(result.RootElement).Where(a => a.TryGetProperty("artist", out _)).Select(a => DeezerRelease(a, new("", ""))).Where(a => a is not null).Cast<CatalogRelease>().Where(r => BotText.Key(r.Artist.Name) == BotText.Key(name)).ToList();
        }, ct);

    public async Task<CatalogResult<CatalogAlbum>> AlbumAsync(CatalogRelease album, bool refresh, CancellationToken ct)
    {
        var (provider, id) = Split(album.Id);
        var answer = await Cached("album:" + album.Id, TimeSpan.FromDays(7), refresh, async () =>
        {
            if (provider == "deezer")
            {
                using var data = await Get(provider, "/album/" + id, ct);
                var actual = DeezerRelease(data.RootElement, album.Artist);
                if (actual?.Id != album.Id) throw Invalid("Album identity did not match the requested release.");
                var tracks = await Pages("/album/" + id + "/tracks?limit=100", ct);
                var songs = tracks.Select(t => DeezerSong(t)).Where(t => t is not null).Cast<CatalogSong>().ToList();
                return new CatalogAlbum(actual, songs, actual.Count > 0 && actual.Count != songs.Count ? "The catalog returned only part of this album. Download all is unavailable; individual songs can still be selected." : "");
            }
            using var result = await Get(provider, "/lookup?id=" + id + "&entity=song&limit=200&country=US", ct);
            var items = Results(result.RootElement).ToArray();
            var release = items.Where(a => Text(a, "wrapperType") == "collection").Select(AppleRelease).FirstOrDefault(a => a?.Id == album.Id);
            if (release is null) throw Invalid("The album is unavailable in Apple's US catalog.");
            var rows = items.Where(t => Id(t, "collectionId", "apple") == album.Id).Select(AppleSong).Where(t => t is not null).Cast<CatalogSong>().OrderBy(t => t.Disc).ThenBy(t => t.Number).ToList();
            return new CatalogAlbum(release, rows, release.Count > 0 && release.Count != rows.Count ? "The catalog's track count differs from the returned songs (some entries may be unavailable or non-audio). Download all is unavailable." : "");
        }, ct).ConfigureAwait(false);
        if (answer.Value.Release.Artist.Id != album.Artist.Id) throw Invalid("This release belongs to a different artist ID. Refresh the artist catalog; no namesake substitution was made.");
        return answer;
    }

    private async Task<CatalogResult<T>> Cached<T>(string key, TimeSpan age, bool refresh, Func<Task<T>> fetch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var savedBeforeWait = await ReadAsync<T>(key, ct).ConfigureAwait(false);
        // Fresh local data never queues behind an unrelated slow network operation.
        if (!refresh && savedBeforeWait is not null && _now() - savedBeforeWait.SavedAt < age) return new(savedBeforeWait.Value, savedBeforeWait.SavedAt, true, savedBeforeWait.Warning);
        using (await _locks.EnterAsync(key, ct).ConfigureAwait(false))
        {
            var old = Read<T>(key);
            if (!refresh && old is not null && _now() - old.SavedAt < age) return new(old.Value, old.SavedAt, true, old.Warning);
            try
            {
                var value = await fetch().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
                var saved = new Cache<T>(1, _now(), value); var warning = "";
                try { AtomicJson.Write(SafeFile(Key(key))!, saved); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { warning = "CATALOG_CACHE_SAVE_FAILED: Results are available, but could not be saved for offline use."; }
                Remember(key, saved with { Warning = warning });
                return new(value, saved.SavedAt, false, warning);
            }
            catch (Exception ex) when (old is not null && Recoverable(ex))
            { return new(old.Value, old.SavedAt, true, "Showing saved metadata. " + Explain(ex)); }
        }
    }
    private Cache<T>? Read<T>(string key)
    {
        if (Memory<T>(key) is {} memory) return memory;
        try
        {
            var path = SafeFile(Key(key))!;
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
            var data = JsonSerializer.Deserialize<Cache<T>>(File.ReadAllText(path), Json);
            if (data is not { Version: 1, Value: not null } || data.SavedAt > _now().AddMinutes(5)) return null;
            Remember(key, data); return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }
    private Cache<T>? Memory<T>(string key) { lock (_memory) return _memory.GetValueOrDefault(key) as Cache<T>; }
    private void Remember<T>(string key, Cache<T> value)
    {
        lock (_memory) { if (_memory.Count >= 96 && !_memory.ContainsKey(key)) _memory.Remove(_memory.Keys.First()); _memory[key] = value; }
    }
    private Task<Cache<T>?> ReadAsync<T>(string key, CancellationToken ct) => Memory<T>(key) is {} cached ? Task.FromResult<Cache<T>?>(cached) : Task.Run(() => Read<T>(key), ct);
    private static string Key(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json";
    private string? SafeFile(string name)
    {
        if (Directory.Exists(_folder) && (File.GetAttributes(_folder) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("CATALOG_CACHE_UNSAFE: the metadata folder is redirected.");
        var path = Path.Combine(_folder, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("CATALOG_CACHE_UNSAFE: the metadata file is redirected.");
        return path;
    }
    private async Task<List<JsonElement>> Pages(string first, CancellationToken ct)
    {
        var output = new List<JsonElement>(); var path = first; var seen = new HashSet<string>();
        for (var count = 0; count < 100; count++)
        {
            if (!seen.Add(path)) throw Invalid("The catalog repeated a page; no incomplete list was saved.");
            using var data = await Get("deezer", path, ct);
            var rows = Data(data.RootElement).ToArray(); output.AddRange(rows.Select(a => a.Clone()));
            var next = Text(data.RootElement, "next");
            if (next.Length == 0)
            {
                if (Number(data.RootElement, "total") is > 0 and var total && total != output.Count) throw Invalid("The catalog returned an incomplete list.");
                return output;
            }
            if (rows.Length == 0 || output.Count > 10000 || !Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Host != "api.deezer.com" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.AbsolutePath != first.Split('?')[0]) throw Invalid("The catalog returned an unsafe or incomplete next page.");
            path = uri.PathAndQuery;
        }
        throw Invalid("The catalog exceeded its page limit; no truncated list was saved.");
    }
    private async Task<JsonDocument> Get(string provider, string path, CancellationToken ct)
    {
        var host = provider == "apple" ? "itunes.apple.com" : "api.deezer.com";
        void CheckCooldown()
        {
            lock (_limitsLock)
            {
                if (_guessedOn.TryGetValue(provider, out var on) && _route?.KeyFor(host) != on) { _wait.Remove(provider); _guessedOn.Remove(provider); }
                if (_wait.TryGetValue(provider, out var until) && until > _now()) throw new SourcesWaitingException(provider + " catalog is waiting until " + until.ToLocalTime().ToString("g") + ". Cached results remain available.", until);
            }
        }
        CheckCooldown();
        if (provider == "apple")
        {
            await _appleStart.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = _nextApple - _now(); if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                CheckCooldown(); await _metadataSlots.WaitAsync(ct).ConfigureAwait(false);
                _nextApple = _now() + _applePace;
            }
            finally { _appleStart.Release(); }
        }
        else await _metadataSlots.WaitAsync(ct).ConfigureAwait(false);
        var url = (provider == "apple" ? "https://itunes.apple.com" : "https://api.deezer.com") + path;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            CheckCooldown();
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                var retry = response.Headers.RetryAfter?.Date ?? _now().Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(30));
                retry = retry < _now().AddSeconds(3) ? _now().AddSeconds(3) : retry > _now().AddDays(7) ? _now().AddDays(7) : retry;
                lock (_limitsLock)
                {
                    if (!_wait.TryGetValue(provider, out var previous) || retry > previous)
                    {
                        _wait[provider] = retry;
                        if (response.Headers.RetryAfter is not null) { _askedBy.Add(provider); _guessedOn.Remove(provider); }
                        else if (_route is not null) _guessedOn[provider] = _route.KeyFor(host);
                    }
                    retry = _wait[provider];
                    try { AtomicJson.Write(SafeFile("limits.json")!, _wait); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
                }
                throw new SourcesWaitingException($"CATALOG_HTTP_{(int)response.StatusCode}: {provider} refused or limited this connection. Check your VPN/proxy or retry later.", retry);
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"CATALOG_HTTP_{(int)response.StatusCode}: {provider} catalog could not be read. Check connectivity and retry.");
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw Invalid("Catalog response exceeded 4 MB.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var bytes = new byte[16384];
            while (true) { var n = await input.ReadAsync(bytes, timeout.Token).ConfigureAwait(false); if (n == 0) break; if (buffer.Length + n > 4 * 1024 * 1024) throw Invalid("Catalog response exceeded 4 MB."); buffer.Write(bytes, 0, n); }
            var data = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            if (data.RootElement.ValueKind != JsonValueKind.Object || data.RootElement.TryGetProperty("error", out _)) { data.Dispose(); throw Invalid(provider + " returned a catalog error. The artist or release may be unavailable."); }
            return data;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("CATALOG_TIMEOUT: " + provider + " did not finish its response in 25 seconds. Check your VPN/proxy and retry."); }
        catch (JsonException ex) { throw new InvalidDataException("CATALOG_FORMAT_CHANGED: The service returned an unsupported response.", ex); }
        finally { _metadataSlots.Release(); }
    }
    private static (string Provider, string Id) Split(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2 || parts[0] is not ("apple" or "deezer") || !long.TryParse(parts[1], out var id) || id <= 0 || parts[1] != id.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw Invalid("Invalid catalog identity.");
        return (parts[0], parts[1]);
    }
    private static InvalidDataException Invalid(string message) => new("CATALOG_INCOMPLETE: " + message);
    private static bool Recoverable(Exception ex) => ex is HttpRequestException or IOException or InvalidDataException or TimeoutException or SourcesWaitingException or JsonException;
    internal static string Explain(Exception ex) => ex is SourcesWaitingException wait ? wait.Message + " Retry after " + wait.Until.ToLocalTime().ToString("g") + "." : ex.Message;
    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? Clean(item.GetString() ?? "") : "";
    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(1000).ToArray());
    private static long Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var n) ? n : 0;
    private static string? Id(JsonElement value, string key, string provider) => Number(value, key) is > 0 and var n ? provider + ":" + n : null;
    private static IEnumerable<JsonElement> Data(JsonElement root) => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : throw Invalid("The catalog's page has no result list.");
    private static IEnumerable<JsonElement> Results(JsonElement root) => root.TryGetProperty("results", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : throw Invalid("Apple returned no result list.");
    private static CatalogArtist? DeezerArtist(JsonElement a) => Id(a, "id", "deezer") is {} id && Text(a, "name") is { Length: > 0 } name ? new(id, name, Picture: Text(a, "picture_medium")) : null;
    private static CatalogArtist? AppleArtist(JsonElement a) => Id(a, "artistId", "apple") is {} id && Text(a, "artistName") is { Length: > 0 } name ? new(id, name, Text(a, "primaryGenreName")) : null;
    private static CatalogRelease? DeezerRelease(JsonElement a, CatalogArtist fallback)
    {
        var artist = a.TryGetProperty("artist", out var by) ? DeezerArtist(by) ?? fallback : fallback;
        return Id(a, "id", "deezer") is {} id && Text(a, "title") is { Length: > 0 } title ? new(id, title, artist, Text(a, "release_date"), Text(a, "record_type"), (int)Math.Clamp(Number(a, "nb_tracks"), 0, 10000), Flag(a, "explicit_lyrics"), Text(a, "cover_medium")) : null;
    }
    private static CatalogRelease? AppleRelease(JsonElement a) => Id(a, "collectionId", "apple") is {} id && Text(a, "collectionName") is { Length: > 0 } name && AppleArtist(a) is {} artist
        ? new(id, name, artist, Text(a, "releaseDate").Split('T')[0], name.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase) ? "single" : name.EndsWith(" - EP", StringComparison.OrdinalIgnoreCase) ? "ep" : "album", (int)Math.Clamp(Number(a, "trackCount"), 0, 10000), Explicit(a, "collectionExplicitness"), Text(a, "artworkUrl100")) : null;
    private static bool? Flag(JsonElement a, string key) => a.TryGetProperty(key, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
    private static bool? Explicit(JsonElement a, string key) => Text(a, key) switch { "explicit" => true, "cleaned" => false, _ => null };
    private static CatalogSong? DeezerSong(JsonElement t)
    {
        var artists = t.TryGetProperty("contributors", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs.EnumerateArray().Select(DeezerArtist).Where(a => a is not null).Cast<CatalogArtist>().DistinctBy(a => a.Id).ToList() : [];
        if (artists.Count == 0 && t.TryGetProperty("artist", out var by) && DeezerArtist(by) is {} artist) artists.Add(artist);
        return Id(t, "id", "deezer") is {} id && Text(t, "title") is { Length: > 0 } title && artists.Count > 0 ? new(id, title, artists, Duration(Number(t, "duration")), Flag(t, "explicit_lyrics"), (int)Math.Clamp(Number(t, "disk_number"), 1, 100), (int)Math.Clamp(Number(t, "track_position"), 0, 10000)) : null;
    }
    private static CatalogSong? AppleSong(JsonElement t) => Text(t, "kind") == "song" && Id(t, "trackId", "apple") is {} id && Text(t, "trackName") is { Length: > 0 } title && AppleArtist(t) is {} artist
        ? new(id, title, [artist], Duration(Number(t, "trackTimeMillis") / 1000d), Explicit(t, "trackExplicitness"), (int)Math.Clamp(Number(t, "discNumber"), 1, 100), (int)Math.Clamp(Number(t, "trackNumber"), 0, 10000)) : null;
    private static double? Duration(double seconds) => seconds is > 0 and <= 86400 ? seconds : null;
    internal static bool ArtworkUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host == "cdn-images.dzcdn.net" || System.Text.RegularExpressions.Regex.IsMatch(uri.Host, @"^is[1-9][0-9]?-ssl\.mzstatic\.com$"));
    public string? SavedArtwork(string? url, bool highQuality = false)
    {
        if (!ArtworkUrl(url)) return null;
        try { var path = SafeFile("art-" + Key(url!)[..64] + ".img")!; return File.Exists(path) && new FileInfo(path).Length is > 0 and var bytes && bytes <= (highQuality ? 4 * 1024 * 1024 : 512 * 1024) ? path : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }
    public async Task<string?> ArtworkAsync(string url, CancellationToken ct, bool highQuality = false)
    {
        if (!ArtworkUrl(url)) return null;
        var sameImage = _artLocks[(int)((uint)StringComparer.Ordinal.GetHashCode(url) % (uint)_artLocks.Length)];
        await sameImage.WaitAsync(ct).ConfigureAwait(false);
        try { return await FetchArtwork(url, ct, highQuality).ConfigureAwait(false); }
        finally { sameImage.Release(); }
    }
    private async Task<string?> FetchArtwork(string url, CancellationToken ct, bool highQuality)
    {
        await _artGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (SavedArtwork(url, highQuality) is {} saved) return saved;
            var limit = highQuality ? 4 * 1024 * 1024 : 512 * 1024;
            var path = SafeFile("art-" + Key(url)[..64] + ".img")!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit || response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png" or "image/webp")) return null;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false); using var data = new MemoryStream(); var bytes = new byte[16384];
            while (true) { var n = await input.ReadAsync(bytes, timeout.Token).ConfigureAwait(false); if (n == 0) break; if (data.Length + n > limit) return null; data.Write(bytes, 0, n); }
            var dimensions = CatalogArtwork.Dimensions(data.ToArray());
            if (dimensions.Width is < 1 or > 8192 || dimensions.Height is < 1 or > 8192 || (long)dimensions.Width * dimensions.Height > 32 * 1024 * 1024) return null;
            Directory.CreateDirectory(_folder); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { data.Position = 0; await data.CopyToAsync(output, timeout.Token).ConfigureAwait(false); } File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return SavedArtwork(url, highQuality);
        }
        finally { _artGate.Release(); }
    }
    public void Dispose() => _http.Dispose();
}
