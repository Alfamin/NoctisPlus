using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Over real Kestrel on loopback: songs a phone sends (noctisUploadTrack, noctisHasTracks), the
/// desktop's profile name in noctisSignIn / getNoctisSyncStatus, and songs whose file is gone
/// left out of what the server lists.
/// </summary>
[Collection("MetadataServiceStatics")]
public class NoctisServerUploadTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-upload-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new() { SyncEnabled = true, SyncDeviceId = "desktop-test", SyncDeviceName = "Test PC" };
    private ServerUserStore _users = null!;
    private TestPersistenceService _persistence = null!;
    private LibrarySyncService _sync = null!;
    private UploadLibrary _lib = null!;
    private NoctisServer _server = null!;
    private HttpClient _http = null!;
    private Track _alpha = null!;
    private static readonly Guid AlbumA = Guid.NewGuid();

    private string Imports => Path.Combine(_dir, "Music", "Noctis Imports");
    private string DeviceFolder => Path.Combine(Imports, "Galaxy S23");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _persistence = new TestPersistenceService();
        _sync = new LibrarySyncService(() => _settings, _persistence);
        var audio = Path.Combine(_dir, "alpha.mp3");
        File.WriteAllBytes(audio, Bytes(3000, 7));
        _alpha = new Track { Id = Guid.NewGuid(), Title = "Alpha", Artist = "X", AlbumArtist = "X", Album = "First", AlbumId = AlbumA, FilePath = audio, Duration = TimeSpan.FromSeconds(100), FileSize = 3000 };
        _lib = new UploadLibrary(_alpha) { UploadRoot = Imports };
        _server = new NoctisServer(_lib, _users, "test", _sync) { PrivateClientsOnly = true };
        await _server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        _persistence.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)((i * seed) % 251)).ToArray();

    private static JsonElement Envelope(string json) => JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");

    private static int ErrorCode(JsonElement r) => r.GetProperty("error").GetProperty("code").GetInt32();

    private async Task<JsonElement> SignIn(string deviceId = "phone-0001", string deviceName = "Galaxy S23")
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["u"] = "alice", ["p"] = "correct horse", ["deviceId"] = deviceId, ["deviceName"] = deviceName, ["f"] = "json",
        });
        var res = await _http.PostAsync("rest/noctisSignIn.view", form, Ct);
        return Envelope(await res.Content.ReadAsStringAsync(Ct));
    }

    private async Task<string> DeviceKey(string deviceId = "phone-0001", string deviceName = "Galaxy S23")
        => (await SignIn(deviceId, deviceName)).GetProperty("noctisSignIn").GetProperty("apiKey").GetString()!;

    private async Task<JsonElement> Upload(string? key, string name, byte[] body, bool chunked = false, string query = "")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"rest/noctisUploadTrack.view?f=json&name={Uri.EscapeDataString(name)}{query}");
        if (key is not null) req.Headers.Add(NoctisServer.KeyHeader, key);
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (chunked) req.Headers.TransferEncodingChunked = true;   // no Content-Length: the cap must hold while reading
        var res = await _http.SendAsync(req, Ct);
        return Envelope(await res.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonElement> Post(string method, string key, IEnumerable<KeyValuePair<string, string>> form)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"rest/{method}.view?f=json") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add(NoctisServer.KeyHeader, key);
        return Envelope(await (await _http.SendAsync(req, Ct)).Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonElement> Get(string method, string key, string query = "")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"rest/{method}.view?f=json{query}");
        req.Headers.Add(NoctisServer.KeyHeader, key);
        return Envelope(await (await _http.SendAsync(req, Ct)).Content.ReadAsStringAsync(Ct));
    }

    private static (string Status, string Name) Answer(JsonElement r)
    {
        Assert.True(r.GetProperty("status").GetString() == "ok", r.ToString());
        var u = r.GetProperty("noctisUpload");
        return (u.GetProperty("status").GetString()!, u.GetProperty("name").GetString()!);
    }

    private string[] FilesUnder(string dir) => Directory.Exists(dir)
        ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/')).OrderBy(f => f).ToArray()
        : Array.Empty<string>();

    // ── noctisUploadTrack ──

    [Fact]
    public async Task Upload_LandsInTheDevicesFolderUnderImports_AndGoesThroughTheLibraryHook()
    {
        var key = await DeviceKey();
        var song = Bytes(5000, 3);

        var (status, name) = Answer(await Upload(key, "01 - Up.mp3", song));

        Assert.Equal("added", status);
        Assert.Equal("01 - Up.mp3", name);
        var landed = Path.Combine(DeviceFolder, "01 - Up.mp3");
        Assert.Equal(song, File.ReadAllBytes(landed));
        Assert.Equal(new[] { landed }, _lib.Imported);
        Assert.Equal(new[] { "Music/Noctis Imports/Galaxy S23/01 - Up.mp3" }, FilesUnder(Path.Combine(_dir, "Music")));  // no temp file left
    }

    [Fact]
    public async Task Upload_SameBytesTwice_IsAlreadyThere_ADifferentSongWithTheSameName_GetsANumber()
    {
        var key = await DeviceKey();
        Assert.Equal("added", Answer(await Upload(key, "Song.flac", Bytes(4000, 3))).Status);

        var again = Answer(await Upload(key, "Renamed on the phone.flac", Bytes(4000, 3)));
        Assert.Equal(("exists", "Song.flac"), again);

        Assert.Equal(("added", "Song (2).flac"), Answer(await Upload(key, "Song.flac", Bytes(4000, 5))));
        Assert.Equal(("added", "Song (3).flac"), Answer(await Upload(key, "Song.flac", Bytes(4100, 5))));
        Assert.Equal(new[]
        {
            "Music/Noctis Imports/Galaxy S23/Song (2).flac",
            "Music/Noctis Imports/Galaxy S23/Song (3).flac",
            "Music/Noctis Imports/Galaxy S23/Song.flac",
        }, FilesUnder(Path.Combine(_dir, "Music")));
        Assert.Equal(3, _lib.Imported.Count);
        Assert.Equal(Bytes(4000, 3), File.ReadAllBytes(Path.Combine(DeviceFolder, "Song.flac")));   // never overwritten
    }

    [Fact]
    public async Task Upload_BytesOfASongAlreadyInTheLibrary_IsAlreadyThere_AndWritesNothing()
    {
        var key = await DeviceKey();
        Assert.Equal(("exists", "alpha.mp3"), Answer(await Upload(key, "Alpha.mp3", File.ReadAllBytes(_alpha.FilePath))));
        Assert.Empty(FilesUnder(Path.Combine(_dir, "Music")));
        Assert.Empty(_lib.Imported);
    }

    [Theory]
    [InlineData("../../evil.mp3", "evil.mp3")]
    [InlineData(@"..\..\Windows\evil.flac", "evil.flac")]
    [InlineData("/etc/passwd.mp3", "passwd.mp3")]
    [InlineData("CON.mp3", "_CON.mp3")]
    [InlineData("lpt1.backup.m4a", "_lpt1.backup.m4a")]
    [InlineData(".hidden.mp3", "hidden.mp3")]
    [InlineData("a:b*c?\"d<e>f|.MP3", "a_b_c__d_e_f_.mp3")]
    [InlineData("trailing dots... .ogg", "trailing dots.ogg")]
    [InlineData(".mp3", "Song.mp3")]
    public async Task Upload_FileNames_AreReducedToAPlainNameInsideTheDevicesFolder(string sent, string stored)
    {
        var key = await DeviceKey();
        var (status, name) = Answer(await Upload(key, sent, Bytes(1000, 11)));
        Assert.Equal("added", status);
        Assert.Equal(stored, name);
        Assert.Equal(new[] { "Music/Noctis Imports/Galaxy S23/" + stored }, FilesUnder(_dir).Where(f => f.StartsWith("Music/")).ToArray());
        Assert.False(File.Exists(Path.Combine(_dir, "evil.mp3")));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("setup.exe")]
    [InlineData("song.mp3.lnk")]
    [InlineData("no extension")]
    [InlineData("")]
    public async Task Upload_ANameThatIsNotAPlayableSong_IsRefused_AndWritesNothing(string sent)
    {
        var key = await DeviceKey();
        var r = await Upload(key, sent, Bytes(1000, 11));
        Assert.Equal(10, ErrorCode(r));
        Assert.Empty(FilesUnder(Path.Combine(_dir, "Music")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_OverTheCap_IsRefused_WithOrWithoutALength_AndLeavesNoFile(bool chunked)
    {
        var key = await DeviceKey();
        _server.UploadLimitBytes = 1000;

        var r = await Upload(key, "big.flac", Bytes(5000, 3), chunked);

        Assert.Equal(0, ErrorCode(r));
        Assert.Contains("MB can be sent", r.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(Directory.Exists(DeviceFolder) ? Directory.GetFiles(DeviceFolder) : Array.Empty<string>());
        Assert.Empty(_lib.Imported);
        // At the cap exactly is fine.
        Assert.Equal("added", Answer(await Upload(key, "fits.flac", Bytes(1000, 3), chunked)).Status);
    }

    [Fact]
    public void TheCap_Is500MB() => Assert.Equal(500L * 1024 * 1024, NoctisServer.MaxUploadBytes);

    [Fact]
    public async Task Upload_NeedsADeviceKey_AndSyncOn()
    {
        // No credentials at all.
        Assert.Equal(10, ErrorCode(await Upload(null, "a.mp3", Bytes(100, 3))));
        // A wrong key.
        Assert.Equal(40, ErrorCode(await Upload("nk_not-a-key", "a.mp3", Bytes(100, 3))));
        // The account's own API key is not a device: there is no device folder to name.
        var accountKey = _users.RegenerateApiKey("alice");
        Assert.Equal(42, ErrorCode(await Upload(accountKey, "a.mp3", Bytes(100, 3))));
        // Sync off on the desktop: the same gate as the sync endpoints.
        var key = await DeviceKey();
        _settings.SyncEnabled = false;
        Assert.Equal(50, ErrorCode(await Upload(key, "a.mp3", Bytes(100, 3))));
        Assert.Equal(50, ErrorCode(await Post("noctisHasTracks", key, new Dictionary<string, string> { ["title"] = "Alpha" })));
        Assert.Empty(FilesUnder(Path.Combine(_dir, "Music")));
        _settings.SyncEnabled = true;
        Assert.Equal("added", Answer(await Upload(key, "a.mp3", Bytes(100, 3))).Status);
    }

    [Fact]
    public async Task Upload_ToAHostThatTakesNoSongs_IsRefused()
    {
        var key = await DeviceKey();
        _lib.UploadRoot = null;
        var r = await Upload(key, "a.mp3", Bytes(100, 3));
        Assert.Equal(0, ErrorCode(r));
        Assert.Contains("doesn't take songs", r.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("Galaxy S23", "Galaxy S23")]
    [InlineData("..", "Phone")]
    [InlineData("../../x", "_.._x")]
    [InlineData("COM1", "_COM1")]
    [InlineData("Pixel: 9 Pro?", "Pixel_ 9 Pro_")]
    [InlineData(null, "Phone")]
    public void DeviceFolderName_IsAPlainFolderName(string? device, string folder)
        => Assert.Equal(folder, NoctisServer.UploadFolderName(device));

    // ── noctisHasTracks ──

    [Fact]
    public async Task HasTracks_MatchesTitleAndArtist_AnyCase_WithLengthsWithinTwoSeconds()
    {
        var key = await DeviceKey();
        var form = new List<KeyValuePair<string, string>>();
        void Ask(string title, string artist, int seconds)
        {
            form.Add(new("title", title));
            form.Add(new("artist", artist));
            form.Add(new("duration", seconds.ToString()));
        }
        Ask(" alpha ", "x", 101);     // the same song, another encode
        Ask("Alpha", "X", 150);       // another version
        Ask("Beta", "X", 100);
        Ask("Alpha", "Y", 100);
        Ask("Alpha", "X", 0);         // length unknown on the phone

        var r = await Post("noctisHasTracks", key, form);

        Assert.Equal(new[] { true, false, false, false, true },
            r.GetProperty("noctisHasTracks").GetProperty("have").EnumerateArray().Select(e => e.GetBoolean()).ToArray());
    }

    [Fact]
    public async Task HasTracks_RefusesTooManySongs_AndNeedsADeviceKey()
    {
        var key = await DeviceKey();
        var form = Enumerable.Range(0, NoctisServer.MaxHasTracksItems + 1).Select(i => new KeyValuePair<string, string>("title", "t" + i)).ToList();
        Assert.Equal(0, ErrorCode(await Post("noctisHasTracks", key, form)));
        Assert.Equal(42, ErrorCode(await Post("noctisHasTracks", _users.RegenerateApiKey("alice"), new Dictionary<string, string> { ["title"] = "Alpha" })));
    }

    [Fact]
    public async Task HasTracks_ASongWhoseFileIsGone_IsNotThere_SoThePhoneSendsIt()
    {
        var key = await DeviceKey();
        File.Delete(_alpha.FilePath);
        var r = await Post("noctisHasTracks", key, new Dictionary<string, string> { ["title"] = "Alpha", ["artist"] = "X", ["duration"] = "100" });
        Assert.False(r.GetProperty("noctisHasTracks").GetProperty("have")[0].GetBoolean());
    }

    // ── The profile name for the phone's header ──

    [Fact]
    public async Task ProfileName_ComesWithSignIn_AndTheSyncStatus_AndIsLeftOutWhenEmpty()
    {
        _lib.ProfileName = "  notheartached ";
        var signIn = (await SignIn()).GetProperty("noctisSignIn");
        Assert.Equal("notheartached", signIn.GetProperty("displayName").GetString());
        Assert.Equal("alice", signIn.GetProperty("user").GetString());
        var key = signIn.GetProperty("apiKey").GetString()!;
        Assert.Equal("notheartached", (await Get("getNoctisSyncStatus", key)).GetProperty("noctisSync").GetProperty("displayName").GetString());

        _lib.ProfileName = "Renamed";
        Assert.Equal("Renamed", (await Get("getNoctisSyncStatus", key)).GetProperty("noctisSync").GetProperty("displayName").GetString());

        // With sync off the status still answers (the phone reads the name from it either way).
        _settings.SyncEnabled = false;
        Assert.Equal("Renamed", (await Get("getNoctisSyncStatus", key)).GetProperty("noctisSync").GetProperty("displayName").GetString());

        _lib.ProfileName = "   ";
        Assert.False((await Get("getNoctisSyncStatus", key)).GetProperty("noctisSync").TryGetProperty("displayName", out _));
        _lib.ProfileName = null;
        Assert.False((await SignIn("phone-0002")).GetProperty("noctisSignIn").TryGetProperty("displayName", out _));
    }

    // ── Songs whose file is gone ──

    [Fact]
    public async Task ASongWhoseFileIsGone_IsLeftOutOfTheCatalog_TheCountAndItsAlbum()
    {
        var key = await DeviceKey();
        var beta = new Track { Id = Guid.NewGuid(), Title = "Beta", Artist = "X", AlbumArtist = "X", Album = "Second", AlbumId = Guid.NewGuid(), FilePath = Path.Combine(_dir, "beta.mp3"), Duration = TimeSpan.FromSeconds(50) };
        File.WriteAllBytes(beta.FilePath, Bytes(100, 3));
        _lib.Tracks.Add(beta);
        _lib.Albums.Add(new Album { Id = beta.AlbumId, Name = "Second", Artist = "X" });
        _server.FileCheckMaxAge = TimeSpan.FromHours(1);

        async Task<string[]> Songs() => (await Get("search3", key, "&query=&songCount=500&albumCount=500&artistCount=0"))
            .GetProperty("searchResult3").GetProperty("song").EnumerateArray().Select(s => s.GetProperty("title").GetString()!).OrderBy(t => t).ToArray();
        async Task<int> Count() => (await Get("getScanStatus", key)).GetProperty("scanStatus").GetProperty("count").GetInt32();

        Assert.Equal(new[] { "Alpha", "Beta" }, await Songs());
        File.Delete(beta.FilePath);

        // The answers are kept a while (one disk pass per catalog read, not per request).
        Assert.Equal(new[] { "Alpha", "Beta" }, await Songs());

        _server.FileCheckMaxAge = TimeSpan.Zero;
        Assert.Equal(new[] { "Alpha" }, await Songs());
        Assert.Equal(1, await Count());                                        // the phone's "settled" check agrees
        Assert.Equal(70, ErrorCode(await Get("getAlbum", key, $"&id=al-{beta.AlbumId:N}")));
        Assert.Equal("ok", (await Get("getAlbum", key, $"&id=al-{AlbumA:N}")).GetProperty("status").GetString());
        Assert.Equal(70, ErrorCode(await Get("download", key, $"&id=tr-{beta.Id:N}")));

        // Back on disk (a drive plugged in again): listed again.
        File.WriteAllBytes(beta.FilePath, Bytes(100, 3));
        Assert.Equal(new[] { "Alpha", "Beta" }, await Songs());
    }

    [Fact]
    public async Task UploadThroughTheRealAdapter_AddsTheSongToTheLibrary_AndTheCatalog()
    {
        var data = Path.Combine(_dir, "desk-data");
        var persistence = new PersistenceService(data);
        var library = new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        var rootAsked = 0;
        var adapter = new LibraryServerAdapter(library, persistence, new NoHistory(), uploadRoot: _ =>
        {
            Interlocked.Increment(ref rootAsked);
            Directory.CreateDirectory(Imports);
            return Task.FromResult<string?>(Imports);
        });
        await using var server = new NoctisServer(adapter, _users, "test", _sync);
        await server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback, ct: Ct);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["u"] = "alice", ["p"] = "correct horse", ["deviceId"] = "phone-0001", ["deviceName"] = "Galaxy S23", ["f"] = "json",
        });
        var key = Envelope(await (await http.PostAsync("rest/noctisSignIn.view", form, Ct)).Content.ReadAsStringAsync(Ct))
            .GetProperty("noctisSignIn").GetProperty("apiKey").GetString()!;
        var source = Path.Combine(_dir, "phone", "up.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        NoctisAccountLibraryTests.WriteMp3(source, "Sent from the phone");

        using var req = new HttpRequestMessage(HttpMethod.Post, "rest/noctisUploadTrack.view?f=json&name=up.mp3") { Content = new ByteArrayContent(File.ReadAllBytes(source)) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        req.Headers.Add(NoctisServer.KeyHeader, key);
        var answer = Envelope(await (await http.SendAsync(req, Ct)).Content.ReadAsStringAsync(Ct));

        Assert.Equal("added", answer.GetProperty("noctisUpload").GetProperty("status").GetString());
        var landed = Path.Combine(Imports, "Galaxy S23", "up.mp3");
        var track = Assert.Single(library.Tracks);
        Assert.Equal(Path.GetFullPath(landed), Path.GetFullPath(track.FilePath));
        Assert.Equal("Sent from the phone", track.Title);
        Assert.Equal(1, rootAsked);
        Assert.Contains("up.mp3", File.ReadAllText(Path.Combine(data, "library.json")));   // persisted like a drop
        using var list = new HttpRequestMessage(HttpMethod.Get, "rest/search3.view?f=json&query=&songCount=10");
        list.Headers.Add(NoctisServer.KeyHeader, key);
        var songs = Envelope(await (await http.SendAsync(list, Ct)).Content.ReadAsStringAsync(Ct)).GetProperty("searchResult3").GetProperty("song");
        Assert.Equal("Sent from the phone", Assert.Single(songs.EnumerateArray()).GetProperty("title").GetString());
    }

    private sealed class NoHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events { get; } = new List<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>A desktop library with the upload hooks: the root is a folder, an import is recorded.</summary>
    private sealed class UploadLibrary(Track track) : IServerLibrary
    {
        public List<Track> Tracks { get; } = new() { track };
        public List<Album> Albums { get; } = new() { new Album { Id = AlbumA, Name = "First", Artist = "X" } };
        public string? ProfileName { get; set; }
        public string? UploadRoot { get; set; }
        public List<string> Imported { get; } = new();

        public Task<string?> UploadRootAsync(CancellationToken ct)
        {
            if (UploadRoot is not null) Directory.CreateDirectory(UploadRoot);
            return Task.FromResult(UploadRoot);
        }

        public Task ImportUploadedAsync(string path, CancellationToken ct)
        {
            lock (Imported) Imported.Add(path);
            return Task.CompletedTask;
        }

        public Task<LibrarySnapshot> SnapshotAsync() => Task.FromResult(new LibrarySnapshot(Tracks.ToList(), Albums.ToList(), new List<Artist> { new() { Id = Guid.NewGuid(), Name = "X" } }, new List<Playlist>()));
        public string? ArtworkPath(Guid albumId) => null;
        public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred) => Task.CompletedTask;
        public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => Task.FromResult(new Playlist { Name = name });
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(false);
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(false);
        public Task ApplyTrackStateAsync(Guid trackId, TrackSyncState state) => Task.CompletedTask;
        public Task ApplyPlaylistStateAsync(Guid playlistId, PlaylistSyncState state) => Task.CompletedTask;
    }
}
