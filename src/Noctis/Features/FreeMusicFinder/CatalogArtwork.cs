using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

internal static class CatalogArtwork
{
    /// <summary>The same picture at <paramref name="size"/> px (Search rows and cards: sharp at
    /// 2x without fetching a 1000 px cover). Both catalogs resize on their side; a picture already
    /// that large is left as it is.</summary>
    internal static string? SizedUrl(string? value, int size)
    {
        if (!ArtistCatalog.ArtworkUrl(value)) return null;
        var url = new Uri(value!);
        if (url.Host == "i.scdn.co") return value; // Spotify image ids encode the variant; never rewrite their paths.
        var match = Regex.Match(url.AbsolutePath, @"/(?<w>[1-9][0-9]{0,3})x(?<h>[1-9][0-9]{0,3})(?<mode>bb|cw|cc|-[a-zA-Z0-9-]+)?\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!match.Success || int.Parse(match.Groups["w"].Value) >= size) return value;
        var mode = url.Host == "cdn-images.dzcdn.net" ? match.Groups["mode"].Value : "bb";
        return url.GetLeftPart(UriPartial.Authority) + url.AbsolutePath[..match.Index] + $"/{size}x{size}{mode}.jpg" + url.Query;
    }

    internal static string? LargeUrl(string? value, int size = 1200)
    {
        if (!ArtistCatalog.ArtworkUrl(value)) return null;
        var url = new Uri(value!); var target = url.Host == "cdn-images.dzcdn.net" ? 1000 : Math.Clamp(size, 320, 1200);
        if (url.Host == "i.scdn.co") return value;
        var match = Regex.Match(url.AbsolutePath, @"/(?<w>[1-9][0-9]{0,3})x(?<h>[1-9][0-9]{0,3})(?<mode>bb|cw|cc|-[a-zA-Z0-9-]+)?\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!match.Success) return value;
        if (int.Parse(match.Groups["w"].Value) >= target && int.Parse(match.Groups["h"].Value) >= target) return value;
        var mode = url.Host == "cdn-images.dzcdn.net" ? match.Groups["mode"].Value : "bb";
        return url.GetLeftPart(UriPartial.Authority) + url.AbsolutePath[..match.Index] + $"/{target}x{target}{mode}.jpg" + url.Query;
    }
    internal static (int Width, int Height) Dimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (data.Slice(12, 4).SequenceEqual("VP8X"u8)) return (1 + data[24] + (data[25] << 8) + (data[26] << 16), 1 + data[27] + (data[28] << 8) + (data[29] << 16));
            if (data.Slice(12, 4).SequenceEqual("VP8L"u8) && data[20] == 0x2f)
            { var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]); return (1 + (int)(bits & 0x3fff), 1 + (int)((bits >> 14) & 0x3fff)); }
            if (data.Slice(12, 4).SequenceEqual("VP8 "u8) && data.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 1, 0x2a }))
                return (BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3fff);
        }
        if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            var at = 2;
            while (at + 4 <= data.Length)
            {
                if (data[at++] != 0xff) break;
                while (at < data.Length && data[at] == 0xff) at++;
                if (at >= data.Length) break;
                var marker = data[at++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (at + 2 > data.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                if (length < 2 || at + length > data.Length) break;
                if (length >= 7 && marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                    return (BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(at + 3)..]));
                at += length;
            }
        }
        return default;
    }
}

internal sealed partial class ArtistCatalog
{
    public async Task<string?> ProgressiveArtworkAsync(string url, Func<string, Task> show, CancellationToken ct, int size = 1200)
    {
        var large = CatalogArtwork.LargeUrl(url, size); if (large is null) return null;
        if (SavedArtwork(large, highQuality: true) is {} ready) { ct.ThrowIfCancellationRequested(); await show(ready).ConfigureAwait(false); return ready; }
        string? preview = null;
        if (large != url)
        {
            try { preview = await ArtworkAsync(url, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException) { }
            if (preview is not null) { ct.ThrowIfCancellationRequested(); await show(preview).ConfigureAwait(false); }
        }
        try
        {
            var sharp = await ArtworkAsync(large, ct, highQuality: true).ConfigureAwait(false);
            if (sharp is not null) { ct.ThrowIfCancellationRequested(); await show(sharp).ConfigureAwait(false); return sharp; }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException) { }
        return preview;
    }
}
