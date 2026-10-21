using Noctis.Features.WordLyrics;
using Xunit;

namespace Noctis.Tests;

public sealed class WordLyricsSongRulesTests
{
    [Fact]
    public void KnownCopiesShareTheNextChoice_EvenBeforeEitherHasARule()
    {
        using var data = new TestPersistenceService();
        var cache = Path.Combine(data.DataDirectory, "partial.mp3");
        var saved = Path.Combine(data.DataDirectory, "saved.mp3");
        File.WriteAllText(cache, "partial audio"); File.WriteAllText(saved, "complete audio with updated tags");
        var rules = new SongRules(data.DataDirectory, _ => { });
        rules.Share(cache, saved); rules.Set(saved, new(Ignored: true));
        Assert.True(rules.Get(cache).Ignored);
        var reloaded = new SongRules(data.DataDirectory, _ => { }); reloaded.Load();
        Assert.True(reloaded.Get(cache).Ignored);
        reloaded.Set(cache, new()); Assert.False(reloaded.Get(saved).Ignored);
    }

    [Fact]
    public void IgnoreSurvivesCompletingTheStream_AndSharingItsSavedCopy()
    {
        using var data = new TestPersistenceService();
        var partial = Path.Combine(data.DataDirectory, "Song.mp3");
        var copy = Path.Combine(Directory.CreateDirectory(Path.Combine(data.DataDirectory, "library")).FullName, "Song.mp3");
        File.WriteAllText(partial, "first bytes");
        var rules = new SongRules(data.DataDirectory, _ => { });
        rules.Set(partial, new(Ignored: true));
        File.AppendAllText(partial, " rest of the song");
        Assert.True(rules.Get(partial).Ignored);
        File.Copy(partial, copy); rules.Share(partial, copy);
        var reloaded = new SongRules(data.DataDirectory, _ => { }); reloaded.Load();
        Assert.True(reloaded.Get(partial).Ignored); Assert.True(reloaded.Get(copy).Ignored);
        reloaded.Set(copy, new());
        Assert.False(reloaded.Get(partial).Ignored);
    }

    [Theory]
    [InlineData("{\"Rules\":null,\"Paths\":{}}")]
    [InlineData("{\"Rules\":{},\"Paths\":null}")]
    [InlineData("{ broken json")]
    public void BrokenRulesNeverPreventStartingThePlayer(string json)
    {
        using var data = new TestPersistenceService(); var messages = new List<string>();
        File.WriteAllText(Path.Combine(data.DataDirectory, "song-rules.json"), json);
        var rules = new SongRules(data.DataDirectory, messages.Add); rules.Load();
        Assert.Single(messages); Assert.False(rules.Get("not a song").Ignored);
    }
}
