using Noctis.Models;
using Noctis.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #204: while a song streams, the seek bar and the lyric lines seek inside the part already downloaded at
/// once; owner #213: past it, they go there and wait (silent, buffering) until the download reaches the spot.</summary>
public class StreamingSeekTests
{
    private static (PlayerViewModel Vm, FakeAudioPlayer Audio) Streaming()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "streaming", Artist = "A", FilePath = Path.Combine("C:", "Music", "s.flac"), Duration = TimeSpan.FromMinutes(3) };
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.ReplaceQueueAndPlay(new[] { track }, 0);
        audio.SeekableUntil = TimeSpan.FromSeconds(100);
        return (vm, audio);
    }

    private static void Tick(FakeAudioPlayer audio, double seconds)
    {
        audio.RaisePositionChanged(TimeSpan.FromSeconds(seconds));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ASeekInsideTheDownloadedPartGoes_PastItGoesThereAndWaitsForTheDownload()
    {
        var (vm, audio) = Streaming();

        vm.SeekToPositionCommand.Execute(0.5);   // 1:30, downloaded
        Assert.Equal([TimeSpan.FromSeconds(90)], audio.Seeks);
        Assert.Equal(90, vm.Position.TotalSeconds, 3);

        vm.SeekToPositionCommand.Execute(0.75);  // 2:15, not yet: the bar stays there, silent, buffering
        Assert.Single(audio.Seeks);
        Assert.Equal(135, vm.Position.TotalSeconds, 3);
        Assert.True(audio.IsMuted);
        Assert.True(vm.IsBuffering);

        Tick(audio, 92);                         // the old spot still plays on (silently): the bar does not go back
        Assert.Equal(135, vm.Position.TotalSeconds, 3);
        Assert.Single(audio.Seeks);

        audio.SeekableUntil = TimeSpan.FromSeconds(140);   // the download got there
        Tick(audio, 93);
        Assert.Equal(TimeSpan.FromSeconds(135), audio.Seeks[^1]);
        Assert.False(audio.IsMuted);
        Assert.False(vm.IsBuffering);
    }

    [AvaloniaFact]
    public void AWaitingSeekPlaysTheMomentTheSongIsWhole_AndAnotherSeekDropsIt()
    {
        var (vm, audio) = Streaming();
        vm.SeekToPositionCommand.Execute(0.9);
        Assert.Empty(audio.Seeks);
        audio.SeekableUntil = null;              // fully downloaded
        Tick(audio, 10);
        Assert.Equal(TimeSpan.FromSeconds(162), audio.Seeks[^1]);

        audio.SeekableUntil = TimeSpan.FromSeconds(100);
        vm.SeekToPositionCommand.Execute(0.9);   // waits again
        vm.SeekToPositionCommand.Execute(0.1);   // then back inside the downloaded part: plays there, sound back
        Assert.Equal(TimeSpan.FromSeconds(18), audio.Seeks[^1]);
        Assert.False(audio.IsMuted);
        Assert.False(vm.IsBuffering);
    }

    [AvaloniaFact]
    public void AMutedSongStaysMutedAfterTheWait()
    {
        var (vm, audio) = Streaming();
        vm.IsMuted = true;
        vm.SeekToPositionCommand.Execute(0.9);
        vm.IsMuted = true;
        audio.SeekableUntil = null;
        Tick(audio, 10);
        Assert.True(audio.IsMuted);
        vm.IsMuted = false;
        Assert.False(audio.IsMuted);
    }
}
