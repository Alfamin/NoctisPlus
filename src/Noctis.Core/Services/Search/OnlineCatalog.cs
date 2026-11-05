namespace Noctis.Services.Search;

/// <summary>Metadata for native catalog pages. Reading it never downloads audio or imports a library item.</summary>
public interface IOnlineCatalog
{
    Task<OnlineAlbumPage> ReadAlbumAsync(OnlineAlbum release, CancellationToken ct);
    Task<OnlineAlbumPage?> FindLibraryAlbumAsync(string artist, string title, IReadOnlyList<string> songTitles, CancellationToken ct);
}

public sealed record OnlineAlbumSong(OnlineSong Song, int Disc = 1, int Number = 0);
public sealed record OnlineAlbumPage(OnlineAlbum Release, IReadOnlyList<OnlineAlbumSong> Songs, string ReleaseDate = "", string Warning = "")
{
    /// <summary>Provider count mismatch must not masquerade as a complete album or a Download all action.</summary>
    public bool IsComplete => Release.TrackCount <= 0 || Release.TrackCount == Songs.Count;
}
