namespace FreeMusicFinder.Tests;

/// <summary>Owner #93: a second pass with plainer names and close matches, only after both bots found nothing exact; what it
/// supplies is saved and marked to check. Most songs never take that pass.</summary>
internal static class CheckTheseTests
{
    /// <summary>Has only a close match: a strict request finds nothing, the loose pass finds it.</summary>
    private sealed class CloseOnly(string id) : IMusicSource
    {
        public string Id { get; } = id;
        public readonly List<MusicRequest> Asked = [];
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MusicRequest>>([]);
        public async Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
        {
            lock (Asked) Asked.Add(request);
            if (!RecordingMatch.Fits(request, "A$AP Rocky", "I Smoked Away My Brain", TimeSpan.FromSeconds(request.Seconds ?? 0) + TimeSpan.FromSeconds(7)))
                throw new TrackUnavailableException("No matching recording.");
            await output.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            return "flac";
        }
    }

    public static async Task RunAsync()
    {
        Check.Section("check these: a second, looser pass only after nothing exact, and a mark to check");
        var wanted = new MusicRequest("I Smoked Away My Brain (I'm God x Demons Mashup) (feat. Imogen Heap & Clams Casino)", "A$AP Rocky, Imogen Heap, Clams Casino", 249);
        var loose = wanted with { Loose = true };

        Check.False(RecordingMatch.Fits(wanted, "A$AP Rocky", "I Smoked Away My Brain", TimeSpan.FromSeconds(256)), "a shorter title 7 s longer is no exact match");
        Check.True(RecordingMatch.Fits(loose, "A$AP Rocky", "I Smoked Away My Brain", TimeSpan.FromSeconds(256)), "the loose pass takes it: same artist, same start, 7 s apart");
        Check.False(RecordingMatch.Fits(loose, "A$AP Rocky", "I Smoked Away My Brain (Remix)", TimeSpan.FromSeconds(250)), "never a remix for the original");
        Check.False(RecordingMatch.Fits(loose, "Someone Else", "I Smoked Away My Brain", TimeSpan.FromSeconds(250)), "never another artist");
        Check.False(RecordingMatch.Fits(loose, "A$AP Rocky", "I Smoked Away My Brain", TimeSpan.FromSeconds(290)), "never 40 s apart");
        Check.False(RecordingMatch.Fits(new MusicRequest("Fit Check", "Jackboys") { Loose = true }, "Jackboys", "Gang Gang", TimeSpan.FromSeconds(200)), "never a different song by the same artist");

        Check.Equal("A$AP Rocky I Smoked Away My Brain", BotQuery.For(loose), "the loose pass asks the plain title");
        Check.Equal("Imogen Heap I Smoked Away My Brain", BotQuery.Full(loose), "and under the next credited artist");
        Check.Equal(BotQuery.For(wanted), BotQuery.For(wanted with { Loose = false }), "an ordinary request is asked as before");

        var hunters = new CloseOnly(BotSelections.Hunters);
        var deez = new CloseOnly("DeezLoad");
        var sources = new MusicSources(hunters, deez, _ => { }, firstBot: () => BotSelections.Hunters);
        using var output = new MemoryStream();
        Check.Equal("flac", await sources.FetchAsync(wanted, output, null, default), "nothing exact: the close match is supplied");
        Check.True(hunters.Asked.Any(r => !r.Loose) && deez.Asked.Any(r => !r.Loose), "both bots were asked the exact way first");
        Check.True(CheckThese.TakeFetched(wanted), "and the song is marked to check");
        Check.False(CheckThese.TakeFetched(wanted), "the mark is taken once");

        var exact = new TransferTests.Source(BotSelections.Hunters);
        var exactSources = new MusicSources(exact, new TransferTests.Source("DeezLoad"), _ => { }, firstBot: () => BotSelections.Hunters);
        await exactSources.FetchAsync(new("Song", "Artist"), output, null, default);
        Check.False(CheckThese.TakeFetched(new("Song", "Artist")), "an exact match is never marked");
        Check.Equal(1, exact.Calls, "and asks no second pass");

        var folder = Check.NewFolder("check-these");
        var song = Path.Combine(folder, "song.flac"); File.WriteAllBytes(song, [1]);
        CheckThese.Load(Path.Combine(folder, "check-these.json"));
        CheckThese.Add(song, wanted);
        Check.True(CheckThese.Contains(song) && CheckThese.Count == 1, "a marked song is listed");
        CheckThese.Load(Path.Combine(folder, "check-these.json"));
        Check.True(CheckThese.Contains(song), "the list survives a restart");
        CheckThese.Remove(song);
        Check.False(CheckThese.Contains(song), "It's right: the mark goes");
        CheckThese.Add(song, wanted); File.Delete(song);
        CheckThese.Load(Path.Combine(folder, "check-these.json"));
        Check.Equal(0, CheckThese.Count, "a song deleted meanwhile drops off the list");
    }
}
