using Noctis.Models;

namespace Noctis.ViewModels;

public partial class SidebarViewModel
{
    /// <summary>Imports ordered references, including repeated positions, through the
    /// native playlist owner. A later fill never overwrites a user's playlist edits.</summary>
    internal async Task ApplyImportedPlaylistAsync(Guid id, string name, string description,
        IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == id);
        if (playlist is null && !create)
            throw new InvalidOperationException("PLAYLIST_REMOVED: You deleted the imported Noctis playlist. It will not be recreated automatically.");
        var added = playlist is null;
        playlist ??= new Playlist { Id = id, Name = name, Description = description, SortMode = "Manual" };
        if (playlist.IsSmartPlaylist)
            throw new InvalidOperationException("PLAYLIST_EDITED: Automatic filling stopped because this became a smart playlist.");
        var oldIds = playlist.TrackIds.ToList();
        if (!added && oldIds.SequenceEqual(tracks)) return; // native save succeeded before an import checkpoint failed
        if (!added && (playlist.Description != description || !oldIds.SequenceEqual(expected) &&
            (remappedExpected is null || !oldIds.SequenceEqual(remappedExpected))))
            throw new InvalidOperationException("PLAYLIST_EDITED: You changed this playlist or its order. Automatic filling stopped to preserve your edits.");
        if (_library.IsScanning || _library.IsPublishingPartial)
            throw new IOException("LIBRARY_SCANNING: Noctis is still scanning. Wait for it to finish, then check the library again.");
        var available = _library.AllTracks.Where(t => !t.IsRemoteStream && File.Exists(t.FilePath)).Select(t => t.Id).ToHashSet();
        if (tracks.Any(t => !available.Contains(t) && !oldIds.Contains(t)))
            throw new IOException("LIBRARY_CHANGED: A matched file moved or disappeared. Check the library again; no playlist changes were made.");
        var oldModified = playlist.ModifiedAt;
        var nav = PlaylistItems.FirstOrDefault(n => n.PlaylistId == id);
        try
        {
            playlist.TrackIds = tracks.ToList();
            playlist.ModifiedAt = DateTime.UtcNow;
            if (added)
            {
                nav = BuildPlaylistNavItem(playlist);
                Playlists.Add(playlist); PlaylistItems.Add(nav); RebuildSidebarRows();
            }
            await _persistence.SavePlaylistsAsync(Playlists.ToList());
        }
        catch
        {
            if (added) { Playlists.Remove(playlist); if (nav is not null) PlaylistItems.Remove(nav); }
            else { playlist.TrackIds = oldIds; playlist.ModifiedAt = oldModified; }
            RebuildSidebarRows();
            throw;
        }
        RefreshPlaylistCounts();
        PlaylistTracksChanged?.Invoke(this, id);
    }

    /// <summary>
    /// Owner #69: a found playlist saved from Search keeps its own cover, copied into the app's playlist covers like
    /// one picked in Edit Playlist. A cover the user already chose is never replaced.
    /// </summary>
    internal async Task KeepFoundCoverAsync(Guid id, string image)
    {
        if (Playlists.FirstOrDefault(p => p.Id == id) is not { } playlist || !string.IsNullOrEmpty(playlist.CoverArtPath) || !File.Exists(image)) return;
        var covers = Path.Combine(Helpers.AppPaths.DataRoot, "playlist_covers");
        Directory.CreateDirectory(covers);
        var path = Path.Combine(covers, $"{id}.jpg");
        File.Copy(image, path, overwrite: true);
        playlist.CoverArtPath = path;
        if (PlaylistItems.FirstOrDefault(n => n.PlaylistId == id) is { } nav) nav.CoverArtPath = path;
        await _persistence.SavePlaylistsAsync(Playlists.ToList());
        PlaylistEdited?.Invoke(this, id);
    }
}
