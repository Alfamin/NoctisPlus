using System.Text;
using FreeMusicFinder;
using Noctis.Services.Search;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #87: playing a song streams it into a cache, never into the library; "Add to songs" puts it there.</summary>
internal static class StreamCacheTests
{
    private static BotTrack Track(string title, Func<CancellationToken, Task>? before = null) =>
        new(title, "Artist", null, "Artist - " + title, async (output, _, ct) =>
        {
            if (before is not null) await before(ct);
            await output.WriteAsync(Encoding.ASCII.GetBytes("fLaC stream data"), ct); return "flac";
        }) { Request = new(title, "Artist") };

    public static async Task RunAsync()
    {
        Choices(); Folders(); Purging(); Copying();
        await PlayedSongsStayOutOfTheLibrary();
        await AddWhileStillDownloading();
        await KeepSurvivesRestart();
        Exclusions();
    }

    private static void Choices()
    {
        Check.Section("Keep played songs: 7 / 30 / 90 days or forever");
        Check.Equal(7, StreamCache.Days("7 days") ?? 0, "7 days");
        Check.Equal(30, StreamCache.Days("30 days") ?? 0, "30 days");
        Check.Equal(30, StreamCache.Days(null) ?? 0, "no choice yet: 30 days");
        Check.Equal(30, StreamCache.Days("something odd") ?? 0, "an unknown value falls back to 30 days");
        Check.True(StreamCache.Days("Forever") is null, "Forever keeps them");
        Check.True(StreamCache.Choices.Contains(StreamCache.DefaultChoice), "the default is one of the choices");
        Check.Equal(StreamCache.LowDiskCap, StreamCache.Cap(null, 1024L * 1024 * 1024) ?? 0, "even Forever shrinks to 1 GB when the drive is almost full");
        Check.True(StreamCache.Cap(null, long.MaxValue) is null, "Forever has no size limit while there is room");
        Check.Equal(StreamCache.NormalCap, StreamCache.Cap(30, long.MaxValue) ?? 0, "a day limit also caps the cache at 20 GB");
    }

    private static void Folders()
    {
        Check.Section("the stream cache lives outside the roaming profile and outside library folders");
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = Path.Combine(roaming, "Noctis Plus", "plugin-data", "dev.moshi.freemusicfinder");
        Check.Equal(Path.Combine(local, "Noctis Plus", "stream-cache"), StreamCache.Folder(data),
            "a roaming profile keeps its gigabytes of audio in the local AppData folder");
        var test = @"C:\Some\Profile\plugin-data\dev.moshi.freemusicfinder";
        Check.Equal(Path.Combine(test, "stream-cache"), StreamCache.Folder(test), "any other profile keeps it beside its data");
        Check.True(StreamCache.Contains(@"C:\Cache", @"C:\Cache\Artist - Song.flac") && !StreamCache.Contains(@"C:\Cache", @"C:\Cache2\x.flac")
            && !StreamCache.Contains(@"C:\Cache", null), "only files inside the cache count as streamed");

        // Fallbacks: Play must always work, so a cache that cannot be used saves into the library as before.
        var usable = Path.Combine(Check.NewFolder("stream-usable"), "stream-cache");
        Check.True(StreamCache.Usable(usable) && Directory.Exists(usable) && !Directory.EnumerateFileSystemEntries(usable).Any(),
            "a writable cache is used, is made when missing, and its write check leaves nothing behind");
        var blocked = Path.Combine(Check.NewFolder("stream-blocked"), "a-file");
        File.WriteAllText(blocked, "a file where the cache folder should be");
        Check.True(!StreamCache.Usable(blocked), "a cache that cannot be made or written is not used");
        Check.True(StreamCache.Fits(@"C:\Users\Someone\AppData\Local\Noctis Plus\stream-cache", "Artist - Song [1234abcd]"), "a normal name fits");
        Check.True(!StreamCache.Fits(@"C:\Users\Someone\AppData\Local\Noctis Plus\stream-cache", new string('x', 200)), "a name past the player's path limit does not");
    }

    private static void Purging()
    {
        Check.Section("played songs are deleted after the chosen days, oldest first past the size limit, never while in use");
        var cache = Check.NewFolder("stream-purge");
        string Song(string name, int daysAgo, int bytes = 10)
        {
            var path = Path.Combine(cache, name);
            File.WriteAllBytes(path, new byte[bytes]);
            var at = DateTime.UtcNow.AddDays(-daysAgo);
            File.SetLastWriteTimeUtc(path, at); File.SetLastAccessTimeUtc(path, at);
            return path;
        }
        var old = Song("old.flac", 40); var recent = Song("recent.flac", 3); var replayed = Song("replayed.flac", 40);
        StreamCache.Touch(replayed);
        var busy = Song("busy.flac", 40); var held = Song("held.flac", 40);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var removed = StreamCache.Purge(cache, 30, DateTime.UtcNow, keep: f => f == busy);
            Check.Equal(1, removed, "only the one song not played for 30 days goes");
        }
        Check.True(!File.Exists(old) && File.Exists(recent), "an old song is deleted, a recent one kept");
        Check.True(File.Exists(replayed), "playing a song again starts its days over");
        Check.True(File.Exists(busy), "a song still downloading is kept");
        Check.True(File.Exists(held), "a song open in the player is kept (it is deleted next time)");
        Check.Equal(0, StreamCache.Purge(cache, null, DateTime.UtcNow), "Forever deletes nothing while there is room");
        Check.Equal(0, StreamCache.Purge(Path.Combine(cache, "missing"), 30, DateTime.UtcNow), "a cache that was never made is fine");

        var full = Check.NewFolder("stream-cap");
        var oldest = Path.Combine(full, "a.flac"); var middle = Path.Combine(full, "b.flac"); var newest = Path.Combine(full, "c.flac");
        foreach (var (path, ago) in new[] { (oldest, 3), (middle, 2), (newest, 1) })
        {
            File.WriteAllBytes(path, new byte[100]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ago)); File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-ago));
        }
        Check.Equal(2, StreamCache.Purge(full, null, DateTime.UtcNow, maxBytes: 150), "over the size limit the least recently played go first");
        Check.True(!File.Exists(oldest) && !File.Exists(middle) && File.Exists(newest), "the most recently played song stays");
    }

    private static void Copying()
    {
        Check.Section("Add to songs copies the song and its lyrics, never over an existing file");
        var cache = Check.NewFolder("stream-copy-cache"); var library = Path.Combine(Check.NewFolder("stream-copy-library"), "Noctis Free Music");
        var song = Path.Combine(cache, "Artist - Song.flac");
        File.WriteAllText(song, "fLaC song"); File.WriteAllText(Path.Combine(cache, "Artist - Song.lrc"), "[00:01.00]line");
        File.WriteAllText(Path.Combine(cache, "Artist - Song 2.flac"), "another song");
        var copy = StreamCache.CopyToLibrary(song, library);
        Check.True(File.ReadAllText(copy) == "fLaC song" && File.Exists(Path.Combine(library, "Artist - Song.lrc")), "song and lyrics are copied, the folder is made");
        Check.True(!File.Exists(Path.Combine(library, "Artist - Song 2.flac")), "a song with a longer name is not taken along");
        Check.True(File.Exists(song), "the cached copy stays (it may be playing)");
        Check.True(!Directory.EnumerateFiles(library, "*.adding").Any(), "no half-copied file is left behind");
        File.WriteAllText(copy, "the user's own file");
        StreamCache.CopyToLibrary(song, library);
        Check.Equal("the user's own file", File.ReadAllText(copy), "an existing file of the same name is never overwritten");
    }

    private static async Task PlayedSongsStayOutOfTheLibrary()
    {
        Check.Section("a played song is saved in the stream cache and never announced to the library; Add copies it");
        var root = Check.NewFolder("stream-play"); var cache = Path.Combine(root, "cache"); var library = Path.Combine(root, "library");
        var announced = new List<string>();
        void Saved(object? _, string path) { lock (announced) announced.Add(path); }
        OnlineMusicSearch.FileSaved += Saved;
        try
        {
            using var queue = new Downloads(_ => { }) { CacheFolder = cache };
            var played = queue.Start(Track("Played"), cache, priority: true);
            await Check.UntilAsync(() => played.State == DownloadState.Saved, "the played song is saved");
            Check.True(played.SavedPath is { } p && p.StartsWith(cache, StringComparison.OrdinalIgnoreCase), "into the stream cache");
            Check.True(queue.IsCached(played) && played.KeepIn is null, "as played only");
            lock (announced) Check.Equal(0, announced.Count, "the library is not told about it");
            var (path, pending) = queue.Keep(played, library);
            Check.True(!pending && path is not null && File.Exists(path) && path.StartsWith(library, StringComparison.OrdinalIgnoreCase), "Add copies it into the library folder");
            lock (announced) Check.True(announced.Count == 1 && announced[0] == path, "and announces exactly that copy to the library");
            Check.Equal(Path.GetFullPath(library), played.KeepIn ?? "", "the song remembers it was added");

            var downloaded = queue.Start(Track("Downloaded"), library, priority: true);
            await Check.UntilAsync(() => downloaded.State == DownloadState.Saved, "an explicit download is saved");
            Check.True(!queue.IsCached(downloaded), "into the library, as before");
            lock (announced) Check.True(announced.Contains(downloaded.SavedPath!), "and announced to the library as before");

            var readOnly = Path.Combine(root, "not-a-folder.txt"); File.WriteAllText(readOnly, "a file where a folder should be");
            var again = queue.Start(Track("Again"), cache, priority: true);
            await Check.UntilAsync(() => again.State == DownloadState.Saved, "another played song");
            await Check.ThrowsAsync<IOException>(() => Task.Run(() => queue.Keep(again, Path.Combine(readOnly, "sub"))), "a copy that cannot be made says so");
            Check.True(again.KeepIn is null, "and the song stays played-only, so Add can be pressed again");
        }
        finally { OnlineMusicSearch.FileSaved -= Saved; }
    }

    private static async Task AddWhileStillDownloading()
    {
        Check.Section("Add while the song is still arriving: it joins the library as soon as it is saved");
        var root = Check.NewFolder("stream-pending"); var cache = Path.Combine(root, "cache"); var library = Path.Combine(root, "library");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var announced = new List<string>();
        void Saved(object? _, string path) { lock (announced) announced.Add(path); }
        OnlineMusicSearch.FileSaved += Saved;
        try
        {
            using var queue = new Downloads(_ => { }) { CacheFolder = cache };
            var song = queue.Start(Track("Arriving", ct => release.Task.WaitAsync(ct)), cache, priority: true);
            await Check.UntilAsync(() => song.State == DownloadState.Running, "the song is downloading");
            var (path, pending) = queue.Keep(song, library);
            Check.True(pending && path is null, "Add is accepted while it downloads");
            release.SetResult();
            await Check.UntilAsync(() => File.Exists(Path.Combine(library, song.Track.FileName + ".flac")), "the finished song is copied into the library");
            lock (announced) Check.Equal(1, announced.Count, "and announced once");
        }
        finally { OnlineMusicSearch.FileSaved -= Saved; }
    }

    private static async Task KeepSurvivesRestart()
    {
        Check.Section("an Add that waits for its download survives a restart");
        var root = Check.NewFolder("stream-restart"); var cache = Path.Combine(root, "cache"); var library = Path.Combine(root, "library");
        var store = Path.Combine(root, "queue.json"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var queue = new Downloads(_ => { }, store, r => Track(r.Title)) { CacheFolder = cache })
        {
            var song = queue.Start(Track("Restarted", async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }), cache, priority: true);
            await entered.Task;
            queue.Keep(song, library);
        }
        using (var restored = new Downloads(_ => { }, store, r => Track(r.Title)) { CacheFolder = cache })
        {
            var song = restored.History.Single();
            Check.Equal(Path.GetFullPath(library), song.KeepIn ?? "", "the library folder is remembered");
            await Check.UntilAsync(() => File.Exists(Path.Combine(library, song.Track.FileName + ".flac")), "after the restart the song finishes and is copied");
        }
    }

    private static void Exclusions()
    {
        Check.Section("the library never takes songs from the stream cache, even inside a library folder");
        var cache = Path.Combine(Check.NewFolder("stream-excluded"), "stream-cache");
        Noctis.Services.LibraryExclusions.Register(cache);
        try
        {
            Check.True(Noctis.Services.LibraryExclusions.IsExcluded(Path.Combine(cache, "a.flac")), "a cached song is excluded");
            Check.True(!Noctis.Services.LibraryExclusions.IsExcluded(cache + "-other\\a.flac"), "a neighbouring folder is not");
            Check.True(!Noctis.Services.LibraryExclusions.IsExcluded(null), "nothing is not");
        }
        finally { Noctis.Services.LibraryExclusions.Unregister(cache); }
        Check.True(!Noctis.Services.LibraryExclusions.IsExcluded(Path.Combine(cache, "a.flac")), "and it is lifted when the feature stops");
    }
}
