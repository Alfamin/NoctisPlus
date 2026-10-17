using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class NativePlaylistTests
{
    public static void Run(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        Check.Section("installed Noctis: isolated native playlists and persistence");
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(root, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, name.Name + ".dll")) : null;
        var app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "Noctis.dll"));
        var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "Noctis.Core.dll"));
        var trackType = core.GetType("Noctis.Models.Track", true)!;
        var tracks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(trackType))!;
        var folder = Check.NewFolder("native-playlists");
        for (var i = 0; i < 3; i++)
        {
            var track = Activator.CreateInstance(trackType)!; var path = Path.Combine(folder, "Synthetic " + i + ".flac"); File.WriteAllText(path, "Synthetic marker");
            Set(track, "Id", Guid.NewGuid()); Set(track, "Title", "Song " + i); Set(track, "Artist", "Artist");
            Set(track, "Duration", TimeSpan.FromSeconds(180)); Set(track, "FilePath", path); tracks.Add(track);
        }
        var libraryType = core.GetType("Noctis.Services.ILibraryService", true)!;
        var library = DispatchProxy.Create(libraryType, typeof(Stub));
        ((Stub)library).Custom = (m, values) => m.Name switch
        {
            "get_Tracks" or "get_AllTracks" => tracks,
            "GetTrackById" => tracks.Cast<object>().FirstOrDefault(t => Equals(Get(t, "Id"), values![0])),
            _ => Stub.Unhandled,
        };
        var persistenceType = core.GetType("Noctis.Services.PersistenceService", true)!;
        var persistence = Activator.CreateInstance(persistenceType, BindingFlags.Instance | BindingFlags.NonPublic, null, [folder], null)!;
        var sidebar = Activator.CreateInstance(app.GetType("Noctis.ViewModels.SidebarViewModel", true)!, persistence, library)!;
        var shell = new Shell(sidebar, library);
        var id = Guid.NewGuid(); var ids = tracks.Cast<object>().Select(t => (Guid)Get(t, "Id")!).ToArray();
        Finish(PlaylistLibrary.ApplyCore(shell, id, "🍒", SpotifyPlaylistTests.Url(1), [ids[2], ids[0], ids[2]], true, []));
        var playlists = (IList)Get(sidebar, "Playlists")!;
        Check.Equal(1, playlists.Count, "native playlist added to live sidebar");
        Check.Equal("🍒", (string)Get(playlists[0]!, "Name")!, "native emoji name retained");
        Check.True(((List<Guid>)Get(playlists[0]!, "TrackIds")!).SequenceEqual(new[] { ids[2], ids[0], ids[2] }), "native playlist preserves exact order and duplicate positions");
        Check.Equal("Manual", (string)Get(playlists[0]!, "SortMode")!, "manual order is the default");
        Check.Equal(1, ((IList)Get(sidebar, "PlaylistItems")!).Count, "playlist sidebar navigation created");
        using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "playlists.json"))))
            Check.Equal(3, saved.RootElement[0].GetProperty("trackIds").GetArrayLength(), "real native persistence writes ordered references");
        var other = Guid.NewGuid();
        Finish(PlaylistLibrary.ApplyCore(shell, other, "Unrelated", SpotifyPlaylistTests.Url(2), [ids[1]], true, []));
        Finish(PlaylistLibrary.ApplyCore(shell, id, "🍒", SpotifyPlaylistTests.Url(1), [ids[2], ids[1], ids[0], ids[2]], false, [ids[2], ids[0], ids[2]]));
        Check.Equal(2, playlists.Count, "filling does not duplicate playlists");
        Check.True(((List<Guid>)Get(playlists[1]!, "TrackIds")!).SequenceEqual(new[] { ids[1] }), "other playlists remain unchanged");
        var before = File.ReadAllBytes(Path.Combine(folder, "playlists.json"));
        using (File.Open(Path.Combine(folder, "playlists.json.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Check.Throws<IOException>(() => Finish(PlaylistLibrary.ApplyCore(shell, id, "🍒", SpotifyPlaylistTests.Url(1), [ids[0]], false, [ids[2], ids[1], ids[0], ids[2]])), "real native save failure surfaces");
        Check.True(((List<Guid>)Get(playlists[0]!, "TrackIds")!).SequenceEqual(new[] { ids[2], ids[1], ids[0], ids[2] }), "failed native update restores live IDs");
        Check.True(before.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "playlists.json"))), "failed native update keeps saved playlists intact");
        Check.Throws<InvalidOperationException>(() => Finish(PlaylistLibrary.ApplyCore(shell, id, "🍒", SpotifyPlaylistTests.Url(1), [ids[0]], false, [])), "native manual edits cannot be overwritten");
        Check.Throws<InvalidOperationException>(() => Finish(PlaylistLibrary.ApplyCore(shell, Guid.NewGuid(), "Removed", SpotifyPlaylistTests.Url(1), [], false, [])), "deleted native playlist is not recreated");
        Check.Throws<IOException>(() => Finish(PlaylistLibrary.ApplyCore(shell, id, "🍒", SpotifyPlaylistTests.Url(1), [Guid.NewGuid()], false, [ids[2], ids[1], ids[0], ids[2]])), "unknown library IDs rejected before mutation");
    }
    private static void Finish(Task task) { Check.Until(() => task.IsCompleted, "native asynchronous playlist operation completed"); task.GetAwaiter().GetResult(); }
    private static object? Get(object obj, string p) => obj.GetType().GetProperty(p)?.GetValue(obj);
    private static void Set(object obj, string p, object value) => obj.GetType().GetProperty(p)!.SetValue(obj, value);
    private sealed class Shell
    {
        private readonly object _library;
        public object Sidebar { get; }
        public Shell(object sidebar, object library) { Sidebar = sidebar; _library = library; }
    }
    public class Stub : DispatchProxy
    {
        public static readonly object Unhandled = new();
        public Func<MethodInfo, object?[]?, object?>? Custom;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var value = Custom?.Invoke(method!, args); if (!ReferenceEquals(value, Unhandled)) return value;
            var type = method!.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                return typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [Default(type.GetGenericArguments()[0])]);
            return Default(type);
        }
        private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) :
            type.IsGenericType && type.GetGenericTypeDefinition() is {} d && (d == typeof(IReadOnlyList<>) || d == typeof(IEnumerable<>))
                ? Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0])) : null;
    }
}
