using Noctis.TelegramSync;
using Xunit;

namespace Noctis.TelegramSync.Tests;

public sealed class TrackMapTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"tgmap-{Guid.NewGuid():N}", "map.json");

    [Fact]
    public void Map_persists_both_directions_atomically()
    {
        var p = TempFile();
        var m = new TgTrackMap(p);
        m.Set(11, "a"); m.Set(22, "b"); m.Set(11, "c");
        m.Save();
        Assert.False(File.Exists(p + ".tmp"));
        var r = new TgTrackMap(p);
        Assert.Equal(2, r.Count);
        Assert.Equal("c", r.TrackFor(11));
        Assert.Equal(22, r.DocumentFor("b"));
        Assert.Null(r.DocumentFor("a"));
    }

    [Fact]
    public void Incoming_song_links_to_existing_local_file_by_size_and_key()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var x = path + ".x.mp3"; var y = path + ".y.mp3";
        File.WriteAllBytes(x, new byte[500]); File.WriteAllBytes(y, new byte[900]);
        var m = new TgTrackMap(path);
        var local = new[] { new TgLocalFile("t1", x, 500, "k1"), new TgLocalFile("t2", y, 900, "k2") };
        Assert.Equal("t2", m.LinkOrNull(new TgDocument(7, "y.mp3", 900), "k2", local));
        Assert.Equal(7, m.DocumentFor("t2"));
        Assert.Null(m.LinkOrNull(new TgDocument(8, "z.mp3", 901), "k2", local)); // size differs: download
        Assert.Null(m.LinkOrNull(new TgDocument(9, "z.mp3", 500), "other", local)); // key differs: download
    }

    [Fact]
    public void Corrupt_file_starts_empty()
    {
        var p = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "{not json");
        Assert.Equal(0, new TgTrackMap(p).Count);
    }

    [Fact]
    public void Snapshot_rule_every_n_then_old_posts_deletable()
    {
        var r = new TgSnapshotRule(3);
        Assert.False(r.SnapshotDue(2));
        Assert.True(r.SnapshotDue(3));
        Assert.False(r.CanDelete(5, null));
        Assert.True(r.CanDelete(5, 10));
        Assert.True(r.CanDelete(10, 10));
        Assert.False(r.CanDelete(11, 10)); // a newer snapshot post may not cover this concurrent edit
    }
}
