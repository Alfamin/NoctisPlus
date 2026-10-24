using System.Net;
using System.Text;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class QueueManagementTests
{
    private static BotTrack Track(string title, Func<CancellationToken, Task>? before = null) =>
        new(title, "Artist", null, "Artist - " + title, async (output, _, ct) =>
        {
            if (before is not null) await before(ct);
            await output.WriteAsync(Encoding.ASCII.GetBytes("fLaC test data"), ct); return "flac";
        }) { Request = new(title, "Artist") };
    public static async Task RunAsync()
    {
        SharedCheckpointRead();
        await RetiredWorkerCannotOverwriteReplacement();
        await Priority(); await RemovalAndRestart(); await CancelRunning(); await ClearAndStorageFailure(); await ClearImports();
        await WorkerSurvivesSaveFailure(); await ReportWhileHeld();
        await SavedHistoryPaths();
        await StallWatchdog();
    }
    private static async Task SavedHistoryPaths()
    {
        Check.Section("saved download history maps to native library rows without touching files");
        var root = Check.NewFolder("queue-saved-paths"); var store = Path.Combine(root, "queue.json");
        var track = Track("History");
        using (var queue = new Downloads(_ => { }, store, r => Track(r.Title)))
        {
            var download = queue.Start(track, root);
            await Check.UntilAsync(() => download.State == DownloadState.Saved, "history path fixture finishes");
            var expected = Path.Combine(root, track.FileName + ".flac");
            Check.Equal(expected, download.SavedPath!, "saved history carries the real completed file path");
            var adapter = new FinderDownloadQueue(queue);
            Check.Equal(expected, adapter.Snapshot().Single().SavedPath!, "native queue adapter publishes completed path");
        }
        using (var restored = new Downloads(_ => { }, store, r => Track(r.Title)))
        {
            var adapter = new FinderDownloadQueue(restored); var entry = adapter.Snapshot().Single();
            Check.True(File.Exists(entry.SavedPath), "completed path restored after restart");
            var bytes = File.ReadAllBytes(entry.SavedPath!);
            adapter.Remove(entry);
            Check.True(adapter.Snapshot().Count == 0 && bytes.SequenceEqual(File.ReadAllBytes(entry.SavedPath!)), "remove saved history only removes entry and preserves exact audio bytes");
        }
        Check.True(Downloader.FindExisting(root, "../escape") is null, "legacy filename cannot escape its download folder while resolving history");
    }
    private static async Task RetiredWorkerCannotOverwriteReplacement()
    {
        Check.Section("a retired queue worker cannot overwrite its replacement checkpoint");
        var root = Check.NewFolder("queue-retired-worker"); var path = Path.Combine(root, "queue.json");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var old = new Downloads(_ => { }, path, r => Track(r.Title));
        old.Changed += d => { if (d.State == DownloadState.Cancelled) retired.TrySetResult(); };
        old.Start(Track("Old running", async _ => { entered.SetResult(); await release.Task; }), root);
        await entered.Task;
        old.Dispose();
        // Read the synchronous handover checkpoint while the cancelled transfer is still
        // deliberately held inside a source that has not observed cancellation yet.
        using var replacement = new Downloads(_ => { }, path, r => Track(r.Title));
        replacement.Clear();
        replacement.Start(Track("New held request"), root, held: true);
        var expected = File.ReadAllBytes(path);
        release.SetResult();
        await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(expected.SequenceEqual(File.ReadAllBytes(path)), "late cancellation never overwrites a newer checkpoint");
        Check.Equal("New held request", replacement.Active.Single().Track.Title, "replacement queue retains only its own pending request");
        Check.Equal(DownloadState.Cancelled, old.Start(Track("After disposal"), root).State, "retired queue cannot accept new requests");
    }
    private static void SharedCheckpointRead()
    {
        var root = Check.NewFolder("queue-shared-checkpoint"); var path = Path.Combine(root, "queue.json");
        AtomicJson.Write(path, new { Version = 1, Paused = true, Items = new[] { new
            { Request = new MusicRequest("Retained", "Artist"), Folder = root, State = 0, Error = "", RetryAt = (DateTimeOffset?)null } } });
        // Model the short-lived writer/replace handle around an atomic save. Never read a real user queue.
        using var checkpoint = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var restored = new Downloads(_ => { }, path, r => Track(r.Title));
        Check.True(restored.Paused && restored.Active.Single().Track.Title == "Retained", "queue restore reads a stable checkpoint while a shareable writer/rename handle is open");
        var legacyPath = Path.Combine(root, "legacy-utf16.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { Version = 1, Paused = true, Items = new[] { new
            { Request = new MusicRequest("Unicode فارسی", "Artist"), Folder = root, State = 0, Error = "", RetryAt = (DateTimeOffset?)null } } }), Encoding.Unicode);
        using var legacy = new Downloads(_ => { }, legacyPath, r => Track(r.Title));
        Check.Equal("Unicode فارسی", legacy.Active.Single().Track.Title, "shared checkpoint reads retain legacy UTF-16/BOM and Unicode song support");
    }
    private static async Task WorkerSurvivesSaveFailure()
    {
        Check.Section("a queue file held open by another program does not stop the download worker");
        var root = Check.NewFolder("queue-save-failure"); var store = Path.Combine(root, "queue.json");
        var logs = new List<string>();
        using var queue = new Downloads(text => { lock (logs) logs.Add(text); }, store, r => Track(r.Title));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.Start(Track("While locked", async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); }), root);
        await entered.Task;
        using (File.Open(store, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            release.SetResult();
            await Check.UntilAsync(() => first.State == DownloadState.Saved, "the song finishes while its queue file cannot be written");
            await Task.Delay(150); // the worker's save after the song fails meanwhile
        }
        var second = queue.Start(Track("After the lock"), root);
        await Check.UntilAsync(() => second.State == DownloadState.Saved, "the worker goes on with the next song after a failed save");
        lock (logs) Check.Equal(1, logs.Count(l => l.StartsWith("QUEUE_SAVE_FAILED")), "the failed save is logged once");
        Check.True(File.ReadAllText(store).Contains("After the lock"), "the queue file is written again once it is free");
    }
    private static async Task ReportWhileHeld()
    {
        Check.Section("a finished download is reported while playlist songs wait for Start");
        var root = Check.NewFolder("queue-report-held");
        using var queue = new Downloads(_ => { });
        var finished = new List<string>(); queue.Finished += text => { lock (finished) finished.Add(text); };
        queue.Start(Track("Held one"), root, held: true); queue.Start(Track("Held two"), root, held: true);
        queue.Start(Track("Wanted now"), root, priority: true);
        await Check.UntilAsync(() => { lock (finished) return finished.Count == 1; }, "Download now is reported although staged songs still wait");
        lock (finished) Check.Equal("Saved to library", finished[0], "a completed priority download uses the compact save notice");
        Check.Equal(2, queue.Active.Count(d => d.Held), "the staged songs still wait for Start");
    }
    private static async Task Priority()
    {
        Check.Section("manual downloads bypass a 25-song batch in their own lane and respect pause/limits");
        var root = Check.NewFolder("queue-priority"); var order = new List<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new Downloads(_ => { });
        queue.Start(Track("Current", async ct => { lock (order) order.Add("Current"); await gate.Task.WaitAsync(ct); }), root);
        await Check.UntilAsync(() => { lock(order)return order.Count==1; }, "current file is transferring");
        for (int i = 0; i < 25; i++)
        {
            var title = "Batch " + i;
            queue.Start(Track(title, _ => { lock (order) order.Add(title); return Task.CompletedTask; }), root);
        }
        var manual = queue.Start(Track("Manual", _ => { lock (order) order.Add("Manual"); return Task.CompletedTask; }), root, priority: true);
        Check.Equal(27, queue.Active.Count, "manual request retained alongside all batch songs");
        // Owner #79: a Play waited a whole other song's transfer before any bot was asked.
        await Check.UntilAsync(() => { lock (order) return order.Contains("Manual"); }, "manual request starts while the current transfer is still running");
        await Task.Delay(150);
        Check.Equal("Current,Manual", string.Join(",", order), "manual request does not interrupt current transfer, and the batch keeps to one lane");
        Check.Equal(DownloadState.Running, queue.Active.First(d => d.Track.Title == "Current").State, "current transfer keeps running beside the manual one");
        gate.SetResult();
        await Check.UntilAsync(() => queue.Active.Count == 0, "priority and batch complete");
        Check.Equal("Current,Manual", string.Join(",", order.Take(2)), "manual download runs before all 25 queued songs");
        Check.Equal(string.Join(",", Enumerable.Range(0, 25).Select(i => "Batch " + i)), string.Join(",", order.Skip(2)), "batch order preserved");
        queue.SetPaused(true);
        var batch = queue.Start(Track("Paused batch"), root);
        var next = queue.Start(Track("Urgent while paused"), root, true);
        await Check.UntilAsync(() => next.State == DownloadState.Saved, "manual priority works with paused batch");
        Check.Equal(DownloadState.Queued, batch.State, "batch stays paused after manual song");
        queue.DownloadNext(batch);
        await Check.UntilAsync(() => batch.State == DownloadState.Saved, "existing queued request can be promoted without duplication");
        Check.Equal(1, queue.History.Count(d => d == batch), "promoted song has only one request");
        var calls = 0; var until = DateTimeOffset.UtcNow.AddHours(1);
        var limited = queue.Start(Track("Limited", _ => { calls++; throw new SourcesWaitingException("Wait", until); }), root, true);
        await Check.UntilAsync(() => limited.State == DownloadState.Waiting, "priority retains provider cooldown");
        queue.DownloadNext(limited); await Task.Delay(100);
        Check.Equal(1, calls, "download next does not bypass cooldown or spam the bot");
        queue.Remove(limited);
        await TwoLanesAtMost();
    }
    private static async Task TwoLanesAtMost()
    {
        Check.Section("Play requests share two transfer lanes; a third waits for a free one");
        var root = Check.NewFolder("queue-lanes"); var started = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new Downloads(_ => { });
        Download Play(string title) => queue.Start(Track(title, async ct => { lock (started) started.Add(title); await release.Task.WaitAsync(ct); }), root, priority: true);
        var one = Play("One"); var two = Play("Two"); var three = Play("Three");
        await Check.UntilAsync(() => { lock (started) return started.Count == 2; }, "two Play requests transfer side by side");
        await Task.Delay(150);
        Check.Equal("One,Two", string.Join(",", started), "the third Play waits; Telegram never carries more than two transfers");
        Check.Equal(DownloadState.Queued, three.State, "the third Play is next in line");
        var removed = queue.Remove(two);
        await Check.UntilAsync(() => { lock (started) return started.Contains("Three"); }, "removing a running request frees its lane at once");
        Check.True(removed && two.State == DownloadState.Removed && one.State == DownloadState.Running, "only the removed transfer was cancelled");
        release.SetResult();
        await Check.UntilAsync(() => one.State == DownloadState.Saved && three.State == DownloadState.Saved, "both remaining lanes finish");
        if (one.State != DownloadState.Saved || three.State != DownloadState.Saved)
            Console.WriteLine($"  LANES one={one.State} \"{one.Error}\" three={three.State} \"{three.Error}\" two={two.State} active={queue.Active.Count} started={string.Join(",", started)}");
        await LatestPlayFirst();
    }
    private static async Task LatestPlayFirst()
    {
        // Owner #83: Kevin's Heart, Blessings, Sundress... the song clicked last waited for the earlier ones' transfers.
        Check.Section("the song clicked to play last starts at once; earlier ones wait until it is done");
        var root = Check.NewFolder("queue-play-now"); var starts = new List<string>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new Downloads(_ => { });
        Download Play(string title, Task until) => queue.Start(Track(title, async ct => { lock (starts) starts.Add(title); await until.WaitAsync(ct); }), root, priority: true);
        var kevin = Play("Kevin's Heart", hold.Task); var blessings = Play("Blessings", hold.Task);
        var batch = queue.Start(Track("Batch song", ct => { lock (starts) starts.Add("Batch song"); return Task.CompletedTask; }), root);
        await Check.UntilAsync(() => { lock (starts) return starts.Count == 2; }, "two earlier songs are transferring");
        var sundress = Play("Sundress", now.Task);
        queue.PlayNow(sundress);
        await Check.UntilAsync(() => { lock (starts) return starts.Contains("Sundress"); }, "the latest Play starts at once although both lanes were busy");
        await Check.UntilAsync(() => kevin.State == DownloadState.Queued && blessings.State == DownloadState.Queued, "the earlier songs are held back to the queue, not failed");
        await Task.Delay(150);
        Check.Equal("Kevin's Heart,Blessings,Sundress", string.Join(",", starts), "nothing else starts while the latest Play is transferring");
        Check.Equal("Sundress", queue.Active[0].Track.Title, "the latest Play is first in the queue");
        now.SetResult(); hold.SetResult();
        await Check.UntilAsync(() => sundress.State == DownloadState.Saved && kevin.State == DownloadState.Saved && blessings.State == DownloadState.Saved && batch.State == DownloadState.Saved, "afterwards the held songs and the backlog finish too");
        Check.Equal(2, starts.Count(s => s == "Kevin's Heart"), "a held song is started again exactly once afterwards");
    }
    private static async Task RemovalAndRestart()
    {
        Check.Section("individual removal and priority survive restart, including old 2.5 state");
        var root = Check.NewFolder("queue-removal"); var path = Path.Combine(root, "queue.json");
        using (var queue = new Downloads(_ => { }, path, r => Track(r.Title)))
        {
            queue.SetPaused(true);
            var a = queue.Start(Track("Remove"), root); var b = queue.Start(Track("Keep"), root);
            Check.True(queue.Remove(a), "queued request removable");
            Check.Equal(DownloadState.Removed, a.State, "user removal differs from resumable shutdown");
            Check.False(File.ReadAllText(path).Contains("Remove"), "removed request omitted from persisted state");
            Check.False(queue.Remove(a), "removing twice is safe");
            Check.Equal(1, queue.Active.Count, "unselected request kept");
            queue.Dispose(); await Task.Delay(100);
        }
        using (var queue = new Downloads(_ => { }, path, r => Track(r.Title)))
        {
            Check.Equal("Keep", queue.Active.Single().Track.Title, "restart resumes only kept song");
            Check.True(queue.Paused, "pause survives restart");
            queue.Clear();
            var until = DateTimeOffset.UtcNow.AddHours(1);
            queue.Start(Track("Deferred", _ => throw new SourcesWaitingException("Wait", until)), root, true);
            await Check.UntilAsync(() => queue.Active.Single().State == DownloadState.Waiting, "priority wait stored");
            queue.Dispose(); await Task.Delay(100);
        }
        using (var queue = new Downloads(_ => { }, path, r => Track(r.Title)))
        {
            Check.True(queue.Active.Single().Priority, "priority survives restart");
            Check.Equal(DownloadState.Waiting, queue.Active.Single().State, "future provider wait survives restart");
            queue.Clear(); queue.Dispose(); await Task.Delay(100);
        }
        using (var empty = new Downloads(_ => { }, path, r => Track(r.Title)))
            Check.Equal(0, empty.Active.Count, "clear remains empty after restart");
        AtomicJson.Write(path, new { Version = 1, Paused = true, Items = new[] { new { Request = new MusicRequest("Legacy", "Artist"), Folder = root, State = 5, Error = "", RetryAt = (DateTimeOffset?)null } } });
        using var legacy = new Downloads(_ => { }, path, r => Track(r.Title));
        Check.Equal(DownloadState.Queued, legacy.Active.Single().State, "legacy shutdown cancellation is still resumable");
        Check.False(legacy.Active.Single().Priority, "old queue without priority field loads in batch lane");
        var manyPath=Path.Combine(root,"many.json");
        AtomicJson.Write(manyPath,new {Version=1,Paused=true,Items=Enumerable.Range(0,200).Select(i=>new
        {Request=new MusicRequest("Ordered "+i,"Artist"),Folder=root,State=0,Error="",RetryAt=(DateTimeOffset?)null})});
        using var many=new Downloads(_=>{},manyPath,r=>Track(r.Title));
        Check.True(many.Active.Select(d=>d.Track.Title).SequenceEqual(Enumerable.Range(0,200).Select(i=>"Ordered "+i)),"200 equal-priority requests retain exact order after restart");
    }
    private static async Task CancelRunning()
    {
        Check.Section("removing running/waiting/failed requests prevents resurrection and cleans partial audio");
        var root = Check.NewFolder("queue-cancel");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var track = new BotTrack("Running", "Artist", null, "Artist - Running", async (output, _, ct) =>
        {
            await output.WriteAsync(Encoding.ASCII.GetBytes("fLaC partial"), ct); entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { canceled.SetResult(); throw; }
            return "flac";
        }) { Request = new("Running", "Artist") };
        using var queue = new Downloads(_ => { });
        var running = queue.Start(track, root); await entered.Task;
        var keep = queue.Start(Track("After cancellation"), root);
        queue.Remove(running); await canceled.Task;
        await Check.UntilAsync(() => keep.State == DownloadState.Saved, "worker continues after canceling current transfer");
        Check.Equal(DownloadState.Removed, running.State, "canceled worker cannot overwrite removed state");
        Check.False(File.Exists(Path.Combine(root, "Artist - Running.flac")), "removed current transfer has no final file");
        Check.False(Directory.GetFiles(root, "*.fmf.part").Any(), "partial transfer removed");
        var waiting = queue.Start(Track("Waiting", _ => throw new SourcesWaitingException("Short wait", DateTimeOffset.UtcNow.AddMilliseconds(150))), root);
        await Check.UntilAsync(() => waiting.State == DownloadState.Waiting, "waiting request created");
        queue.Remove(waiting); await Task.Delay(250);
        Check.Equal(DownloadState.Removed, waiting.State, "expired wait cannot resurrect removed song");
        var failed = queue.Start(Track("Failed", _ => throw new TimeoutException("Fake failure")), root);
        await Check.UntilAsync(() => failed.State == DownloadState.Failed, "failed request created");
        queue.Remove(failed); queue.RetryFailed(); await Task.Delay(50);
        Check.Equal(0, queue.Active.Count, "retry failed skips removed failures");
        var attempts=0;
        var retry=queue.Start(Track("Retry individually",_=>++attempts==1?throw new TimeoutException("Once"):Task.CompletedTask),root);
        await Check.UntilAsync(()=>retry.State==DownloadState.Failed,"individual retry starts with one failure");
        queue.SetPaused(true);queue.DownloadNext(retry);
        await Check.UntilAsync(()=>retry.State==DownloadState.Saved,"download now retries one failure despite paused batch");
        Check.Equal(1,queue.History.Count(d=>d==retry),"individual retry does not duplicate history");
        var clearEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearCancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Start(Track("Clear running",async ct=>
        {clearEntered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}catch(OperationCanceledException){clearCancelled.SetResult();throw;}}),root,true);
        await clearEntered.Task;queue.Start(Track("Clear pending"),root);queue.Clear();await clearCancelled.Task;
        Check.Equal(0,queue.Active.Count,"clear stops running and pending requests");
        Check.False(File.Exists(Path.Combine(root,"Artist - Clear running.flac")),"clear leaves no new final audio file");
    }
    private static async Task ClearAndStorageFailure()
    {
        Check.Section("clear keeps downloaded files and failed persistence does not lose requests");
        var root = Check.NewFolder("queue-clear"); var store = Path.Combine(root, "queue.json");
        using var queue = new Downloads(_ => { }, store, r => Track(r.Title));
        var saved = queue.Start(Track("Already downloaded"), root);
        await Check.UntilAsync(() => saved.State == DownloadState.Saved, "completed file ready");
        queue.SetPaused(true); var keep = queue.Start(Track("Keep on error"), root);
        using (File.Open(store, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ThrowsStorage(() => queue.Remove(keep), "locked storage rejects removal visibly");
            Check.Equal(DownloadState.Queued, keep.State, "failed removal keeps request active");
            ThrowsStorage(() => queue.Clear(), "locked storage rejects clear visibly");
            Check.Equal(2, queue.History.Count, "failed clear rolls back history");
        }
        queue.Clear();
        Check.Equal(0, queue.History.Count, "clear erases queued requests and history");
        Check.True(File.Exists(Path.Combine(root, "Artist - Already downloaded.flac")), "clear does not erase downloaded files");
        Check.Equal(DownloadState.Removed, keep.State, "pending request permanently removed");
        Check.Equal(DownloadState.Saved, saved.State, "saved song remains saved");
        Check.False(Directory.GetFiles(root,"*.tmp").Any(),"failed atomic saves leave no temporary queue debris");
    }
    private sealed class BlockedMetadata : HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); }
    }
    private static void ThrowsStorage(Action action,string text)
    {
        try {action();Check.True(false,text);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Check.True(true,text);}
    }
    private static async Task ClearImports()
    {
        Check.Section("clear cancels metadata imports and prevents in-flight producers from refilling queue");
        var root = Check.NewFolder("queue-import-clear");
        using var account = new TelegramAccount(Path.Combine(root, "fake.dat"), () => "direct");
        var sources = new MusicSources(new TransferTests.Source("DeezLoad"), new TransferTests.Source("Music Hunters"), _ => { });
        using var queue = new Downloads(_ => { }); queue.SetPaused(true);
        using var handler = new BlockedMetadata();
        var path = Path.Combine(root, "imports.json");
        using var imports = new PlaylistTransfers(account, sources, queue, path, _ => false, new PlaylistCatalog(handler));
        var job = imports.Start("https://open.spotify.com/playlist/0000000000000000000001", root);
        var step = imports.Step(job, default); await handler.Entered.Task;
        imports.Clear(); queue.Clear();
        await Check.ThrowsAsync<OperationCanceledException>(() => step, "clear cancels outstanding metadata HTTP request");
        Check.Equal(0, imports.Jobs.Count, "playlist job removed");
        Check.Equal("[]", File.ReadAllText(path), "cleared import state durable");
        await imports.Step(job, default);
        Check.Equal(0, queue.Active.Count, "old job cannot enqueue after clear");
        var again = imports.Start(job.Url, root);
        Check.False(ReferenceEquals(job, again), "deliberately reimporting creates a fresh job");
        imports.Clear();
    }

    /// <summary>Owner #156: a transfer that hangs and ignores its cancel (a request on a dead connection) is stopped, the
    /// Telegram connection reset and the song queued again; the songs behind it keep downloading; after MaxStalls it fails.</summary>
    private static async Task StallWatchdog()
    {
        Check.Section("a stuck download is restarted, the queue goes on, and a song that keeps sticking is failed");
        var root = Check.NewFolder("queue-stall");
        var (limit, retry) = (Downloads.StallLimit, Downloads.StallRetry);
        Downloads.StallLimit = TimeSpan.FromMilliseconds(400); Downloads.StallRetry = TimeSpan.FromMilliseconds(50);
        try
        {
            var attempts = 0; var resets = 0; var log = new List<string>();
            var hung = new BotTrack("Hung", "Artist", null, "Artist - Hung", async (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                await Task.Delay(Timeout.Infinite); // ignores its cancel, like a request on a dead connection
                return "flac";
            }) { Request = new("Hung", "Artist") };
            using var queue = new Downloads(l => { lock (log) log.Add(l); }) { ResetConnection = () => { Interlocked.Increment(ref resets); return Task.CompletedTask; } };
            var stuck = queue.Start(hung, root);
            var next = queue.Start(Track("Behind it"), root);
            await Check.UntilAsync(() => next.State == DownloadState.Saved, "the song behind a stuck one downloads: the queue is not held");
            await Check.UntilAsync(() => stuck.State == DownloadState.Failed, "a song that sticks every time is failed after three tries");
            Check.Equal(Downloads.MaxStalls, attempts, "it was tried three times");
            Check.True(resets >= 1, "the Telegram connection was reset for a stuck transfer");
            Check.True(stuck.Error.Contains("No progress", StringComparison.Ordinal), "the failure says why");
            lock (log) Check.True(log.Any(l => l.Contains("showed no progress", StringComparison.Ordinal)), "the log says a stuck song was restarted");

            var slow = 0;
            var alive = new BotTrack("Slow but alive", "Artist", null, "Artist - Slow but alive", async (output, progress, ct) =>
            {
                for (var i = 0; i < 8; i++) { progress?.Report(i / 10.0); await Task.Delay(150, ct); Interlocked.Increment(ref slow); }
                await output.WriteAsync(Encoding.ASCII.GetBytes("fLaC slow"), ct); return "flac";
            }) { Request = new("Slow but alive", "Artist") };
            var steady = queue.Start(alive, root);
            await Check.UntilAsync(() => steady.State == DownloadState.Saved, "a slow transfer that keeps reporting progress is never stopped");
            Check.Equal(8, slow, "it ran once, start to finish");
        }
        finally { (Downloads.StallLimit, Downloads.StallRetry) = (limit, retry); }
    }
}
