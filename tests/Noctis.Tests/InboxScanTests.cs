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
/// Inbox for new music (Discord request, MusicBee's Inbox): with Settings › Library › Inbox
/// on, a song a scan or the folder watcher finds for the first time in a music folder that
/// already holds songs waits in the Inbox, out of Songs/Albums/Artists, until it is added to
/// the library or deleted. A fresh library's first scan and a newly added music folder are
/// an import and skip it.
/// </summary>
[Collection("MetadataServiceStatics")]
public class InboxScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;
    private readonly RecordingAudit _audit = new();

    public InboxScanTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(Path.Combine(_music, "Album"));
        WriteMp3(Path.Combine(_music, "Album", "01 - First.mp3"), "First");
        WriteMp3(Path.Combine(_music, "Album", "02 - Second.mp3"), "Second");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string DataDir => Path.Combine(_root, "data");
    private string NewDownload => Path.Combine(_music, "Downloads", "New One.mp3");

    private static void WriteMp3(string path, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        Retag(path, title);
    }

    private static void Retag(string path, string title)
    {
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = "Fixture Album";
            f.Save();
        }
        // A rescan notices a change by size + mtime; make sure the clock moved.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    private LibraryService NewLibrary(PersistenceService persistence) =>
        new(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), _audit);

    private async Task<(LibraryService Library, PersistenceService Persistence)> LibraryAsync(bool inbox)
    {
        var persistence = new PersistenceService(DataDir);
        var settings = new AppSettings { InboxEnabled = inbox };
        settings.MusicFolders.Add(_music);
        await persistence.SaveSettingsAsync(settings);
        return (NewLibrary(persistence), persistence);
    }

    /// <summary>A library already scanned with the Inbox on, plus one new download in it.</summary>
    private async Task<(LibraryService Library, PersistenceService Persistence)> WithInboxSongAsync()
    {
        var (library, persistence) = await LibraryAsync(inbox: true);
        await library.ScanAsync(new[] { _music });
        Assert.Equal(2, library.Tracks.Count);
        Assert.Empty(library.InboxTracks);

        WriteMp3(NewDownload, "New One");
        await library.ScanAsync(new[] { _music });
        return (library, persistence);
    }

    private static Track? ByTitle(IEnumerable<Track> tracks, string title)
        => tracks.FirstOrDefault(t => t.Title == title);

    private static async Task SetInboxAsync(PersistenceService persistence, bool on)
    {
        var settings = await persistence.LoadSettingsAsync();
        settings.InboxEnabled = on;
        await persistence.SaveSettingsAsync(settings);
    }

    [Fact]
    public async Task NewFileFoundByARescan_WaitsInTheInbox_OutOfTheLibraryViews()
    {
        var (library, _) = await WithInboxSongAsync();

        var inbox = Assert.Single(library.InboxTracks);
        Assert.Equal("New One", inbox.Title);
        Assert.True(inbox.IsInInbox);
        Assert.Equal(2, library.Tracks.Count);
        Assert.Null(ByTitle(library.Tracks, "New One"));
        // Same album tag as the library's songs, yet not grouped in with them.
        var album = Assert.Single(library.Albums);
        Assert.Equal(2, album.TrackCount);
        Assert.DoesNotContain(album.Tracks, t => t.Id == inbox.Id);
        Assert.Equal(2, library.Artists.Sum(a => a.TrackCount));
        // Still found by id: it plays, queues and survives the player's "deleted" purge.
        Assert.Same(inbox, library.GetTrackById(inbox.Id));
        Assert.Contains(library.AllTracks, t => t.Id == inbox.Id);
    }

    [Fact]
    public async Task FreshLibrarysFirstScan_IsAnImport_NotTheInbox()
    {
        var (library, _) = await LibraryAsync(inbox: true);

        await library.ScanAsync(new[] { _music });

        Assert.Equal(2, library.Tracks.Count);
        Assert.Empty(library.InboxTracks);
    }

    [Fact]
    public async Task NewlyAddedMusicFolder_IsAnImport_NotTheInbox()
    {
        var (library, persistence) = await LibraryAsync(inbox: true);
        await library.ScanAsync(new[] { _music });

        var second = Path.Combine(_root, "second");
        WriteMp3(Path.Combine(second, "Other.mp3"), "Other");
        var settings = await persistence.LoadSettingsAsync();
        settings.MusicFolders.Add(second);
        await persistence.SaveSettingsAsync(settings);
        await library.ScanAsync(new[] { _music, second });

        Assert.Equal(3, library.Tracks.Count);
        Assert.Empty(library.InboxTracks);

        // Once that folder holds songs, the next new file in it is a new arrival.
        WriteMp3(Path.Combine(second, "Later.mp3"), "Later");
        await library.ScanAsync(new[] { _music, second });
        Assert.Equal("Later", Assert.Single(library.InboxTracks).Title);
    }

    [Fact]
    public async Task InboxOff_NewFilesGoStraightIntoTheLibrary()
    {
        var (library, _) = await LibraryAsync(inbox: false);
        await library.ScanAsync(new[] { _music });

        WriteMp3(NewDownload, "New One");
        await library.ScanAsync(new[] { _music });

        Assert.Empty(library.InboxTracks);
        Assert.NotNull(ByTitle(library.Tracks, "New One"));
    }

    [Fact]
    public async Task KnownSongs_NeverGoToTheInbox_EvenWhenReTagged()
    {
        var (library, _) = await WithInboxSongAsync();

        Retag(Path.Combine(_music, "Album", "01 - First.mp3"), "First (Remaster)");
        await library.ScanAsync(new[] { _music });

        Assert.NotNull(ByTitle(library.Tracks, "First (Remaster)"));
        Assert.Single(library.InboxTracks);
    }

    /// <summary>The scan matched files against the visible index only: an unchanged Inbox
    /// song came back as a brand-new track (play count and DateAdded gone).</summary>
    [Fact]
    public async Task InboxSong_KeepsItsStateAndMark_ThroughRescans()
    {
        var (library, _) = await WithInboxSongAsync();
        var before = Assert.Single(library.InboxTracks);
        before.PlayCount = 7;
        var dateAdded = before.DateAdded;

        await library.ScanAsync(new[] { _music });
        var unchanged = Assert.Single(library.InboxTracks);
        Assert.Same(before, unchanged);
        Assert.Equal(7, unchanged.PlayCount);
        Assert.Equal(dateAdded, unchanged.DateAdded);

        Retag(NewDownload, "New One (Fixed Tags)");
        await library.ScanAsync(new[] { _music });
        var retagged = Assert.Single(library.InboxTracks);
        Assert.Equal("New One (Fixed Tags)", retagged.Title);
        Assert.Equal(before.Id, retagged.Id);
        Assert.Equal(7, retagged.PlayCount);
        Assert.Equal(2, library.Tracks.Count);
    }

    [Fact]
    public async Task TheMark_SurvivesARestart_AndOnlyInboxSongsCarryIt()
    {
        var (library, persistence) = await WithInboxSongAsync();
        var id = Assert.Single(library.InboxTracks).Id;

        var json = await File.ReadAllTextAsync(Path.Combine(DataDir, "library.json"));
        Assert.Equal(1, json.Split("\"isInInbox\":true").Length - 1);
        Assert.DoesNotContain("\"isInInbox\":false", json);

        var restarted = NewLibrary(persistence);
        await restarted.LoadAsync();
        await restarted.BackgroundInit;
        Assert.Equal(id, Assert.Single(restarted.InboxTracks).Id);
        Assert.Equal(2, restarted.Tracks.Count);
        Assert.NotNull(restarted.GetTrackById(id));

        await restarted.ScanAsync(new[] { _music });
        Assert.Equal(id, Assert.Single(restarted.InboxTracks).Id);
    }

    [Fact]
    public async Task Accept_MovesTheSongIntoTheLibrary_AndPersists()
    {
        var (library, persistence) = await WithInboxSongAsync();
        var song = Assert.Single(library.InboxTracks);
        var discovered = song.DateAdded;
        var updates = 0;
        library.LibraryUpdated += (_, _) => updates++;

        await library.AcceptInboxTracksAsync(new[] { song.Id });

        Assert.Empty(library.InboxTracks);
        Assert.False(song.IsInInbox);
        Assert.True(song.DateAdded >= discovered);
        Assert.Equal(3, library.Tracks.Count);
        Assert.Equal(3, Assert.Single(library.Albums).TrackCount);
        Assert.Equal(1, updates);

        var restarted = NewLibrary(persistence);
        await restarted.LoadAsync();
        Assert.Empty(restarted.InboxTracks);
        Assert.Equal(3, restarted.Tracks.Count);

        // Accepted for good: the next scan does not send it back.
        await restarted.ScanAsync(new[] { _music });
        Assert.Empty(restarted.InboxTracks);
    }

    [Fact]
    public async Task Accept_IgnoresIdsNotInTheInbox()
    {
        var (library, _) = await WithInboxSongAsync();
        var known = ByTitle(library.Tracks, "First")!;
        var updates = 0;
        library.LibraryUpdated += (_, _) => updates++;

        await library.AcceptInboxTracksAsync(new[] { known.Id, Guid.NewGuid() });

        Assert.Single(library.InboxTracks);
        Assert.Equal(0, updates);
    }

    [Fact]
    public async Task Delete_RemovesTheSong_AndTheNextScanDoesNotBringItBack()
    {
        var (library, _) = await WithInboxSongAsync();
        var song = Assert.Single(library.InboxTracks);

        // The Inbox's Delete runs the app's Remove-from-Library flow (Keep Files / Recycle Bin).
        await library.RemoveTracksAsync(new[] { song.Id });

        Assert.Empty(library.InboxTracks);
        Assert.Null(library.GetTrackById(song.Id));
        await library.ScanAsync(new[] { _music });
        Assert.Empty(library.InboxTracks);
        Assert.Equal(2, library.Tracks.Count);
    }

    [Fact]
    public async Task InboxOff_ReleasesMarkedSongsOnLoad()
    {
        var (library, persistence) = await WithInboxSongAsync();
        var id = Assert.Single(library.InboxTracks).Id;
        await SetInboxAsync(persistence, on: false);

        var restarted = NewLibrary(persistence);
        await restarted.LoadAsync();

        Assert.Empty(restarted.InboxTracks);
        Assert.Equal(3, restarted.Tracks.Count);
        Assert.False(restarted.GetTrackById(id)!.IsInInbox);
    }

    [Fact]
    public async Task WatcherFoundFile_GoesToTheInbox_ButTheUsersOwnImportDoesNot()
    {
        var (library, _) = await LibraryAsync(inbox: true);
        await library.ScanAsync(new[] { _music });

        WriteMp3(NewDownload, "New One");
        await ((ILibraryService)library).ImportDiscoveredFilesAsync(new[] { NewDownload });
        Assert.Equal("New One", Assert.Single(library.InboxTracks).Title);

        var dropped = Path.Combine(_music, "Album", "03 - Dropped.mp3");
        WriteMp3(dropped, "Dropped");
        await library.ImportFilesAsync(new[] { dropped });
        Assert.NotNull(ByTitle(library.Tracks, "Dropped"));
        Assert.Single(library.InboxTracks);
    }

    /// <summary>A conversion or download saved into a music folder: the folder watcher's batch
    /// (1.5 s debounce) usually reaches the file before the converter's or downloader's own
    /// import, which runs once every file is done. That user add must still take the song into
    /// the library, unchanged since the watcher read it or re-tagged in between.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsersOwnImport_OfAFileTheWatcherPutInTheInbox_TakesItIntoTheLibrary(bool changedSince)
    {
        var (library, _) = await LibraryAsync(inbox: true);
        await library.ScanAsync(new[] { _music });

        WriteMp3(NewDownload, "New One");
        await ((ILibraryService)library).ImportDiscoveredFilesAsync(new[] { NewDownload });
        Assert.Single(library.InboxTracks);

        if (changedSince) Retag(NewDownload, "New One (cover added)");
        await library.ImportFilesAsync(new[] { NewDownload });

        Assert.Empty(library.InboxTracks);
        var song = Assert.Single(library.Tracks, t => t.FilePath == NewDownload);
        Assert.False(song.IsInInbox);
    }

    [Fact]
    public async Task InboxSongUnderAHiddenFolder_IsInNeitherList()
    {
        var (library, _) = await WithInboxSongAsync();
        var song = Assert.Single(library.InboxTracks);

        await library.SetFolderHiddenAsync(Path.Combine(_music, "Downloads"), hidden: true);
        Assert.Empty(library.InboxTracks);
        Assert.Equal(2, library.Tracks.Count);

        await library.SetFolderHiddenAsync(Path.Combine(_music, "Downloads"), hidden: false);
        Assert.Same(song, Assert.Single(library.InboxTracks));
    }

    [Fact]
    public void RootsHoldingTracks_SkipsFoldersWithNoKnownSongs()
    {
        var empty = Path.Combine(_root, "empty");
        var tracks = new List<Track> { new() { FilePath = Path.Combine(_music, "Album", "01 - First.mp3") } };

        var roots = LibraryService.RootsHoldingTracks(new[] { _music, empty }, tracks);

        Assert.Equal(new[] { _music }, roots);
    }

    private sealed class RecordingAudit : IAuditTrailService
    {
        public readonly List<string> Events = new();
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
        {
            lock (Events) Events.Add(auditEvent.EventType);
            return Task.CompletedTask;
        }
    }
}
