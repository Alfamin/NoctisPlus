using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Noctis.Services;
using Noctis.ViewModels;

namespace FreeMusicFinder;

/// <summary>Native library/playlist access. No private-field reflection or plugin-host
/// contract probing: the owning services validate and persist their own objects.</summary>
internal sealed class PlaylistLibrary : IPlaylistLibrary
{
    private readonly Func<ILibraryService> _library;
    private readonly Func<SidebarViewModel> _sidebar;
    private static MainWindowViewModel Main() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainWindowViewModel
        ?? throw new InvalidOperationException("LIBRARY_NOT_READY: Noctis is still opening. Try again when the library is ready.");

    public PlaylistLibrary() : this(() => Main().Library, () => Main().Sidebar) { }
    internal PlaylistLibrary(Func<ILibraryService> library, Func<SidebarViewModel> sidebar)
    { _library = library; _sidebar = sidebar; }

    public async Task<IReadOnlyList<LocalSong>> SnapshotAsync() => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        var library = _library();
        if (library.IsScanning || library.IsPublishingPartial)
            throw new IOException("LIBRARY_SCANNING: Noctis is still scanning. Wait for it to finish, then check the library again; no missing downloads were guessed.");
        var visible = library.Tracks.Select(t => t.Id).ToHashSet();
        return (IReadOnlyList<LocalSong>)library.AllTracks.Where(t => !t.IsRemoteStream && Path.IsPathFullyQualified(t.FilePath))
            .Select(t => new LocalSong(t.Id, t.Artist, t.Title, t.FilePath, t.Duration, t.IsExplicit, visible.Contains(t.Id), t.AlbumArtist, t.IsFavorite, t.Rating, t.IsDisliked)).ToArray();
    });

    public async Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => _sidebar().ApplyImportedPlaylistAsync(id, name, ImportDescription(url), tracks, create, expected, remappedExpected));
    }

    internal static string ImportDescription(string url) => "Imported from " + (MusicLink.TryParse(url, out var link) && link!.Service == "deezer" ? "Deezer" : "Spotify") + "\n" + url;
}
