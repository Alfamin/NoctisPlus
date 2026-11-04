using System.Globalization;
using System.Text;
using Noctis.Services.Sync;

namespace Noctis.TelegramSync;

/// <summary>One library entry: the first-added copy of a recording, the other copies kept as alternate sources.</summary>
public sealed record TgLibrarySong(string Id, SongSyncState Song, IReadOnlyList<string> AlternateIds);

/// <summary>
/// Read-side helpers over a merged <see cref="TgLedger"/>: the visible library (tombstones out,
/// the same recording under different document ids shown once), play totals (sum of every
/// device's own counter) and playlist membership (per-song add/remove, order from the header).
/// </summary>
public static class TgLibraryView
{
    public static IReadOnlyList<TgLibrarySong> Songs(TgLedger ledger)
    {
        var live = ledger.All(TgKinds.Song)
            .Select(i => (i.Id, Song: SyncJson.Deserialize<SongSyncState>(i.Payload)))
            .Where(x => x.Song is { Removed: false })
            .OrderBy(x => x.Song!.AddedUtc).ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
        var result = new List<TgLibrarySong>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var alternates = new List<List<string>>();
        foreach (var (id, song) in live)
        {
            var key = RecordingKey(song!.Artist, song.Title);
            if (byKey.TryGetValue(key, out var at) && Math.Abs(result[at].Song.DurationMs - song.DurationMs) <= 2000)
            {
                alternates[at].Add(id);
                continue;
            }
            byKey[key] = result.Count;
            alternates.Add(new List<string>());
            result.Add(new TgLibrarySong(id, song, alternates[^1]));
        }
        return result;
    }

    /// <summary>Total plays of a song = sum of every device's own counter (plays on two devices while apart add up).</summary>
    public static int PlayCount(TgLedger ledger, string songId)
    {
        var prefix = songId + "@";
        return ledger.All(TgKinds.Plays).Where(i => i.Id.StartsWith(prefix, StringComparison.Ordinal))
            .Sum(i => SyncJson.Deserialize<PlayCounterState>(i.Payload)?.Count ?? 0);
    }

    /// <summary>The playlist's songs: present entries, in the header's order (last writer), then new ones by add time.</summary>
    public static IReadOnlyList<string>? PlaylistSongs(TgLedger ledger, Guid playlist)
    {
        var header = ledger.Get(TgKinds.Playlist, playlist.ToString("D"));
        var state = header is null ? null : SyncJson.Deserialize<TgPlaylistHeader>(header.Payload);
        if (state is { Deleted: true }) return null;
        var prefix = playlist.ToString("N") + "|";
        var present = ledger.All(TgKinds.PlaylistEntry)
            .Where(i => i.Id.StartsWith(prefix, StringComparison.Ordinal) && SyncJson.Deserialize<PlaylistEntryState>(i.Payload)?.Present == true)
            .ToDictionary(i => i.Id[prefix.Length..], i => i.UpdatedUtc, StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var s in state?.Order ?? []) if (present.Remove(s)) ordered.Add(s);
        ordered.AddRange(present.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key));
        return ordered;
    }

    /// <summary>Artist + title, NFC, case- and bracket-insensitive (the desktop's RecordingMatch lives in the app project, not reusable here).</summary>
    public static string RecordingKey(string artist, string title)
    {
        static string Norm(string s)
        {
            s = (s ?? string.Empty).Normalize(NormalizationForm.FormC).ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            var depth = 0;
            foreach (var c in s)
            {
                if (c is '(' or '[') { depth++; continue; }
                if (c is ')' or ']') { if (depth > 0) depth--; continue; }
                if (depth == 0 && (char.IsLetterOrDigit(c))) sb.Append(c);
            }
            return sb.ToString();
        }
        return Norm(artist) + "\u001f" + Norm(title);
    }
}

/// <summary>
/// The Telegram log's playlist header: the existing <see cref="PlaylistSyncState"/> fields, with
/// membership moved out to per-song <see cref="TgKinds.PlaylistEntry"/> items so edits on two
/// devices merge; <see cref="Order"/> is the last writer's order.
/// </summary>
public sealed record TgPlaylistHeader(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("color")] string Color,
    [property: System.Text.Json.Serialization.JsonPropertyName("order")] List<string> Order,
    [property: System.Text.Json.Serialization.JsonPropertyName("deleted")] bool Deleted = false);
