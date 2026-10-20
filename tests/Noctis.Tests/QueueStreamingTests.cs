using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using System.Runtime.CompilerServices;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #95: a playlist's songs you don't have stand in the queue and are fetched when it reaches them; nothing
/// about the queue (History, Previous, Pause, the song before ending) may go wrong because of them.</summary>
public class QueueStreamingTests
{
    internal sealed class Source : IOnlineMusicSearch
    {
        internal readonly Dictionary<string, TaskCompletionSource<Track>> Pending = [];
        internal readonly Dictionary<string, CancellationToken> Tokens = [];
        internal readonly List<(string Id, bool Keep)> Asked = [];
        internal readonly List<(string Id, bool Now)> Downloads_ = [];
        internal bool SignedIn = true;
        internal Func<string?, bool>? CacheTest;
        public bool IsCachedFile(string? path) => CacheTest?.Invoke(path) == true;
        internal readonly Dictionary<string, string> CompleteFiles = [];
        public string? CompletedPlaybackPath(OnlineSong song) => CompleteFiles.GetValueOrDefault(song.Id);
        public Task<Track> PreparePlaybackAsync(OnlineSong song, CancellationToken ct) => PreparePlaybackAsync(song, false, ct);
        public Task<Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct)
        { Asked.Add((song.Id, keep)); Tokens[song.Id] = ct; return (Pending[song.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task; }
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "synthetic";
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct) { await Task.Yield(); yield break; }
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public bool CanDownload => SignedIn;
        internal int SignInAsked;
        public void RequestSignIn() => SignInAsked++;
        /// <summary>Downloads finish at once (into the stream cache), as when the next songs are ready.</summary>
        internal bool FinishDownloads;
        public OnlineDownloadStatus StatusOf(OnlineSong song)
        { lock (Downloads_) return FinishDownloads && Downloads_.Any(d => d.Id == song.Id) ? new(OnlineDownloadState.Streamed, 1) : OnlineDownloadStatus.None; }
        public event EventHandler? DownloadsChanged;
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct)
        {
            lock (Downloads_) Downloads_.AddRange(songs.Select(s => (s.Id, now)));
            if (FinishDownloads) DownloadsChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult("Downloading.");
        }
        public IDownloadQueue Downloads => throw new NotSupportedException();
        internal readonly List<string> Cancelled = [];
        public bool CancelQueued(OnlineSong song) { lock (Cancelled) Cancelled.Add(song.Id); return true; }
        internal readonly List<string> AskedAhead = [];
        internal TaskCompletionSource? HoldAsks; internal CancellationToken AskToken;
        public Task AskAheadAsync(OnlineSong song, CancellationToken ct)
        { lock (AskedAhead) AskedAhead.Add(song.Id); AskToken = ct; return HoldAsks?.Task.WaitAsync(ct) ?? Task.CompletedTask; }
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
    }

    private static Track Local(string title) => new() { Id = Guid.NewGuid(), Title = title, Artist = "Synthetic", Duration = TimeSpan.FromMinutes(3), FilePath = "C:/m/" + title + ".flac" };
    private static Track StandIn(Source source, string id) => PlayerViewModel.StandIn(source, new OnlineSong(id, "Online " + id, "Synthetic", Seconds: 180), keep: true);
    private static void Settle() { for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> done) { var end = DateTime.UtcNow.AddSeconds(3); while (!done() && DateTime.UtcNow < end) { await Task.Delay(10); Settle(); } }

    private static (PlayerViewModel Player, FakeAudioPlayer Audio) NewPlayer()
    {
        var audio = new FakeAudioPlayer();
        // The other tests look at the songs ahead right away; owner #111's wait after a new queue has its own test.
        return (new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService()) { QueueSettle = TimeSpan.Zero }, audio);
    }

    [AvaloniaFact]
    public void AheadWindowsCountOwnedQueuePositions_InsteadOfReachingForFourMissingSongs()
    {
        var source = new Source { FinishDownloads = true }; var (player, _) = NewPlayer();
        player.ReplaceQueueAndPlay([Local("Now"), Local("One"), Local("Two"), Local("Three"), Local("Four"),
            StandIn(source, "outside")], 0); Settle();
        Assert.Empty(source.Downloads_);
        Assert.Empty(source.AskedAhead);
        player.ReplaceQueueAndPlay([Local("Now"), Local("One"), StandIn(source, "two"),
            Local("Three"), StandIn(source, "four"), StandIn(source, "five")], 0);
        var end = DateTime.UtcNow.AddSeconds(3);
        while (source.AskedAhead.Count == 0 && DateTime.UtcNow < end) { Thread.Sleep(10); Settle(); }
        Assert.Equal(new[] { "two" }, source.Downloads_.Select(d => d.Id));
        Assert.Equal(new[] { "four" }, source.AskedAhead);
        player.ClearQueue(); Settle();
    }

    [AvaloniaFact]
    public async Task ANewQueue_FetchesNothingAhead_UntilItHasSettled_ButNextMovesAtOnce()
    {
        // Owner #111: a playlist played and shuffled seconds later fetched two queues. The song that plays is not delayed.
        var source = new Source(); var (player, _) = NewPlayer(); player.QueueSettle = TimeSpan.FromMilliseconds(600);
        var list = new List<Track> { Local("A") };
        for (int i = 0; i < 6; i++) list.Add(StandIn(source, "s" + i));
        player.ReplaceQueueAndPlay(list, 0); Settle();
        await Task.Delay(200); Settle();
        Assert.Empty(source.Downloads_);
        lock (source.AskedAhead) Assert.Empty(source.AskedAhead);
        player.ReplaceQueueAndPlay(list, 0); Settle(); // another big change starts the wait again
        await Task.Delay(400); Settle();
        Assert.Empty(source.Downloads_);
        await Until(() => source.Downloads_.Count >= PlayerViewModel.StreamAheadSongs);
        Assert.Equal(PlayerViewModel.StreamAheadSongs, source.Downloads_.Count); // settled: the next two download

        var (other, _) = NewPlayer(); other.QueueSettle = TimeSpan.FromSeconds(30);
        var second = new Source(); var songs = new List<Track> { Local("B"), Local("C") };
        for (int i = 0; i < 4; i++) songs.Add(StandIn(second, "t" + i));
        other.ReplaceQueueAndPlay(songs, 0); Settle();
        Assert.Empty(second.Downloads_);
        other.NextCommand.Execute(null); Settle(); // a skip moves the songs ahead at once, wait or not
        await Until(() => second.Downloads_.Count > 0);
        Assert.NotEmpty(second.Downloads_);
    }

    [AvaloniaFact]
    public async Task AStandIn_IsFetched_ThenPlayed_AndTheSongBeforeIsInHistoryOnce()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        Track a = Local("A"), b = StandIn(source, "b"), c = Local("C");
        player.ReplaceQueueAndPlay([a, b, c], 0); Settle();
        player.NextCommand.Execute(null); Settle();
        Assert.Equal([("b", true)], source.Asked);                // kept: it is a song of your playlist
        Assert.Same(a, player.CurrentTrack);                          // the song before keeps playing meanwhile
        Assert.True(player.IsPreparingOnlinePlayback);
        var real = Local("B");
        source.Pending["b"].SetResult(real);
        await Until(() => ReferenceEquals(player.CurrentTrack, real));
        Assert.Same(real, player.CurrentTrack);
        Assert.Single(player.History, t => ReferenceEquals(t, a));
        Assert.DoesNotContain(player.UpNext, t => t.IsPendingStream);
    }

    [AvaloniaFact]
    public void SkippingAgainWhileFetching_GoesToTheNextSong_WithoutRepeatingHistory()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        Track a = Local("A"), b = StandIn(source, "b"), c = Local("C");
        player.ReplaceQueueAndPlay([a, b, c], 0); Settle();
        player.NextCommand.Execute(null); Settle();
        player.NextCommand.Execute(null); Settle();
        Assert.Same(c, player.CurrentTrack);
        Assert.True(source.Tokens["b"].IsCancellationRequested);
        Assert.Single(player.History, t => ReferenceEquals(t, a));
    }

    [AvaloniaFact]
    public void TheSongBeforeEnding_WhileFetching_DoesNotMoveTheQueueOnAgain()
    {
        var source = new Source(); var (player, audio) = NewPlayer();
        Track a = Local("A"), b = StandIn(source, "b"), c = Local("C");
        player.ReplaceQueueAndPlay([a, b, c], 0); Settle();
        player.NextCommand.Execute(null); Settle();
        audio.RaiseTrackEnded(); Settle();
        Assert.Same(a, player.CurrentTrack);
        Assert.False(source.Tokens["b"].IsCancellationRequested);
        Assert.Same(c, player.UpNext.Single());
    }

    [AvaloniaFact]
    public void PauseOrPrevious_WhileFetching_PutsTheSongBackAsNext()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        Track a = Local("A"), b = StandIn(source, "b"), c = Local("C");
        player.ReplaceQueueAndPlay([a, b, c], 0); Settle();
        player.NextCommand.Execute(null); Settle();
        player.PlayPauseCommand.Execute(null); Settle();
        Assert.Equal(new[] { b, c }, player.UpNext);
        Assert.DoesNotContain(a, player.History);
        Assert.Equal(PlaybackState.Paused, player.State);
        player.NextCommand.Execute(null); Settle();
        player.PreviousCommand.Execute(null); Settle();
        Assert.Same(a, player.CurrentTrack);
        Assert.Equal(new[] { b, c }, player.UpNext);
    }

    [AvaloniaFact]
    public async Task ASongNoBotHas_IsPassedOver_AndThreeInARowLeaveTheRestOut()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        var list = new List<Track> { Local("A") };
        for (int i = 0; i < 5; i++) list.Add(StandIn(source, "s" + i));
        var last = Local("Z"); list.Add(last);
        player.ReplaceQueueAndPlay(list, 0); Settle();
        player.NextCommand.Execute(null); Settle();
        for (int i = 0; i < 3; i++)
        {
            await Until(() => source.Pending.ContainsKey("s" + i));
            source.Pending["s" + i].SetException(new InvalidOperationException("Neither bot supplied a matching recording."));
            await Until(() => i == 2 ? ReferenceEquals(player.CurrentTrack, last) : source.Pending.ContainsKey("s" + (i + 1)));
        }
        Assert.Same(last, player.CurrentTrack);                    // s3 and s4 were left out, not tried one by one
        Assert.DoesNotContain(source.Asked, x => x.Id is "s3" or "s4");
    }

    [AvaloniaFact]
    public void NotSignedIn_TheSongsToFetchAreLeftOut_AndSignInIsOfferedOnce()
    {
        var source = new Source { SignedIn = false }; var (player, _) = NewPlayer();
        Track a = Local("A"), c = Local("C");
        player.ReplaceQueueAndPlay([a, StandIn(source, "b"), StandIn(source, "d"), c], 0); Settle();
        player.NextCommand.Execute(null); Settle(); Settle();
        Assert.Same(c, player.CurrentTrack);
        Assert.Equal(1, source.SignInAsked);
        Assert.Empty(source.Asked);
    }

    [AvaloniaFact]
    public void TurningItOff_LeavesOnlyTheSongsOnThisComputer()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        Track a = Local("A"), c = Local("C");
        player.ReplaceQueueAndPlay([a, StandIn(source, "b"), c, StandIn(source, "d")], 0); Settle();
        player.StreamMissingSongs = false; Settle();
        Assert.Equal(new[] { c }, player.UpNext);
    }

    [AvaloniaFact]
    public void TheNextSongsToFetch_AreDownloadedAhead_InQueueOrder()
    {
        var source = new Source { FinishDownloads = true }; var (player, _) = NewPlayer();
        var list = new List<Track> { Local("A") };
        for (int i = 0; i < 6; i++) list.Add(StandIn(source, "s" + i));
        player.ReplaceQueueAndPlay(list, 0); Settle();
        var end = DateTime.UtcNow.AddSeconds(3);
        while (source.Downloads_.Count < PlayerViewModel.StreamAheadSongs && DateTime.UtcNow < end) { Thread.Sleep(10); Settle(); }
        lock (source.Downloads_)
            Assert.Equal(new[] { ("s0", true), ("s1", true) }, source.Downloads_.OrderBy(d => d.Id));
        // Owner #100: once those two are ready, the next ones are only asked for (ready in the bot chat), one at a time, in order.
        end = DateTime.UtcNow.AddSeconds(3);
        while (source.AskedAhead.Count < 2 && DateTime.UtcNow < end) { Thread.Sleep(10); Settle(); }
        Thread.Sleep(50); Settle();
        lock (source.AskedAhead) Assert.Equal(new[] { "s2", "s3" }, source.AskedAhead); // four ready in all (owner, 2026-10-20)
    }

    [Fact]
    public async Task APlaylistOrSavedQueue_NeverTakesAStandIn()
    {
        var kept = await SidebarViewModel.InLibraryAsync([Local("A"), StandIn(new Source(), "b")], new Source());
        Assert.Single(kept);
    }
    [AvaloniaFact]
    public void ClearingOrReshufflingTheQueue_DropsTheDownloadsAskedAhead_ThatItNoLongerNeeds()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        var list = new List<Track> { Local("A") };
        for (int i = 0; i < 8; i++) list.Add(StandIn(source, "s" + i));
        player.ReplaceQueueAndPlay(list, 0); Settle();
        var end = DateTime.UtcNow.AddSeconds(3);
        while (source.Downloads_.Count < PlayerViewModel.StreamAheadSongs && DateTime.UtcNow < end) { Thread.Sleep(10); Settle(); }
        // s0 moves to the end of the queue: it is no longer among the next two, s2 is.
        var s0 = player.UpNext[0]; player.UpNext.RemoveAt(0); player.UpNext.Add(s0); Settle();
        lock (source.Cancelled) Assert.Equal(new[] { "s0" }, source.Cancelled);
        Assert.Empty(source.AskedAhead); // live: asking waits while the next two are still downloading (it shares the bot lanes)
        player.ClearQueue(); Settle();
        lock (source.Cancelled) Assert.Equal(new[] { "s0", "s1", "s2" }, source.Cancelled.OrderBy(x => x));
    }
    [AvaloniaFact]
    public void APagesToggle_LeavesOnlyThatPagesSongsOut_AndIsRemembered()
    {
        // Owner #100: per page (one playlist on, another off), in the page header, not in Settings.
        var source = new Source(); var (player, _) = NewPlayer();
        Track a = Local("A");
        Track one = PlayerViewModel.StandIn(source, new OnlineSong("p1", "P1", "Synthetic", Seconds: 180), keep: true, "playlist:1");
        Track two = PlayerViewModel.StandIn(source, new OnlineSong("p2", "P2", "Synthetic", Seconds: 180), keep: false, "album:2");
        player.ReplaceQueueAndPlay([a, one, two], 0); Settle();
        player.SetStreamPage("playlist:1", false); Settle();
        Assert.Equal(new[] { two }, player.UpNext);
        Assert.False(player.StreamsPage("playlist:1")); Assert.True(player.StreamsPage("album:2"));
        Assert.Equal(new[] { "playlist:1" }, player.StreamOffPages);
        player.SetStreamPage("playlist:1", true);
        Assert.Empty(player.StreamOffPages);
        player.LoadStreamOffPages(["artist:x"]);
        Assert.False(player.StreamsPage("artist:x"));
    }

    [AvaloniaFact]
    public void JumpingPastTheSongsAskedAhead_StopsAskingForThem_AndAsksForTheNewNextOnes()
    {
        // Owner #100: clicking far down the queue (or clearing it) stops what is being asked for at once.
        var source = new Source { HoldAsks = new(), FinishDownloads = true }; var (player, _) = NewPlayer();
        var list = new List<Track> { Local("A") };
        for (int i = 0; i < 20; i++) list.Add(StandIn(source, "s" + i));
        player.ReplaceQueueAndPlay(list, 0); Settle();
        var end = DateTime.UtcNow.AddSeconds(3);
        while (source.AskedAhead.Count < 1 && DateTime.UtcNow < end) { Thread.Sleep(10); Settle(); }
        var first = source.AskToken;
        lock (source.AskedAhead) Assert.Equal(new[] { "s2" }, source.AskedAhead); // one at a time
        for (int i = 0; i < 12; i++) player.UpNext.RemoveAt(0); // the next songs are now s12, s13, …
        Settle();
        end = DateTime.UtcNow.AddSeconds(3);
        while (source.AskedAhead.Count < 2 && DateTime.UtcNow < end) { Thread.Sleep(10); Settle(); }
        Assert.True(first.IsCancellationRequested);
        lock (source.AskedAhead) Assert.Equal("s14", source.AskedAhead[^1]);
        player.ClearQueue(); Settle();
        Assert.True(source.AskToken.IsCancellationRequested);
    }

    [AvaloniaFact]
    public async Task ASongSavedToTheLibrary_DoesNotTakeTheOtherSongsToFetchOutOfTheQueue()
    {
        // Live 2026-10-20: the first song downloaded ahead was imported, the library reconcile dropped every stand-in.
        var source = new Source(); var audio = new FakeAudioPlayer(); var library = new FakeLibraryService();
        var a = Local("A"); library.TrackList.Add(a);
        var player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService()) { QueueSettle = TimeSpan.Zero };
        Track b = StandIn(source, "b"), c = StandIn(source, "c");
        player.ReplaceQueueAndPlay([a, b, c], 0); Settle();
        library.TrackList.Add(Local("Imported")); library.RaiseLibraryUpdated();
        await Until(() => false); // let the reconcile run
        Assert.Equal(new[] { b, c }, player.UpNext);
    }
}
