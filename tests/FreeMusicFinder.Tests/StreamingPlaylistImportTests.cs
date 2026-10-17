using System.Net;
using System.Text.Json;
using FreeMusicFinder;
using Noctis.ViewModels;

namespace FreeMusicFinder.Tests;

internal static class StreamingPlaylistImportTests
{
    internal static void Run()
    {
        Check.Section("native Spotify/Deezer playlist import and explicit missing downloads");
        var folder = Check.NewFolder("native-streaming");
        var library = new SpotifyPlaylistTests.Library();
        var owned = library.Add(Path.Combine(folder, "root-a/sub"), "Song 0");
        library.Add(Path.Combine(folder, "root-b"), "Song 2");
        var sources = new MusicSources(new TransferTests.Source("DeezLoad"), new TransferTests.Source("Music Hunters"), _ => { });
        using var downloads = new Downloads(_ => { });
        using var imports = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), library, downloads, sources.Track, new PlaylistCatalog(new Http()));
        var signIns = 0;
        var signedIn = false;
        using var vm = new StreamingPlaylistImportViewModel(imports, () => folder, () => signedIn, () => signIns++);
        vm.Links = SpotifyPlaylistTests.Url(1) + "\nhttps://www.deezer.com/en/playlist/123?utm_source=test";
        Run(vm.ImportCommand.ExecuteAsync(null));
        Check.Equal(2, vm.Imports.Count, "one paste imports both catalogs");
        Check.True(imports.Jobs.All(j => j.Created), "both become normal local playlists");
        var dz = imports.Jobs.Single(j => j.Url.StartsWith("https://www.deezer.com/"));
        Check.Equal("Deezer test", library.Names[dz.LibraryId], "Deezer playlist name retained");
        Check.True(library.Lists[dz.LibraryId].SequenceEqual(new[] { owned.Id }), "only existing recording references are used initially");
        Check.Equal(0, downloads.History.Count, "import never queues or downloads audio automatically");
        var spotify = vm.Imports.Single(r => r.Job.Url == SpotifyPlaylistTests.Url(1));
        Check.Equal(2, spotify.Missing.Count, "missing repeated positions remain visible");
        Run(vm.QueueMissingCommand.ExecuteAsync(spotify));
        Check.Equal(1, signIns, "explicit missing-download request offers sign-in when needed");
        Check.Equal(0, downloads.History.Count, "missing download is not silently queued while signed out");
        signedIn = true;
        var navigation = 0;
        vm.DownloadsRequested += () => navigation++;
        Run(vm.QueueMissingCommand.ExecuteAsync(spotify));
        Check.Equal(1, downloads.Active.Count, "same missing song listed twice is queued once");
        Check.True(downloads.Active.All(d => d.Held), "queued downloads wait for the native Downloads Start action");
        Check.Equal(1, navigation, "queueing leads to the native Downloads page");
        Run(vm.QueueMissingCommand.ExecuteAsync(vm.Imports.Single(r => r.Job.Url == dz.Url)));
        Check.Equal(2, downloads.Active.Count, "second playlist queues only its additional missing recording");
        Check.True(downloads.Active.All(d => d.Held), "all imports use the same held queue");
        Check.True(PlaylistLibrary.ImportDescription(dz.Url).StartsWith("Imported from Deezer\n"), "native source description identifies Deezer");
        Check.True(PlaylistLibrary.ImportDescription(SpotifyPlaylistTests.Url(1)).StartsWith("Imported from Spotify\n"), "existing Spotify descriptions stay compatible");
        var added = library.Add(folder, "Song 1");
        Run(vm.CheckLibraryCommand.ExecuteAsync(null));
        Check.True(library.Lists[spotify.Job.LibraryId].SequenceEqual(new[] { owned.Id, added.Id, library.Songs[1].Id, added.Id }),
            "new local recording fills its exact repeated source positions");

        vm.Links = "https://attacker.invalid/playlist/123";
        Run(vm.ImportCommand.ExecuteAsync(null));
        Check.True(vm.Error.Contains("Spotify or Deezer") && imports.Jobs.Count == 2, "bad paste reports a precise error without changing imports");

        using var limited = new SpotifyPlaylists(Path.Combine(folder, "limited-imports.json"), library, downloads, sources.Track,
            new PlaylistCatalog(new Http { RateSpotify = true }));
        Run(limited.ImportAsync(SpotifyPlaylistTests.Url(1) + "\nhttps://www.deezer.com/playlist/123", default));
        Check.True(!limited.Jobs[0].Created && limited.Jobs[0].MetadataRetryAt > DateTimeOffset.UtcNow,
            "Spotify rate limit keeps an explicit saved retry state");
        Check.True(limited.Jobs[1].Created, "Spotify cooldown does not block a Deezer playlist in the same paste");
        var originalId = spotify.Job.LibraryId;
        Run(vm.RemoveImportCommand.ExecuteAsync(spotify));
        Check.True(imports.Jobs.Count == 1 && library.Lists.ContainsKey(originalId), "removing import management keeps the normal native playlist");
    }

    private static void Run(Task task)
    {
        Check.Until(() => task.IsCompleted, "native import operation completes");
        task.GetAwaiter().GetResult();
    }

    private sealed class Http : HttpMessageHandler
    {
        public bool RateSpotify;
        private readonly HttpMessageInvoker _spotify = new(new SpotifyPlaylistTests.Http());
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "api.deezer.com") return RateSpotify
                ? Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)) : _spotify.SendAsync(request, ct);
            var data = new
            {
                title = "Deezer test", nb_tracks = 3,
                tracks = new { data = new[] { 0, 4, 1 }.Select(n => new { id = n + 100, title = "Song " + n, artist = new { name = "Artist" }, duration = 180, explicit_lyrics = true }) },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data)) });
        }
        protected override void Dispose(bool disposing) { if (disposing) _spotify.Dispose(); base.Dispose(disposing); }
    }
}
