using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A file another program has open the moment Noctis reads it: a download or copy finishing,
/// a tag editor saving, an antivirus or indexer scan. The tag read hit a sharing violation and
/// returned null, so the scan (or the folder watcher's import) silently left the song out, and
/// nothing brought it back until a later full scan: the watcher had already used its event.
/// A briefly busy file is now waited out; one still held after that is left for the next pass,
/// never recorded as read.
/// </summary>
[Collection("MetadataServiceStatics")]
public class ScanBusyFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "busy-" + Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public ScanBusyFileTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(Path.Combine(_music, "Album"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Song(string name) => Path.Combine(_music, "Album", name);

    /// <summary>The bytes of a tagged fixture mp3 (one MPEG-1 Layer III frame repeated, see
    /// FileSystemSourceScanTests), built beside the music folder so a test can write them
    /// into place the way a download or copy would.</summary>
    private byte[] Mp3Bytes(string title)
    {
        var scratch = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mp3");
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(scratch))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using (var f = TagLib.File.Create(scratch))
        {
            f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = "Fixture Album";
            f.Save();
        }
        var bytes = File.ReadAllBytes(scratch);
        File.Delete(scratch);
        return bytes;
    }

    private void WriteMp3(string path, string title) => File.WriteAllBytes(path, Mp3Bytes(title));

    private async Task<LibraryService> LibraryAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        return new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
    }

    /// <summary>Runs <paramref name="then"/> after <paramref name="ms"/> on a thread of its own, so
    /// a busy thread pool (the scan's own workers) cannot hold it back past the read's wait.</summary>
    private static Task After(int ms, Action then)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            Thread.Sleep(ms);
            try { then(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true }.Start();
        return done.Task;
    }

    [Fact]
    public async Task ReadTrackMetadata_FileHeldOpenForAMoment_IsReadOnceItIsLetGo()
    {
        var path = Song("01 - First.mp3");
        WriteMp3(path, "First");

        var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var released = After(300, holder.Dispose);
        var track = new MetadataService().ReadTrackMetadata(path);
        await released;

        Assert.NotNull(track);
        Assert.Equal("First", track!.Title);
    }

    /// <summary>A song whose download finishes while the scan runs: its writer still has it
    /// open, half written, when the scan reaches it.</summary>
    [Fact]
    public async Task Scan_NewFileStillBeingWritten_ComesInOnceTheWriterFinishes()
    {
        WriteMp3(Song("01 - First.mp3"), "First");
        var bytes = Mp3Bytes("Second");
        var writer = new FileStream(Song("02 - Second.mp3"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        writer.Write(bytes, 0, bytes.Length / 2);
        writer.Flush();
        var library = await LibraryAsync();

        var finished = After(300, () =>
        {
            writer.Write(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2);
            writer.Dispose();
        });
        await library.ScanAsync(new[] { _music }, TestContext.Current.CancellationToken);
        await finished;

        Assert.Equal(new[] { "First", "Second" }, library.Tracks.Select(t => t.Title).OrderBy(t => t));
        Assert.Equal(bytes.Length, library.Tracks.Single(t => t.Title == "Second").FileSize);
    }

    /// <summary>The folder watcher only imports a file that looks finished, but another program
    /// can open it in the moment between that check and the tag read.</summary>
    [Fact]
    public async Task Import_FileHeldOpenForAMoment_StillComesIn()
    {
        var path = Song("01 - First.mp3");
        WriteMp3(path, "First");
        var library = await LibraryAsync();

        var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var released = After(300, holder.Dispose);
        await library.ImportDiscoveredFilesAsync(new[] { path }, TestContext.Current.CancellationToken);
        await released;

        Assert.Equal("First", Assert.Single(library.Tracks).Title);
    }

    /// <summary>A file held for longer than the wait is left out of this scan, not recorded as
    /// read: the next scan reads it like any new file.</summary>
    [Fact]
    public async Task Scan_FileHeldTheWholeScan_IsLeftForTheNextScan()
    {
        WriteMp3(Song("01 - First.mp3"), "First");
        WriteMp3(Song("02 - Second.mp3"), "Second");
        var library = await LibraryAsync();

        using (new FileStream(Song("02 - Second.mp3"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await library.ScanAsync(new[] { _music }, TestContext.Current.CancellationToken);
        Assert.Equal("First", Assert.Single(library.Tracks).Title);
        // Named in the session log, not only counted.
        var log = DebugLog.Snapshot();
        Assert.Contains("skipped, unreadable: " + Song("02 - Second.mp3"), log);
        Assert.Contains("Skipped '02 - Second.mp3': still in use", log);

        await library.ScanAsync(new[] { _music }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "First", "Second" }, library.Tracks.Select(t => t.Title).OrderBy(t => t));
    }

    /// <summary>A writer that lets others read and write (some downloaders) and stays open the
    /// whole scan. Windows still refuses the read (TagLib does not share write), elsewhere the
    /// scan reads the part written so far. Either way the finished file is read by the next
    /// scan: what was recorded, if anything, has another size, so no unchanged-file fast path.</summary>
    [Fact]
    public async Task Scan_FileReadWhileStillGrowing_IsReadAgainOnceFinished()
    {
        WriteMp3(Song("01 - First.mp3"), "First");
        var bytes = Mp3Bytes("Second");
        var library = await LibraryAsync();

        using (var writer = new FileStream(Song("02 - Second.mp3"), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write(bytes, 0, bytes.Length - 417 * 20);
            writer.Flush();
            await library.ScanAsync(new[] { _music }, TestContext.Current.CancellationToken);
            writer.Write(bytes, bytes.Length - 417 * 20, 417 * 20);
        }
        await library.ScanAsync(new[] { _music }, TestContext.Current.CancellationToken);

        var second = library.Tracks.Single(t => t.Title == "Second");
        Assert.Equal(bytes.Length, second.FileSize);
        Assert.Equal(2, library.Tracks.Count);
    }

    /// <summary>Windows: a byte-range lock (what some backup and sync tools take) lets the open
    /// succeed and fails the reads instead. Advisory elsewhere, where reads never fail.</summary>
    [Fact]
    public async Task ReadTrackMetadata_RangeLockedForAMoment_IsReadOnceUnlocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Song("01 - First.mp3");
        WriteMp3(path, "First");

        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        holder.Lock(0, holder.Length);
        var released = After(300, holder.Dispose);
        var track = new MetadataService().ReadTrackMetadata(path);
        await released;

        Assert.NotNull(track);
        Assert.Equal("First", track!.Title);
    }
}
