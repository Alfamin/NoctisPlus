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
        var available = library.AllTracks.Where(t => IsLocalFile(t) && Check(t.FilePath)).Select(t => t.Id).ToHashSet();
        return onUi(() => playlists.ApplyImportedPlaylistAsync(id, name, Description(url), tracks, create, expected, remappedExpected, available));
    }

    // Owner #307: Search builds a matcher that asks "does this file still exist" of every library song, and on the phone each
    // answer is a call into Android's document provider (a content:// song): ~2,000 of them held the search up every time the
    // library changed or two minutes passed. An answer is kept for 30 minutes, and the library fills them in beforehand (Warm).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Found, long At)> _found = new(StringComparer.Ordinal);
    private static readonly long KeepMs = (long)TimeSpan.FromMinutes(30).TotalMilliseconds;

    public bool Exists(string path)
    {
        var now = Environment.TickCount64;
        if (_found.TryGetValue(path, out var known) && now - known.At < KeepMs) return known.Found;
        var found = Check(path);
        _found[path] = (found, now);
        return found;
    }

    private bool Check(string path) => accessible?.Invoke(path) ?? File.Exists(path);

    /// <summary>Asks about the library's songs that have no fresh answer yet, four at a time, off the UI thread.</summary>
    internal void Warm()
    {
        if (library.IsScanning || library.IsPublishingPartial) return;
        var now = Environment.TickCount64;
        var paths = library.AllTracks.Where(IsLocalFile).Select(t => t.FilePath)
            .Where(p => !(_found.TryGetValue(p, out var known) && now - known.At < KeepMs)).ToArray();
        if (paths.Length == 0) return;
        try { Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 4 }, p => _found[p] = (Check(p), Environment.TickCount64)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("Finder", $"Library file check stopped: {ex.GetType().Name}"); }
    }

    private static bool IsLocalFile(Noctis.Models.Track t) => !t.IsRemoteStream &&
        (Path.IsPathFullyQualified(t.FilePath) || t.FilePath.StartsWith("content://", StringComparison.Ordinal));

    /// <summary>Same text as the desktop's (PlaylistLibrary.ImportDescription), so a shared playlists.json
    /// reads the same on both and the edit check compares like with like.</summary>
    internal static string Description(string url)
        => "Imported from " + (MusicLink.TryParse(url, out var link) && link!.Service == "deezer" ? "Deezer" : "Spotify") + "\n" + url;
}
