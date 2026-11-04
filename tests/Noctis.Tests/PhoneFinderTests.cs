using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Services.Finder;
using Noctis.Mobile.Services.Telegram;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.ViewModels.Settings;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;
using Xunit;

namespace Noctis.Tests;

/// <summary>Cloud #260: phone Music Finder wiring (sign-in pane in Settings, online Search, downloads,
/// private files, playlist import). Fakes only: no Telegram, no network, no device.</summary>
public class PhoneFinderTests
{
    // ── Fakes ──

    private sealed class SignIn : IPhoneTelegramSignIn
    {
        public bool IsLoggedIn { get; set; }
        public string? SignedInAs { get; set; }
        public int? ExpectedCodeLength => null;
        public int Resets;
        public Func<CancellationToken, Task<string?>>? Continue;
        public Task<string?> StartLoginAsync(string phone, string? s, string? sec, CancellationToken ct = default) => Task.FromResult<string?>("verification_code");
        public Task<string?> ContinueLoginAsync(string answer, CancellationToken ct = default)
            => Continue?.Invoke(ct) ?? Done();
        private Task<string?> Done() { IsLoggedIn = true; SignedInAs = "@me"; return Task.FromResult<string?>(null); }
        public Task LogOutAsync(CancellationToken ct = default) { IsLoggedIn = false; return Task.CompletedTask; }
        public Task ResetLoginAsync(CancellationToken ct = default) { Resets++; return Task.CompletedTask; }
    }

    private sealed class Queue : IDownloadQueue
    {
        public List<DownloadEntry> Items = new();
        public bool IsPaused { get; set; }
        public bool FailStorage;
        public int Retries;
        public event EventHandler? Changed;
        public IReadOnlyList<DownloadEntry> Snapshot() => Items.ToList();
        public void SetPaused(bool paused) => IsPaused = paused;
        public void RetryFailed() => Retries++;
        public void StartHeld() { }
        public void Clear() { if (FailStorage) throw new IOException("QUEUE_SAVE_FAILED"); Items.Clear(); }
        public void DownloadNext(DownloadEntry entry) { }
        public void Remove(DownloadEntry entry) { if (FailStorage) throw new IOException("QUEUE_SAVE_FAILED"); Items.Remove(entry); }
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Online : IOnlineMusicSearch
    {
        public bool CanDownload { get; set; } = true;
        public Dictionary<string, Func<string, CancellationToken, IAsyncEnumerable<OnlineSearchResult>>> Answers = new();
        public List<string> Asked = new();
        public int Prepares, Downloads_;
        public TaskCompletionSource<Track>? Prepare;
        public Dictionary<string, OnlineDownloadStatus> Status = new();
        public Queue Queue = new();
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "";
        public IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, CancellationToken ct)
        { Asked.Add(query); return Answers[query](query, ct); }
        public string? CachedArtwork(string url) => url == "cached" ? "/art/cached.jpg" : null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>("/art/" + url + ".jpg");
        public void RequestSignIn() { }
        public OnlineDownloadStatus StatusOf(OnlineSong song) => Status.TryGetValue(song.Id, out var s) ? s : OnlineDownloadStatus.None;
        public event EventHandler? DownloadsChanged;
        public void RaiseChanged() => DownloadsChanged?.Invoke(this, EventArgs.Empty);
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct)
        { Downloads_++; foreach (var s in songs) Status[s.Id] = new(OnlineDownloadState.Queued); return Task.FromResult("Queued"); }
        public IDownloadQueue Downloads => Queue;
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
        public Task<Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct)
        { Prepares++; Assert.False(keep); return (Prepare ??= new()).Task.WaitAsync(ct); }
    }

    private static async IAsyncEnumerable<OnlineSearchResult> Results(string query, [EnumeratorCancellation] CancellationToken ct,
        Task? gate = null, params OnlineSong[] songs)
    {
        if (gate != null) await gate.WaitAsync(ct);
        yield return new OnlineSearchResult(query, songs, [], [], Complete: true);
    }

    private static OnlineSong Song(string id, Guid? local = null, string art = "") => new(id, "Title " + id, "Artist", ArtworkUrl: art, LocalTrackId: local);

    private static OnlineResultsViewModel NewOnline(Online source, List<IReadOnlyList<Track>> played, Action? signIn = null)
        => new(source, a => a(), played.Add, signIn ?? (() => { }), (_, ct) => Task.Delay(1, ct));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition());
    }

    // ── Settings → Telegram ──

    [AvaloniaFact]
    public async Task Settings_ShowsTelegramRow_OnlyWithASignInService_AndItsStateFollowsTheSignIn()
    {
        using var plain = MobileFixtures.MakeRig();
        var without = new SettingsPageViewModel(plain.Shell);
        Assert.False(without.HasTelegram); Assert.Null(without.Telegram);

        var signIn = new SignIn();
        var shell = new ShellViewModel(plain.Shell.Library, plain.Shell.Player, plain.Shell.Lyrics) { TelegramSignIn = signIn, Marshal = a => a() };
        var page = new SettingsPageViewModel(shell);
        Assert.True(page.HasTelegram);
        Assert.Equal("Off", page.TelegramValue);
        page.OpenPaneCommand.Execute(SettingsPane.Telegram);
        Assert.Equal("Telegram", page.HeaderTitle);
        Assert.True(page.ShowsTelegramPane);

        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        page.Telegram!.Phone = "+989120004521";
        await page.Telegram.SendCodeCommand.ExecuteAsync(null);
        page.Telegram.Code = "12345";
        await page.Telegram.SubmitCodeCommand.ExecuteAsync(null);
        Assert.True(page.Telegram.IsSignedIn);
        Assert.Equal("On", page.TelegramValue);
        Assert.Contains(nameof(SettingsPageViewModel.TelegramRowName), changed);
        Assert.Equal("Telegram, On", page.TelegramRowName);
        page.OnClosed();
    }

    [AvaloniaFact]
    public async Task CancelWhileACodeIsChecked_ReturnsToTheNumber_ResetsTheAttempt_AndIgnoresTheLateAnswer()
    {
        var held = new TaskCompletionSource<string?>();
        var signIn = new SignIn { Continue = ct => held.Task };
        var vm = new TelegramPaneViewModel(signIn) { Phone = "+989120004521" };
        await vm.SendCodeCommand.ExecuteAsync(null);
        vm.Code = "123456";
        var pending = vm.SubmitCodeCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        vm.CancelSignInCommand.Execute(null);
        await pending;
        Assert.Equal(TelegramStep.Phone, vm.Step);
        Assert.Equal(1, signIn.Resets);
        Assert.Equal("", vm.Code);
        held.SetResult(null);
        await Task.Yield();
        Assert.False(vm.IsSignedIn);
    }

    [Fact]
    public void BuiltInPairIsNeverSavedAsTheUsersOwn_AndAUsersOwnPairIsKept()
    {
        Assert.Equal((0, ""), PhoneTelegramSignIn.LoginPair(usesBuiltIn: true, 123456, new string('a', 32)));
        Assert.Equal((777, "custom"), PhoneTelegramSignIn.LoginPair(usesBuiltIn: false, 777, "custom"));
    }

    // ── Online search ──

    [Fact]
    public async Task OnlySearchesAfterTyping_AndAStaleAnswerNeverReplacesTheLatestQuery()
    {
        var source = new Online();
        var slow = new TaskCompletionSource();
        source.Answers["drake old"] = (q, ct) => Results(q, ct, slow.Task, Song("old"));
        source.Answers["drake new"] = (q, ct) => Results(q, ct, null, Song("new"));
        var vm = NewOnline(source, new());

        vm.Search("d"); // too short: nothing asked
        vm.Search("drake old");
        await Until(() => source.Asked.Contains("drake old"));
        vm.Search("drake new");
        await Until(() => vm.SongCount == 1 && !vm.IsSearching);
        slow.SetResult();
        await Task.Delay(50);
        Assert.Equal(new[] { "new" }, vm.Songs.Select(s => s.Song.Id));
        Assert.DoesNotContain("d", source.Asked);
    }

    [Fact]
    public async Task ASongTheLibraryHas_IsLeftToTheLibraryRow_AndCachedArtworkIsUsedWithoutAsking()
    {
        var source = new Online();
        source.Answers["q1"] = (q, ct) => Results(q, ct, null, Song("have", Guid.NewGuid()), Song("a", art: "cached"), Song("a", art: "cached"), Song("b", art: "remote"));
        var vm = NewOnline(source, new());
        vm.Search("q1");
        await Until(() => vm.SongCount == 2);
        Assert.Equal(new[] { "a", "b" }, vm.Songs.Select(s => s.Song.Id));
        Assert.Equal("/art/cached.jpg", vm.Songs[0].ArtworkPath);
        await Until(() => vm.Songs[1].ArtworkPath == "/art/remote.jpg");
    }

    /// <summary>A fake whose waits are held per call, and which (like a real slow transfer) may ignore cancellation.</summary>
    private sealed class HeldOnline : IOnlineMusicSearch
    {
        public readonly List<(string Id, TaskCompletionSource<Track> Done, CancellationToken Ct)> Waits = new();
        public bool HonourCancel = true;
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "";
        public IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, CancellationToken ct) => Results(query, ct, null, Song("a"), Song("b"));
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public bool CanDownload => true;
        public void RequestSignIn() { }
        public OnlineDownloadStatus StatusOf(OnlineSong song) => OnlineDownloadStatus.None;
        public event EventHandler? DownloadsChanged { add { } remove { } }
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) => Task.FromResult("");
        public IDownloadQueue Downloads => new Queue();
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
        public Task<Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct)
        {
            var done = new TaskCompletionSource<Track>(TaskCreationOptions.RunContinuationsAsynchronously);
            Waits.Add((song.Id, done, ct));
            return HonourCancel ? done.Task.WaitAsync(ct) : done.Task;
        }
    }

    private static async Task<(OnlineResultsViewModel Vm, HeldOnline Source, List<IReadOnlyList<Track>> Played)> HeldRig(bool honourCancel = true)
    {
        var source = new HeldOnline { HonourCancel = honourCancel };
        var played = new List<IReadOnlyList<Track>>();
        var vm = new OnlineResultsViewModel(source, a => a(), played.Add, () => { }, (_, ct) => Task.Delay(1, ct));
        vm.Search("qq");
        await Until(() => vm.SongCount == 2);
        return (vm, source, played);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // the source finishes the superseded wait late instead of observing the cancel
    public async Task GeneratedPlayCommand_SecondSongCancelsTheFirstWait_AndALateFirstAnswerNeverPlays(bool honourCancel)
    {
        var (vm, source, played) = await HeldRig(honourCancel);
        var a = vm.Songs[0]; var b = vm.Songs[1];
        Assert.True(vm.PlayCommand.CanExecute(a));
        vm.PlayCommand.Execute(a);
        // What a Button checks: B (and A again) stay tappable while A is fetched. The toolkit's default
        // (non-concurrent) command reports false here and the row buttons would be disabled.
        Assert.True(vm.PlayCommand.IsRunning);
        Assert.True(vm.PlayCommand.CanExecute(b));
        Assert.True(vm.PlayCommand.CanExecute(a));
        vm.PlayCommand.Execute(a);                 // repeated tap while A waits
        Assert.Single(source.Waits);
        vm.PlayCommand.Execute(b);
        Assert.Equal(2, source.Waits.Count);
        Assert.True(source.Waits[0].Ct.IsCancellationRequested);
        Assert.False(source.Waits[1].Ct.IsCancellationRequested);
        source.Waits[0].Done.TrySetResult(new Track { Title = "late a" });
        source.Waits[1].Done.TrySetResult(new Track { Title = "b" });
        await Until(() => played.Count == 1 && !b.IsWorking);
        await Task.Delay(20);
        Assert.Equal(new[] { "b" }, played.Select(p => p.Single().Title));
        Assert.False(a.IsWorking);
    }

    [Fact]
    public async Task GeneratedPlayCommand_AThenBThenA_PlaysOnlyTheLastA()
    {
        var (vm, source, played) = await HeldRig(honourCancel: false);
        var a = vm.Songs[0]; var b = vm.Songs[1];
        vm.PlayCommand.Execute(a); vm.PlayCommand.Execute(b); vm.PlayCommand.Execute(a);
        Assert.Equal(new[] { "a", "b", "a" }, source.Waits.Select(w => w.Id));
        Assert.True(source.Waits[0].Ct.IsCancellationRequested && source.Waits[1].Ct.IsCancellationRequested);
        source.Waits[2].Done.SetResult(new Track { Title = "a again" });
        source.Waits[1].Done.SetResult(new Track { Title = "b" });
        source.Waits[0].Done.SetResult(new Track { Title = "first a" });
        await Until(() => played.Count == 1);
        await Task.Delay(20);
        Assert.Equal(new[] { "a again" }, played.Select(p => p.Single().Title));
    }

    [Fact]
    public async Task GeneratedPlayCommand_ErrorOfASupersededWaitIsNotShown_AndDisposeCancelsTheCurrentWait()
    {
        var (vm, source, played) = await HeldRig(honourCancel: false);
        var a = vm.Songs[0]; var b = vm.Songs[1];
        vm.PlayCommand.Execute(a); vm.PlayCommand.Execute(b);
        source.Waits[0].Done.SetException(new IOException("The download was removed or interrupted."));
        await Task.Delay(20);
        Assert.Null(vm.Message);
        source.Waits[1].Done.SetException(new IOException("LIBRARY_SCANNING: Noctis is still scanning."));
        await Until(() => vm.Message == "Noctis is still scanning.");
        vm.PlayCommand.Execute(a);
        vm.Dispose();
        Assert.True(source.Waits[2].Ct.IsCancellationRequested);
        source.Waits[2].Done.SetResult(new Track { Title = "after dispose" });
        await Task.Delay(20);
        Assert.Empty(played);
    }

    [Fact]
    public async Task Download_QueuesOnce_AndSignedOutAsksToSignIn()
    {
        var source = new Online();
        source.Answers["qq"] = (q, ct) => Results(q, ct, null, Song("a"));
        var asked = 0;
        var vm = NewOnline(source, new(), () => asked++);
        vm.Search("qq");
        await Until(() => vm.SongCount == 1);
        var item = vm.Songs[0];
        await vm.DownloadCommand.ExecuteAsync(item);
        await vm.DownloadCommand.ExecuteAsync(item);
        Assert.Equal(1, source.Downloads_);
        Assert.True(item.IsRequested); Assert.Equal("Queued", item.Status);

        source.Status[item.Song.Id] = new(OnlineDownloadState.Downloading, 0.5);
        source.RaiseChanged();
        Assert.Equal("50%", item.Status.Replace(" ", ""));

        source.CanDownload = false;
        await vm.PlayCommand.ExecuteAsync(item);
        Assert.Equal(1, asked); Assert.Equal(0, source.Prepares);
    }

    [Theory]
    [InlineData("LIBRARY_SCANNING: Noctis is still scanning.", "Noctis is still scanning.")]
    [InlineData("The download was removed or interrupted.", "The download was removed or interrupted.")]
    public void ErrorsShowTheSentence_NotTheCode(string message, string shown)
        => Assert.Equal(shown, OnlineResultsViewModel.Short(new IOException(message)));

    // ── Downloads ──

    [Fact]
    public void Downloads_PauseResumeRetryRemove_AndAStorageFailureIsShownNotLost()
    {
        var queue = new Queue();
        var running = new DownloadEntry(1, "Song", "Artist", OnlineDownloadState.Downloading, 0.25, "", true, false);
        var failed = new DownloadEntry(2, "Other", "Artist", OnlineDownloadState.Failed, 0, "Bot is busy", false, false);
        queue.Items.AddRange([running, failed]);
        var vm = new FinderDownloadsViewModel(queue, a => a());
        Assert.True(vm.HasEntries); Assert.True(vm.HasFailed);
        Assert.Equal("Artist · 25%", vm.Entries[0].Subtitle.Replace(" %", "%"));
        Assert.Equal("Artist · Bot is busy", vm.Entries[1].Subtitle);

        vm.TogglePauseCommand.Execute(null);
        Assert.True(queue.IsPaused); Assert.Equal("Resume", vm.PauseLabel);
        vm.TogglePauseCommand.Execute(null);
        Assert.False(queue.IsPaused);
        vm.RetryFailedCommand.Execute(null);
        Assert.Equal(1, queue.Retries);

        queue.FailStorage = true;
        vm.RemoveCommand.Execute(vm.Entries[1]);
        Assert.Equal(2, vm.Entries.Count);
        Assert.NotNull(vm.ErrorText);
        queue.FailStorage = false;
        vm.RemoveCommand.Execute(vm.Entries[1]);
        Assert.Single(vm.Entries); Assert.Null(vm.ErrorText);
        vm.ClearCommand.Execute(null);
        Assert.False(vm.HasEntries);
        vm.Dispose();
        queue.Items.Add(running); queue.Raise();
        Assert.Empty(vm.Entries); // no longer following the queue
    }

    // ── Private files ──

    [Fact]
    public void PrivateFiles_ReadSidecarsAndAudioByPath_IncludingPersianNames_AndRefuseOversizedOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var audio = Path.Combine(dir, "محسن چاوشی - امیر بی گزند.flac");
            File.WriteAllBytes(audio, [1, 2, 3]);
            File.WriteAllText(Path.Combine(dir, "محسن چاوشی - امیر بی گزند.LRC"), "[00:01.00]سلام");
            var files = new PathTrackFileAccess();
            var lrc = files.ReadSidecar(audio, [".ttml", ".lrc"]);
            Assert.NotNull(lrc); Assert.Equal(".lrc", lrc!.Extension);
            using (var s = files.OpenAudio(audio)) Assert.Equal(3, s!.Length);

            Assert.Null(files.ReadSidecar("content://com.android.externalstorage.documents/tree/x/document/y", [".lrc"]));
            Assert.Null(files.OpenAudio(Path.Combine(dir, "missing.flac")));
            Assert.False(PathTrackFileAccess.IsFilePath("content://a/b"));

            var big = Path.Combine(dir, "big.flac");
            File.WriteAllBytes(big, [0]);
            File.WriteAllBytes(Path.Combine(dir, "big.ttml"), new byte[PathTrackFileAccess.MaxSidecarBytes + 1]);
            Assert.Null(files.ReadSidecar(big, [".ttml"]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OnlyFilesInsideTheFinderFolderJoinTheLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "finder-music");
        Assert.True(PhoneFinderHost.IsInside(root, Path.Combine(root, "A - B.flac")));
        Assert.False(PhoneFinderHost.IsInside(root, Path.Combine(root + "-cache", "A - B.flac")));
        Assert.False(PhoneFinderHost.IsInside(root, Path.Combine(root, "..", "x.flac")));
    }

    // ── Playlist import ──

    [AvaloniaFact]
    public async Task PlaylistImport_FillsOnce_KeepsUserEdits_AndDoesNotRecreateADeletedPlaylist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = new Track { Id = Guid.NewGuid(), Title = "A", Artist = "X", FilePath = Path.Combine(dir, "a.flac") };
        var b = new Track { Id = Guid.NewGuid(), Title = "B", Artist = "X", FilePath = Path.Combine(dir, "b.flac") };
        File.WriteAllBytes(a.FilePath, [0]); File.WriteAllBytes(b.FilePath, [0]);
        using var rig = MobileFixtures.MakeRig([a, b]);
        try
        {
            var lib = new PhonePlaylistLibrary(rig.Library, rig.Shell.Library, work => work());
            var snapshot = await lib.SnapshotAsync();
            Assert.Equal(2, snapshot.Count);

            var id = Guid.NewGuid();
            const string url = "https://open.spotify.com/playlist/abc";
            await lib.ApplyAsync(id, "Mix", url, [a.Id], create: true, expected: []);
            var saved = (await rig.Persistence.LoadPlaylistsAsync()).Single(p => p.Id == id);
            Assert.Equal(new[] { a.Id }, saved.TrackIds);
            Assert.StartsWith("Imported from Spotify", saved.Description);

            // A later fill that expects what the import wrote goes on.
            await lib.ApplyAsync(id, "Mix", url, [a.Id, b.Id], create: false, expected: [a.Id]);
            // The user reordered it meanwhile: the next fill stops, nothing changes.
            await rig.Shell.Library.RemoveFromPlaylistAsync(rig.Shell.Library.Playlists.Single(p => p.Id == id), [a]);
            var edited = await Assert.ThrowsAsync<InvalidOperationException>(() => lib.ApplyAsync(id, "Mix", url, [a.Id, b.Id, a.Id], false, [a.Id, b.Id]));
            Assert.StartsWith("PLAYLIST_EDITED", edited.Message);
            Assert.Equal(new[] { b.Id }, (await rig.Persistence.LoadPlaylistsAsync()).Single(p => p.Id == id).TrackIds);

            // Deleted by the user: never recreated by a later fill.
            await rig.Shell.Library.DeletePlaylistAsync(rig.Shell.Library.Playlists.Single(p => p.Id == id));
            var removed = await Assert.ThrowsAsync<InvalidOperationException>(() => lib.ApplyAsync(id, "Mix", url, [b.Id], false, [b.Id]));
            Assert.StartsWith("PLAYLIST_REMOVED", removed.Message);

            // A matched song whose file vanished stops the change.
            File.Delete(b.FilePath);
            var gone = await Assert.ThrowsAsync<IOException>(() => lib.ApplyAsync(Guid.NewGuid(), "New", url, [b.Id], true, []));
            Assert.StartsWith("LIBRARY_CHANGED", gone.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
