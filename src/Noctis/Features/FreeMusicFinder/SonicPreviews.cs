using System.Net;
using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>Analysis-only previews, using Finder's proxy/offline route but a separate bounded lane. Signed URLs live only
/// in this call. No Telegram, full-song request, playback or library writes. A failure is a missing vector, never a failed page.</summary>
internal sealed class SonicPreviews : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _slots = new(6, 6);
    private const int MaxPreview = 4 * 1024 * 1024;
    public SonicPreviews(HttpMessageHandler handler) => _http = new(handler)
    { Timeout = TimeSpan.FromSeconds(8), DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };

    public async Task<byte[]?> FetchAsync(CatalogSong song, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Refetch a signed URL once after expiry. Do not retry a whole failed batch indefinitely.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                var token = deadline.Token;
                var preview = await LocateAsync(song, token).ConfigureAwait(false);
                if (preview is null) return null;
                using var response = await _http.GetAsync(preview, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (attempt == 0 && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) continue;
                if (!response.IsSuccessStatusCode) return null;
                var type = response.Content.Headers.ContentType?.MediaType;
                if (type is not null && !(type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || type == "application/octet-stream")) return null;
                var data = await BoundedAsync(response, MaxPreview, token).ConfigureAwait(false);
                return data is { Length: > 1000 } ? data : null;
            }
            return null;
        }
        finally { _slots.Release(); }
    }

    private async Task<Uri?> LocateAsync(CatalogSong song, CancellationToken ct)
    {
        string[] parts = song.Id.Split(':', 2);
        if (parts.Length != 2 || parts[1].Length is < 1 or > 20 || !parts[1].All(char.IsAsciiDigit)) return null;
        string url = parts[0] switch
        {
            "deezer" => "https://api.deezer.com/track/" + parts[1],
            "apple" => "https://itunes.apple.com/lookup?id=" + parts[1] + "&country=US",
            _ => "",
        };
        if (url.Length == 0) return null;
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await BoundedAsync(response, 1024 * 1024, ct).ConfigureAwait(false);
        if (bytes is null) return null;
        using var doc = JsonDocument.Parse(bytes);
        var row = doc.RootElement;
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (parts[0] == "apple")
        {
            if (!row.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
            row = results.EnumerateArray().FirstOrDefault(r => r.TryGetProperty("trackId", out var id) && id.ToString() == parts[1]);
            if (row.ValueKind != JsonValueKind.Object) return null;
        }
        else if (!row.TryGetProperty("id", out var id) || id.ToString() != parts[1]) return null;
        string field = parts[0] == "deezer" ? "preview" : "previewUrl";
        return row.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(p.GetString(), UriKind.Absolute, out var uri) && SafeUri(uri, parts[0]) ? uri : null;
    }

    internal static bool SafeUri(Uri uri, string provider) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (provider == "deezer" ? uri.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase)
            : provider == "apple" && (uri.Host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("audio-ssl.itunes.apple.com", StringComparison.OrdinalIgnoreCase)));

    private static async Task<byte[]?> BoundedAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximum) return null;
        using var data = new MemoryStream();
        using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int n = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return data.ToArray();
            if (data.Length + n > maximum) return null;
            data.Write(buffer, 0, n);
        }
    }

    public void Dispose() => _http.Dispose();
}
