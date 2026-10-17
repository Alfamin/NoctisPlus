using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

internal sealed partial class ArtistCatalog
{
    // Portrait lookups never hold the catalog gate or delay opening a result. A single
    // route handles these public pages, their images and the rest of the metadata.
    private readonly SemaphoreSlim _portraitGate = new(2, 2);
    public async Task<string?> PortraitAsync(CatalogArtist artist, CancellationToken ct, Func<string, Task>? show = null)
    {
        var (provider, id) = Split(artist.Id);
        Task<string?> Image(string url) => show is null ? ArtworkAsync(url, ct) : ProgressiveArtworkAsync(url, show, ct, size: 640);
        if (ArtworkUrl(artist.Picture)) return await Image(artist.Picture).ConfigureAwait(false);
        var key = "portrait:" + artist.Id;
        await _portraitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = await ReadAsync<string>(key, ct).ConfigureAwait(false);
            if (saved is not null && _now() - saved.SavedAt < TimeSpan.FromDays(saved.Value.Length > 0 ? 7 : 1))
                return saved.Value.Length == 0 ? null : await Image(saved.Value).ConfigureAwait(false);
            try
            {
                string picture;
                if (provider == "deezer")
                {
                    // Legacy search caches predate the picture field.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var response = await _http.GetAsync("https://api.deezer.com/artist/" + id, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) return null;
                    var body = await PortraitBody(response, timeout.Token).ConfigureAwait(false);
                    if (body is null) return null;
                    using var data = System.Text.Json.JsonDocument.Parse(body);
                    var actual = DeezerArtist(data.RootElement);
                    picture = actual?.Id == artist.Id && ArtworkUrl(actual.Picture) ? actual.Picture : "";
                }
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    picture = ""; var url = new Uri("https://music.apple.com/us/artist/" + id);
                    for (var hop = 0; hop < 3; hop++)
                    {
                        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                        {
                            if (response.Headers.Location is not {} location || !Uri.TryCreate(url, location, out var next) || !AppleArtistPage(next, id)) return null;
                            url = next; continue;
                        }
                        if (!response.IsSuccessStatusCode) return null;
                        var body = await PortraitBody(response, timeout.Token).ConfigureAwait(false);
                        if (body is null) return null;
                        picture = ApplePortrait(body, id) ?? ""; break;
                    }
                }
                var cached = new Cache<string>(1, _now(), picture);
                try { AtomicJson.Write(SafeFile(Key(key))!, cached); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
                Remember(key, cached);
                return picture.Length == 0 ? null : await Image(picture).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TimeoutException or SourcesWaitingException or OperationCanceledException or System.Text.Json.JsonException or RegexMatchTimeoutException)
            { return saved is { Value.Length: > 0 } ? SavedArtwork(saved.Value) : null; }
        }
        finally { _portraitGate.Release(); }
    }

    private static async Task<string?> PortraitBody(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) return null;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[16384];
        while (true) { var n = await input.ReadAsync(bytes, ct).ConfigureAwait(false); if (n == 0) break; if (buffer.Length + n > 2 * 1024 * 1024) return null; buffer.Write(bytes, 0, n); }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static bool AppleArtistPage(Uri page, string id) => page.Scheme == "https" && page.Host == "music.apple.com" && page.IsDefaultPort && page.UserInfo.Length == 0 && page.Query.Length == 0 &&
        Regex.IsMatch(page.AbsolutePath, @"^/[a-z]{2}/artist/(?:[^/]+/)?" + Regex.Escape(id) + @"/?$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    internal static string? ApplePortrait(string html, string id)
    {
        string? canonical = null, picture = null;
        // Only metadata attributes are read; scripts and arbitrary URLs are never followed.
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]{0,8192}>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            var attributes = Regex.Matches(tag.Value, "([\\w:-]+)\\s*=\\s*([\"'])(.*?)\\2", RegexOptions.Singleline, TimeSpan.FromSeconds(1))
                .Cast<Match>().GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups[3].Value), StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("property", out var property) || !attributes.TryGetValue("content", out var content)) continue;
            if (property == "og:url") canonical = content;
            if (property == "og:image") picture = content;
        }
        if (!Uri.TryCreate(canonical, UriKind.Absolute, out var page) || !AppleArtistPage(page, id)) return null;
        if (!ArtworkUrl(picture)) return null;
        // Apple's artist OG image uses the source portrait. Request a small square
        // thumbnail, rather than the large social-sharing rendition.
        return Regex.Replace(picture!, @"/\d+x\d+[^/]*\.(png|jpg|jpeg|webp)$", "/160x160bb.jpg", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }
}
