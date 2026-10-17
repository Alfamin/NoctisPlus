using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;

namespace FreeMusicFinder;

/// <summary>View models only. Catalog albums are never added to ILibraryService, persisted,
/// scanned, or handed to playback. Every playback list contains real local Track objects.</summary>
internal static class NativeCatalogBridge
{
    internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static object? Get(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
    internal static void Set(object value, string name, object? data) => (value.GetType().GetProperty(name) ?? throw Unsupported(name)).SetValue(value, data);
    internal static Exception Unsupported(string detail) => new NotSupportedException("CATALOG_HOST_UNSUPPORTED: This Noctis layout is not compatible (" + detail + "). Your local pages and music were kept.");
    internal static Guid Id(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes("FreeMusicFinder/catalog/" + key)).AsSpan(0, 16));
    internal static IList List(Type type, IEnumerable<object> values)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!; foreach (var value in values) list.Add(value); return list;
    }
    private static FieldInfo Field(object vm, string name) => vm.GetType().GetField(name, Private) ?? throw Unsupported(name);
    internal static IReadOnlyList<object> Values(object? values) => values is IEnumerable sequence ? sequence.Cast<object>().ToArray() : [];
    internal static Dictionary<object, CatalogRelease> ApplyArtist(object vm, CatalogProfile profile, Dictionary<object, CatalogRelease>? previous = null, bool consolidated = false)
    {
        if (vm.GetType().FullName != "Noctis.ViewModels.ArtistDetailViewModel") throw Unsupported("artist view model");
        var releasesField = Field(vm, "_allReleases"); var appearsField = Field(vm, "_allAppearsOn");
        var apply = vm.GetType().GetMethod("ApplyLists", Private) ?? throw Unsupported("artist release filtering");
        var type = releasesField.FieldType.GenericTypeArguments.Single();
        if (type.FullName != "Noctis.Models.Album") throw Unsupported("album model");
        var existing = Values(releasesField.GetValue(vm)); var appears = Values(appearsField.GetValue(vm));
        var map = new Dictionary<object, CatalogRelease>(ReferenceEqualityComparer.Instance);
        var native = new List<object>(); var featured = new List<object>();
        var releases = consolidated ? profile.Releases : CatalogEditions.Consolidate(profile.Releases);
        var ownedByEdition = existing.Concat(appears).GroupBy(a => (CatalogEditions.Key(Get(a, "Name") as string ?? ""), BotText.Key(Get(a, "Artist") as string))).ToDictionary(g => g.Key, g => g.First());
        var represented = releases.Select(r => (CatalogEditions.Key(r.Title), BotText.Key(r.Artist.Name))).ToHashSet();
        var reusable = previous?.ToDictionary(p => p.Value.Id, p => p);
        foreach (var release in releases)
        {
            ownedByEdition.TryGetValue((CatalogEditions.Key(release.Title), BotText.Key(release.Artist.Name)), out var owned);
            var album = reusable is not null && reusable.TryGetValue(release.Id, out var found) && found.Value == release ? found.Key : Album(type, release, owned);
            map.Add(album, release);
            (release.Artist.Id == profile.Artist.Id ? native : featured).Add(album);
        }
        // Same-name local releases are represented by a catalog tile; local-only/unreleased albums stay.
        bool Represented(object a) => represented.Contains((CatalogEditions.Key(Get(a, "Name") as string ?? ""), BotText.Key(Get(a, "Artist") as string)));
        native.AddRange(existing.Where(a => !Represented(a))); featured.AddRange(appears.Where(a => !Represented(a)));
        var oldReleases = releasesField.GetValue(vm); var oldAppears = appearsField.GetValue(vm); var oldLatest = Get(vm, "LatestRelease");
        try
        {
            releasesField.SetValue(vm, List(type, native.OrderByDescending(a => Get(a, "Year"))));
            appearsField.SetValue(vm, List(type, featured));
            apply.Invoke(vm, null);
            var latest = releases.FirstOrDefault(r => r.Artist.Id == profile.Artist.Id);
            if (latest is not null) Set(vm, "LatestRelease", map.First(m => m.Value.Id == latest.Id).Key);
            // Native header commands enumerate _allReleases directly. Only the rendered
            // collections may retain catalog tiles; command inputs must remain local.
            releasesField.SetValue(vm, oldReleases); appearsField.SetValue(vm, oldAppears);
        }
        catch
        {
            releasesField.SetValue(vm, oldReleases); appearsField.SetValue(vm, oldAppears); Set(vm, "LatestRelease", oldLatest);
            try { apply.Invoke(vm, null); } catch { } throw;
        }
        return map;
    }
    internal static void RestoreArtist(object vm)
    { vm.GetType().GetMethod("Rebuild", Private)?.Invoke(vm, null); }

    internal static object Album(Type albumType, CatalogRelease release, object? owned)
    {
        var album = Activator.CreateInstance(albumType)!;
        Set(album, "Id", Id(release.Id)); Set(album, "Name", release.Title); Set(album, "Artist", release.Artist.Name);
        Set(album, "Year", int.TryParse(release.Date.Split('-')[0], out var year) ? year : 0); Set(album, "TrackCount", release.Count);
        if (owned is not null) Set(album, "ArtworkPath", Get(owned, "ArtworkPath"));
        var trackType = albumType.GetProperty("Tracks")!.PropertyType.GenericTypeArguments.Single();
        var marker = Activator.CreateInstance(trackType)!;
        Set(marker, "Id", Id("classification:" + release.Id)); Set(marker, "Title", release.Title); Set(marker, "Artist", release.Artist.Name);
        Set(marker, "ReleaseDate", release.Date); Set(marker, "TrackCount", release.Count);
        Set(marker, "IsExplicit", release.Explicit == true);
        Set(marker, "IsReleaseTypeOverridden", true);
        var kindType = trackType.GetProperty("ReleaseType")!.PropertyType;
        var kind = release.Kind switch { "single" => "Single", "ep" => "EP", "compile" or "compilation" => "Compilation", "live" => "Live", _ => "Album" };
        Set(marker, "ReleaseType", Enum.Parse(kindType, kind));
        // Classification marker has no file and lives solely in the artist's display model.
        // OpenAlbum creates another model containing only verified local tracks.
        Set(album, "Tracks", List(trackType, [marker])); return album;
    }
    internal static IReadOnlyList<object> LocalTracks(object main) => Values(GetLibrary(main).GetType().GetProperty("AllTracks")?.GetValue(GetLibrary(main)) ?? Get(GetLibrary(main), "Tracks"));
    internal static IReadOnlyList<string> ArtistAlbums(object main, string name)
    {
        if (Get(main, "CurrentView") is {} view && BotText.Key(Get(view, "ArtistName") as string) == BotText.Key(name))
            return Values(view.GetType().GetField("_allReleases", Private)?.GetValue(view)).OrderByDescending(a => Values(Get(a, "Tracks")).Count).Select(a => Get(a, "Name") as string ?? "").ToArray();
        return Values(Get(GetLibrary(main), "Albums")).Where(a => BotText.Key(Get(a, "Artist") as string) == BotText.Key(name))
            .OrderByDescending(a => Values(Get(a, "Tracks")).Count).Select(a => Get(a, "Name") as string ?? "").ToArray();
    }
    private static object GetLibrary(object main) => main.GetType().GetField("_library", Private)?.GetValue(main) ?? throw Unsupported("local library");
    internal static object OpenAlbum(object main, object tile, CatalogAlbum metadata, IReadOnlyDictionary<string, DiscoveryMatch> matches)
    {
        var album = AlbumForPage(main, tile, metadata, matches);
        var open = AlbumNavigation(main.GetType(), album.GetType());
        var args = open.GetParameters().Select(p => p.DefaultValue).ToArray(); args[0] = album; open.Invoke(main, args);
        var vm = Get(main, "CurrentView") ?? throw Unsupported("native album page");
        AlbumArtwork(vm, Get(album, "ArtworkPath") as string); return vm;
    }
    internal static object AlbumForPage(object main, object tile, CatalogAlbum metadata, IReadOnlyDictionary<string, DiscoveryMatch> matches)
    {
        var type = tile.GetType(); var album = Activator.CreateInstance(type)!;
        foreach (var name in new[] { "Id", "Name", "Artist", "Year", "TrackCount", "ArtworkPath" }) Set(album, name, Get(tile, name));
        var tracks = LocalTracks(main);
        var originals = Values(Get(GetLibrary(main), "Albums")).Where(a => CatalogEditions.Matches(metadata.Release, Get(a, "Name") as string ?? "", Get(a, "Artist") as string ?? "")).SelectMany(a => Values(Get(a, "Tracks")));
        var owned = metadata.Songs.Select(s => matches[s.Id].Local?.Id).Where(id => id is not null).Distinct().Select(id => tracks.FirstOrDefault(t => Equals(Get(t, "Id"), id))).Where(t => t is not null).Cast<object>()
            .Concat(originals).Where(t => Get(t, "FilePath") is string path && File.Exists(path)).DistinctBy(t => Get(t, "Id")).ToArray();
        var trackType = type.GetProperty("Tracks")!.PropertyType.GenericTypeArguments.Single(); Set(album, "Tracks", List(trackType, owned));
        Set(album, "TotalDuration", TimeSpan.FromSeconds(metadata.Songs.Sum(s => s.Seconds ?? 0)));
        return album;
    }
    internal static bool AlbumArtwork(object vm, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        Set(Get(vm, "Album")!, "ArtworkPath", path);
        // AlbumDetailView binds to HeaderArtPath, not Album.ArtworkPath. Its generated
        // setter also refreshes the native cover viewer, tint and property notifications.
        if (!Equals(Get(vm, "HeaderArtPath"), path)) Set(vm, "HeaderArtPath", path);
        return true;
    }
    internal static MethodInfo AlbumNavigation(Type shell, Type album) => shell.GetMethods(Private | BindingFlags.Public).SingleOrDefault(m => m.Name == "OpenAlbumDetail" && m.GetParameters().FirstOrDefault()?.ParameterType == album && m.GetParameters().Skip(1).All(p => p.HasDefaultValue)) ?? throw Unsupported("native album navigation");
    internal static void OpenArtist(object main, string name)
    {
        var method = main.GetType().GetMethod("OpenArtistDiscography", Private, [typeof(string)]) ?? throw Unsupported("native artist navigation"); method.Invoke(main, [name]);
    }
    internal static void Play(object main, object vm, LocalSong song)
    {
        var player = Get(main, "Player") ?? throw Unsupported("player"); var tracks = Values(Get(vm, "Tracks"));
        var at = tracks.ToList().FindIndex(t => Equals(Get(t, "Id"), song.Id)); if (at < 0 || !File.Exists(song.Path)) throw new IOException("The local song moved. Wait for Noctis's library scan and try again.");
        var type = tracks[0].GetType(); var method = player.GetType().GetMethods().SingleOrDefault(m => m.Name == "PlayFromRow" && m.GetParameters().Length == 2) ?? throw Unsupported("native playback");
        method.Invoke(player, [List(type, tracks), at]);
    }
}

/// <summary>Only this transient album view sees a catalog album alongside real library albums.
/// Calls and event subscriptions otherwise forward to the original service. No global replacement.</summary>
public class CatalogAlbumLibrary : DispatchProxy
{
    internal object Original = null!;
    internal object Album = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "get_Albums")
        {
            var values = NativeCatalogBridge.Values(targetMethod.Invoke(Original, args)); var type = Album.GetType();
            return NativeCatalogBridge.List(type, new[] { Album }.Concat(values.Where(a => !Equals(NativeCatalogBridge.Get(a, "Id"), NativeCatalogBridge.Get(Album, "Id")))));
        }
        try { return targetMethod.Invoke(Original, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    internal static object Attach(object vm)
    {
        var field = vm.GetType().GetField("_library", NativeCatalogBridge.Private) ?? throw NativeCatalogBridge.Unsupported("album library view");
        var original = field.GetValue(vm)!; if (original is CatalogAlbumLibrary) return original;
        var proxy = DispatchProxy.Create(field.FieldType, typeof(CatalogAlbumLibrary));
        ((CatalogAlbumLibrary)proxy).Original = original; ((CatalogAlbumLibrary)proxy).Album = NativeCatalogBridge.Get(vm, "Album")!; field.SetValue(vm, proxy); return original;
    }
}
