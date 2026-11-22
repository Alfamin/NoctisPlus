using Noctis.Mobile.Services.Finder;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #307: Search asks whether every library song still exists, and on the phone each answer is a call into the
/// document provider. The answers are kept, and filled in ahead of the search.</summary>
public class PhoneLibraryExistsCacheTests
{
    private static Track Song(int n) => new() { Id = Guid.NewGuid(), Title = "Song " + n, Artist = "A", FilePath = "content://docs/tree/x/song" + n + ".mp3" };

    private static (PhonePlaylistLibrary Library, FakeLibraryService Service, List<string> Asked) Make(int songs)
    {
        var service = new FakeLibraryService();
        for (var i = 0; i < songs; i++) service.TrackList.Add(Song(i));
        var asked = new List<string>();
        var library = new PhonePlaylistLibrary(service, null!, work => work(), path => { lock (asked) asked.Add(path); return true; });
        return (library, service, asked);
    }

    [Fact]
    public void AnAnswerIsKept_SoTheSearchIsNotHeldUpByTheSameSongsAgain()
    {
        var (library, service, asked) = Make(3);
        var path = service.TrackList[0].FilePath;

        Assert.True(library.Exists(path));
        Assert.True(library.Exists(path));
        Assert.True(library.Exists(path));

        Assert.Single(asked);   // one call into the provider, not three
    }

    [Fact]
    public void WarmingAsksForEverySongOnce_AndTheSearchThenAsksNothing()
    {
        var (library, service, asked) = Make(40);

        library.Warm();
        Assert.Equal(40, asked.Distinct().Count());
        var afterWarm = asked.Count;

        foreach (var track in service.TrackList) Assert.True(library.Exists(track.FilePath));
        library.Warm();

        Assert.Equal(afterWarm, asked.Count);   // the search's own checks were all answered from the kept ones
    }

    [Fact]
    public void ASongTheProviderDoesNotHave_IsReportedAsMissing()
    {
        var service = new FakeLibraryService();
        var gone = Song(1);
        service.TrackList.Add(gone);
        var library = new PhonePlaylistLibrary(service, null!, work => work(), path => path != gone.FilePath);

        library.Warm();

        Assert.False(library.Exists(gone.FilePath));
    }
}
