using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

/// <summary>
/// A Spotify playlist read whole, in pages of 100, from the same query Spotify's own web player uses: every song's
/// title, artists, length, explicit flag, album and cover arrive with the list. A 500-song playlist took 43 s through
/// the embed pages (one page per song after the first 100); this way it takes a few seconds (owner #69: snappy).
/// When Spotify answers in any other form, the embed reading below takes over unchanged.
/// </summary>
internal sealed partial class PlaylistCatalog
{
    private const string PlaylistHash = "bb67e0af06e8d6f52b531f97468ee4acd44cd0f82b988e15c2ea47b1148efc77";
    internal const int PageSize = 100;

    /// <summary>The whole list with metadata, or null when Spotify's answer is not the expected one.</summary>
    /// <param name="lenient">Owner #104: for covers only: an entry Spotify gives without an id (a song removed from the
    /// service) is kept as an empty entry instead of refusing the whole list.</param>
    internal async Task<PlaylistManifest?> FullPlaylistAsync(MusicLink link, CancellationToken ct, bool lenient = false)
    {
        try
        {
            var token = await TokenAsync(ct).ConfigureAwait(false);
            var first = await PageAsync(link.Id, 0, token, ct, lenient).ConfigureAwait(false);
            if (first.Total is < 0 or > 10000) return null;
            var pages = new List<CatalogEntry>[(first.Total + PageSize - 1) / PageSize];
            if (pages.Length > 0) pages[0] = first.Entries;
            var offsets = Enumerable.Range(1, Math.Max(0, pages.Length - 1)).ToArray();
            await Parallel.ForEachAsync(offsets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (page, cancel) =>
            {
                var next = await PageAsync(link.Id, page * PageSize, token, cancel, lenient).ConfigureAwait(false);
                if (next.Total != first.Total) throw new InvalidDataException("PLAYLIST_CHANGED: its size changed while reading.");
                pages[page] = next.Entries;
            }).ConfigureAwait(false);
            var entries = pages.SelectMany(p => p ?? []).ToList();
            if (entries.Count != first.Total) return null;
            for (var i = 0; i < entries.Count; i++) entries[i].Position = i + 1;
            return new PlaylistManifest { Url = link.Url, Name = first.Name.Length > 0 ? first.Name : "Spotify import", Total = first.Total, Entries = entries };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Noctis.Services.DebugLog.Write("Playlist", "Spotify's paged list unavailable, reading embed pages: " + ex.Message);
            return null;
        }
    }

    private async Task<(string Name, int Total, List<CatalogEntry> Entries)> PageAsync(string id, int offset, string token, CancellationToken ct, bool lenient = false)
    {
        var variables = JsonSerializer.Serialize(new { uri = "spotify:playlist:" + id, offset, limit = PageSize, enableWatchFeedEntrypoint = false });
        var url = "https://api-partner.spotify.com/pathfinder/v1/query?operationName=fetchPlaylist&variables=" + Uri.EscapeDataString(variables) +
            "&extensions=" + Uri.EscapeDataString("{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"" + PlaylistHash + "\"}}");
        var page = ParsePlaylistPage(await Get(url, token, ct).ConfigureAwait(false), lenient);
        var expected = Math.Min(PageSize, page.Total - offset);
        if (page.Entries.Count != expected) throw new InvalidDataException($"METADATA_INCOMPLETE: Spotify returned {page.Entries.Count} of {expected} entries at {offset}.");
        return page;
    }

    internal static (string Name, int Total, List<CatalogEntry> Entries) ParsePlaylistPage(string raw, bool lenient = false)
    {
        using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 96 });
        if (Path(doc.RootElement, ["data", "playlistV2"]) is not { ValueKind: JsonValueKind.Object } playlist ||
            !playlist.TryGetProperty("content", out var content) || !content.TryGetProperty("totalCount", out var count) || !count.TryGetInt32(out var total) ||
            !content.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("METADATA_UNAVAILABLE: Spotify's playlist answer has an unknown form.");
        var entries = new List<CatalogEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (Path(item, ["itemV2", "data"]) is not { ValueKind: JsonValueKind.Object } data)
            {
                if (lenient) { entries.Add(new CatalogEntry()); continue; }
                throw new InvalidDataException("METADATA_INCOMPLETE: an entry has no data.");
            }
            var uri = String(data, "uri", "");
            if (lenient && uri.Length == 0) { entries.Add(new CatalogEntry()); continue; }
            if (uri.Length is 0 or > 1000) throw new InvalidDataException("METADATA_INCOMPLETE: a playlist entry has no identifier.");
            var entry = new CatalogEntry { Uri = uri };
            if (String(data, "__typename", "") == "Track" && Regex.IsMatch(uri, @"^spotify:track:[A-Za-z0-9]{22}$"))
            {
                var title = String(data, "name", "");
                var artist = Path(data, ["artists", "items"]) is { ValueKind: JsonValueKind.Array } artists
                    ? string.Join(", ", artists.EnumerateArray().Select(a => Path(a, ["profile"]) is { } p ? String(p, "name", "") : "").Where(n => n.Length > 0)) : "";
                double? seconds = Path(data, ["trackDuration", "totalMilliseconds"]) is { ValueKind: JsonValueKind.Number } ms && ms.TryGetDouble(out var milliseconds)
                    && milliseconds > 0 && milliseconds <= 86400000 ? milliseconds / 1000 : null;
                bool? explicitFlag = Path(data, ["contentRating", "label"]) is { ValueKind: JsonValueKind.String } label ? label.GetString() == "EXPLICIT" : null;
                if (title.Length > 0 && artist.Length > 0 && title.Length <= 1000 && artist.Length <= 1000 && !title.Any(char.IsControl) && !artist.Any(char.IsControl))
                    entry.Request = new(title, artist, seconds, TrackUrl(uri), explicitFlag);
                if (Path(data, ["albumOfTrack"]) is { ValueKind: JsonValueKind.Object } album)
                {
                    entry.Album = new string(String(album, "name", "").Where(c => !char.IsControl(c)).Take(1000).ToArray());
                    var covers = Path(album, ["coverArt", "sources"]) is { ValueKind: JsonValueKind.Array } sources
                        ? sources.EnumerateArray().Select(s => (Url: String(s, "url", ""), Width: s.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var px) ? px : 0))
                            .Where(s => ArtistCatalog.ArtworkUrl(s.Url)).ToList() : [];
                    entry.Artwork = covers.FirstOrDefault(s => s.Width == 300).Url ?? covers.OrderByDescending(s => s.Width).FirstOrDefault().Url ?? "";
                }
            }
            entries.Add(entry);
        }
        return (new string(String(playlist, "name", "").Where(c => !char.IsControl(c)).Take(200).ToArray()), total, entries);
    }
}
