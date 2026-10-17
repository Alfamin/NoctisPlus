using System.Net;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class SpotifyPlaylistTests
{
    internal static string Id(int n) => n.ToString("D22");
    internal static string Url(int n) => "https://open.spotify.com/playlist/" + Id(n);
    internal sealed class Library : IPlaylistLibrary
    {
        public List<LocalSong> Songs = [];
        public Dictionary<Guid, List<Guid>> Lists = new();
        public Dictionary<Guid, string> Names = new();
        public bool Denied;
        public int Saves;
        public Exception? ReadError;
        public Task<IReadOnlyList<LocalSong>> SnapshotAsync() => ReadError is {} error ? Task.FromException<IReadOnlyList<LocalSong>>(error) : Task.FromResult<IReadOnlyList<LocalSong>>(Songs.ToArray());
        public Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
        {
            if (Denied) throw new UnauthorizedAccessException("Synthetic denied");
            if (!Lists.ContainsKey(id) && !create) throw new InvalidOperationException("PLAYLIST_REMOVED: deleted by user");
            if (Lists.TryGetValue(id, out var same) && same.SequenceEqual(tracks)) return Task.CompletedTask;
            if (Lists.TryGetValue(id, out var old) && !old.SequenceEqual(expected) && !old.SequenceEqual(tracks) && (remappedExpected is null || !old.SequenceEqual(remappedExpected))) throw new InvalidOperationException("PLAYLIST_EDITED: user reordered");
            Lists[id] = tracks.ToList(); Names[id] = name; Saves++; return Task.CompletedTask;
        }
        public LocalSong Add(string folder, string title, string artist = "Artist", double seconds = 180)
        {
            Directory.CreateDirectory(folder); var path = Path.Combine(folder, Guid.NewGuid() + ".flac"); File.WriteAllText(path, "Synthetic file marker");
            var song = new LocalSong(Guid.NewGuid(), artist, title, path, TimeSpan.FromSeconds(seconds)); Songs.Add(song); return song;
        }
    }
    internal sealed class Http : HttpMessageHandler
    {
        public int Calls;
        public int Count = 251;
        public readonly Dictionary<int, int[]> Lists = new() { [1] = [0, 1, 2, 1], [2] = [2, 1, 3], [3] = [4] };
        public int? Broken;
        public bool PreviewOnly, RateLimit;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; ct.ThrowIfCancellationRequested();
            var number = int.Parse(request.RequestUri!.Segments.Last());
            if (RateLimit) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
            if (Broken == number) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            int[] indices = number == 9 ? Enumerable.Range(0, Count).ToArray() : Lists.GetValueOrDefault(number, []);
            if (request.RequestUri.Host == "spclient.wg.spotify.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { length = indices.Length, contents = new { items = indices.Select(i => new { uri = "spotify:track:" + Id(i) }) } })) });
            var token = PreviewOnly ? "" : "synthetic-test-token";
            var html = "<script id=\"__NEXT_DATA__\">" + JsonSerializer.Serialize(new { props = new { pageProps = new { state = new { settings = new { session = new { accessToken = token } }, data = new { entity = new { name = number == 1 ? "Feeling Shit" : number == 2 ? "🍒" : "Gym", trackList = indices.Select(i => new { uri = "spotify:track:" + Id(i), title = "Song " + i, subtitle = "Artist", duration = 180000, isExplicit = true }) } } } } } }) + "</script>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }
    public static async Task RunAsync()
    {
        Links(); Matching(); await Imports(); await OnePlaylist(); await Failures(); await HeldQueue(); await UnavailableAndMoved();
    }
    private static async Task OnePlaylist()
    {
        Check.Section("queueing the missing songs of one playlist only");
        var folder = Check.NewFolder("playlist-one"); var library = new Library();
        library.Add(folder, "Song 0"); library.Add(folder, "Song 2");
        var source = new TransferTests.Source("DeezLoad"); var sources = new MusicSources(source, new TransferTests.Source("Music Hunters"), _ => { });
        using var downloads = new Downloads(_ => { }, Path.Combine(folder, "queue.json"), sources.Track);
        using var imports = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), library, downloads, sources.Track, new PlaylistCatalog(new Http()));
        await imports.ImportAsync(Url(1) + "\n" + Url(2), default);
        var first = imports.Jobs[0]; var second = imports.Jobs[1]; // missing: "Song 1" (listed twice) · "Song 1" and "Song 3"
        Check.Equal(1, await imports.QueueMissingAsync(folder, only: first), "only the picked playlist's missing songs are queued; a song it lists twice, once");
        Check.Equal("Song 1", string.Join(", ", downloads.Active.Select(d => d.Track.Title)), "the other playlist's own missing song is left alone");
        Check.True(imports.Status.Contains("from \"Feeling Shit\""), "the status names the playlist");
        Check.True(downloads.Active.All(d => d.Held), "they wait for Start like any others");
        Check.Equal(0, await imports.QueueMissingAsync(folder, only: first), "asked again, nothing is queued twice");
        Check.True(imports.Status.StartsWith("Nothing new to queue for \"Feeling Shit\""), "and the status says so");
        Check.Equal(1, await imports.QueueMissingAsync(folder, only: second), "the second playlist adds only the song that is not queued yet");
        Check.Equal(0, await imports.QueueMissingAsync(folder), "after both, all playlists together have nothing left to queue");
        library.Lists[first.LibraryId].Reverse(); // the user reorders it in Noctis
        Check.Equal(0, await imports.QueueMissingAsync(folder, only: first), "a playlist edited in Noctis is not queued for");
        Check.True(imports.Status.StartsWith("PLAYLIST_EDITED"), "and the status says why");
        Check.Equal(0, source.Calls, "queueing never asks a bot for audio");
    }
    private static void Links()
    {
        Check.Section("multiple Spotify playlists: safe paste and canonical identities");
        Check.Equal(2, SpotifyPlaylists.Links(Url(1) + "?si=test\r\n\r\n" + Url(2) + "\n" + Url(1)).Count, "duplicate links/tracking/blank lines collapse in paste order");
        Check.Equal(Url(1), SpotifyPlaylists.Links("spotify:playlist:" + Id(1))[0], "Spotify URI accepted");
        foreach (var value in new[] { "", "https://attacker.example/playlist/x", Url(1) + "\ninvalid", "https://open.spotify.com/track/" + Id(1), string.Concat(Enumerable.Repeat(Url(1) + "\n", 101)) })
            Check.Throws<FormatException>(() => SpotifyPlaylists.Links(value), "invalid paste rejected before any mutation");
        Check.Equal("🍒", SpotifyPlaylists.DisplayName("🍒"), "emoji playlist names survive");
        Check.Equal("../Gym", SpotifyPlaylists.DisplayName("../Gym\n"), "name is display text and never an audio path");
    }
    private static void Matching()
    {
        Check.Section("local matching across folders, duplicates, versions and missing files");
        var library = new Library(); var folder = Check.NewFolder("playlist-match");
        var a = library.Add(Path.Combine(folder, "main/sub"), "Song 1");
        var b = library.Add(Path.Combine(folder, "telegram"), "Song 2");
        library.Add(folder, "Song 1 (Live)"); library.Add(folder, "Song 1", "Other Artist");
        library.Add(folder, "Song 1 (Clean)"); library.Add(folder, "Song 1", "Artist", 250);
        var request = new MusicRequest("Song 1", "Artist", 180, Explicit: true);
        Check.Equal(a.Id, new PlaylistMatcher(library.Songs).Find(request)!.Id, "correct recording wins across nested folders");
        Check.Equal(b.Id, new PlaylistMatcher(library.Songs).Find(new("Song 2", "Artist", 180))!.Id, "second library root is included");
        Check.Equal<LocalSong?>(null, new PlaylistMatcher(library.Songs).Find(new("Song 1 (Remix)", "Artist", 180)), "remix is not an original");
        Check.Equal<LocalSong?>(null, new PlaylistMatcher(library.Songs).Find(new("Song 1", "Artist", 400)), "duration mismatch stays missing");
        Check.True(new PlaylistMatcher(library.Songs).MissingReason(new("Song 1", "Artist", 400)).Contains("Different recording length"), "same-name wrong-length recordings explain why they were not matched");
        // Spotify's way of writing a title against the tags of the files.
        var bonus = library.Add(folder, "Song 9 (Bonus)"); var guest = library.Add(folder, "Song 8", "Artist; Guest");
        Check.Equal(bonus.Id, new PlaylistMatcher(library.Songs).Find(new("Song 9 - Bonus", "Artist", 180))?.Id, "\"Song - Bonus\" finds the file tagged \"Song (Bonus)\"");
        Check.Equal(guest.Id, new PlaylistMatcher(library.Songs).Find(new("Song 8 (with Guest)", "Guest, Artist", 180))?.Id, "a guest in Spotify's title and a semicolon in the file's artists");
        // A leak: on Spotify under its uploader's name, in the library under the artist's, with the uploader as album artist.
        var leak = library.Add(folder, "Song 7", "Real Artist", 129) with { AlbumArtist = "Uploader" };
        var official = library.Add(folder, "Song 7", "Uploader", 129);
        Check.Equal(leak.Id, new PlaylistMatcher(new[] { leak }).Find(new("Song 7", "uploader", 128.9))?.Id, "the album artist counts when title and length agree");
        Check.Equal<LocalSong?>(null, new PlaylistMatcher(new[] { leak }).Find(new("Song 7", "uploader", 160)), "not with another length");
        Check.Equal<LocalSong?>(null, new PlaylistMatcher(new[] { leak }).Find(new("Song 7", "uploader")), "and not without a length");
        Check.Equal(official.Id, new PlaylistMatcher(new[] { leak, official }).Find(new("Song 7", "Uploader", 129))?.Id, "a file by the artist itself comes before one that only has it as album artist");
        Check.Equal<LocalSong?>(null, new PlaylistMatcher(new[] { leak }).Find(new("Song 7", "Somebody Else", 129)), "the same title and length under an unrelated name is not taken");
        Check.True(new PlaylistMatcher(new[] { leak }).MissingReason(new("Song 7", "Somebody Else", 129)).Contains("but by Real Artist"), "and the list says that such a song is there, and by whom");
        var hidden = library.Add(folder, "Hidden song") with { Visible = false };
        Check.Equal(hidden.Id, new PlaylistMatcher(new[] { hidden }).Find(new("Hidden song", "Artist", 180))!.Id, "hidden local recording is found rather than downloaded twice");
        File.Delete(a.Path);
        Check.Equal<LocalSong?>(null, new PlaylistMatcher(library.Songs).Find(request), "deleted path and wrong versions never count as present");
        Check.True(SpotifyPlaylists.SameRecording(new("Song", "Artist", 180, "https://open.spotify.com/track/" + Id(1), true), new("Song", "Artist", 181, "https://open.spotify.com/track/" + Id(2), true)), "album and single Spotify IDs share a download when recording metadata agrees");
        Check.False(SpotifyPlaylists.SameRecording(new("Song", "Artist", 180, Explicit: true), new("Song", "Artist", 180, Explicit: false)), "explicit/clean variants keep separate downloads");
    }
    private static async Task Imports()
    {
        Check.Section("ordered local playlists, overlapping entries, shared staged downloads and restarts");
        var folder = Check.NewFolder("playlist-import"); var library = new Library();
        var zero = library.Add(Path.Combine(folder, "main"), "Song 0"); var two = library.Add(Path.Combine(folder, "telegram/sub"), "Song 2");
        var http = new Http(); var source = new TransferTests.Source("DeezLoad"); var backup = new TransferTests.Source("Music Hunters");
        var sources = new MusicSources(source, backup, _ => { }); var store = Path.Combine(folder, "imports.json");
        using var downloads = new Downloads(_ => { }, Path.Combine(folder, "queue.json"), sources.Track);
        using (var imports = new SpotifyPlaylists(store, library, downloads, sources.Track, new PlaylistCatalog(http)))
        {
            await imports.ImportAsync(Url(1) + "\n" + Url(2), default);
            Check.Equal(2, library.Lists.Count, "two Noctis playlists created with no Telegram login");
            var first = imports.Jobs[0]; var second = imports.Jobs[1];
            Check.Equal("Feeling Shit", library.Names[first.LibraryId], "original name used");
            Check.Equal("🍒", library.Names[second.LibraryId], "emoji name used");
            Check.True(library.Lists[first.LibraryId].SequenceEqual(new[] { zero.Id, two.Id }), "present songs retain relative Spotify order around gaps");
            Check.Equal(0, source.Calls + backup.Calls, "metadata import never requests audio");
            var calls = http.Calls; await imports.ImportAsync(Url(1), default);
            Check.Equal(calls, http.Calls, "same import resumes saved metadata without refetching Spotify");
            Check.Equal(2, library.Lists.Count, "repeat paste creates no duplicate playlist");
            Check.Equal(2, await imports.QueueMissingAsync(Path.Combine(folder, "downloads")), "overlapping missing songs queued once across both playlists");
            Check.Equal(2, downloads.Active.Count, "shared download queue contains two unique songs");
            Check.True(downloads.Active.All(d => d.Held), "downloads staged until explicit start");
            await Task.Delay(80); Check.Equal(0, source.Calls + backup.Calls, "staged requests do not start in the background");
            Check.Equal(0, await imports.QueueMissingAsync(Path.Combine(folder, "downloads")), "second click does not duplicate queued songs");
            var one = library.Add(Path.Combine(folder, "downloads"), "Song 1"); await imports.CheckLibraryAsync();
            Check.True(library.Lists[first.LibraryId].SequenceEqual(new[] { zero.Id, one.Id, two.Id, one.Id }), "download fills original positions, including repeated Spotify entries");
            Check.True(library.Lists[second.LibraryId].SequenceEqual(new[] { two.Id, one.Id }), "same local audio is referenced by a second playlist");
            var saves = library.Saves; await imports.CheckLibraryAsync();
            Check.Equal(saves, library.Saves, "unchanged library does not rewrite native playlists");
            library.Lists[second.LibraryId].Reverse(); var three = library.Add(folder, "Song 3"); await imports.CheckLibraryAsync();
            Check.True(second.FillingStopped && second.Status.StartsWith("PLAYLIST_EDITED"), "manual playlist changes stop automatic filling");
            Check.False(library.Lists[second.LibraryId].Contains(three.Id), "user edits preserved");
        }
        using (var resumed = new SpotifyPlaylists(store, library, downloads, sources.Track, new PlaylistCatalog(new Http { PreviewOnly = true })))
        {
            Check.Equal(2, resumed.Jobs.Count, "saved imports and local playlist IDs survive restart");
            Check.Equal(4, resumed.Jobs[0].Found, "repeated entries included in saved found count");
            var second = resumed.Jobs[1]; await resumed.RemoveAsync(second);
            Check.Equal(1, resumed.Jobs.Count, "remove import stops saved tracking");
            Check.Equal(2, library.Lists.Count, "remove import keeps both native playlists");
            library.Lists.Remove(resumed.Jobs[0].LibraryId); File.Delete(zero.Path); await resumed.CheckLibraryAsync();
            Check.True(resumed.Jobs[0].FillingStopped, "deleted native playlist is not automatically recreated");
        }
        Check.Equal(0, source.Calls + backup.Calls, "all import/repair checks remain metadata-only");
    }
    private static async Task Failures()
    {
        Check.Section("playlist imports: 251 entries, inaccessible metadata, permissions and queue clearing");
        var folder = Check.NewFolder("playlist-errors"); var library = new Library();
        for (var i = 0; i < 245; i++) library.Add(Path.Combine(folder, "nested"), "Song " + i);
        var source = new TransferTests.Source("DeezLoad"); var sources = new MusicSources(source, new TransferTests.Source("Music Hunters"), _ => { });
        using var downloads = new Downloads(_ => { });
        using var imports = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), library, downloads, sources.Track, new PlaylistCatalog(new Http { Broken = 3 }));
        await imports.ImportAsync(Url(3) + "\n" + Url(9), default);
        Check.True(imports.Jobs[0].Status.Contains("403"), "blocked playlist explains HTTP error");
        Check.Equal(245, imports.Jobs[1].Found, "245 local songs found beyond the preview page");
        Check.Equal(251, imports.Jobs[1].Total, "full count retained");
        Check.Equal(245, library.Lists[imports.Jobs[1].LibraryId].Count, "failure in earlier playlist does not stop later playlist");
        Check.Equal(6, await imports.QueueMissingAsync(folder), "only six missing songs enter shared queue");
        downloads.Clear();
        Check.Equal(0, downloads.Active.Count, "clear queue removes staged songs");
        await imports.CheckLibraryAsync();
        Check.Equal(0, downloads.Active.Count, "library checking does not refill cleared queue");
        Check.Equal(1, library.Lists.Count, "queue clearing does not erase Noctis playlists");
        library.Denied = true;
        using var denied = new SpotifyPlaylists(Path.Combine(folder, "denied.json"), library, downloads, sources.Track, new PlaylistCatalog(new Http()));
        await denied.ImportAsync(Url(1), default);
        Check.True(denied.Jobs[0].Status.StartsWith("PLAYLIST_SAVE_DENIED"), "permissions problem gives actionable error");
        Check.False(denied.Jobs[0].Created, "failed save is not described as a created playlist");
        Check.Equal(0, await denied.QueueMissingAsync(folder), "failed native import cannot request downloads");
        library.Denied = false; await denied.ImportAsync(Url(1), default);
        Check.True(denied.Jobs[0].Created, "permission repair resumes same import");
        using var preview = new SpotifyPlaylists(Path.Combine(folder, "preview.json"), library, downloads, sources.Track, new PlaylistCatalog(new Http { PreviewOnly = true }));
        await preview.ImportAsync(Url(1), default);
        Check.True(preview.Jobs[0].Status.Contains("METADATA_INCOMPLETE"), "preview-only metadata rejected clearly");
        Check.False(preview.Jobs[0].Created, "no partial-preview playlist created");
        using var quota = new SpotifyPlaylists(Path.Combine(folder, "quota.json"), library, downloads, sources.Track, new PlaylistCatalog(new Http { RateLimit = true }));
        await quota.ImportAsync(Url(1), default);
        Check.True(quota.Jobs[0].Status.Contains("retry"), "metadata quota is reported without contacting either bot");
        await quota.ImportAsync(Url(1) + "\n" + Url(2), default);
        Check.True(quota.Jobs.All(j => j.MetadataRetryAt > DateTimeOffset.UtcNow || j.Status.StartsWith("METADATA_LIMIT")), "metadata quota wait survives repeat/batch import without new requests");
        // The computer's proxy is on but cannot get out, as a VPN app that is not connected: it turns every request down.
        using var proxy = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); proxy.Start();
        var port = ((IPEndPoint)proxy.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await proxy.AcceptTcpClientAsync(); var stream = client.GetStream(); var one = new byte[1]; var head = "";
                while (!head.EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) > 0) head += (char)one[0];
                await stream.WriteAsync("HTTP/1.1 503 Service Unavailable\r\n\r\n"u8.ToArray());
            }
        });
        using var unreachable = new SpotifyPlaylists(Path.Combine(folder, "unreachable.json"), library, downloads, sources.Track,
            new PlaylistCatalog(new MetadataRoute("", null, (_, _) => new ProxyChoice(ProxyKind.Http, "127.0.0.1", port))));
        await unreachable.ImportAsync(Url(1), default);
        Check.Equal($"METADATA_UNREACHABLE: Spotify could not be reached through the computer's proxy 127.0.0.1:{port}. The proxy could not connect to Spotify (answer 503). Check that the VPN or proxy is connected, then retry.",
            unreachable.Jobs[0].Status, "Spotify is read through the computer's proxy, and a proxy that cannot get out is named in the import's status");
        Check.False(unreachable.Jobs[0].Created, "no playlist is made from a list that could not be read");
        var bad = Path.Combine(folder, "bad.json"); File.WriteAllText(bad, "{bad");
        Check.Throws<InvalidDataException>(() => new SpotifyPlaylists(bad, library, downloads, sources.Track), "corrupt state kept and rejected");
        Check.Equal("{bad", File.ReadAllText(bad), "saved corrupt file not replaced");
        Check.Equal(0, source.Calls, "failure checks never request real audio");
    }
    private static async Task HeldQueue()
    {
        Check.Section("staged queue compatibility, release, priority and restart");
        var folder = Check.NewFolder("playlist-held"); var file = Path.Combine(folder, "queue.json");
        var source = new TransferTests.Source("DeezLoad"); var sources = new MusicSources(source, new TransferTests.Source("Music Hunters"), _ => { });
        using (var queue = new Downloads(_ => { }, file, sources.Track))
        {
            queue.Start(sources.Track(new("Staged", "Artist", 180)), folder, held: true);
            queue.Start(sources.Track(new("Regular", "Artist", 180)), folder);
            await Check.UntilAsync(() => queue.History.Any(d => d.State == DownloadState.Saved), "ordinary queue runs while new import stays staged");
            Check.Equal(1, source.Calls, "only regular request fetched");
        }
        using (var queue = new Downloads(_ => { }, file, sources.Track))
        {
            Check.True(queue.Active.Single().Held, "held state survives queue restart");
            Check.Equal(1, queue.ReleaseHeld(), "start downloads releases staged request");
            await Check.UntilAsync(() => queue.Active.Count == 0, "released request completes normally");
            var item = queue.Start(sources.Track(new("Priority", "Artist", 180)), folder, held: true);
            queue.DownloadNext(item);
            await Check.UntilAsync(() => item.State == DownloadState.Saved, "download now starts a single staged request");
            Check.False(item.Held, "priority promotion clears the staging hold");
        }
    }
    private static async Task UnavailableAndMoved()
    {
        Check.Section("imports preserve offline references and accept native file remapping");
        var folder = Check.NewFolder("playlist-moves"); var library = new Library();
        var zero = library.Add(folder, "Song 0"); var one = library.Add(folder, "Song 1");
        var sources = new MusicSources(new TransferTests.Source("DeezLoad"), new TransferTests.Source("Music Hunters"), _ => { });
        using var queue = new Downloads(_ => { });
        using var imports = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), library, queue, sources.Track, new PlaylistCatalog(new Http()));
        await imports.ImportAsync(Url(1), default); var job = imports.Jobs.Single();
        var before = library.Lists[job.LibraryId].ToArray();
        File.Move(zero.Path, zero.Path + ".offline"); await imports.CheckLibraryAsync();
        Check.True(library.Lists[job.LibraryId].SequenceEqual(before), "disconnected/unavailable file does not erase its playlist reference");
        Check.Equal(2, job.Found, "unavailable file is not counted as a present match");
        File.Move(zero.Path + ".offline", zero.Path); await imports.CheckLibraryAsync();
        Check.Equal(3, job.Found, "reconnected file restores its match without a download");
        var movedPath = Path.Combine(folder, "new location.flac"); File.Move(one.Path, movedPath);
        var moved = one with { Id = Guid.NewGuid(), Path = movedPath }; library.Songs.Remove(one); library.Songs.Add(moved);
        library.Lists[job.LibraryId] = before.Select(id => id == one.Id ? moved.Id : id).ToList();
        var two = library.Add(folder, "Song 2"); await imports.CheckLibraryAsync();
        Check.False(job.FillingStopped, "native track ID remap during a file move is not mistaken for a manual reorder");
        Check.True(library.Lists[job.LibraryId].SequenceEqual(new[] { zero.Id, moved.Id, two.Id, moved.Id }), "move and gap download in one update preserve order and duplicate entries");
        Check.Equal(0, queue.History.Count, "file recovery never queues unsolicited downloads");
    }
}
