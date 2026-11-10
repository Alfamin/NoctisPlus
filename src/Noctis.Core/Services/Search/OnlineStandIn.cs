using Noctis.Models;

namespace Noctis.Services.Search;

/// <summary>Shared online queue identity; hosts resolve the pending song before playback.</summary>
public static class OnlineStandIn
{
    public static Track Create(IOnlineMusicSearch source, OnlineSong song, bool keep, string? page = null) => new()
    {
        Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("stream:" + song.Id))),
        Title = song.Title, Artist = song.ArtistDisplay, Album = song.Album,
        Duration = TimeSpan.FromSeconds(song.Seconds is > 0 and <= 86400 ? song.Seconds.Value : 0),
        IsExplicit = song.Explicit == true,
        PendingStream = new Noctis.ViewModels.PendingStream(source, song, keep, page),
        // Owner #290: a cover already on disk for the song shows from the start (queue rows, mini player, Now Playing).
        AlbumArtworkPath = CachedCover(source, song),
    };

    private static string? CachedCover(IOnlineMusicSearch source, OnlineSong song)
    {
        try { return source.CachedQueueCover(song); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}
