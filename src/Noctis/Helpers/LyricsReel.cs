using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Owner #243: "Make a Reel" for a song, from every song menu (right-click and the three dots) and the player bar's,
/// as one static command, so no view wires its own (same idea as <see cref="LyricsBackgroundOverrides"/>). The lyrics
/// view model sets <see cref="Opener"/>: it reuses the page's lyrics for the song playing and reads any other song's
/// own lyric files.
/// </summary>
public static class LyricsReel
{
    internal static Func<Track, Task>? Opener { get; set; }

    public static ICommand OpenForTrackCommand { get; } =
        new AsyncRelayCommand<object?>(p => p is Track track && Opener is { } open ? open(track) : Task.CompletedTask);
}
