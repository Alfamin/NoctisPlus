using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

public sealed partial class ShellViewModel
{
    /// <summary>The host's way to delete a song's file (Android overrides it for document addresses).</summary>
    public ISongFileRemover SongFiles { get; init; } = new PlainSongFileRemover();

    /// <summary>A song that is stored on this phone: not one of the computer's streamed songs, not an online
    /// stand-in that is only waiting to be fetched.</summary>
    internal static bool CanRemoveFromPhone(Track track) =>
        !track.IsRemoteStream && !track.IsPendingStream && track.FilePath.Length > 0;

    /// <summary>
    /// #306: Remove from Phone, in a song's menu and in Select mode. Asks first (the files are deleted); the songs
    /// leave the library, every playlist and the queue. <paramref name="done"/> runs once the owner has chosen.
    /// </summary>
    public void AskRemoveSongs(IReadOnlyList<Track> tracks, Action? done = null)
    {
        var songs = tracks.Where(CanRemoveFromPhone).DistinctBy(t => t.Id).ToList();
        if (songs.Count == 0)
        {
            Player.Notice = "These songs are not stored on this phone";
            done?.Invoke();
            return;
        }
        var title = songs.Count == 1 ? $"Remove \"{songs[0].Title}\" from this phone?" : $"Remove {songs.Count} songs from this phone?";
        ShowPrompt(new PromptViewModel(this, title,
            songs.Count == 1 ? "The song and its file are deleted from the phone. This cannot be undone."
                             : "The songs and their files are deleted from the phone. This cannot be undone.",
            new[]
            {
                new PromptAction("Remove from Phone", async _ => { await RemoveSongsAsync(songs); done?.Invoke(); }, isDestructive: true),
            }));
    }

    /// <summary>Deletes the files and takes the songs out of the library, the playlists and the queue. A file the
    /// phone may not delete (a folder it only reads) stays, and its song is hidden instead (Settings › Hidden shows
    /// it again). Returns how many files were deleted and how many stay.</summary>
    internal async Task<(int Deleted, int Kept)> RemoveSongsAsync(IReadOnlyList<Track> songs)
    {
        var gone = new List<Track>();
        var kept = new List<Track>();
        foreach (var song in songs)
        {
            var ok = false;
            try { ok = await Task.Run(() => SongFiles.TryDelete(song.FilePath)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("Library", $"Remove from phone failed: {ex.GetType().Name}"); }
            (ok ? gone : kept).Add(song);
        }
        if (gone.Count > 0)
        {
            var ids = gone.Select(t => t.Id).ToHashSet();
            foreach (var playlist in Library.Playlists.Where(p => !p.IsSmartPlaylist && p.TrackIds.Any(ids.Contains)).ToList())
                await Library.RemoveFromPlaylistAsync(playlist, gone.Where(t => playlist.TrackIds.Contains(t.Id)).ToList());
            try { await Library.Service.RemoveTracksAsync(ids); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("Library", $"Remove from phone: library not updated ({ex.GetType().Name})"); }
        }
        if (kept.Count > 0) await SetHiddenAsync(kept.Select(t => t.FilePath).ToList(), hidden: true);
        Player.Notice = (gone.Count, kept.Count) switch
        {
            (var d, 0) => d == 1 ? "Removed from this phone" : $"{d} songs removed from this phone",
            (0, var k) => k == 1 ? "Android does not let Noctis delete this file: the song is hidden (Settings › Hidden)"
                                 : $"Android does not let Noctis delete these files: the songs are hidden (Settings › Hidden)",
            (var d, var k) => $"{d} removed from this phone; {k} could not be deleted and are hidden (Settings › Hidden)",
        };
        return (gone.Count, kept.Count);
    }
}