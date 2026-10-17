using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>
/// Optional, and it uses a real Telegram account: asks DeezLoad for one track the way the plugin
/// does, in whatever state the chat with the bot is (emptied, to see a first contact), and prints
/// what the bot answers. FMF_LIVE_BOT holds a Spotify or Deezer track link, FMF_TELEGRAM_STORE the
/// plugin's login file, FMF_LIVE_PROXY the "Proxy" setting (empty: the computer's own).
/// It sends one message to the bot from that account. The file the bot sends is looked at, never
/// downloaded. Noctis must be closed: one login cannot be used from two programs at once.
/// </summary>
internal static class LiveBotTest
{
    public static async Task RunAsync(string trackLink, string? store, string? proxy)
    {
        Check.Section("live: asking DeezLoad for one track with the real account");
        if (string.IsNullOrWhiteSpace(store) || !File.Exists(store)) throw new ArgumentException("FMF_TELEGRAM_STORE (the plugin's login file) is required with FMF_LIVE_BOT.");
        if (!MusicLink.TryParse(trackLink, out var link) || link!.Kind != "track") throw new ArgumentException("FMF_LIVE_BOT needs a Spotify or Deezer track link.");
        using var account = new TelegramAccount(store, () => proxy ?? "", way => Console.WriteLine("  " + way));
        if (!account.IsLoggedIn) { Console.WriteLine("  this login file holds no login; nothing was sent"); return; }

        // What is asked for, from the public metadata, as a playlist import has it.
        using var catalog = new PlaylistCatalog(PlaylistCatalog.MetadataHandler(proxy ?? ""));
        var request = (await catalog.ReadAsync(link, null, _ => { }, _ => { }, default)).Entries.Single().Request
            ?? throw new InvalidDataException("The link's metadata could not be read; nothing was sent.");
        Console.WriteLine($"  asking for: {request.Artist} - {request.Title} ({request.Seconds:0} s)");

        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = patience.Token;
        var client = await account.ConnectAsync(ct);
        foreach (var name in new[] { DeezLoadSource.Bot, TelegramBotSource.Bot })
        {
            var newest = await client.Messages_GetHistory((await client.Contacts_ResolveUsername(name)).User, limit: 5);
            Console.WriteLine($"  chat with @{name}: {(newest.Messages.Length == 0 ? "empty, so the next message is a first contact as far as Telegram shows" : newest.Messages.Length + " of its newest 5 messages are there")}");
        }

        var bot = (await client.Contacts_ResolveUsername(DeezLoadSource.Bot)).User;
        Message sent;
        try { sent = await client.SendMessageAsync(bot, link.Url); } // what DeezLoadSource.FetchAsync sends, with no /start before it
        catch (RpcException ex) { Console.WriteLine($"  Telegram did not take the message: {ex.Message}"); return; }
        Console.WriteLine("  sent the track link (no /start before it); waiting up to 3 minutes for the bot");

        var said = new Dictionary<int, string>();
        var end = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < end)
        {
            foreach (var answer in await DeezLoadSource.ReadNew(client, bot, sent.id, ct))
            {
                var text = Describe(answer, request);
                if (said.GetValueOrDefault(answer.id) == text) continue; // the bot rewrites its progress message
                said[answer.id] = text;
                Console.WriteLine($"  [{(DateTime.UtcNow - (end - TimeSpan.FromMinutes(3))).TotalSeconds,3:0} s] bot: {text}");
                if (answer.media is MessageMediaDocument { document: Document file } && DeezLoadSource.Fits(request, file))
                {
                    Console.WriteLine("  RESULT: the bot sent the track that was asked for. It was not downloaded.");
                    return;
                }
                if (BotLimits.Read("DeezLoad", answer.message ?? "", DateTimeOffset.UtcNow) is { } limit)
                {
                    Console.WriteLine($"  RESULT: the bot set a limit (until {limit.Until.ToLocalTime():ddd HH:mm}).");
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
        Console.WriteLine(said.Count == 0 ? "  RESULT: the bot answered nothing in 3 minutes." : "  RESULT: the bot answered, but sent no fitting track in 3 minutes.");
    }

    private static string Describe(Message answer, MusicRequest request)
    {
        var parts = new List<string>();
        if (answer.message is { Length: > 0 } text) parts.Add("\"" + (text.Length > 200 ? text[..200] + "…" : text).ReplaceLineEndings(" / ") + "\"");
        if (answer.media is MessageMediaDocument { document: Document file })
        {
            var audio = file.attributes?.OfType<DocumentAttributeAudio>().FirstOrDefault();
            parts.Add($"file {file.Filename} · {file.mime_type} · {file.size / 1048576.0:0.0} MB · {audio?.performer} - {audio?.title} · {audio?.duration} s · {(DeezLoadSource.Fits(request, file) ? "fits the request" : "does NOT fit the request")}");
        }
        else if (answer.media is not null) parts.Add("media: " + answer.media.GetType().Name);
        if (answer.reply_markup is ReplyInlineMarkup markup)
            parts.Add("buttons: " + string.Join(", ", markup.rows.SelectMany(r => r.buttons).Select(b => b.type is InlineButtonTypeUrl to ? $"[{b.text} -> {to.url}]" : $"[{b.text}]")));
        return string.Join(" · ", parts);
    }
}
