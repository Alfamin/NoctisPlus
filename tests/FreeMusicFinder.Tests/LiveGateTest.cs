using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #190 live check: DeezLoad greets /start normally but answers a song request with "Join Updates Channel
/// to use this Bot!". With FMF_LIVE_GATE_LEAVE=1 the account first leaves DeezLoad's channel; then one track is asked
/// for the way the app asks, the gate is joined the way the app joins it, and the track is asked for again. The file is
/// not downloaded.</summary>
internal static class LiveGateTest
{
    public static async Task RunAsync(string trackLink, string? store, string? proxy)
    {
        Console.WriteLine("\nDeezLoad channel gate, live");
        if (string.IsNullOrWhiteSpace(store) || !File.Exists(store)) throw new ArgumentException("FMF_TELEGRAM_STORE is required with FMF_LIVE_GATE.");
        using var account = new TelegramAccount(store, () => proxy ?? "", way => Console.WriteLine("  " + way));
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = patience.Token;
        var client = await account.ConnectAsync(ct);
        var channel = (await client.Contacts_ResolveUsername(BotJoin.Known[DeezLoadSource.Bot])).Chat as Channel
            ?? throw new InvalidDataException("DeezLoad's channel could not be found.");
        if (Environment.GetEnvironmentVariable("FMF_LIVE_GATE_LEAVE") == "1" && !channel.flags.HasFlag(Channel.Flags.left))
        {
            await client.Channels_LeaveChannel(channel);
            Console.WriteLine($"  left \"{channel.title}\" so DeezLoad has something to ask");
        }

        using var catalog = new PlaylistCatalog(PlaylistCatalog.MetadataHandler(proxy ?? ""));
        if (!MusicLink.TryParse(trackLink, out var link) || link!.Kind != "track") throw new ArgumentException("FMF_LIVE_GATE needs a Spotify or Deezer track link.");
        var request = (await catalog.ReadAsync(link, null, _ => { }, _ => { }, ct)).Entries.Single().Request
            ?? throw new InvalidDataException("The link's metadata could not be read; nothing was sent.");
        Console.WriteLine($"  asking for: {request.Artist} - {request.Title}");
        var deezLoad = new DeezLoadSource(account, notice => Console.WriteLine("  notice: " + notice));

        // What FreeMusicPlugin does when a bot asks for its channel.
        Task<bool>? joining = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Join(string provider, string bot, IReadOnlyList<string> links)
        {
            Console.WriteLine($"  [{clock.Elapsed.TotalSeconds:0.0} s] {provider} asked for its channel: {string.Join(", ", links)}; joining");
            joining = account.JoinGateAsync(bot, provider, links, ct);
        }
        BotJoin.Needed += Join;
        try
        {
            try
            {
                await deezLoad.RequestFileAsync(request, null, ct);
                Console.WriteLine($"  [{clock.Elapsed.TotalSeconds:0.0} s] DeezLoad sent the track at once (it did not ask for its channel)");
            }
            catch (SourceLimitException ex) { Console.WriteLine($"  [{clock.Elapsed.TotalSeconds:0.0} s] request answered: {ex.Message}"); }
            if (joining is null) { Console.WriteLine("  RESULT: no channel was asked for."); return; }
            Console.WriteLine($"  [{clock.Elapsed.TotalSeconds:0.0} s] joined: {await joining}");
            var again = System.Diagnostics.Stopwatch.StartNew();
            var file = await deezLoad.RequestFileAsync(request, null, ct);
            Console.WriteLine($"  [{clock.Elapsed.TotalSeconds:0.0} s] asked again: {file.Source} has the track after {again.Elapsed.TotalSeconds:0.0} s (not downloaded)");
            Console.WriteLine("  RESULT: the gate was joined by itself and DeezLoad answers again.");
        }
        finally { BotJoin.Needed -= Join; }
    }
}
