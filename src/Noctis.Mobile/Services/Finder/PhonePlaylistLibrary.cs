using FreeMusicFinder;
using Noctis.Mobile.ViewModels;
using Noctis.Services;

namespace Noctis.Mobile.Services.Finder;

/// <summary>
/// Music Finder's view of the phone library and playlists. The host checks access to ordinary files
/// and granted content:// documents; a revoked folder grant must not count as an owned recording.
/// Playlist changes go through the phone library's single, serialized playlist writer.
/// </summary>
internal sealed class PhonePlaylistLibrary(ILibraryService library, LibraryViewModel playlists, Func<Func<Task>, Task> onUi,
    Func<string, bool>? accessible = null)
    : IPlaylistLibrary
{
    public Task<IReadOnlyList<LocalSong>> SnapshotAsync()
    {
        if (library.IsScanning || library.IsPublishingPartial)
            throw new IOException("LIBRARY_SCANNING: Noctis is still scanning. Wait for it to finish, then check the library again; no missing downloads were guessed.");
        var visible = library.Tracks.Select(t => t.Id).ToHashSet();
        IReadOnlyList<LocalSong> songs = library.AllTracks.Where(IsLocalFile)
            .Select(t => new LocalSong(t.Id, t.Artist, t.Title, t.FilePath, t.Duration, t.IsExplicit, visible.Contains(t.Id),
                t.AlbumArtist, t.IsFavorite, t.Rating, t.IsDisliked)).ToArray();
        return Task.FromResult(songs);
    }

    public Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create,
        IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
    {
        if (library.IsScanning || library.IsPublishingPartial)
            throw new IOException("LIBRARY_SCANNING: Noctis is still scanning. Wait for it to finish, then check the library again.");
        var available = library.AllTracks.Where(t => IsLocalFile(t) && Exists(t.FilePath)).Select(t => t.Id).ToHashSet();
        return onUi(() => playlists.ApplyImportedPlaylistAsync(id, name, Description(url), tracks, create, expected, remappedExpected, available));
    }

    public bool Exists(string path) => accessible?.Invoke(path) ?? File.Exists(path);

    private static bool IsLocalFile(Noctis.Models.Track t) => !t.IsRemoteStream &&
        (Path.IsPathFullyQualified(t.FilePath) || t.FilePath.StartsWith("content://", StringComparison.Ordinal));

    /// <summary>Same text as the desktop's (PlaylistLibrary.ImportDescription), so a shared playlists.json
    /// reads the same on both and the edit check compares like with like.</summary>
    internal static string Description(string url)
        => "Imported from " + (MusicLink.TryParse(url, out var link) && link!.Service == "deezer" ? "Deezer" : "Spotify") + "\n" + url;
}
