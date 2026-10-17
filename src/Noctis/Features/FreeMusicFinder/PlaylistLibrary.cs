using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace FreeMusicFinder;

internal sealed record LocalSong(Guid Id, string Artist, string Title, string Path, TimeSpan Duration, bool Explicit = false, bool Visible = true, string AlbumArtist = "");
internal interface IPlaylistLibrary
{
    Task<IReadOnlyList<LocalSong>> SnapshotAsync();
    Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null);
}

/// <summary>The plugin kit has no playlist writer or complete library enumeration. Use the
/// desktop's live objects and native save/notification method, after checking its contract.</summary>
internal sealed class PlaylistLibrary(Func<object?>? main = null) : IPlaylistLibrary
{
    private readonly Func<object?> _main = main ?? (() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext);
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(object obj, string property) => obj.GetType().GetProperty(property)?.GetValue(obj);
    private static NotSupportedException Changed() => new("PLAYLIST_HOST_UNSUPPORTED: This Noctis version does not expose compatible local playlists. Update Noctis; your music and playlists were kept.");
    private object Main() => _main() is {} main && main.GetType().FullName == "Noctis.ViewModels.MainWindowViewModel" ? main : throw Changed();
    private static object Library(object main) => main.GetType().GetField("_library", Fields)?.GetValue(main) ?? throw Changed();

    public async Task<IReadOnlyList<LocalSong>> SnapshotAsync() => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        var library = Library(Main());
        if (Get(library, "IsScanning") is true || Get(library, "IsPublishingPartial") is true)
            throw new IOException("LIBRARY_SCANNING: Noctis is still scanning. Wait for it to finish, then check the library again; no missing downloads were guessed.");
        if (Get(library, "Tracks") is not IEnumerable visibleTracks) throw Changed();
        var visible = visibleTracks.Cast<object>().Select(t => (Guid)Get(t, "Id")!).ToHashSet();
        var tracks = Get(library, "AllTracks") as IEnumerable ?? visibleTracks;
        var songs = new List<LocalSong>();
        foreach (var track in tracks.Cast<object>())
        {
            if (Get(track, "IsRemoteStream") is true) continue;
            if (Get(track, "Id") is not Guid id || Get(track, "FilePath") is not string path ||
                Get(track, "Artist") is not string artist || Get(track, "Title") is not string title ||
                Get(track, "Duration") is not TimeSpan duration) throw Changed();
            if (Path.IsPathFullyQualified(path)) songs.Add(new(id, artist, title, path, duration, Get(track, "IsExplicit") is true, visible.Contains(id), Get(track, "AlbumArtist") as string ?? ""));
        }
        return (IReadOnlyList<LocalSong>)songs;
    });

    public async Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => ApplyCore(Main(), id, name, url, tracks, create, expected, remappedExpected));
    }

    // Internal entry point lets compatibility tests use a synthetic shell with native services.
    internal static async Task ApplyCore(object main, Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
    {
        var sidebar = Get(main, "Sidebar") ?? throw Changed();
        if (Get(sidebar, "Playlists") is not IList playlists || Get(sidebar, "PlaylistItems") is not IList navs) throw Changed();
        var collectionType = sidebar.GetType().GetProperty("Playlists")!.PropertyType;
        if (!collectionType.IsGenericType) throw Changed();
        var playlistType = collectionType.GetGenericArguments()[0];
        if (playlistType.FullName != "Noctis.Models.Playlist") throw Changed();
        var add = sidebar.GetType().GetMethods().SingleOrDefault(m => m.Name == "AddTracksToPlaylist" && m.GetParameters().Length == 2);
        var build = sidebar.GetType().GetMethod("BuildPlaylistNavItem", Fields);
        var rebuild = sidebar.GetType().GetMethod("RebuildSidebarRows", Fields | BindingFlags.Public);
        if (add is null || add.GetParameters()[0].ParameterType != typeof(Guid) || add.ReturnType != typeof(Task) || build is null || rebuild is null) throw Changed();
        var inputType = add.GetParameters()[1].ParameterType;
        if (!inputType.IsGenericType || inputType.GetGenericTypeDefinition() != typeof(IEnumerable<>) ||
            inputType.GetGenericArguments()[0].FullName != "Noctis.Models.Track") throw Changed();
        foreach (var p in new[] { "Id", "Name", "Description", "TrackIds", "SortMode", "IsSmartPlaylist" })
            if (playlistType.GetProperty(p)?.CanWrite != true) throw Changed();
        var playlist = playlists.Cast<object>().FirstOrDefault(p => Equals(Get(p, "Id"), id));
        if (playlist is null && !create) throw new InvalidOperationException("PLAYLIST_REMOVED: You deleted the imported Noctis playlist. It will not be recreated automatically.");
        bool added = playlist is null;
        playlist ??= Activator.CreateInstance(playlistType) ?? throw Changed();
        if (Get(playlist, "IsSmartPlaylist") is true) throw new InvalidOperationException("PLAYLIST_EDITED: Automatic filling stopped because this became a smart playlist.");
        if (Get(playlist, "TrackIds") is not List<Guid> original) throw Changed();
        var oldIds = original.ToList();
        if (!added && oldIds.SequenceEqual(tracks)) return; // recover a crash after the native save but before the import checkpoint
        if (!added && !Equals(Get(playlist, "Description"), ImportDescription(url)))
            throw new InvalidOperationException("PLAYLIST_EDITED: This playlist no longer identifies the original Spotify import. Automatic filling stopped to preserve your edits.");
        if (!added && !oldIds.SequenceEqual(expected) && (remappedExpected is null || !oldIds.SequenceEqual(remappedExpected))) throw new InvalidOperationException("PLAYLIST_EDITED: You changed this playlist's songs or order. Automatic filling stopped to preserve your edits.");
        // Validate every requested ID against the live library before any mutation.
        var library = Library(main);
        if ((Get(library, "AllTracks") ?? Get(library, "Tracks")) is not IEnumerable libraryTracks) throw Changed();
        var available = libraryTracks.Cast<object>().Where(t => Get(t, "IsRemoteStream") is not true &&
            Get(t, "FilePath") is string path && File.Exists(path)).Select(t => (Guid)Get(t, "Id")!).ToHashSet();
        if (tracks.Any(t => !available.Contains(t) && !oldIds.Contains(t))) throw new IOException("LIBRARY_CHANGED: A matched file moved or disappeared. Check the library again; no playlist changes were made.");
        object? nav = null;
        try
        {
            if (added)
            {
                Set(playlist, "Id", id); Set(playlist, "Name", name); Set(playlist, "Description", ImportDescription(url)); Set(playlist, "SortMode", "Manual");
            }
            Set(playlist, "TrackIds", tracks.ToList());
            if (added)
            {
                nav = build.Invoke(sidebar, [playlist]) ?? throw Changed();
                playlists.Add(playlist); navs.Add(nav); rebuild.Invoke(sidebar, null);
            }
            var empty = Activator.CreateInstance(typeof(List<>).MakeGenericType(inputType.GetGenericArguments()[0]));
            // Empty append persists the ordered IDs and refreshes the sidebar/open playlist.
            await (Task)(add.Invoke(sidebar, [id, empty]) ?? throw Changed());
        }
        catch
        {
            if (added) { playlists.Remove(playlist); if (nav is not null) navs.Remove(nav); }
            else Set(playlist, "TrackIds", oldIds);
            try { rebuild.Invoke(sidebar, null); } catch { }
            throw;
        }
    }
    private static void Set(object obj, string property, object value) => obj.GetType().GetProperty(property)!.SetValue(obj, value);
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
