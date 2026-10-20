using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

public class PlaybackDownloadWindowTests
{
    [Fact]
    public void RestartParksSpeculativeRequestsWithoutLosingRecordingMetadata_AndKeepsExplicitSaves()
    {
        using var data = new TestPersistenceService(); var cache = Path.Combine(data.DataDirectory, "cache");
        var library = Path.Combine(data.DataDirectory, "library"); var store = Path.Combine(data.DataDirectory, "queue.json");
        BotTrack Song(string name) => Track(name) with { Request = new(name, "Synthetic", 180) };
        using (var first = new Downloads(_ => { }, store, r => Song(r.Title), cache))
        {
            first.SetPaused(true);
            first.Start(Song("played"), cache, held: true);
            first.Start(Song("explicit"), library, held: true);
        }
        using var restored = new Downloads(_ => { }, store, r => Song(r.Title), cache);
        Assert.DoesNotContain(restored.Active, d => d.Track.Title == "played");
        var parked = Assert.Single(restored.History, d => d.Track.Title == "played");
        Assert.Equal(DownloadState.Cancelled, parked.State); Assert.NotNull(parked.Track.Request);
        Assert.Contains(restored.Active, d => d.Track.Title == "explicit");
    }
    private static BotTrack Track(string name) => new(name, "Synthetic", TimeSpan.FromMinutes(3), name,
        (_, _, ct) => Task.FromCanceled<string>(ct.IsCancellationRequested ? ct : new CancellationToken(true)));

    [Fact]
    public void UnchangedWindowDoesNotEmitAChangeLoop_ForAnAlmostFinishedOldTransfer()
    {
        using var data = new TestPersistenceService(); var cache = Path.Combine(data.DataDirectory, "cache");
        using var downloads = new Downloads(_ => { }) { CacheFolder = cache };
        downloads.SetPaused(true);
        var almost = downloads.Start(Track("almost"), cache, held: true); almost.State = DownloadState.Running; almost.Progress = .9;
        int changes = 0; downloads.Changed += _ => changes++;
        downloads.SetPlaybackWindow(["new"]); Assert.Equal(1, changes);
        for (int i = 0; i < 100; i++) downloads.SetPlaybackWindow(["new"]);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void DotsInsideTitlesDoNotCollapseDifferentRecordingNames()
    {
        using var data = new TestPersistenceService(); var cache = Path.Combine(data.DataDirectory, "cache");
        using var downloads = new Downloads(_ => { }) { CacheFolder = cache };
        downloads.SetPaused(true); downloads.SetPlaybackWindow(["Mr. A.flac"]);
        Assert.Equal(DownloadState.Removed, downloads.Start(Track("Mr. B"), cache, held: true).State);
        Assert.Equal(DownloadState.Queued, downloads.Start(Track("Mr. A"), cache, held: true).State);
    }

    [Fact]
    public void RapidOneFourEightClicks_LeaveOnlyTheLatestThreeAudioRequests_AndExplicitSaves()
    {
        using var data = new TestPersistenceService();
        var cache = Path.Combine(data.DataDirectory, "cache"); var library = Path.Combine(data.DataDirectory, "library");
        using var downloads = new Downloads(_ => { }) { CacheFolder = cache };
        downloads.SetPaused(true);
        var first = Enumerable.Range(1, 3).Select(i => downloads.Start(Track(i.ToString()), cache, held: true)).ToArray();
        var explicitSave = downloads.Start(Track("explicit"), library, held: true);
        var added = downloads.Start(Track("added"), cache, held: true); downloads.Keep(added, library);
        downloads.SetPlaybackWindow(["4", "5", "6"]);
        Assert.All(first, d => Assert.Equal(DownloadState.Cancelled, d.State));
        var middle = Enumerable.Range(4, 3).Select(i => downloads.Start(Track(i.ToString()), cache, held: true)).ToArray();
        downloads.SetPlaybackWindow(["8", "9", "10"]);
        Assert.All(middle, d => Assert.Equal(DownloadState.Cancelled, d.State));
        foreach (var i in new[] { "8", "9", "10" }) downloads.Start(Track(i), cache, held: true);
        Assert.Equal(new[] { "10", "8", "9" }, downloads.Active.Where(d => d.KeepIn is null && downloads.IsCached(d)).Select(d => d.Track.FileName).Order());
        Assert.Contains(explicitSave, downloads.Active); Assert.Contains(added, downloads.Active);
        // A slow earlier QueueAsync completion cannot put 4 back into the queue.
        Assert.Equal(DownloadState.Removed, downloads.Start(Track("4"), cache, held: true).State);
        Assert.DoesNotContain(downloads.Active, d => d.Track.FileName == "4");
    }

    [Fact]
    public void AlmostFinishedOldTransferMayFinishOnlyIntoCache_AndWaitingOldOnesAreRemoved()
    {
        using var data = new TestPersistenceService(); var cache = Path.Combine(data.DataDirectory, "cache");
        using var downloads = new Downloads(_ => { }) { CacheFolder = cache };
        downloads.SetPaused(true);
        var almost = downloads.Start(Track("almost"), cache, held: true); almost.State = DownloadState.Running; almost.Progress = .86;
        var waiting = downloads.Start(Track("waiting"), cache, held: true); waiting.State = DownloadState.Waiting;
        downloads.SetPlaybackWindow(["new"]);
        Assert.True(almost.FinishIntoCache); Assert.Contains(almost, downloads.Active);
        Assert.Equal(DownloadState.Cancelled, waiting.State);
        Assert.DoesNotContain(waiting, downloads.Active);
    }
}
