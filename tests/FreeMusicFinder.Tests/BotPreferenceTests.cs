using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class BotPreferenceTests
{
    public static async Task RunAsync()
    {
        Check.Section("native bot preference applies to searches and queued downloads without restart");
        var hunters = new TransferTests.Source(BotSelections.Hunters);
        var deez = new TransferTests.Source("DeezLoad");
        var first = "DeezLoad";
        var sources = new MusicSources(hunters, deez, _ => { }, firstBot: () => first);
        using var output = new MemoryStream();
        await sources.SearchAsync("Song", default);
        Check.Equal(1, deez.Searches, "DeezLoad-first searches start with DeezLoad");
        Check.Equal(0, hunters.Searches, "successful primary does not query reserve");
        await sources.FetchAsync(new("Song", "Artist"), output, null, default);
        Check.Equal(1, deez.Calls, "fresh downloads use configured default");
        first = BotSelections.Hunters;
        await sources.FetchAsync(new("Song", "Artist", PreferredSource: "DeezLoad"), output, null, default);
        Check.Equal(1, hunters.Calls, "new choice overrides historical bulk-queue preference");
        await sources.SearchAsync("Song", default);
        Check.Equal(1, hunters.Searches, "setting change affects the next search without restart");
        var pickedHunters = new TransferTests.Source(BotSelections.Hunters);
        var pickedDeez = new TransferTests.Source("DeezLoad");
        var chosenRouter = new MusicSources(pickedHunters, pickedDeez, _ => { }, firstBot: () => BotSelections.Hunters);
        await chosenRouter.FetchAsync(new("Picked", "Artist", CachedFrom: "DeezLoad", MessageId: 42,
            Selection: new("DeezLoad", "deezload2bot", 42, "1", "data")), output, null, default);
        Check.Equal(1, pickedHunters.Calls, "current explicit bot choice overrides stale cached and selected bot provenance");
        Check.Equal(0, pickedDeez.Calls, "selected reserve is not asked before configured first bot");
        hunters.Fetch = (_, _) => throw new TrackUnavailableException("recording unavailable");
        await sources.FetchAsync(new("Other", "Artist"), output, null, default);
        Check.Equal(2, deez.Calls, "reserve supplies a song missing from the chosen primary");
        hunters.Fetch = (_, _) => throw new SourceLimitException(BotSelections.Hunters, "flood", DateTimeOffset.UtcNow.AddMinutes(1), global: true);
        await Check.ThrowsAsync<SourcesWaitingException>(() => sources.FetchAsync(new("Third", "Artist"), output, null, default), "Telegram-wide wait is never bypassed with the reserve");
        Check.Equal(2, deez.Calls, "no reserve request after global flood wait");
        await LegacySearchLimitAsync();
        await TwoFetchLanesAsync();
        await BusyRaceAsync();
    }

    private sealed class RaceSource(string id) : IRacingMusicSource
    {
        public string Id => id;
        public int Fetches, Requests; public int? AfterBusy; public bool Cancelled;
        public Func<MusicRequest, Stream, Task<string>> Fetch = (_, _) => Task.FromResult("flac");
        public Func<CancellationToken, Task> Answer = _ => Task.CompletedTask;
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MusicRequest>>([]);
        public Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct) { Fetches++; return Fetch(request, output); }
        public async Task<BotFile> RequestFileAsync(MusicRequest request, int? afterBusyReply, CancellationToken ct)
        {
            Requests++; AfterBusy = afterBusyReply;
            try { await Answer(ct); } catch (OperationCanceledException) { Cancelled = true; throw; }
            return new(id, async (output, _, token) => { await output.WriteAsync(System.Text.Encoding.ASCII.GetBytes("fLaC " + id), token); return "flac"; });
        }
    }

    private static async Task BusyRaceAsync()
    {
        // Owner #84: clicking fast made Music Hunters answer "Please wait for the current download to complete before
        // starting a new one" and the song went nowhere.
        Check.Section("a busy Music Hunters races DeezLoad: whichever bot sends the file first is used");
        Check.True(BotText.IsBusy("Please wait for the current download to complete before starting a new one."), "the busy answer is recognised");
        Check.True(BotText.IsBusy("⏳ Please wait until the previous download is finished."), "in other wording too");
        Check.True(!BotText.IsBusy("1. Weezer - Wait for the Current (3:12)") && !BotText.IsBusy(null), "a result line or a song title is not a busy answer");
        Check.True(BotText.IsDownloadDone("Download completed!") && BotText.IsDownloadDone("✅ Done"), "the bot saying it is free again is recognised");
        Check.True(!BotText.IsDownloadDone("Please wait for the current download to complete before starting a new one."), "the busy answer is not taken for 'done'");

        using var output = new MemoryStream();
        var hunters = new RaceSource(BotSelections.Hunters) { Fetch = (_, _) => throw new BotBusyException(BotSelections.Hunters, "busy", 77) };
        var deezRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deez = new RaceSource("DeezLoad") { Answer = ct => deezRelease.Task.WaitAsync(ct) };
        var sources = new MusicSources(hunters, deez, _ => { }, firstBot: () => BotSelections.Hunters);
        var ext = await sources.FetchAsync(new("Sundress", "A$AP Rocky"), output, null, default);
        Check.True(ext == "flac" && System.Text.Encoding.ASCII.GetString(output.ToArray()) == "fLaC " + BotSelections.Hunters, "Music Hunters, free again first, supplies the file");
        Check.Equal(77, hunters.AfterBusy ?? 0, "Music Hunters is asked again after its own 'please wait' answer");
        Check.True(deez.Requests == 1 && deez.Cancelled, "DeezLoad was asked at once, and its wait was dropped when Music Hunters won");

        var slowHunters = new RaceSource(BotSelections.Hunters) { Fetch = (_, _) => throw new BotBusyException(BotSelections.Hunters, "busy", 5), Answer = ct => Task.Delay(Timeout.Infinite, ct) };
        var quick = new RaceSource("DeezLoad");
        using var second = new MemoryStream();
        await new MusicSources(slowHunters, quick, _ => { }, firstBot: () => BotSelections.Hunters).FetchAsync(new("Kevin's Heart", "J. Cole"), second, null, default);
        Check.Equal("fLaC DeezLoad", System.Text.Encoding.ASCII.GetString(second.ToArray()), "DeezLoad first: its file is used");
        await Check.UntilAsync(() => slowHunters.Cancelled, "and the Music Hunters wait stops");

        var noHunters = new RaceSource(BotSelections.Hunters) { Fetch = (_, _) => throw new BotBusyException(BotSelections.Hunters, "busy", 5), Answer = _ => throw new TrackUnavailableException("no link") };
        var silent = new RaceSource("DeezLoad") { Answer = _ => throw new SourceLimitException("DeezLoad", "DeezLoad did not answer", DateTimeOffset.UtcNow.AddMinutes(10)) };
        using var third = new MemoryStream();
        await Check.ThrowsAsync<SourcesWaitingException>(() => new MusicSources(noHunters, silent, _ => { }, firstBot: () => BotSelections.Hunters).FetchAsync(new("Blessings", "Big Sean"), third, null, default),
            "both failing keeps the song for later (DeezLoad's silence is remembered), not lost");
    }

    private static async Task LegacySearchLimitAsync()
    {
        Check.Section("an old build's 24-hour Music Hunters 'download limit' (really its daily search limit) no longer skips the first bot");
        // Owner #79, the TEST profile's real file: saved 2026-10-19 14:15 UTC by a build without the name-search fix.
        var root = Check.NewFolder("legacy-hunters-limit"); var state = Path.Combine(root, "source-limits.json");
        var seen = DateTimeOffset.Parse("2026-10-19T14:15:04Z"); var now = seen.AddHours(2);
        File.WriteAllText(state, "[{\"Id\":\"Music Hunters\",\"Until\":\"" + seen.AddHours(24).ToString("O") + "\",\"Reason\":\"Music Hunters reported a download limit. No reset time was supplied; checking again in 24 hours.\"}]");
        var hunters = new TransferTests.Source(BotSelections.Hunters); var deez = new TransferTests.Source("DeezLoad");
        var sources = new MusicSources(hunters, deez, _ => { }, state, () => now, firstBot: () => BotSelections.Hunters);
        using var output = new MemoryStream();
        await sources.FetchAsync(new("SICKO MODE", "Travis Scott"), output, null, default);
        Check.Equal(1, hunters.Calls, "the chosen first bot is asked again (track links work under the search limit)");
        Check.Equal(0, deez.Calls, "the reserve is not used for it");
        await sources.SearchAsync("sicko mode", default);
        Check.Equal(0, hunters.Searches, "name searches still wait out the 3 hours since the answer");
        now = seen.AddHours(3).AddMinutes(1);
        await new MusicSources(hunters, deez, _ => { }, state, () => now, firstBot: () => BotSelections.Hunters).SearchAsync("sicko mode", default);
        Check.Equal(1, hunters.Searches, "after 3 hours name searches go to Music Hunters again");

        // A real download limit is learned again from the bot and saved with its scope, so it is kept.
        hunters.Fetch = (_, _) => throw BotLimits.Read(BotSelections.Hunters, "You have reached your download limit.", now)!;
        var relearn = new MusicSources(hunters, deez, _ => { }, state, () => now, firstBot: () => BotSelections.Hunters);
        await relearn.FetchAsync(new("Real limit", "Artist"), output, null, default);
        Check.Equal(1, deez.Calls, "a real download limit hands the song to the reserve");
        Check.True(File.ReadAllText(state).Contains("\"Scope\":\"download\""), "the new limit is saved with its scope");
        var after = hunters.Calls;
        await new MusicSources(hunters, deez, _ => { }, state, () => now, firstBot: () => BotSelections.Hunters).FetchAsync(new("Next", "Artist"), output, null, default);
        Check.Equal(after, hunters.Calls, "a scoped download limit survives a restart and is not mistaken for the old search-limit entry");
        Check.Equal(2, deez.Calls, "the reserve keeps supplying songs meanwhile");
    }

    private static async Task TwoFetchLanesAsync()
    {
        Check.Section("two downloads reach the bots side by side; a bot-menu change does not wait for them (owner #183)");
        var started = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new TransferTests.Source("DeezLoad")
        { Fetch = async (_, output) => { Interlocked.Increment(ref started); await release.Task; await output.WriteAsync("fLaC"u8.ToArray()); return "flac"; } };
        var sources = new MusicSources(new TransferTests.Source(BotSelections.Hunters), slow, _ => { }, firstBot: () => "DeezLoad");
        using MemoryStream a = new(), b = new(), c = new();
        var first = sources.FetchAsync(new("Dark Side of the Moon", "Lil Wayne"), a, null, default);
        var second = sources.FetchAsync(new("SICKO MODE", "Travis Scott"), b, null, default);
        await Check.UntilAsync(() => Volatile.Read(ref started) == 2, "the Play is asked for while the other song still transfers");
        var third = sources.FetchAsync(new("Finesse", "Drake"), c, null, default);
        var sync = sources.SynchronizeQualityAsync(default);
        await Task.Delay(150);
        Check.True(Volatile.Read(ref started) == 2 && !third.IsCompleted, "a third download waits for a free lane");
        Check.True(sync.IsCompleted, "the menu change is done while both songs still transfer (a friend's MP3 switch took minutes)");
        release.SetResult();
        await Task.WhenAll(first, second, third, sync);
        Check.Equal(3, Volatile.Read(ref started), "all three songs were fetched");
    }
}
