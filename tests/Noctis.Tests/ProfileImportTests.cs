using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Noctis.Services;
using Noctis.Services.ProfileImport;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The first launch of a new Noctis Plus profile carries the whole official Noctis profile over: everything, stats
/// and sign-ins included, with a consistent database snapshot, never touching the source and never importing over
/// a working Plus profile. Synthetic profiles only; nothing here reads a real one.
/// </summary>
public sealed class ProfileImportTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _dest;

    public ProfileImportTests()
    {
        // Spaces and non-Latin letters on purpose: real profiles live under such user names.
        _root = Path.Combine(Path.GetTempPath(), "noctis import тест ✓ " + Guid.NewGuid().ToString("N")[..8]);
        _source = Path.Combine(_root, "Roaming", "Noctis");
        _dest = Path.Combine(_root, "Roaming", "Noctis Plus");
        Directory.CreateDirectory(_source);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp folder; the OS cleans it */ }
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────

    private static string Json(string s) => s.Replace("\\", "\\\\");

    private void Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    private void Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private string Settings(int webPort = 9420) => $$"""
        {
          "musicFolders": ["D:\\music-fixture"],
          "webRemotePort": {{webPort}},
          "localApiPort": 9421,
          "noctisServerPort": 4747,
          "syncDeviceId": "abcdef123456",
          "lastFmSessionKey": "fixture-session",
          "profileName": "تست",
          "profileAvatarPath": "{{Json(Path.Combine(_source, "avatar.png"))}}",
          "pluginSettingValues": { "dev.moshi.freemusicfinder": { "lastfmApiKey": "fixture-key" } },
          "volume": 0.85
        }
        """;

    /// <summary>A profile like a real one. Returns the connection that keeps library.db's WAL un-checkpointed.</summary>
    private SqliteConnection BuildProfile()
    {
        Write("settings.json", Settings());
        Write("settings.json.bak", Settings());
        Write("library.json", """[ { "id": "t1", "filePath": "D:\\music-fixture\\a.flac", "playCount": 12, "rating": 5 } ]""");
        Write("playlists.json", """[ { "id": "p1", "name": "Late night", "trackIds": ["t1"] } ]""");
        Write("play_history.json", """[ { "trackId": "t1", "playedAt": "2026-10-01T20:00:00Z" } ]""");
        Write("queue.json", """{ "items": ["t1"] }""");
        Write("wrap_archive.json", """{ "2025": { "minutes": 4210 } }""");
        Write("indexes.json", $$"""{ "albums": [ { "artworkPath": "{{Json(Path.Combine(_source, "artwork", "x.jpg"))}}" } ] }""");
        Write(Path.Combine("artist_info", "a.similar.json"),
            $$"""{ "image": "{{_source.Replace('\\', '/')}}/artist_images/a.jpg", "names": ["B"] }""");
        Write(Path.Combine("artwork", "x.jpg"), Bytes(300_000, 1));
        Write(Path.Combine("models", "whisper", "m.bin"), Bytes(1_200_000, 2));
        Write(Path.Combine("cache", "artwork_thumbs", "t.bin"), Bytes(50_000, 3));
        Write(Path.Combine("plugin-data", "dev.moshi.freemusicfinder", "telegram.dat"), Bytes(17_000, 4));
        Write(Path.Combine("plugin-data", "dev.moshi.freemusicfinder", "download-queue.json"), """{ "pending": [] }""");
        Write(Path.Combine("plugin-data", "dev.moshi.trueshuffle", "listens.jsonl"), "{\"t\":\"t1\",\"end\":\"Finished\"}\n");
        Write(Path.Combine("plugins", "dev.moshi.trueshuffle", "TrueShuffle.dll"), Bytes(2_000, 5));
        Write(Path.Combine("plugins", "community.example", "Example.dll"), Bytes(2_000, 6));
        Write("crash.log", "old crash");
        Write("crashlog-20261016-134342.log", "preserved crash");
        Write("session.log", "live session");
        Write("auto-update.json", """{ "dismissedVersion": "1.6.4" }""");
        Write(".instance.lock", "");
        Write("library.json.tmp", "half-written");

        // library.db as the running app leaves it: WAL mode, the newest stats only in library.db-wal.
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_source, "library.db"), Pooling = false }.ToString());
        db.Open();
        Exec(db, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        Exec(db, "CREATE TABLE track_user_state (id TEXT PRIMARY KEY, play_count INTEGER, rating INTEGER);");
        for (var i = 0; i < 50; i++) Exec(db, $"INSERT INTO track_user_state VALUES ('t{i}', {i}, {i % 6});");
        Assert.True(File.Exists(Path.Combine(_source, "library.db-wal")));
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private Dictionary<string, string> Fingerprint(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f),
            f =>
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return $"{s.Length}:{File.GetLastWriteTimeUtc(f).Ticks}:{Convert.ToHexString(SHA256.HashData(s))}";
            });

    private ProfileImporter Importer(Func<bool>? running = null, Func<string, long?>? free = null)
        => new(_source, _dest, running, free ?? (_ => long.MaxValue));

    private string DestText(string relative) => File.ReadAllText(Path.Combine(_dest, relative));

    private sealed class SyncProgress(Action<ProfileImportProgress> report) : IProgress<ProfileImportProgress>
    {
        public void Report(ProfileImportProgress value) => report(value);
    }

    // ── tests ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FreshProfile_CarriesEverything_AndLeavesTheSourceUntouched()
    {
        using var liveDb = BuildProfile();
        var before = Fingerprint(_source);
        Assert.Equal(ProfileImportDecisionKind.Start, ProfileImporter.Evaluate(_source, _dest).Kind);

        var reports = new List<ProfileImportProgress>();
        var result = await Importer().RunAsync(new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Completed, result.Outcome);
        Assert.Equal(before, Fingerprint(_source));
        Assert.Contains(reports, r => r.Step == "copying" && r.FilesDone == r.FilesTotal && r.BytesDone == r.BytesTotal);

        // Byte-for-byte: media, models, caches, sign-ins, plugin data and community plugins.
        foreach (var rel in new[]
                 {
                     Path.Combine("artwork", "x.jpg"), Path.Combine("models", "whisper", "m.bin"),
                     Path.Combine("cache", "artwork_thumbs", "t.bin"),
                     Path.Combine("plugin-data", "dev.moshi.freemusicfinder", "telegram.dat"),
                     Path.Combine("plugin-data", "dev.moshi.freemusicfinder", "download-queue.json"),
                     Path.Combine("plugin-data", "dev.moshi.trueshuffle", "listens.jsonl"),
                     Path.Combine("plugins", "dev.moshi.trueshuffle", "TrueShuffle.dll"),
                     Path.Combine("plugins", "community.example", "Example.dll"),
                     "library.json", "playlists.json", "play_history.json", "queue.json", "wrap_archive.json",
                 })
            Assert.Equal(File.ReadAllBytes(Path.Combine(_source, rel)), File.ReadAllBytes(Path.Combine(_dest, rel)));

        // The official app's own logs and update choices: kept, but where Plus never reads them as its own.
        foreach (var name in new[] { "crash.log", "crashlog-20261016-134342.log", "session.log", "auto-update.json" })
        {
            Assert.False(File.Exists(Path.Combine(_dest, name)), name);
            Assert.True(File.Exists(Path.Combine(_dest, ProfileImporter.ImportedLogsFolder, name)), name);
        }
        foreach (var name in new[] { ".instance.lock", "library.json.tmp", "library.db-wal", "library.db-shm" })
            Assert.False(File.Exists(Path.Combine(_dest, name)), name);

        // Settings: same choices and sign-ins; ports and sync identity of its own; paths into the new profile.
        var settings = JsonNode.Parse(DestText("settings.json"))!.AsObject();
        Assert.Equal(9430, (int)settings["webRemotePort"]!);
        Assert.Equal(9431, (int)settings["localApiPort"]!);
        Assert.Equal(4747, (int)settings["noctisServerPort"]!);
        Assert.NotEqual("abcdef123456", (string)settings["syncDeviceId"]!);
        Assert.Equal(12, ((string)settings["syncDeviceId"]!).Length);
        Assert.Equal("fixture-session", (string)settings["lastFmSessionKey"]!);
        Assert.Equal("fixture-key", (string)settings["pluginSettingValues"]!["dev.moshi.freemusicfinder"]!["lastfmApiKey"]!);
        Assert.Equal("تست", (string)settings["profileName"]!);
        Assert.Equal(0.85, (double)settings["volume"]!);
        Assert.Equal(@"D:\music-fixture", (string)settings["musicFolders"]![0]!);
        Assert.Equal(Path.Combine(_dest, "avatar.png"), (string)settings["profileAvatarPath"]!);
        Assert.Equal(9430, (int)JsonNode.Parse(DestText("settings.json.bak"))!["webRemotePort"]!);

        Assert.Equal(Path.Combine(_dest, "artwork", "x.jpg"),
            (string)JsonNode.Parse(DestText("indexes.json"))!["albums"]![0]!["artworkPath"]!);
        Assert.Equal(_dest.Replace('\\', '/') + "/artist_images/a.jpg",
            (string)JsonNode.Parse(DestText(Path.Combine("artist_info", "a.similar.json")))!["image"]!);

        // Stats that existed only in the live WAL came over in one consistent database.
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(_dest, "library.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            copy.Open();
            using var count = copy.CreateCommand();
            count.CommandText = "SELECT COUNT(*), SUM(play_count) FROM track_user_state;";
            using var reader = count.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(50, reader.GetInt32(0));
            Assert.Equal(Enumerable.Range(0, 50).Sum(), reader.GetInt32(1));
        }

        Assert.False(Directory.Exists(Path.Combine(_dest, ProfileImporter.WorkFolderName, "staging")));
        Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(_source, _dest).Kind);
        Assert.Contains("\"Done\"", File.ReadAllText(Path.Combine(_dest, ProfileImporter.WorkFolderName, "state.json")));
    }

    [Fact]
    public async Task InterruptedCopy_ResumesNextLaunch_AndFinishesTheSame()
    {
        using var liveDb = BuildProfile();
        using var cancel = new CancellationTokenSource();
        var first = await Importer().RunAsync(
            new SyncProgress(p => { if (p.FilesDone >= 4) cancel.Cancel(); }), cancel.Token);

        Assert.Equal(ProfileImportOutcome.Cancelled, first.Outcome);
        Assert.False(File.Exists(Path.Combine(_dest, "settings.json")), "nothing is moved into place before the copy is whole");
        Assert.Equal(ProfileImportDecisionKind.Resume, ProfileImporter.Evaluate(_source, _dest).Kind);

        var second = await Importer().RunAsync(null, CancellationToken.None);
        Assert.Equal(ProfileImportOutcome.Completed, second.Outcome);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_source, "models", "whisper", "m.bin")),
            File.ReadAllBytes(Path.Combine(_dest, "models", "whisper", "m.bin")));
        Assert.Equal(9430, (int)JsonNode.Parse(DestText("settings.json"))!["webRemotePort"]!);
        Assert.Empty(Directory.EnumerateFiles(_dest, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InterruptedMove_FinishesFromStaging_EvenWithoutTheSource()
    {
        var work = Path.Combine(_dest, ProfileImporter.WorkFolderName);
        Directory.CreateDirectory(Path.Combine(work, "staging", "artwork"));
        File.WriteAllText(Path.Combine(work, "staging", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(work, "staging", "artwork", "y.jpg"), "y");
        File.WriteAllText(Path.Combine(work, "state.json"), """{ "Version": 1, "Phase": "Committing", "Source": "gone" }""");
        Directory.Delete(_source, recursive: true);

        Assert.Equal(ProfileImportDecisionKind.Resume, ProfileImporter.Evaluate(_source, _dest).Kind);
        var result = await Importer().RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Completed, result.Outcome);
        Assert.Equal("{}", DestText("settings.json"));
        Assert.Equal("y", DestText(Path.Combine("artwork", "y.jpg")));
    }

    [Fact]
    public async Task WorkingPlusProfile_IsNeverImportedOver()
    {
        using var liveDb = BuildProfile();
        Directory.CreateDirectory(_dest);
        File.WriteAllText(Path.Combine(_dest, "library.json"), "[]");
        var before = Fingerprint(_dest);

        Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(_source, _dest).Kind);
        var result = await Importer().RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Completed, result.Outcome);
        Assert.Equal(before, Fingerprint(_dest));
        Assert.False(Directory.Exists(Path.Combine(_dest, ProfileImporter.WorkFolderName)));
    }

    [Fact]
    public void SameOrNestedFolders_AreNeverImported()
    {
        Write("settings.json", "{}");
        Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(_source, _source).Kind);
        Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(_source, _source + Path.DirectorySeparatorChar).Kind);
        Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(_source, Path.Combine(_source, "inner")).Kind);
        Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(Path.Combine(_root, "Roaming"), _dest).Kind);
        if (OperatingSystem.IsWindows())
            Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(_source, _source.ToUpperInvariant()).Kind);
        // "Noctis" is a prefix of "Noctis Plus" but not its parent: that pair is fine.
        Assert.Equal(ProfileImportDecisionKind.Start, ProfileImporter.Evaluate(_source, _dest).Kind);
    }

    [Fact]
    public async Task WhileNoctisIsOpen_NothingIsCopied()
    {
        using var liveDb = BuildProfile();
        var result = await Importer(running: () => true).RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.SourceAppRunning, result.Outcome);
        Assert.False(Directory.Exists(_dest));
    }

    [Fact]
    public async Task NoctisOpenedDuringTheCopy_WaitsBeforeMovingIntoPlace()
    {
        using var liveDb = BuildProfile();
        var opened = false;
        var importer = Importer(running: () => opened);
        var result = await importer.RunAsync(new SyncProgress(p => { if (p.FilesDone > 3) opened = true; }), CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.SourceAppRunning, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_dest, "settings.json")));

        opened = false;
        Assert.Equal(ProfileImportOutcome.Completed, (await importer.RunAsync(null, CancellationToken.None)).Outcome);
        Assert.True(File.Exists(Path.Combine(_dest, "settings.json")));
    }

    [Fact]
    public async Task ExistingPlusFile_IsKept_NotOverwritten()
    {
        using var liveDb = BuildProfile();
        Write("favorite_artists.json", """["from noctis"]""");
        Directory.CreateDirectory(_dest);
        File.WriteAllText(Path.Combine(_dest, "favorite_artists.json"), """["plus own"]""");

        var result = await Importer().RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Completed, result.Outcome);
        Assert.Equal("""["plus own"]""", DestText("favorite_artists.json"));
        Assert.Contains(result.Warnings, w => w.Contains("favorite_artists.json") && w.Contains("Kept"));
        Assert.True(File.Exists(Path.Combine(_dest, ProfileImporter.WorkFolderName, "staging", "favorite_artists.json")));
    }

    [Fact]
    public async Task DamagedSettings_UseTheirBackup()
    {
        Write("settings.json", "{ \"webRemotePort\": 94");
        Write("settings.json.bak", Settings(webPort: 9420));
        Write("library.json", "[]");

        var result = await Importer().RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Completed, result.Outcome);
        Assert.Equal(9430, (int)JsonNode.Parse(DestText("settings.json"))!["webRemotePort"]!);
        Assert.Contains(result.Warnings, w => w.Contains("backup"));
    }

    [Fact]
    public async Task DamagedSettingsWithoutBackup_StartWithDefaults_AndTheRestComesOver()
    {
        Write("settings.json", "not json");
        Write("library.json", "[]");

        var result = await Importer().RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Completed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_dest, "settings.json")));
        Assert.Equal("not json", DestText("settings.json.damaged"));
        Assert.Equal("[]", DestText("library.json"));
    }

    [Fact]
    public async Task NotEnoughSpace_FailsReadably_AndChangesNothing()
    {
        using var liveDb = BuildProfile();
        var result = await Importer(free: _ => 10_000).RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Failed, result.Outcome);
        Assert.Contains("Not enough free space", result.Error);
        Assert.False(File.Exists(Path.Combine(_dest, "settings.json")));
    }

    [Fact]
    public async Task DamagedDatabase_IsLeftOut_AndTheRestComesOver()
    {
        Write("settings.json", Settings());
        Write("library.json", "[]");
        var header = Encoding.ASCII.GetBytes("SQLite format 3\0");
        Write("library.db", header.Concat(Bytes(8_000, 7)).ToArray());

        var result = await Importer().RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Completed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_dest, "library.db")));
        Assert.Contains(result.Warnings, w => w.Contains("library.db"));
        Assert.True(File.Exists(Path.Combine(_dest, "settings.json")));
    }

    [Fact]
    public async Task LinkedFolders_AreNotFollowed()
    {
        Write("settings.json", "{}");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
        try { Directory.CreateSymbolicLink(Path.Combine(_source, "linked"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no link privilege here

        var result = await Importer().RunAsync(null, CancellationToken.None);

        Assert.Equal(ProfileImportOutcome.Completed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_dest, "linked")));
        Assert.Contains(result.Warnings, w => w.Contains("linked"));
    }

    [Fact]
    public async Task StartFresh_IsRemembered()
    {
        Write("settings.json", "{}");
        var importer = Importer();
        importer.MarkSkipped("test");

        Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(_source, _dest).Kind);
        Assert.Equal(ProfileImportOutcome.Completed, (await importer.RunAsync(null, CancellationToken.None)).Outcome);
        Assert.False(File.Exists(Path.Combine(_dest, "settings.json")));
    }

    [Theory]
    [InlineData("crash.log", true)]
    [InlineData("crashlog-20261016-134342.log", true)]
    [InlineData("session.log", true)]
    [InlineData("auto-update.json", true)]
    [InlineData("settings.json", false)]
    [InlineData("library.db", false)]
    public void OfficialLogs_GoToTheirOwnFolder(string name, bool relocated)
        => Assert.Equal(relocated ? Path.Combine(ProfileImporter.ImportedLogsFolder, name) : name, ProfileImporter.TargetOf(name));

    [Fact]
    public void NestedLogs_StayWhereTheyAre()
        => Assert.Equal(Path.Combine("plugin-data", "x", "crash.log"), ProfileImporter.TargetOf(Path.Combine("plugin-data", "x", "crash.log")));

    [Fact]
    public void OfficialNoctisMutex_IsDetected()
    {
        var name = "NoctisPlusImportTest-" + Guid.NewGuid().ToString("N");
        Assert.False(ProfileImporter.IsMutexHeld(name));
        using (new Mutex(initiallyOwned: true, name))
            Assert.True(ProfileImporter.IsMutexHeld(name));
        Assert.False(ProfileImporter.IsMutexHeld(name));
    }

    // ── when it runs on its own ─────────────────────────────────────────────────────────────

    private Func<string, string?> Env(params (string Name, string Value)[] vars)
        => name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Startup_ReleaseOnItsDefaultProfile_ImportsTheOfficialProfile()
    {
        var appData = Path.Combine(_root, "Roaming");
        Write("settings.json", "{}");

        var importer = ProfileImportStartup.Prepare(Env(), debugBuild: false, _dest, appData);

        Assert.NotNull(importer);
        Assert.Equal(_source, importer!.SourceRoot);
        Assert.Equal(_dest, importer.DestinationRoot);
    }

    [Fact]
    public void Startup_HonoursOfficialNoctisDataDirOverride()
    {
        var elsewhere = Path.Combine(_root, "custom noctis");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "library.json"), "[]");

        var importer = ProfileImportStartup.Prepare(Env(("NOCTIS_DATA_DIR", elsewhere)), debugBuild: false, _dest, Path.Combine(_root, "Roaming"));

        Assert.Equal(elsewhere, importer!.SourceRoot);
    }

    [Fact]
    public void Startup_DebugBuildsAndCustomPlusProfiles_NeverImportOnTheirOwn()
    {
        var appData = Path.Combine(_root, "Roaming");
        Write("settings.json", "{}");

        Assert.Null(ProfileImportStartup.Prepare(Env(), debugBuild: true, _dest, appData));
        Assert.Null(ProfileImportStartup.Prepare(Env(("NOCTIS_PLUS_DATA_DIR", _dest)), debugBuild: false, _dest, appData));
    }

    [Fact]
    public void Startup_ExplicitTestSource_WorksInAnyBuild()
    {
        var fixture = Path.Combine(_root, "fixture profile");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "settings.json"), "{}");

        var importer = ProfileImportStartup.Prepare(Env((ProfileImportStartup.ImportFromVariable, fixture)), debugBuild: true, _dest, Path.Combine(_root, "Roaming"));

        Assert.Equal(fixture, importer!.SourceRoot);
        Assert.False(importer.IsSourceAppRunning());
    }

    [Fact]
    public void Startup_NothingToImport_StartsNormally()
    {
        Assert.Null(ProfileImportStartup.Prepare(Env(), debugBuild: false, _dest, Path.Combine(_root, "empty appdata")));
    }
}
