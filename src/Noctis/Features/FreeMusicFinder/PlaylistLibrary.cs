using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Noctis.Services;
using Noctis.ViewModels;

namespace FreeMusicFinder;

internal sealed record LocalSong(Guid Id, string Artist, string Title, string Path, TimeSpan Duration, bool Explicit = false, bool Visible = true, string AlbumArtist = "");
internal interface IPlaylistLibrary
{
    Task<IReadOnlyList<LocalSong>> SnapshotAsync();
    Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null);
}

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
            .Select(t => new LocalSong(t.Id, t.Artist, t.Title, t.FilePath, t.Duration, t.IsExplicit, visible.Contains(t.Id), t.AlbumArtist)).ToArray();
    });

    public async Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => _sidebar().ApplyImportedPlaylistAsync(id, name, ImportDescription(url), tracks, create, expected, remappedExpected));
    }

    internal static string ImportDescription(string url) => "Imported from " + (MusicLink.TryParse(url, out var link) && link!.Service == "deezer" ? "Deezer" : "Spotify") + "\n" + url;
}
internal sealed class PlaylistMatcher
{
    private readonly ILookup<string, LocalSong> _titles;
    public PlaylistMatcher(IEnumerable<LocalSong> songs)
    {
        _titles = songs.Where(s => File.Exists(s.Path)).OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(s => Keys(s.Title).Select(key => (key, s))).ToLookup(pair => pair.key, pair => pair.s);
    }
    // Looked up under the title without its brackets, and under its name alone: Spotify writes
    // "Song - Remix" and "Song (with Guest)" where the files say "Song (Remix)" and "Song".
    private static IEnumerable<string> Keys(string title) => new[] { BotText.Key(BotText.Plain(title)), RecordingMatch.TitleKey(title) }.Distinct();
    private IEnumerable<LocalSong> Named(string title) => Keys(title).SelectMany(key => _titles[key]).Distinct();
    private static TimeSpan? Length(LocalSong s) => s.Duration > TimeSpan.Zero ? s.Duration : null;
    private static bool ByArtist(MusicRequest request, LocalSong s, bool lengths = true) => RecordingMatch.Fits(request, s.Artist, s.Title, lengths ? Length(s) : null);
    // A song put out under another name (the uploader of a leak, a compilation) often keeps that name
    // as its album artist. That counts only together with the length.
    private static bool ByAlbumArtist(MusicRequest request, LocalSong s) => s.AlbumArtist.Length > 0 && request.Duration is not null && Length(s) is {} length
        && RecordingMatch.Fits(request, s.AlbumArtist, s.Title, length);
    public LocalSong? Find(MusicRequest request) => Named(request.Title)
        .Where(s => ByArtist(request, s) || ByAlbumArtist(request, s))
        .OrderByDescending(s => s.Visible)
        .ThenByDescending(s => ByArtist(request, s))
        .ThenByDescending(s => request.Explicit == true && s.Explicit)
        .ThenByDescending(s => BotText.Key(request.Title) == BotText.Key(s.Title))
        .ThenBy(s => request.Seconds is {} seconds ? Math.Abs(s.Duration.TotalSeconds - seconds) : 0)
        .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    public string MissingReason(MusicRequest request)
    {
        var named = Named(request.Title).ToArray();
        var candidates = named.Where(s => ByArtist(request with { Seconds = null }, s, lengths: false)).ToArray();
        if (request.Seconds is {} seconds && candidates.Where(s => s.Duration > TimeSpan.Zero).OrderBy(s => Math.Abs(s.Duration.TotalSeconds - seconds)).FirstOrDefault() is {} near)
            return $"Different recording length: Spotify {TimeSpan.FromSeconds(seconds):m\\:ss}, local {near.Duration:m\\:ss}. A different version will not be substituted.";
        // The same title and length under a name Spotify does not give: likely the same song, but not known to be.
        if (request.Duration is not null && named.FirstOrDefault(s => Length(s) is not null && ByArtist(request with { Artist = s.Artist }, s)) is {} other)
            return $"Same title and length in your library, but by {other.Artist}. It is not taken for the same recording without a shared artist.";
        return "No matching local recording (artist, title and version checked)";
    }
}

