using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A scan publishes the tracks it has found so far every 1.5 s so a first scan fills the
/// views in live. On a rescan of a library it already has, that partial set used to REPLACE
/// the library: every known song the walk had not reached yet vanished from Songs, Albums
/// and Artists (and the counts) until it was walked again, then came back — on a phone
/// with Scan on Startup this was the library shrinking right after launch. The known
/// tracks must stay while the walk runs; a file that is gone still drops out at the end.
/// </summary>
[Collection("MetadataServiceStatics")]
public class RescanProgressivePublishTests : IDisposable
{
    private const int FileCount = 6;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public RescanProgressivePublishTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(_music);
        for (var i = 1; i <= FileCount; i++)
            WriteMp3(Path.Combine(_music, $"{i:00} - Song {i}.mp3"), $"Song {i}", "Tester", "Fixture Album");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Rescan_KeepsEveryKnownTrackOnScreenWhileTheWalkRuns_AndDropsDeletedFilesAtTheEnd()
    {
        var source = new SlowSource(_music);
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var library = new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence),
            new NoOpAudit(), fileSystem: source);
        await library.ScanAsync(new[] { "fake://tree/music" }, TestContext.Current.CancellationToken);
        Assert.Equal(FileCount, library.Tracks.Count);

        // One file gone, every other file unchanged; the walk is slow enough for the
        // progressive publisher to run several times before it ends.
        File.Delete(Path.Combine(_music, "06 - Song 6.mp3"));
        source.DelayPerEntry = TimeSpan.FromMilliseconds(700);
        var counts = new List<int>();
        library.LibraryUpdated += (_, _) => { lock (counts) counts.Add(library.Tracks.Count); };

        await library.ScanAsync(new[] { "fake://tree/music" }, TestContext.Current.CancellationToken);

        Assert.Equal(FileCount - 1, library.Tracks.Count);
        lock (counts)
        {
            // The partial publishes come first, then the final one (raised again at "done"):
            // never fewer than the library already had until the deleted file is taken out.
            var partial = counts.TakeWhile(c => c != FileCount - 1).ToList();
            Assert.True(partial.Count >= 1, $"expected partial publishes, got [{string.Join(", ", counts)}]");
            Assert.All(partial, c => Assert.Equal(FileCount, c));
            Assert.All(counts.Skip(partial.Count), c => Assert.Equal(FileCount - 1, c));
        }
    }

    private static void WriteMp3(string path, string title, string artist, string album)
    {
        // One MPEG-1 Layer III frame header padded to its size, repeated — enough for TagLib.
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { artist }; f.Tag.Album = album;
        f.Save();
    }

    /// <summary>The fixture folder as pathless "fake://" entries, yielded with a pause each.</summary>
    private sealed class SlowSource : IFileSystemSource
    {
        private readonly string _dir;
        public TimeSpan DelayPerEntry = TimeSpan.Zero;
        public SlowSource(string dir) => _dir = dir;

        public bool RootExists(string root) => true;

        public IEnumerable<ScanEntry> EnumerateAudioFiles(string root, IReadOnlyCollection<string> excludedRoots,
            IReadOnlySet<string> ignoredFolderNames, Action<string> reportFailedDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(_dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (!MetadataService.SupportedExtensions.Contains(Path.GetExtension(file))) continue;
                if (DelayPerEntry > TimeSpan.Zero) Thread.Sleep(DelayPerEntry);
                var fi = new FileInfo(file);
                var captured = file;
                yield return new ScanEntry(root + "/" + fi.Name, fi.Name, fi.Length, fi.LastWriteTimeUtc, LocalPath: null,
                    OpenRead: () => File.OpenRead(captured));
            }
        }
    }

    private sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
