using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;
using Source = Noctis.Tests.QueueStreamingTests.Source;

namespace Noctis.Tests;

/// <summary>Owner #286: the end of a For you page plays the next page, and a song no bot sends no longer holds the queue
/// forever (time offline does not count).</summary>
public class ForYouPageEndTests
{
    private static OnlineSong Song(string id) => new(id, "Online " + id, "Synthetic", Seconds: 180);
    private static Track Local(string title) => new() { Id = Guid.NewGuid(), Title = title, Artist = "Synthetic", Duration = TimeSpan.FromMinutes(3), FilePath = "C:/m/" + title + ".flac" };
    private static void Settle() { for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> done, double seconds = 3)
    { var end = DateTime.UtcNow.AddSeconds(seconds); while (!done() && DateTime.UtcNow < end) { await Task.Delay(10); Settle(); } }

    private static (PlayerViewModel Player, FakeAudioPlayer Audio) NewPlayer()
    {
        var audio = new FakeAudioPlayer();
        return (new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService()) { QueueSettle = TimeSpan.Zero }, audio);
    }

    private static async Task<Track> PlayLastOfPage(PlayerViewModel player, Source source)
    {
        Assert.True(player.PlayOnlinePage(source, [Song("a")], 0)); Settle();
        var real = Local("A");
        source.Pending["a"].SetResult(real);
        await Until(() => ReferenceEquals(player.CurrentTrack, real));
        Assert.Same(real, player.CurrentTrack);
        Assert.Empty(player.UpNext);
        return real;
    }

    [AvaloniaFact]
    public async Task NotForMe_OnThePagesLastSong_PlaysTheNextPage_InsteadOfOnlyPausing()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        var asked = 0;
        player.ContinueForYouPage = () => { asked++; return true; };
        await PlayLastOfPage(player, source);
        player.SkipDismissed(Song("a")); Settle();
        Assert.Equal(1, asked);
        Assert.NotEqual(PlaybackState.Playing, player.State);
    }

    [AvaloniaFact]
    public async Task ThePagesLastSongEnding_PlaysTheNextPage()
    {
        var source = new Source(); var (player, audio) = NewPlayer();
        var asked = 0;
        player.ContinueForYouPage = () => { asked++; return true; };
        await PlayLastOfPage(player, source);
        audio.RaiseTrackEnded(); Settle();
        Assert.Equal(1, asked);
    }

    [AvaloniaFact]
    public async Task AQueueFromAnotherPage_EndsAsBefore()
    {
        var source = new Source(); var (player, audio) = NewPlayer();
        var asked = 0;
        player.ContinueForYouPage = () => { asked++; return true; };
        var standIn = PlayerViewModel.StandIn(source, Song("x"), keep: true, page: "playlist");
        player.ReplaceQueueAndPlay([standIn], 0); Settle();
        var real = Local("X");
        source.Pending["x"].SetResult(real);
        await Until(() => ReferenceEquals(player.CurrentTrack, real));
        audio.RaiseTrackEnded(); Settle();
        Assert.Equal(0, asked);
    }

    [AvaloniaFact]
    public async Task ASongNoBotSends_GivesWayToTheNextSong_AfterTheWaitOnline()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        player.StandInWait = TimeSpan.FromMilliseconds(300);
        player.StandInWaitStep = TimeSpan.FromMilliseconds(50);
        player.MayStreamAhead = () => true;
        var next = Local("Next");
        player.ReplaceQueueAndPlay([PlayerViewModel.StandIn(source, Song("silent"), keep: true), next], 0); Settle();
        Assert.Contains(source.Asked, a => a.Id == "silent");
        await Until(() => ReferenceEquals(player.CurrentTrack, next));
        Assert.Same(next, player.CurrentTrack);
    }

    [AvaloniaFact]
    public async Task ASongToFetch_KeepsWaitingWhileOffline()
    {
        var source = new Source(); var (player, _) = NewPlayer();
        player.StandInWait = TimeSpan.FromMilliseconds(200);
        player.StandInWaitStep = TimeSpan.FromMilliseconds(50);
        player.MayStreamAhead = () => false;   // offline: no time counts
        var next = Local("Next");
        player.ReplaceQueueAndPlay([PlayerViewModel.StandIn(source, Song("later"), keep: true), next], 0); Settle();
        await Until(() => ReferenceEquals(player.CurrentTrack, next), 0.8);
        Assert.NotSame(next, player.CurrentTrack);
        Assert.False(source.Tokens["later"].IsCancellationRequested);
        player.ClearQueue(); Settle();
    }
}
