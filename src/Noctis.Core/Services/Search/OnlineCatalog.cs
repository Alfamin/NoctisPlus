namespace Noctis.Services.Search;

/// <summary>Metadata for native catalog pages. Reading it never downloads audio or imports a library item.</summary>
public interface IOnlineCatalog
{
    OnlineAlbum? KnownAlbum(string id) => null;
    Task<OnlineArtistPage?> ReadArtistAsync(OnlineArtist artist, CancellationToken ct) => Task.FromResult<OnlineArtistPage?>(null);
    Task<OnlineArtistPage?> FindLibraryArtistAsync(string name, IReadOnlyList<string> albums, IReadOnlyList<string> titles, CancellationToken ct) => Task.FromResult<OnlineArtistPage?>(null);
    Task<OnlineAlbumPage> ReadAlbumAsync(OnlineAlbum release, CancellationToken ct);
    Task<OnlineAlbumPage?> FindLibraryAlbumAsync(string artist, string title, IReadOnlyList<string> songTitles, CancellationToken ct);
}

public sealed record OnlineArtistPage(OnlineArtist Artist, IReadOnlyList<OnlineAlbum> Releases, IReadOnlyList<OnlineSong> Songs, string Warning = "");

public sealed record OnlineAlbumSong(OnlineSong Song, int Disc = 1, int Number = 0);
public sealed record OnlineAlbumPage(OnlineAlbum Release, IReadOnlyList<OnlineAlbumSong> Songs, string ReleaseDate = "", string Warning = "")
{
    /// <summary>Provider count mismatch must not masquerade as a complete album or a Download all action.</summary>
    public bool IsComplete => Release.TrackCount <= 0 || Release.TrackCount == Songs.Count;
}
