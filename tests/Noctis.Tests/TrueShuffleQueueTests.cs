using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Features.TrueShuffle;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// True Shuffle's typed queue adapter on Noctis's real PlayerViewModel: the same promises the plugin's reflection
/// bridge made (its NativePlayerTests), now without reflection.
/// </summary>
public class TrueShuffleQueueTests
{
    private static (PlayerViewModel Player, FakeAudioPlayer Audio, FakeLibraryService Library, List<Track> Tracks, ShuffleQueue Queue) Rig(int count = 5)
    {
        var audio = new FakeAudioPlayer(); var library = new FakeLibraryService();
        var tracks = Enumerable.Range(1, count).Select(i => new Track
        {
            Title = "Song " + i, Artist = "Test artist", Duration = TimeSpan.FromMinutes(3),
            FilePath = Path.Combine(Path.GetTempPath(), "true-shuffle-synthetic-" + i + ".mp3"),
        }).ToList();
        library.TrackList.AddRange(tracks);
        var player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        return (player, audio, library, tracks, new ShuffleQueue(player, library, () => true, () => null));
    }

    private static string[] Titles(IEnumerable<Track> tracks) => tracks.Select(t => t.Title).ToArray();

    [AvaloniaFact]
    public void Apply_KeepsThePlayingSong_GivesTheExactOrder_AndTurnsNoctisShuffleAndRadioOff()
    {
        var (player, audio, _, tracks, queue) = Rig();
        player.ReplaceQueueAndPlay(tracks.Take(3).ToList(), 0);
        Dispatcher.UIThread.RunJobs();
        var playing = player.CurrentTrack; var plays = audio.PlayedPaths.Count;
        player.ToggleShuffleCommand.Execute(null);
        player.IsRadioActive = true;

        queue.Apply([tracks[4], tracks[2], tracks[3]], keepCurrent: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(playing, player.CurrentTrack);
        Assert.Equal(plays, audio.PlayedPaths.Count);
        Assert.Equal(["Song 1", "Song 5", "Song 3", "Song 4"], Titles(queue.Snapshot(ShuffleScope.CurrentQueue)));
        Assert.False(player.IsShuffleEnabled);
        Assert.False(player.IsRadioActive);
    }

    [AvaloniaFact]
    public void Apply_WithoutKeepingTheSong_StartsTheFirstOfTheOrder()
    {
        var (player, audio, _, tracks, queue) = Rig();
        queue.Apply([tracks[3], tracks[0]], keepCurrent: false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Song 4", player.CurrentTrack?.Title);
        Assert.Equal(["Song 1"], Titles(player.UpNext));
        Assert.Equal(tracks[3].FilePath, audio.PlayedPaths.LastOrDefault());
    }

    [AvaloniaFact]
    public void AppendAndReplaceUpcoming_NeverTouchThePlayingSong_AndPreviousForgetsTheStartList()
    {
        var (player, audio, _, tracks, queue) = Rig();
        player.ReplaceQueueAndPlay(tracks.ToList(), 2); // Song 1-2 sit above the picked song
        Dispatcher.UIThread.RunJobs();
        var playing = player.CurrentTrack; var plays = audio.PlayedPaths.Count;
        Assert.Equal(["Song 1", "Song 2", "Song 3", "Song 4", "Song 5"], Titles(queue.PlayedList()));

        queue.AppendUpcoming([tracks[0]]);
        Assert.Equal(["Song 4", "Song 5", "Song 1"], Titles(player.UpNext));
        queue.ReplaceUpcoming([tracks[1], tracks[4]]);
        Assert.Equal(["Song 2", "Song 5"], Titles(player.UpNext));
        Assert.Same(playing, player.CurrentTrack);
        Assert.Equal(plays, audio.PlayedPaths.Count);
        Assert.Empty(player.PrecedingInQueue);
    }

    [AvaloniaFact]
    public void Snapshot_WholeLibrary_AndOpenPlaylistNeedsAPlaylistPage()
    {
        var (_, _, _, tracks, queue) = Rig(4);
        Assert.Equal(Titles(tracks), Titles(queue.Snapshot(ShuffleScope.WholeLibrary)));
        Assert.Throws<InvalidOperationException>(() => queue.Snapshot(ShuffleScope.OpenPlaylist));
    }

    [AvaloniaFact]
    public void DuringASession_AddedSongsGoAheadOfTheShuffle_InOrder_WithoutDuplicates()
    {
        // Blueprint queue rule: songs the user adds stay ahead of generated songs, in order, in the native queue.
        var (player, audio, _, tracks, queue) = Rig(8);
        player.ReplaceQueueAndPlay([tracks[0]], 0);
        Dispatcher.UIThread.RunJobs();
        var plays = audio.PlayedPaths.Count;
        queue.Apply([tracks[1], tracks[2], tracks[3]], keepCurrent: true);
        queue.BeginSession(player.UpNext);

        player.AddToQueue(tracks[4]);
        player.AddToQueue(tracks[5]);
        Assert.Equal(["Song 5", "Song 6", "Song 2", "Song 3", "Song 4"], Titles(player.UpNext));

        player.AddRangeToQueue([tracks[6], tracks[2]]); // Song 3 was in the shuffle: it moves up, it is not doubled
        Assert.Equal(["Song 5", "Song 6", "Song 7", "Song 3", "Song 2", "Song 4"], Titles(player.UpNext));

        queue.AppendUpcoming([tracks[7]]);              // the shuffle's own refill goes behind everything...
        player.AddToQueue(tracks[1]);                   // ...and the user's next song still goes ahead of the shuffle
        Assert.Equal(["Song 5", "Song 6", "Song 7", "Song 3", "Song 2", "Song 4", "Song 8"], Titles(player.UpNext));

        player.AddNext(tracks[3]);                       // Play Next keeps Noctis's own place: the very front
        Assert.Equal("Song 4", player.UpNext[0].Title);
        Assert.Equal(plays, audio.PlayedPaths.Count);    // nothing restarted the playing song

        queue.EndSession();
        player.AddToQueue(tracks[0]);
        Assert.Equal("Song 1", player.UpNext[^1].Title); // normal Noctis queue again
    }

    [AvaloniaFact]
    public void WithoutASession_AddToQueueAppendsAsNoctisAlwaysDid()
    {
        var (player, _, _, tracks, queue) = Rig(4);
        player.ReplaceQueueAndPlay([tracks[0], tracks[1]], 0);
        Dispatcher.UIThread.RunJobs();
        Assert.False(queue.InSession);
        player.AddToQueue(tracks[2]);
        player.AddRangeToQueue([tracks[3]]);
        Assert.Equal(["Song 2", "Song 3", "Song 4"], Titles(player.UpNext));
    }

    [Fact]
    public void RollingQueue_HandsOutThePlanInWindows_AndKnowsWhatRemains()
    {
        var plan = Enumerable.Range(1, 5).Select(i => new Track { Title = "Song " + i }).Select(t => new ShuffleEntry(t, ShuffleQueue.SongOf(t))).ToList();
        var roll = new RollingQueue(ShuffleMode.NoRepeats, ShuffleScope.WholeLibrary, false, null, plan, taken: 2, new());
        Assert.Equal(3, roll.Remaining);
        Assert.Equal(["Song 3", "Song 4"], Titles(roll.Take(2)));
        Assert.Equal(["Song 5"], Titles(roll.Take(10)));
        Assert.Equal(0, roll.Remaining);
        Assert.Empty(roll.Take(1));
    }

    [Fact]
    public void SameSongs_IgnoresPlayCountsAndTags_ButNotMembershipOrOrder()
    {
        // The plugin refused a mode click ("source changed, apply again") when a play count was saved while it
        // prepared; that was one cause of clicks that "didn't take" (owner report 2026-10-17).
        var a = new Track { Title = "A" }; var b = new Track { Title = "B" }; var c = new Track { Title = "C" };
        var before = new List<Track> { a, b };
        a.PlayCount++; b.Rating = 80;
        Assert.True(ShuffleQueue.SameSongs(before, [a, b]));
        Assert.False(ShuffleQueue.SameSongs(before, [b, a]));
        Assert.False(ShuffleQueue.SameSongs(before, [a, b, c]));
    }

    [Fact]
    public void SongOf_CarriesTheShuffleTags_AndRemembersEachIdentityOnce()
    {
        var t = new Track { Title = "T", Artist = "A", FilePath = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".mp3"),
            PlayCount = 3, IsExplicit = true, SnoozedUntil = DateTime.UtcNow.AddHours(1) };
        var identities = new Dictionary<string, string>();
        var song = ShuffleQueue.SongOf(t, identities);
        Assert.Equal((t.Id.ToString(), 3, true), (song.Id, song.PlayCount, song.Explicit));
        Assert.Equal(t.SnoozedUntil, song.SnoozedUntil);
        Assert.Single(identities);
        Assert.Equal(song.Key, ShuffleQueue.SongOf(t, identities).Key);
        Assert.Single(identities);
    }
}
