using System;
using System.Collections.Generic;
using System.Linq;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// A page view whose list can hold a multi-selection (Ctrl+click / Ctrl+A). Window-level
/// shortcuts that act on "the selection" (GitHub #131: ReplayGain) read it through this
/// instead of reaching into each page. Albums and favorites expand to their tracks.
/// </summary>
public interface ITrackSelectionSource
{
    IReadOnlyList<Track> GetSelectedTracks();
}

public static class TrackSelection
{
    /// <summary>Album selections expand to their tracks, album by album.</summary>
    public static List<Track> FromAlbums(IEnumerable<Album> albums)
    {
        var tracks = new List<Track>();
        foreach (var album in albums)
            if (album.Tracks is { } albumTracks) tracks.AddRange(albumTracks);
        return tracks;
    }

    /// <summary>
    /// What the ReplayGain shortcut scans, first non-empty wins: the queue panel's
    /// selection (when focus is in it), the current page's selection, then the playing
    /// track. Empty means there is nothing to scan and the key is left alone.
    /// Streams and disc tracks (an MRL such as <c>http://</c> or <c>cdda://</c>, not a file)
    /// are dropped from the playing-track fallback: the scanner reads files.
    /// </summary>
    public static IReadOnlyList<Track> ForReplayGain(
        IReadOnlyList<Track>? queueSelection,
        IReadOnlyList<Track>? pageSelection,
        Track? playing)
    {
        if (queueSelection is { Count: > 0 }) return queueSelection;
        if (pageSelection is { Count: > 0 }) return pageSelection;
        if (playing is not null && IsFile(playing.FilePath)) return new[] { playing };
        return Array.Empty<Track>();
    }

    private static bool IsFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Contains("://", StringComparison.Ordinal);

    /// <summary>Queue-row indices (as <see cref="QueueRowSelection{T}.Snapshot"/> returns
    /// them) to tracks, skipping indices that no longer exist.</summary>
    public static List<Track> FromIndices(IReadOnlyList<Track> rows, IEnumerable<int> indices) =>
        indices.Where(i => i >= 0 && i < rows.Count).OrderBy(i => i).Select(i => rows[i]).ToList();
}
