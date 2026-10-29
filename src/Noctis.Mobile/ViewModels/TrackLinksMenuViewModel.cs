using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Now Playing's artist line, Apple Music's: a small glass bubble growing out of the tapped
/// artist with two rows, Go to Album (the album's name under it) and Go to Artist (the song's
/// artists under it). The rows act as the ⋯ menu's Go to Album and Go to Artist do: the artist
/// row opens the artist the song is filed under (Track.GroupingArtist, the Artists list's key)
/// and shows every credited name, as the ⋯ menu does. Each row closes the menu, and the page it
/// opens closes the player (ShellViewModel.Navigate).
/// </summary>
public sealed partial class TrackLinksMenuViewModel(ShellViewModel shell, Track track)
{
    public Track Track { get; } = track;

    /// <summary>Go to Album's second line.</summary>
    public string? AlbumLine => Track.Album;

    /// <summary>Go to Artist's second line: every credited artist.</summary>
    public string? ArtistLine => Track.Artist;

    [RelayCommand]
    private void GoToAlbum()
    {
        var album = shell.Library.Service.GetAlbumById(Track.AlbumId);
        shell.CloseSheet();
        if (album != null) shell.OpenAlbumCommand.Execute(album);
    }

    [RelayCommand]
    private void GoToArtist()
    {
        var name = Track.GroupingArtist;
        shell.CloseSheet();
        if (!string.IsNullOrWhiteSpace(name)) shell.OpenArtistCommand.Execute(name);
    }
}
