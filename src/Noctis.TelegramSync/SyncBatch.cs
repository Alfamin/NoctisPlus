using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Services.Sync;

namespace Noctis.TelegramSync;

/// <summary>Kinds on the Telegram log: the desktop ledger's two plus the ones the log adds.</summary>
public static class TgKinds
{
    public const string Track = SyncKinds.Track;
    public const string Playlist = SyncKinds.Playlist;
    /// <summary>Library membership (<see cref="SongSyncState"/>), id <c>tg:&lt;documentId&gt;</c>.</summary>
    public const string Song = "song";
    /// <summary>One device's own play counter for one song, id <c>&lt;songId&gt;@&lt;device&gt;</c>. Total = sum over devices.</summary>
    public const string Plays = "plays";
    /// <summary>One song's membership in one playlist, id <c>&lt;playlistGuid&gt;|&lt;songId&gt;</c> (add/remove merge per song).</summary>
    public const string PlaylistEntry = "playlistEntry";

    public static string SongId(long documentId) => $"tg:{documentId}";
    public static string PlaysId(string songId, string device) => $"{songId}@{device}";
    public static string EntryId(Guid playlist, string songId) => $"{playlist:N}|{songId}";
}

public sealed record BatchItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("updatedUtc")] DateTime UpdatedUtc);

/// <summary>One post on the sync channel: <c>{ v:1, device, seq, items }</c>. A snapshot also carries the last message id it covers.</summary>
public sealed record SyncBatch(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("items")] List<BatchItem> Items,
    [property: JsonPropertyName("snapshot")] bool Snapshot = false,
    [property: JsonPropertyName("upTo")] long UpToMessageId = 0);

/// <summary>Library membership as it travels. <see cref="Removed"/> is the tombstone.</summary>
public sealed record SongSyncState(
    [property: JsonPropertyName("documentId")] long DocumentId,
    [property: JsonPropertyName("peer")] long Peer,
    [property: JsonPropertyName("msgId")] long MsgId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("addedUtc")] DateTime AddedUtc,
    [property: JsonPropertyName("removed")] bool Removed = false);

/// <summary>A device's own counter; only that device writes it, so last-writer-wins never loses plays.</summary>
public sealed record PlayCounterState(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("lastPlayed")] DateTime? LastPlayed);

public sealed record PlaylistEntryState([property: JsonPropertyName("present")] bool Present);

/// <summary>JSON on the wire, gzip above 3 KB (detected by the gzip magic on read).</summary>
public static class BatchCodec
{
    public const int GzipAbove = 3 * 1024;
    public const int Version = 1;

    public static byte[] Encode(SyncBatch batch)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(batch, SyncJson.Options);
        if (json.Length <= GzipAbove) return json;
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(json);
        return ms.ToArray();
    }

    /// <summary>Null for anything that is not a v1 batch (foreign posts in the channel are skipped, never thrown on).</summary>
    public static SyncBatch? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            byte[] json;
            if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
            {
                using var gz = new GZipStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gz.CopyTo(ms);
                json = ms.ToArray();
            }
            else json = data.ToArray();
            var b = JsonSerializer.Deserialize<SyncBatch>(json, SyncJson.Options);
            return b is { V: Version, Device.Length: > 0, Items: not null } ? b : null;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return null; }
    }
}
