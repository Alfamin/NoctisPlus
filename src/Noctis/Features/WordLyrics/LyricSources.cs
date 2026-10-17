using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FreeMusicFinder;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Features.WordLyrics;

internal sealed record LyricChoice(string Label, string Text);

internal static class LyricSources
{
    internal static async Task<List<LyricChoice>> FindAsync(Track song, string page, CancellationToken ct, string? proxy = null)
    {
        using var http = new HttpClient(PlaylistCatalog.MetadataHandler(proxy ?? FreeMusicPlugin.Current?.MetadataProxy ?? "")) { Timeout = TimeSpan.FromSeconds(25) };
        if (page.Trim().Length > 0)
        {
            var url = GeniusUrl(page);
            for (var n = 0; n < 4; n++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("NoctisPlus/2.0");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
                { url = GeniusUrl(new Uri(url, next).AbsoluteUri); continue; }
                response.EnsureSuccessStatusCode();
                var html = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
                var words = await Task.Run(() => GeniusText(html), ct);
                if (words.Length < 40) throw new InvalidOperationException("Genius did not provide complete lyric containers. It may be blocked; check the VPN/proxy or paste the words yourself.");
                return [new("Genius · review these words before timing", words)];
            }
            throw new HttpRequestException("Genius redirected too many times.");
        }
        var source = new LrcLibService(http);
        var rows = new List<LrcLibResult>();
        LrcLibResult? exact = null;
        try { exact = await source.GetLyricsAsync(song.Artist, song.Title, song.Duration.TotalSeconds, ct); }
        catch (LyricsProviderException) { /* /get may be unavailable while /search still works */ }
        if (exact is not null) rows.Add(exact);
        try { rows.AddRange(await source.SearchLyricsAsync(song.Artist, song.Title, ct)); }
        catch (LyricsProviderException) when (exact is not null) { }
        return rows.DistinctBy(r => r.Id).Where(r => !r.Instrumental && (r.HasSyncedLyrics || !string.IsNullOrWhiteSpace(r.PlainLyrics)))
            .Where(r => !song.IsExplicit || !Regex.IsMatch(r.TrackName ?? "", @"\b(clean|censored)\b", RegexOptions.IgnoreCase))
            .OrderBy(r => Math.Abs(r.Duration - song.Duration.TotalSeconds))
            .Select(r => new LyricChoice($"{r.ArtistName} · {r.TrackName} · {TimeSpan.FromSeconds(Math.Clamp(r.Duration, 0, 86400)):m\\:ss} · difference {Math.Abs(r.Duration - song.Duration.TotalSeconds):0}s", r.HasSyncedLyrics ? r.SyncedLyrics! : r.PlainLyrics!)).Take(30).ToList();
    }

    internal static Uri GeniusUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            uri.Host is not ("genius.com" or "www.genius.com") || !Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').EndsWith("-lyrics", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a public HTTPS Genius song page ending in -lyrics, or leave it empty for LRCLIB.");
        return new UriBuilder(uri) { Query = "", Fragment = "" }.Uri;
    }

    /// <summary>MIT WordLyrics GeniusText container algorithm, typed in C#. Only text is returned;
    /// markup, annotations and page headers are never shown as lyric content.</summary>
    internal static string GeniusText(string html)
    {
        if (html.Length > 2_000_000) throw new InvalidDataException("The lyric page is too large.");
        var stack = new List<string>(); int? depth = null; var current = new StringBuilder(); var parts = new List<string>();
        var empty = new HashSet<string>("area base br col embed hr img input link meta param source track wbr".Split(' '));
        foreach (Match token in Regex.Matches(html, @"<!--[\s\S]*?-->|<[^>]*>|[^<]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            var text = token.Value;
            if (!text.StartsWith('<')) { if (depth is not null) current.Append(WebUtility.HtmlDecode(text)); continue; }
            var name = Regex.Match(text, @"^</?\s*([a-zA-Z0-9]+)").Groups[1].Value.ToLowerInvariant();
            if (name.Length == 0) continue;
            if (text.StartsWith("</"))
            {
                var closing = stack.LastIndexOf(name); if (closing < 0) continue;
                if (depth is { } d && closing + 1 <= d) { parts.Add(current.ToString()); current.Clear(); depth = null; }
                stack.RemoveRange(closing, stack.Count - closing); continue;
            }
            if (!empty.Contains(name)) stack.Add(name);
            var lyrics = Regex.IsMatch(text, "data-lyrics-container\\s*=\\s*['\"]true['\"]", RegexOptions.IgnoreCase) ||
                Regex.Matches(text, "class\\s*=\\s*['\"]([^'\"]*)['\"]", RegexOptions.IgnoreCase).Any(m => m.Groups[1].Value.Split(' ').Contains("lyrics"));
            if (lyrics && depth is null) { depth = stack.Count; current.Clear(); }
            else if (depth is not null && name is "br" or "p" or "div") current.Append('\n');
        }
        return depth is null ? LyricDrafts.Normalize(string.Join("\n", parts).Replace('\u00a0', ' ')) : "";
    }
}
