using FreeMusicFinder;
using System.Text;

namespace FreeMusicFinder.Tests;

internal static class HunterLinkTests
{
    private sealed class Bot : IHunterBotSource
    {
        public readonly List<string> Calls = [];
        public Exception? Error;
        public Func<Task>? OnSearch;
        public bool Empty;
        public Task<string> FetchSelectedAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
            => Task.FromResult("flac");
        public async Task<IReadOnlyList<BotTrack>> SearchAsync(string query, CancellationToken ct)
        {
            Calls.Add("search");
            if (OnSearch is not null) await OnSearch();
            if (Error is not null) throw Error;
            return Empty ? [] : [new("Song", "Artist", TimeSpan.FromSeconds(200), "song", (_, _, _) => Task.FromResult("flac"))];
        }
        public async Task<string> FetchLinkAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
        {
            Calls.Add("link:" + request.Url);
            await output.WriteAsync(Encoding.ASCII.GetBytes("fLaClink"), ct);
            return "flac";
        }
    }

    public static async Task RunAsync()
    {
        Check.Section("Music Hunters name quota uses an already-resolving link, not another name search");
        var request = new MusicRequest("Song", "Artist", 200);
        var resolving = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        var bot = new Bot { Error = new SourceLimitException(BotSelections.Hunters, "daily search", DateTimeOffset.UtcNow.AddHours(3), searchOnly: true) };
        bot.OnSearch = () => { Check.True(started, "link lookup began before the name search"); resolving.SetResult("https://www.deezer.com/track/3135556"); return Task.CompletedTask; };
        var source = new HunterMusicSource(bot, resolveLink: (_, _) => { started = true; return resolving.Task; });
        using var output = new MemoryStream();
        Check.Equal("flac", await source.FetchAsync(request, output, null, default), "name quota takes the link path");
        Check.Equal("search,link:https://www.deezer.com/track/3135556", string.Join(",", bot.Calls), "exactly one name request and one link request");

        var known = new Bot { Empty = true };
        var lookedUp = false;
        await new HunterMusicSource(known, resolveLink: (_, _) => { lookedUp = true; return Task.FromResult<string?>(null); })
            .FetchAsync(request with { Url = "https://open.spotify.com/track/0123456789012345678901" }, output, null, default);
        Check.False(lookedUp, "catalog track links need no metadata lookup");
        Check.Equal(2, known.Calls.Count, "empty name results immediately use the existing link");

        foreach (var error in new Exception[] { new SourceLimitException(BotSelections.Hunters, "download limit", DateTimeOffset.UtcNow.AddHours(1)),
            new SourceLimitException(BotSelections.Hunters, "Telegram flood", DateTimeOffset.UtcNow.AddHours(1), global: true, searchOnly: true) })
        {
            var limited = new Bot { Error = error };
            await Check.ThrowsAsync<SourceLimitException>(() => new HunterMusicSource(limited).FetchAsync(request, output, null, default), "download/global limits are honored without a link retry");
            Check.Equal(1, limited.Calls.Count, "a real limit never triggers another bot request");
        }

        var noLink = new Bot { Error = bot.Error };
        await Check.ThrowsAsync<TrackUnavailableException>(() => new HunterMusicSource(noLink).FetchAsync(request, output, null, default), "unresolved link releases the request to the reserve");
        Check.Equal(1, noLink.Calls.Count, "no repeat name search after daily quota");

        var nameWorks = new Bot();
        await new HunterMusicSource(nameWorks, resolveLink: (_, _) => Task.FromException<string?>(new HttpRequestException("offline")))
            .FetchAsync(request, output, null, default);
        Check.Equal("search", string.Join(",", nameWorks.Calls), "optional lookup failure cannot break a matching name result");
        Check.False(HunterMusicSource.IsTrackLink("https://evil.invalid/track/3135556"), "untrusted URLs are never sent as music links");
        Check.False(HunterMusicSource.IsTrackLink("https://open.spotify.com/playlist/0123456789012345678901"), "playlist is not sent for a single recording");
        Check.True(BotText.IsEmptySearch("❌ No results found. Try a link."), "plain no-result reply does not wait for missing buttons");
        Check.False(BotText.IsEmptySearch("1. Artist - Nothing Found (3:20)"), "a song title is not an empty-result signal");
        Check.False(BotText.IsSearchLimit("1. Artist - Daily Search Limit (3:20)"), "a numbered song title cannot impose a provider quota");
        Check.True(BotLimits.Read(BotSelections.Hunters, "Daily search limit reached for today. Send a link.", DateTimeOffset.UtcNow)?.SearchOnly == true,
            "a search quota remains search-only when read outside the name-search path");

        var file = new TL.Document { mime_type = "audio/mpeg", attributes = [new TL.DocumentAttributeFilename { file_name = "Song.mp3" }, new TL.DocumentAttributeAudio { performer = "Artist", title = "Song", duration = 200 }] };
        var incoming = new TL.Message { media = new TL.MessageMediaDocument { document = file } };
        Check.True(TelegramBotSource.IsLinkFile(request, incoming), "linked MP3 audio is a valid recording, not FLAC-only");
        incoming.flags |= TL.Message.Flags.out_;
        Check.False(TelegramBotSource.IsLinkFile(request, incoming), "outgoing messages cannot satisfy a linked download");
        incoming.flags = 0;
        file.attributes.OfType<TL.DocumentAttributeAudio>().Single().performer = "Other Artist";
        Check.False(TelegramBotSource.IsLinkFile(request, incoming), "another artist's late file cannot satisfy the current link");
        using var canceled = new CancellationTokenSource();
        var cancelBot = new Bot { Empty = true, OnSearch = () => { canceled.Cancel(); return Task.CompletedTask; } };
        await Check.ThrowsAsync<OperationCanceledException>(() => new HunterMusicSource(cancelBot).FetchAsync(request with { Url = "https://www.deezer.com/track/3135556" }, output, null, canceled.Token), "cancellation wins before the link send");
        Check.Equal(1, cancelBot.Calls.Count, "cancel never sends a second Telegram message");

        var limitsPath = Path.Combine(Check.NewFolder("hunter-link-limits"), "limits.json");
        AtomicJson.Write(limitsPath, new[] { new SourceCooldown(BotSelections.Hunters + " search", DateTimeOffset.UtcNow.AddHours(2), "name search only") });
        var linkProvider = new TransferTests.Source(BotSelections.Hunters);
        var reserve = new TransferTests.Source("DeezLoad");
        var router = new MusicSources(linkProvider, reserve, _ => { }, limitsPath);
        await router.SearchAsync("Song", default);
        Check.Equal(0, linkProvider.Searches, "persisted search-only cooldown suppresses name queries");
        await router.FetchAsync(request, output, null, default);
        Check.Equal(1, linkProvider.Calls, "same cooldown does not suppress track downloads");
        var silentReply = new TaskCompletionSource<TL.Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Check.ThrowsAsync<TimeoutException>(() => TelegramBotSource.WaitForLinkReplyAsync(_ => silentReply.Task, default, TimeSpan.FromMilliseconds(25)),
            "a Telegram history call ignoring cancellation cannot outlive the linked-reply deadline");
        using var userStop = new CancellationTokenSource();
        userStop.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => TelegramBotSource.WaitForLinkReplyAsync(_ => silentReply.Task, userStop.Token),
            "owner cancellation is preserved rather than turned into a reserve retry");
    }
}
