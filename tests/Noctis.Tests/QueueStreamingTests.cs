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
    private sealed class Source : IOnlineMusicSearch
    {
        internal readonly Dictionary<string, TaskCompletionSource<Track>> Pending = [];
        internal readonly Dictionary<string, CancellationToken> Tokens = [];
        internal readonly List<(string Id, bool Keep)> Asked = [];
        internal readonly List<(string Id, bool Now)> Downloads_ = [];
        internal bool SignedIn = true;
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
        public OnlineDownloadStatus StatusOf(OnlineSong song) => OnlineDownloadStatus.None;
        public event EventHandler? DownloadsChanged { add { } remove { } }
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct)
        { lock (Downloads_) Downloads_.AddRange(songs.Select(s => (s.Id, now))); return Task.FromResult("Downloading."); }
        public IDownloadQueue Downloads => throw new NotSupportedException();
        internal readonly List<string> Cancelled = [];
        public bool CancelQueued(OnlineSong song) { lock (Cancelled) Cancelled.Add(song.Id); return true; }
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
        return (new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService()), audio);
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
        var source = new Source(); var (player, _) = NewPlayer();
        var list = new List<Track> { Local("A") };
        for (int i = 0; i < 6; i++) list.Add(StandIn(source, "s" + i));
        player.ReplaceQueueAndPlay(list, 0); Settle();
        var end = DateTime.UtcNow.AddSeconds(3);
        while (source.Downloads_.Count < PlayerViewModel.StreamAheadSongs && DateTime.UtcNow < end) { Thread.Sleep(10); Settle(); }
        lock (source.Downloads_)
            Assert.Equal(new[] { ("s0", true), ("s1", false), ("s2", false) }, source.Downloads_.OrderBy(d => d.Id));
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
        // s0 moves to the end of the queue: it is no longer among the next places, s3 is.
        var s0 = player.UpNext[0]; player.UpNext.RemoveAt(0); player.UpNext.Add(s0); Settle();
        lock (source.Cancelled) Assert.Equal(new[] { "s0" }, source.Cancelled);
        player.ClearQueue(); Settle();
        lock (source.Cancelled) Assert.Equal(new[] { "s0", "s1", "s2", "s3" }, source.Cancelled.OrderBy(x => x));
    }
}