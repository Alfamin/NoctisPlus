using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Noctis.Features.WordLyrics;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>The same native WordLyrics actions on library rows and every player surface.</summary>
internal static class WordLyricsMenu
{
    internal static void Bind(MenuItem timing, MenuItem ignore, Track? track, WordLyricsService? service = null)
    {
        service ??= WordLyricsFeature.Current;
        timing.IsVisible = ignore.IsVisible = service?.Enabled == true;
        timing.IsEnabled = ignore.IsEnabled = track is { IsRemoteStream: false, FilePath.Length: > 0 };
        timing.Header = "Word timing / repair…";
        ignore.Header = track is not null && service?.IsIgnored(track.FilePath) == true
            ? "Enable WordLyrics for this song" : "Ignore WordLyrics for this song";
        timing.Command = new RelayCommand<Track>(song => { if (song is not null) service?.Show(track: song); });
        ignore.Command = new RelayCommand<Track>(song =>
        {
            if (song is not null && service is not null) service.SetIgnored(song.FilePath, !service.IsIgnored(song.FilePath));
        });
        timing.CommandParameter = ignore.CommandParameter = track;
    }
}
