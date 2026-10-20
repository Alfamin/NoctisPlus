using System.Text.Json;
using Noctis.Features.WordLyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #96: once a run is over, a song copy WordLyrics made is removed only when its song is still there byte for
/// byte; any copy that might be the only one left is kept.</summary>
public class WordLyricsBackupCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-backup-clean-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Run(string source, string backup, IEnumerable<string> songs)
    {
        var run = Directory.CreateDirectory(Path.Combine(_root, "runs", "lib", "run " + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllLines(Path.Combine(run, "summary.txt"), ["2 songs in " + source, "", "Backup copy: " + backup, "Check of the music folder: original audio preserved"]);
        File.WriteAllText(Path.Combine(run, "request.json"), JsonSerializer.Serialize(new { schema = 1, source, songs = songs.ToArray() }));
        return run;
    }

    [Fact]
    public void ACopyIdenticalToItsSong_IsRemoved_AnyOtherCopyIsKept()
    {
        var music = Directory.CreateDirectory(Path.Combine(_root, "music")).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(_root, "WordLyrics Backups", "music 2026-10-20 (single songs)")).FullName;
        File.WriteAllBytes(Path.Combine(music, "same.flac"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(backup, "same.flac"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(music, "changed.flac"), [9, 9, 9, 9]);
        File.WriteAllBytes(Path.Combine(backup, "changed.flac"), [1, 2, 3, 4]);   // same length, other bytes
        File.WriteAllBytes(Path.Combine(backup, "gone.flac"), [5, 6]);            // its song was deleted since
        var run = Run(music, backup, [Path.Combine(music, "same.flac"), Path.Combine(music, "changed.flac"), Path.Combine(music, "gone.flac")]);

        var result = BackupCleaner.CleanRun(run);

        Assert.Equal(1, result.Removed);
        Assert.Equal(2, result.Kept);
        Assert.False(File.Exists(Path.Combine(backup, "same.flac")));
        Assert.True(File.Exists(Path.Combine(backup, "changed.flac")));
        Assert.True(File.Exists(Path.Combine(backup, "gone.flac")));
        Assert.True(File.Exists(Path.Combine(music, "same.flac")));                // the song itself is never touched
    }

    [Fact]
    public void AWholeFolderCopy_IsMatchedByItsPlaceInTheMusicFolder_AndEmptyFoldersGo()
    {
        var music = Directory.CreateDirectory(Path.Combine(_root, "music2")).FullName;
        Directory.CreateDirectory(Path.Combine(music, "Album"));
        var backup = Path.Combine(_root, "WordLyrics Backups", "music2 2026-10-20");
        Directory.CreateDirectory(Path.Combine(backup, "Album"));
        File.WriteAllBytes(Path.Combine(music, "Album", "a.flac"), [7, 7]);
        File.WriteAllBytes(Path.Combine(backup, "Album", "a.flac"), [7, 7]);
        File.WriteAllText(backup + ".wordlyrics.json", "{}");
        var run = Run(music, backup, []);

        Assert.Equal(1, BackupCleaner.CleanRun(run).Removed);
        Assert.False(Directory.Exists(backup));
        Assert.False(File.Exists(backup + ".wordlyrics.json")); // its note goes with it
    }

    [Fact]
    public void ABackupPlaceInsideTheMusic_IsNeverCleaned()
    {
        var music = Directory.CreateDirectory(Path.Combine(_root, "music3")).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(music, "copies")).FullName;
        File.WriteAllBytes(Path.Combine(music, "s.flac"), [1]);
        File.WriteAllBytes(Path.Combine(backup, "s.flac"), [1]);
        var run = Run(music, backup, [Path.Combine(music, "s.flac")]);

        Assert.Equal(0, BackupCleaner.CleanRun(run).Removed);
        Assert.True(File.Exists(Path.Combine(backup, "s.flac")));
    }
}
