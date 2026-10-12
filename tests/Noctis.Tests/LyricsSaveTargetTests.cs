using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #135: Settings ▸ Lyrics ▸ "Save lyrics to" — Separate file (.lrc) / Inside the audio file
/// / Both — honoured by the shared writer behind Save Lyrics to File, Lyrics Studio and the bulk
/// fetch. The default is the behaviour from before the setting: files only.
/// </summary>
public class LyricsSaveTargetTests : IDisposable
{
    internal sealed class RecordingMetadata : IMetadataService
    {
        public readonly List<(string Path, string? Plain, string? Synced, bool Guarded)> LyricsWrites = new();
        public int TrackWrites;
        public bool Result = true;

        public bool WriteLyrics(string filePath, string? plain, string? synced, Func<EmbeddedLyricsText, bool>? onlyIf = null)
        {
            lock (LyricsWrites) LyricsWrites.Add((filePath, plain, synced, onlyIf != null));
            return Result;
        }

        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) { Interlocked.Increment(ref TrackWrites); return true; }
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    /// <summary>Runs queued writes synchronously, so a test sees them at once.</summary>
    private sealed class InlineTagWriter : IDeferredTagWriter
    {
        public readonly List<string> Keys = new();
        public void Enqueue(string path, string key, Action write) { Keys.Add(key); write(); }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Func<string?>? InUsePath { get; set; }
        public int PendingCount => 0;
    }

    private const string Elrc = "[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.00>";
    private const string Lrc = "[00:01.00]Hello world";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "target135-" + Guid.NewGuid().ToString("N"));
    private readonly string _audio;
    private readonly List<string> _trashed = new();
    private readonly RecordingMetadata _metadata = new();
    private readonly InlineTagWriter _tags = new();
    private readonly AppWrittenSidecarRegistry _registry;

    public LyricsSaveTargetTests()
    {
        Directory.CreateDirectory(_dir);
        _audio = Path.Combine(_dir, "song.flac");
        File.WriteAllText(_audio, "x");
        _registry = new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LyricsWriter Writer(LyricsSaveTarget target) => new(_metadata, _tags, _registry, Path.Combine(_dir, "cache"))
    {
        TrashFile = p => { _trashed.Add(Path.GetFileName(p)); File.Delete(p); return true; },
        SaveTarget = () => target,
    };

    private Track NewTrack() => new() { Title = "Song", FilePath = _audio, SourceType = SourceType.Local };

    private string Side(string ext) => Path.ChangeExtension(_audio, ext);

    [Fact]
    public void Default_IsSeparateFiles_AsBefore()
    {
        Assert.Equal(LyricsSaveTarget.Sidecar, LyricsSaveOptions.ParseTarget(new AppSettings().LyricsSaveTarget));
        Assert.Equal(LyricsSaveTarget.Sidecar, LyricsSaveOptions.ParseTarget("garbage"));
        Assert.Equal(LyricsSaveTarget.Sidecar, LyricsSaveSettings.CurrentTarget()); // no shell in tests
        Assert.Equal(LyricsPreferredType.WordSynced, LyricsSaveOptions.ParsePreferredType(new AppSettings().LyricsPreferredType));
        Assert.False(new AppSettings().LyricsAutoDownload);
    }

    [Fact]
    public void SeparateFile_WritesSidecars_AndNoTags()
    {
        var outcome = Writer(LyricsSaveTarget.Sidecar).SaveDetailed(NewTrack(), null, Elrc, embedInTags: false, replaceForeignSidecar: true);

        Assert.True(outcome.SidecarWritten);
        Assert.False(outcome.TagsQueued);
        Assert.True(File.Exists(Side(".elrc")));
        Assert.True(File.Exists(Side(".lrc")));
        Assert.Empty(_metadata.LyricsWrites);
        Assert.Equal(0, _metadata.TrackWrites);
    }

    [Fact]
    public void InsideTheFile_WritesTheWordTimedTextToTheTags_AndNoFile()
    {
        var outcome = Writer(LyricsSaveTarget.Embedded).SaveDetailed(NewTrack(), null, Elrc, embedInTags: false, replaceForeignSidecar: true);

        Assert.True(outcome.Saved);
        Assert.True(outcome.TagsQueued);
        Assert.False(outcome.SidecarWritten);
        Assert.False(File.Exists(Side(".elrc")));
        Assert.False(File.Exists(Side(".lrc")));
        var write = Assert.Single(_metadata.LyricsWrites);
        Assert.Equal((_audio, "Hello world", Elrc), (write.Path, write.Plain, write.Synced));
        Assert.Equal(new[] { "lyrics" }, _tags.Keys); // deferred: never while the song plays
    }

    [Fact]
    public void Both_WritesSidecarsAndTags()
    {
        var outcome = Writer(LyricsSaveTarget.Both).SaveDetailed(NewTrack(), "Hello world", Lrc, embedInTags: false, replaceForeignSidecar: true);

        Assert.True(outcome.SidecarWritten);
        Assert.True(outcome.TagsQueued);
        Assert.Equal(Lrc, File.ReadAllText(Side(".lrc")));
        Assert.Single(_metadata.LyricsWrites);
    }

    [Fact]
    public void InsideTheFile_RemovesTheAppsOwnSidecars_ThatWouldHideTheTags()
    {
        var w = Writer(LyricsSaveTarget.Sidecar);
        w.SaveDetailed(NewTrack(), null, Elrc, embedInTags: false, replaceForeignSidecar: false);
        Assert.True(File.Exists(Side(".lrc")));

        Writer(LyricsSaveTarget.Embedded).SaveDetailed(NewTrack(), null, Lrc, embedInTags: false, replaceForeignSidecar: false);

        Assert.False(File.Exists(Side(".lrc")));
        Assert.False(File.Exists(Side(".elrc")));
        Assert.False(_registry.Contains(Side(".lrc")));
    }

    [Fact]
    public void InsideTheFile_AutomaticSave_LeavesTheUsersOwnLrc_ExplicitSaveTrashesIt()
    {
        File.WriteAllText(Side(".lrc"), "[00:05.00]the user's own");

        Writer(LyricsSaveTarget.Embedded).SaveDetailed(NewTrack(), null, Lrc, embedInTags: false, replaceForeignSidecar: false);
        Assert.True(File.Exists(Side(".lrc")));
        Assert.Empty(_trashed);

        var outcome = Writer(LyricsSaveTarget.Embedded).SaveDetailed(NewTrack(), null, Lrc, embedInTags: false, replaceForeignSidecar: true);
        Assert.True(outcome.ReplacedForeignSidecar);
        Assert.Equal(new[] { "song.lrc" }, _trashed);
    }

    [Fact]
    public void InsideTheFile_AnExplicitTtml_IsStillWritten()
    {
        var outcome = Writer(LyricsSaveTarget.Embedded).SaveDetailed(NewTrack(), null, Lrc, embedInTags: false, replaceForeignSidecar: true, ttml: "<tt/>");

        Assert.True(File.Exists(Side(".ttml")));
        Assert.False(File.Exists(Side(".lrc")));
        Assert.True(outcome.TagsQueued);
    }

    [Fact]
    public void FilesChangedBySave_InsideTheFile_NamesTheLrcEvenWhenItHoldsTheSameLyrics()
    {
        File.WriteAllText(Side(".lrc"), Lrc);

        Assert.Empty(LyricsWriter.FilesChangedBySave(_audio, Lrc));
        Assert.Equal(new[] { "“song.lrc”" }, LyricsWriter.FilesChangedBySave(_audio, Lrc, target: LyricsSaveTarget.Embedded));
    }

    [Fact]
    public void LegacyEmbedOption_StillWritesPlainThroughTheTrackSave_WhenSavingToFiles()
    {
        Writer(LyricsSaveTarget.Sidecar).SaveDetailed(NewTrack(), "Hello world", Lrc, embedInTags: true, replaceForeignSidecar: false);

        Assert.Equal(1, _metadata.TrackWrites);
        Assert.Empty(_metadata.LyricsWrites);
    }

    [Fact]
    public void StreamedSong_NeverWritesTags()
    {
        var track = NewTrack();
        track.SourceType = SourceType.Jellyfin;

        var outcome = Writer(LyricsSaveTarget.Embedded).SaveDetailed(track, null, Lrc, embedInTags: false, replaceForeignSidecar: true);

        Assert.False(outcome.TagsQueued);
        Assert.Empty(_metadata.LyricsWrites);
    }

    [Fact]
    public void Remove_WithTags_ClearsEveryLyricsField()
    {
        Writer(LyricsSaveTarget.Embedded).Remove(NewTrack(), clearTags: true);

        var write = Assert.Single(_metadata.LyricsWrites);
        Assert.Null(write.Plain);
        Assert.Null(write.Synced);
    }
}
