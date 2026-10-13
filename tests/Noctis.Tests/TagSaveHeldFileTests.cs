using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Live, 10-13 (Windows 11): Edit Info on the playing .m4a (paused) → change lyrics → Save
/// kept the dialog open with "Couldn't write tags", log "Tag save failed …: Access to the
/// path is denied." The save tags a copy and renames it over the original, and File.Move
/// (MoveFileEx) refuses to replace a file while any handle is open on it, even one shared
/// for delete. LibVLC's handle on the playing track shares delete (VlcTagSaveReleaseTests),
/// so a POSIX-semantics rename replaces it untouched; a reader that does not share delete
/// (the side decode's ffmpeg) is asked to let go of the file instead.
/// </summary>
[Collection("Tag save replace")] // shares MetadataService.PosixReplaceDisabled with VlcTagSaveReleaseTests
public class TagSaveHeldFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public TagSaveHeldFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>ffmpeg's open of its input (_wsopen, _SH_DENYNO): shared for read and write, not delete.</summary>
    private static FileStream OpenWithoutDeleteSharing(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    /// <summary>LibVLC's open of the playing track: shared for read, write and delete.</summary>
    private static FileStream OpenLikeLibVlc(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string ReadLyrics(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Tag.Lyrics ?? "";
    }

    /// <summary>A holder stand-in that does not share delete: lets go of the file on request
    /// and reopens it afterwards, recording what it saw.</summary>
    private sealed class FakePlayer : IOpenFileHolder, IDisposable
    {
        private readonly string _path;
        public FileStream? Handle;
        public int Releases;
        public int Reopens;
        public string? LyricsSeenOnReopen;

        public FakePlayer(string path)
        {
            _path = path;
            Handle = OpenWithoutDeleteSharing(path);
        }

        public Action? Release(string path)
        {
            if (Handle == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                return null;
            Releases++;
            Handle.Dispose();
            Handle = null;
            return () =>
            {
                Reopens++;
                LyricsSeenOnReopen = ReadLyrics(_path);
                Handle = OpenWithoutDeleteSharing(_path);
            };
        }

        public void Dispose() => Handle?.Dispose();
    }

    [Fact]
    public void Save_OverAFileHeldWithoutDeleteSharing_IsRefused_WhenNothingLetsGo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file sharing rules");
        var path = MultiGenreTagTests.CreateM4a(_dir);
        var before = File.ReadAllBytes(path);
        using (OpenWithoutDeleteSharing(path))
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                MetadataService.SaveTagsAtomicallyOrThrow(path, f => f.Tag.Lyrics = "new words"));
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir)); // no .noctis-* copy left behind
    }

    [Fact]
    public void Save_OverThePlayingFile_HasThePlayerLetGo_ThenReopen()
    {
        var path = MultiGenreTagTests.CreateM4a(_dir);
        using var player = new FakePlayer(path);
        OpenFileHolders.Register(player);
        try
        {
            MetadataService.SaveTagsAtomicallyOrThrow(path, f => f.Tag.Lyrics = "new words");
        }
        finally
        {
            OpenFileHolders.Unregister(player);
        }

        Assert.Equal("new words", ReadLyrics(path));
        Assert.Single(Directory.GetFiles(_dir));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(1, player.Releases);
            Assert.Equal(1, player.Reopens);
            // Reopened on the saved file, not on the old one.
            Assert.Equal("new words", player.LyricsSeenOnReopen);
            Assert.NotNull(player.Handle);
        }
    }

    [Fact]
    public void Save_OfAFileNobodyHolds_NeverInterruptsThePlayer()
    {
        var playing = MultiGenreTagTests.CreateM4a(_dir);
        var otherDir = Path.Combine(_dir, "other");
        Directory.CreateDirectory(otherDir);
        var other = MultiGenreTagTests.CreateM4a(otherDir);
        using var player = new FakePlayer(playing);
        OpenFileHolders.Register(player);
        try
        {
            MetadataService.SaveTagsAtomicallyOrThrow(other, f => f.Tag.Lyrics = "other words");
        }
        finally
        {
            OpenFileHolders.Unregister(player);
        }

        Assert.Equal("other words", ReadLyrics(other));
        Assert.Equal(0, player.Releases);
        Assert.Equal(0, player.Reopens);
    }

    [Fact]
    public void Save_ThatStillCannotReplaceTheFile_ReopensThePlayerAnyway()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file sharing rules");
        var path = MultiGenreTagTests.CreateM4a(_dir);
        using var player = new FakePlayer(path);
        OpenFileHolders.Register(player);
        try
        {
            // Another app keeps the file open too: the rename still fails.
            using (OpenWithoutDeleteSharing(path))
            {
                Assert.Throws<UnauthorizedAccessException>(() =>
                    MetadataService.SaveTagsAtomicallyOrThrow(path, f => f.Tag.Lyrics = "new words"));
            }
        }
        finally
        {
            OpenFileHolders.Unregister(player);
        }

        Assert.Equal(1, player.Releases);
        Assert.Equal(1, player.Reopens);
        Assert.NotNull(player.Handle);
        Assert.Equal("", player.LyricsSeenOnReopen);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_OverAFileHeldLikeLibVlc_ReplacesIt_WithoutAskingTheHolder_AndTheHolderKeepsTheOldData()
    {
        var path = MultiGenreTagTests.CreateM4a(_dir);
        var before = File.ReadAllBytes(path);
        var asked = new Holder("player", new List<string>(), holds: true);
        OpenFileHolders.Register(asked);
        try
        {
            using var held = OpenLikeLibVlc(path);
            MetadataService.SaveTagsAtomicallyOrThrow(path, f => f.Tag.Lyrics = "new words");

            Assert.Equal("new words", ReadLyrics(path));
            // The open handle still reads the file as it was (POSIX rename on Windows,
            // rename(2) elsewhere): playback is not touched.
            var old = new byte[before.Length];
            held.Position = 0;
            held.ReadExactly(old);
            Assert.Equal(before, old);
        }
        finally
        {
            OpenFileHolders.Unregister(asked);
        }
        Assert.Equal(0, asked.Releases);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void RunReleased_ReopensEveryHolderThatLetGo_InReverseOrder_EvenWhenTheActionThrows()
    {
        var order = new List<string>();
        var a = new Holder("a", order, holds: true);
        var b = new Holder("b", order, holds: false);
        var c = new Holder("c", order, holds: true);
        foreach (var h in new[] { a, b, c }) OpenFileHolders.Register(h);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                OpenFileHolders.RunReleased(Path.Combine(_dir, "x.m4a"), () =>
                {
                    order.Add("action");
                    throw new InvalidOperationException();
                }));
        }
        finally
        {
            foreach (var h in new[] { a, b, c }) OpenFileHolders.Unregister(h);
        }

        Assert.Equal(new[] { "release a", "release b", "release c", "action", "reopen c", "reopen a" }, order);
    }

    private sealed class Holder(string name, List<string> order, bool holds) : IOpenFileHolder
    {
        public int Releases;

        public Action? Release(string path)
        {
            Releases++;
            if (!path.EndsWith("x.m4a", StringComparison.Ordinal)) return null;
            order.Add("release " + name);
            return holds ? () => order.Add("reopen " + name) : null;
        }
    }
}
