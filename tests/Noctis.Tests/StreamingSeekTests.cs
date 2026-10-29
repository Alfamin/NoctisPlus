using Noctis.Models;
using Noctis.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #204: while a song streams, the seek bar and the lyric lines reach only the part already downloaded.</summary>
public class StreamingSeekTests
{
    [AvaloniaFact]
    public void ASeekInsideTheDownloadedPartGoes_PastItIsIgnored()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "streaming", Artist = "A", FilePath = Path.Combine("C:", "Music", "s.flac"), Duration = TimeSpan.FromMinutes(3) };
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var audio = new FakeAudioPlayer();
        var vm = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.ReplaceQueueAndPlay(new[] { track }, 0);
        audio.SeekableUntil = TimeSpan.FromSeconds(100);

        vm.SeekToPositionCommand.Execute(0.5);   // 1:30, downloaded
        Assert.Equal([TimeSpan.FromSeconds(90)], audio.Seeks);
        Assert.Equal(90, vm.Position.TotalSeconds, 3);

        vm.SeekToPositionCommand.Execute(0.75);  // 2:15, not yet
        Assert.Single(audio.Seeks);
        Assert.Equal(90, vm.Position.TotalSeconds, 3);

        audio.SeekableUntil = null;              // fully downloaded
        vm.SeekToPositionCommand.Execute(0.75);
        Assert.Equal(TimeSpan.FromSeconds(135), audio.Seeks[^1]);
    }
}
