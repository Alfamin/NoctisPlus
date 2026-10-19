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
    }
}
