using System.Diagnostics;
using Noctis.Services.Sync;
using Noctis.TelegramSync;
using Xunit;

namespace Noctis.TelegramSync.Tests;

public sealed class SyncLogTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A shared real clock; each device may add its own skew.</summary>
    private sealed class World
    {
        public DateTime Now = T0;
        public FakeTelegramDrive Drive = new();
        public long Channel;
        public World()
        {
            Drive.Clock = () => Now;
            Channel = Drive.FindOrCreateChannelAsync(TgMarkers.Sync, "Noctis Plus Sync", TgMarkers.For(TgMarkers.Sync, Guid.NewGuid())).Result.Id;
        }
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);

        public TgSyncLog Device(string name, TimeSpan skew = default, string? cursorPath = null)
        {
            var ledger = new TgLedger(() => Now);
            return new TgSyncLog(Drive, Channel, name, ledger, () => Now + skew, cursorPath) { MinPostInterval = TimeSpan.FromSeconds(5) };
        }
    }

    private static SongSyncState Song(long doc, string title, string artist = "Artist", long durationMs = 200_000, DateTime? added = null, bool removed = false) =>
        new(doc, -100, doc, title, artist, "Album", durationMs, 5_000_000, added ?? T0, removed);

    private static async Task Sync(World w, params TgSyncLog[] devices)
    {
        foreach (var d in devices) { await d.FlushAsync(); }
        w.Advance(10);
        foreach (var d in devices) { await d.PullAsync(); }
    }

    [Fact]
    public async Task Two_devices_converge_after_adds_removes_hearts_and_ratings()
    {
        var w = new World();
        var pc = w.Device("pc");
        var phone = w.Device("phone");

        pc.RecordJson(TgKinds.Song, TgKinds.SongId(1), Song(1, "One"));
        pc.RecordJson(TgKinds.Song, TgKinds.SongId(2), Song(2, "Two"));
        phone.RecordJson(TgKinds.Song, TgKinds.SongId(3), Song(3, "Three"));
        await Sync(w, pc, phone);

        pc.RecordJson(TgKinds.Song, TgKinds.SongId(2), Song(2, "Two", removed: true));
        phone.RecordJson(TgKinds.Track, TgKinds.SongId(1), new TrackSyncState(true, 5, false, 0, null, w.Now));
        await Sync(w, pc, phone);

        Assert.Equal(pc.Ledger.Fingerprint(), phone.Ledger.Fingerprint());
        Assert.Equal(new[] { "tg:1", "tg:3" }, TgLibraryView.Songs(phone.Ledger).Select(s => s.Id).OrderBy(x => x));
        var heart = SyncJson.Deserialize<TrackSyncState>(pc.Ledger.Get(TgKinds.Track, "tg:1")!.Payload)!;
        Assert.True(heart.Favorite);
        Assert.Equal(5, heart.Rating);
    }

    [Fact]
    public async Task Three_devices_converge_and_concurrent_edits_tie_break_by_device_id()
    {
        var w = new World();
        var a = w.Device("a"); var b = w.Device("b"); var c = w.Device("c");
        var at = w.Now;
        a.RecordJson(TgKinds.Track, "tg:9", new TrackSyncState(true, 1, false, 0, null, null), at);
        b.RecordJson(TgKinds.Track, "tg:9", new TrackSyncState(false, 2, false, 0, null, null), at);
        c.RecordJson(TgKinds.Track, "tg:9", new TrackSyncState(true, 3, false, 0, null, null), at);
        await Sync(w, a, b, c);

        Assert.Equal(a.Ledger.Fingerprint(), b.Ledger.Fingerprint());
        Assert.Equal(b.Ledger.Fingerprint(), c.Ledger.Fingerprint());
        Assert.Equal("c", a.Ledger.Get(TgKinds.Track, "tg:9")!.Device); // same stamp: highest device id (ordinal) wins
    }

    [Fact]
    public async Task Later_edit_wins_over_earlier_one()
    {
        var w = new World();
        var a = w.Device("a"); var b = w.Device("b");
        b.RecordJson(TgKinds.Track, "tg:9", new TrackSyncState(false, 2, false, 0, null, null));
        w.Advance(60);
        a.RecordJson(TgKinds.Track, "tg:9", new TrackSyncState(true, 4, false, 0, null, null));
        await Sync(w, a, b);
        Assert.Equal(4, SyncJson.Deserialize<TrackSyncState>(b.Ledger.Get(TgKinds.Track, "tg:9")!.Payload)!.Rating);
    }

    [Fact]
    public async Task Device_clock_two_hours_ahead_does_not_hold_the_item()
    {
        var w = new World();
        var fast = w.Device("fast", TimeSpan.FromHours(2));
        var ok = w.Device("ok");
        fast.RecordJson(TgKinds.Track, "tg:1", new TrackSyncState(false, 1, false, 0, null, null));
        await Sync(w, fast, ok);
        // ok's stored copy was clamped to now + 2 min on the way in.
        Assert.True(ok.Ledger.Get(TgKinds.Track, "tg:1")!.UpdatedUtc <= w.Now + SyncStore.MaxClockSkew);

        w.Advance(300); // five real minutes later, the right clock edits it
        ok.RecordJson(TgKinds.Track, "tg:1", new TrackSyncState(true, 5, false, 0, null, null));
        await Sync(w, ok);
        Assert.Equal(5, SyncJson.Deserialize<TrackSyncState>(ok.Ledger.Get(TgKinds.Track, "tg:1")!.Payload)!.Rating);
    }

    [Fact]
    public async Task Posts_read_twice_or_out_of_order_apply_once()
    {
        var w = new World();
        var pc = w.Device("pc"); var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Plays, TgKinds.PlaysId("tg:1", "pc"), new PlayCounterState(3, w.Now));
        await pc.FlushAsync();
        w.Drive.DuplicateReads = true;
        Assert.Equal(1, await phone.PullAsync());
        Assert.Equal(0, await phone.PullAsync());
        Assert.True(phone.Cursor.Seen("pc", 1));
        Assert.Equal(3, TgLibraryView.PlayCount(phone.Ledger, "tg:1"));
    }

    [Fact]
    public void Cursor_folds_out_of_order_seqs_and_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tgcursor-{Guid.NewGuid():N}.json");
        try
        {
            var c = new TgSyncCursor();
            c.Mark("pc", 1); c.Mark("pc", 3); c.Mark("pc", 4);
            Assert.Equal(1, c.Contiguous["pc"]);
            Assert.True(c.Seen("pc", 3));
            Assert.False(c.Seen("pc", 2));
            c.LastMessageId = 77;
            c.Save(path);
            var back = TgSyncCursor.Load(path);
            back.Mark("pc", 2);
            Assert.Equal(4, back.Contiguous["pc"]);
            Assert.Equal(77, back.LastMessageId);
            Assert.False(back.Above.ContainsKey("pc"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reader_restarted_from_its_cursor_file_reads_only_new_posts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tgcursor-{Guid.NewGuid():N}.json");
        try
        {
            var w = new World();
            var pc = w.Device("pc");
            pc.RecordJson(TgKinds.Song, "tg:1", Song(1, "One"));
            await pc.FlushAsync();
            Assert.Equal(1, await w.Device("phone", cursorPath: path).PullAsync());
            w.Advance(10);
            pc.RecordJson(TgKinds.Song, "tg:2", Song(2, "Two"));
            await pc.FlushAsync();
            var restarted = w.Device("phone", cursorPath: path);
            Assert.Equal(1, await restarted.PullAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Play_counts_on_two_devices_while_apart_add_up()
    {
        var w = new World();
        var pc = w.Device("pc"); var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Plays, TgKinds.PlaysId("tg:1", "pc"), new PlayCounterState(4, w.Now));
        phone.RecordJson(TgKinds.Plays, TgKinds.PlaysId("tg:1", "phone"), new PlayCounterState(3, w.Now));
        await Sync(w, pc, phone);
        Assert.Equal(7, TgLibraryView.PlayCount(pc.Ledger, "tg:1"));
        Assert.Equal(7, TgLibraryView.PlayCount(phone.Ledger, "tg:1"));
    }

    [Fact]
    public async Task Playlist_adds_and_removes_on_two_devices_merge_per_song_and_delete_is_a_tombstone()
    {
        var w = new World();
        var pc = w.Device("pc"); var phone = w.Device("phone");
        var pl = Guid.NewGuid();
        pc.RecordJson(TgKinds.Playlist, pl.ToString("D"), new TgPlaylistHeader("Mix", "#112233", ["tg:1", "tg:2"]));
        pc.RecordJson(TgKinds.PlaylistEntry, TgKinds.EntryId(pl, "tg:1"), new PlaylistEntryState(true));
        pc.RecordJson(TgKinds.PlaylistEntry, TgKinds.EntryId(pl, "tg:2"), new PlaylistEntryState(true));
        await Sync(w, pc, phone);

        // Apart: the PC adds 3, the phone removes 1.
        w.Advance(5);
        pc.RecordJson(TgKinds.PlaylistEntry, TgKinds.EntryId(pl, "tg:3"), new PlaylistEntryState(true));
        phone.RecordJson(TgKinds.PlaylistEntry, TgKinds.EntryId(pl, "tg:1"), new PlaylistEntryState(false));
        await Sync(w, pc, phone);
        Assert.Equal(new[] { "tg:2", "tg:3" }, TgLibraryView.PlaylistSongs(pc.Ledger, pl));
        Assert.Equal(new[] { "tg:2", "tg:3" }, TgLibraryView.PlaylistSongs(phone.Ledger, pl));

        phone.RecordJson(TgKinds.Playlist, pl.ToString("D"), new TgPlaylistHeader("", "", [], Deleted: true));
        await Sync(w, pc, phone);
        Assert.Null(TgLibraryView.PlaylistSongs(pc.Ledger, pl));
    }

    [Fact]
    public async Task Removal_wins_membership_but_state_is_kept_for_a_re_add()
    {
        var w = new World();
        var pc = w.Device("pc"); var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Song, "tg:1", Song(1, "One"));
        await Sync(w, pc, phone);
        pc.RecordJson(TgKinds.Song, "tg:1", Song(1, "One", removed: true));
        phone.RecordJson(TgKinds.Track, "tg:1", new TrackSyncState(true, 0, false, 0, null, null));
        await Sync(w, pc, phone);
        Assert.Empty(TgLibraryView.Songs(phone.Ledger));
        Assert.NotNull(phone.Ledger.Get(TgKinds.Track, "tg:1"));
        w.Advance(5);
        phone.RecordJson(TgKinds.Song, "tg:1", Song(1, "One"));
        await Sync(w, pc, phone);
        Assert.Single(TgLibraryView.Songs(pc.Ledger));
        Assert.True(SyncJson.Deserialize<TrackSyncState>(pc.Ledger.Get(TgKinds.Track, "tg:1")!.Payload)!.Favorite);
    }

    [Fact]
    public async Task Same_recording_added_on_both_devices_shows_once_with_an_alternate()
    {
        var w = new World();
        var pc = w.Device("pc"); var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Song, "tg:10", Song(10, "Gol-e Sangam", "Banan", 180_000, T0));
        phone.RecordJson(TgKinds.Song, "tg:20", Song(20, "gol-e sangam (Remastered)", "BANAN", 181_500, T0.AddMinutes(1)));
        phone.RecordJson(TgKinds.Song, "tg:30", Song(30, "Gol-e Sangam", "Banan", 240_000, T0.AddMinutes(2))); // different length: another recording
        await Sync(w, pc, phone);
        var songs = TgLibraryView.Songs(pc.Ledger);
        Assert.Equal(2, songs.Count);
        Assert.Equal("tg:10", songs[0].Id);
        Assert.Equal(new[] { "tg:20" }, songs[0].AlternateIds);
    }

    [Fact]
    public async Task Flood_wait_keeps_the_batch_and_posting_is_paced()
    {
        var w = new World();
        var pc = w.Device("pc");
        pc.RecordJson(TgKinds.Song, "tg:1", Song(1, "One"));
        w.Drive.FailNextPosts = 1;
        w.Drive.FloodWait = TimeSpan.FromSeconds(30);
        Assert.False(await pc.FlushAsync());
        Assert.Equal(1, pc.PendingCount);
        w.Advance(10);
        Assert.False(await pc.FlushAsync()); // still inside the retry-after: no call at all
        Assert.Equal(0, w.Drive.Posts);
        w.Advance(25);
        Assert.True(await pc.FlushAsync());
        pc.RecordJson(TgKinds.Song, "tg:2", Song(2, "Two"));
        Assert.False(await pc.FlushAsync()); // paced: one post per 5 s
        w.Advance(5);
        Assert.True(await pc.FlushAsync());
        Assert.Equal(2, w.Drive.Posts);
    }

    [Fact]
    public async Task New_device_reads_snapshot_plus_newer_posts_even_when_old_posts_are_gone()
    {
        var w = new World();
        var pc = w.Device("pc");
        for (var i = 1; i <= 5; i++) { pc.RecordJson(TgKinds.Song, $"tg:{i}", Song(i, $"S{i}")); await pc.FlushAsync(); w.Advance(6); }
        var snap = await pc.PostSnapshotAsync();
        w.Drive.DeleteBefore(w.Channel, snap.Id); // old posts deleted after the snapshot
        w.Advance(6);
        pc.RecordJson(TgKinds.Song, "tg:6", Song(6, "S6"));
        await pc.FlushAsync();

        var phone = w.Device("phone");
        Assert.True(await phone.LoadSnapshotAsync());
        Assert.Equal(1, await phone.PullAsync());
        Assert.Equal(6, TgLibraryView.Songs(phone.Ledger).Count);
        Assert.Equal(pc.Ledger.Fingerprint(), phone.Ledger.Fingerprint());
    }

    [Fact]
    public async Task Missed_post_is_recovered_from_the_snapshot()
    {
        var w = new World();
        var pc = w.Device("pc"); var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Song, "tg:1", Song(1, "One"));
        await pc.FlushAsync();
        var lost = (await w.Drive.ReadHistoryAsync(w.Channel, 0)).Last().Id;
        await w.Drive.DeleteMessagesAsync(w.Channel, [lost]); // the phone never sees it
        await phone.PullAsync();
        Assert.Empty(TgLibraryView.Songs(phone.Ledger));

        await pc.PostSnapshotAsync();
        Assert.True(await phone.LoadSnapshotAsync());
        Assert.Single(TgLibraryView.Songs(phone.Ledger));
    }

    [Fact]
    public async Task Expired_file_reference_is_refreshed_by_re_reading_the_message()
    {
        var w = new World();
        var pc = w.Device("pc"); var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Song, "tg:1", Song(1, "One"));
        await pc.FlushAsync();
        var id = (await w.Drive.ReadHistoryAsync(w.Channel, 0)).Last().Id;
        w.Drive.ExpiredReferences.Add(id);
        Assert.Equal(1, await phone.PullAsync());
    }

    [Fact]
    public void Codec_gzips_above_3_kb_and_round_trips()
    {
        var small = new SyncBatch(1, "pc", 1, [new BatchItem("song", "tg:1", "{}", T0)]);
        var big = new SyncBatch(1, "pc", 2, Enumerable.Range(0, 200).Select(i => new BatchItem("song", $"tg:{i}", "{\"title\":\"x\"}", T0)).ToList());
        var s = BatchCodec.Encode(small);
        var b = BatchCodec.Encode(big);
        Assert.Equal((byte)'{', s[0]);
        Assert.Equal(0x1F, b[0]);
        Assert.Equal(200, BatchCodec.Decode(b)!.Items.Count);
        Assert.Equal("tg:1", BatchCodec.Decode(s)!.Items[0].Id);
        Assert.Null(BatchCodec.Decode("not json"u8));
    }

    [Fact]
    public async Task Channel_is_found_again_by_marker_not_title()
    {
        var drive = new FakeTelegramDrive();
        var first = await drive.FindOrCreateChannelAsync(TgMarkers.Sync, "Noctis Plus Sync", TgMarkers.For(TgMarkers.Sync, Guid.NewGuid()));
        var again = await drive.FindOrCreateChannelAsync(TgMarkers.Sync, "Renamed by the user", TgMarkers.For(TgMarkers.Sync, Guid.NewGuid()));
        var library = await drive.FindOrCreateChannelAsync(TgMarkers.Library, "Noctis Plus Sync", TgMarkers.For(TgMarkers.Library, Guid.NewGuid()));
        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, library.Id);
    }

    [Fact]
    public async Task Budget_merge_of_10000_songs_and_10000_track_states_and_snapshot()
    {
        var w = new World();
        var pc = w.Device("pc");
        var items = new List<BatchItem>(20_000);
        for (var i = 0; i < 10_000; i++)
        {
            items.Add(new BatchItem(TgKinds.Song, $"tg:{i}", SyncJson.Serialize(Song(i, $"Title number {i}", $"Artist {i % 700}", 180_000 + i)), T0));
            items.Add(new BatchItem(TgKinds.Track, $"tg:{i}", SyncJson.Serialize(new TrackSyncState(i % 3 == 0, i % 6, false, i % 50, T0, null)), T0));
        }
        var batch = new SyncBatch(1, "phone", 1, items);
        var encoded = BatchCodec.Encode(batch);

        var sw = Stopwatch.StartNew();
        var decoded = BatchCodec.Decode(encoded)!;
        foreach (var it in decoded.Items) pc.Ledger.Apply(it, decoded.Device);
        var merge = sw.Elapsed;

        sw.Restart();
        await pc.PostSnapshotAsync();
        var snapshot = sw.Elapsed;
        var pinned = await w.Drive.GetPinnedAsync(w.Channel);
        var size = pinned!.Document!.Size;

        TestContext.Current.TestOutputHelper?.WriteLine($"[budget] decode+merge 20,000 items: {merge.TotalMilliseconds:F0} ms; snapshot build+post: {snapshot.TotalMilliseconds:F0} ms, {size / 1024.0:F0} KB gzip");
        Assert.Equal(20_000, pc.Ledger.Count);
        Assert.True(merge < TimeSpan.FromMilliseconds(1000), $"merge {merge.TotalMilliseconds} ms (budget 200 ms on PC; generous bound)");
        Assert.True(snapshot < TimeSpan.FromMilliseconds(2500), $"snapshot {snapshot.TotalMilliseconds} ms (budget 500 ms; generous bound)");
        Assert.True(size < 2 * 1024 * 1024, $"snapshot {size} bytes (budget 2 MB)");
    }

    [Fact]
    public async Task Reading_250_posts_takes_three_history_calls()
    {
        var w = new World();
        var pc = w.Device("pc");
        for (var i = 0; i < 250; i++) { pc.RecordJson(TgKinds.Song, $"tg:{i}", Song(i, $"S{i}")); await pc.FlushAsync(); w.Advance(6); }
        var phone = w.Device("phone");
        Assert.Equal(250, await phone.PullAsync());
        Assert.Equal(3, phone.HistoryCalls);
    }

    [Fact]
    public async Task Item_recorded_while_a_flush_is_posting_is_kept_and_posted_next()
    {
        var w = new World();
        var pc = w.Device("pc");
        var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Song, TgKinds.SongId(1), Song(1, "One"));
        var nested = true;
        w.Drive.OnPost = () =>
        {
            w.Drive.OnPost = null;
            pc.RecordJson(TgKinds.Song, TgKinds.SongId(2), Song(2, "Two"));
            nested = pc.FlushAsync().GetAwaiter().GetResult(); // one flush at a time
        };
        Assert.True(await pc.FlushAsync());
        Assert.False(nested);
        Assert.Equal(1, pc.PendingCount);
        w.Advance(10);
        Assert.True(await pc.FlushAsync());
        Assert.Equal(0, pc.PendingCount);
        await phone.PullAsync();
        Assert.Equal(new[] { "tg:1", "tg:2" }, TgLibraryView.Songs(phone.Ledger).Select(s => s.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Reinstall_with_the_same_device_id_never_reuses_a_seq()
    {
        var w = new World();
        var pc = w.Device("pc");
        var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Song, TgKinds.SongId(1), Song(1, "One"));
        await Sync(w, pc, phone);

        var pc2 = w.Device("pc"); // fresh cursor, same id
        await pc2.PullAsync();
        pc2.RecordJson(TgKinds.Song, TgKinds.SongId(2), Song(2, "Two"));
        w.Advance(10);
        Assert.True(await pc2.FlushAsync());
        Assert.Equal(2, pc2.Cursor.OwnSeq);
        await phone.PullAsync();
        Assert.Equal(new[] { "tg:1", "tg:2" }, TgLibraryView.Songs(phone.Ledger).Select(s => s.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Seq_is_saved_before_the_post_so_a_crash_after_posting_does_not_reuse_it()
    {
        var w = new World();
        var path = Path.Combine(Path.GetTempPath(), $"tgcur-{Guid.NewGuid():N}.json");
        try
        {
            var pc = w.Device("pc", cursorPath: path);
            pc.RecordJson(TgKinds.Song, TgKinds.SongId(1), Song(1, "One"));
            long savedDuringPost = -1;
            w.Drive.OnPost = () => { w.Drive.OnPost = null; savedDuringPost = TgSyncCursor.Load(path).OwnSeq; };
            Assert.True(await pc.FlushAsync());
            Assert.Equal(1, savedDuringPost);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Failed_batch_download_is_read_again_on_the_next_pull()
    {
        var w = new World();
        var pc = w.Device("pc");
        var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Song, TgKinds.SongId(1), Song(1, "One"));
        Assert.True(await pc.FlushAsync());
        w.Drive.FailNextDownloads = 1;
        await Assert.ThrowsAsync<TelegramFloodWaitException>(() => phone.PullAsync());
        Assert.Equal(0, phone.Cursor.LastMessageId);
        Assert.Equal(1, await phone.PullAsync());
        Assert.Equal(new[] { "tg:1" }, TgLibraryView.Songs(phone.Ledger).Select(s => s.Id));
    }

    [Fact]
    public async Task Flood_wait_rejection_gives_the_seq_back_so_readers_see_no_gap()
    {
        var w = new World();
        var pc = w.Device("pc");
        var phone = w.Device("phone");
        pc.RecordJson(TgKinds.Song, TgKinds.SongId(1), Song(1, "One"));
        w.Drive.FailNextPosts = 1;
        Assert.False(await pc.FlushAsync());
        Assert.Equal(0, pc.Cursor.OwnSeq);
        w.Advance(60);
        Assert.True(await pc.FlushAsync());
        await phone.PullAsync();
        Assert.Equal(1, phone.Cursor.Contiguous["pc"]);
        Assert.False(phone.Cursor.Above.ContainsKey("pc"));
    }
}
