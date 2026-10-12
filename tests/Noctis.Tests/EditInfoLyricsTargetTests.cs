using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #135 in Edit Info: "Save lyrics to" decides whether edited lyrics go to the .lrc, the
/// audio file's tags, or both — and an unrelated save (a title fix) neither strips timed lyrics
/// out of the tags nor drops a new .lrc beside the song.
/// </summary>
public class EditInfoLyricsTargetTests : IDisposable
{
    private const string Lrc = "[00:01.00]first line\n[00:02.00]second line";
    private const string NewLrc = "[00:01.50]first line\n[00:02.50]second line";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "editinfo135-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _trashed = new();

    public EditInfoLyricsTargetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<(MetadataViewModel Vm, Track Track)> Open(string path, LyricsSaveTarget target)
    {
        var track = new MetadataService().ReadTrackMetadata(path)!;
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            TrashFile = p => { _trashed.Add(Path.GetFileName(p)); File.Delete(p); return true; },
            LyricsSaveTarget = () => target,
        };
        await vm.InitializeAsync();
        return (vm, track);
    }

    private static string Lf(string s) => s.Replace("\r\n", "\n");

    [Theory]
    [InlineData(LyricsSaveTarget.Sidecar)]
    [InlineData(LyricsSaveTarget.Embedded)]
    public async Task TitleFix_OnASongWithLrcInItsLyricsTag_KeepsTheTag_AndAddsNoLrc(LyricsSaveTarget target)
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "mp3", f => f.Tag.Lyrics = Lrc);
        var (vm, _) = await Open(path, target);
        Assert.Equal(Lrc, Lf(vm.SyncedLyrics));

        vm.Title = "A title fix";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        Assert.Equal("A title fix", new MetadataService().ReadTrackMetadata(path)!.Title);
        Assert.Equal(Lrc, Lf(EmbeddedLyrics.ReadFile(path).Text));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".lrc")));
    }

    [Fact]
    public async Task InsideTheFile_EditedSyncedLyrics_GoIntoTheTags_NotAnLrc()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac");
        var (vm, track) = await Open(path, LyricsSaveTarget.Embedded);

        vm.HasCustomSyncedLyrics = true;
        vm.SyncedLyrics = NewLrc;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".lrc")));
        Assert.Equal(NewLrc, Lf(EmbeddedLyrics.ReadFile(path).TimedText));
        Assert.Equal(NewLrc, Lf(track.SyncedLyrics));
    }

    [Fact]
    public async Task Both_EditedSyncedLyrics_GoToTheLrcAndTheTags()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "opus");
        var (vm, _) = await Open(path, LyricsSaveTarget.Both);

        vm.HasCustomSyncedLyrics = true;
        vm.SyncedLyrics = NewLrc;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(NewLrc, Lf(File.ReadAllText(Path.ChangeExtension(path, ".lrc"))));
        Assert.Equal(NewLrc, Lf(EmbeddedLyrics.ReadFile(path).TimedText));
    }

    [Fact]
    public async Task SeparateFile_EditedSyncedLyrics_GoToTheLrc_AndLeaveTheTagsAlone()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "mp3");
        var (vm, _) = await Open(path, LyricsSaveTarget.Sidecar);

        vm.HasCustomSyncedLyrics = true;
        vm.SyncedLyrics = NewLrc;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(NewLrc, Lf(File.ReadAllText(Path.ChangeExtension(path, ".lrc"))));
        Assert.True(string.IsNullOrEmpty(EmbeddedLyrics.ReadFile(path).TimedText));
    }

    [Fact]
    public async Task InsideTheFile_RemovedSyncedLyrics_LeaveTheTags()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "mp3");
        Assert.True(new MetadataService().WriteLyrics(path, "first line\nsecond line", Lrc));
        var (vm, _) = await Open(path, LyricsSaveTarget.Embedded);
        Assert.Equal(Lrc, Lf(vm.SyncedLyrics));

        vm.RemoveSyncedLyricsCommand.Execute(null);
        vm.HasCustomLyrics = false;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        Assert.True(EmbeddedLyrics.ReadFile(path).IsEmpty);
    }
}
