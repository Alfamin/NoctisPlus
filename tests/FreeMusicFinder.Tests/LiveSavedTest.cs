using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #190: reads the newest Saved Messages (bot answers the owner forwarded there) and says whether each
/// one is read as "join our channel first". Read-only: nothing is sent.</summary>
internal static class LiveSavedTest
{
    public static async Task RunAsync(int count, string? store, string? proxy)
    {
        Console.WriteLine("\nSaved Messages, newest " + count);
        if (string.IsNullOrWhiteSpace(store) || !File.Exists(store)) throw new ArgumentException("FMF_TELEGRAM_STORE (the plugin's login file) is required with FMF_LIVE_SAVED.");
        using var account = new TelegramAccount(store, () => proxy ?? "", way => Console.WriteLine("  " + way));
        if (!account.IsLoggedIn) { Console.WriteLine("  this login file holds no login"); return; }
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var client = await account.ConnectAsync(patience.Token);
        var history = await client.Messages_GetHistory(InputPeer.Self, limit: count);
        foreach (var m in history.Messages.OfType<Message>().OrderBy(m => m.id))
        {
            if (m.fwd_from is null) continue; // only forwarded bot answers; the owner's own notes are never printed
            var from = m.fwd_from is { } fwd ? (fwd.from_name ?? (history.UserOrChat(fwd.from_id) is User user ? "@" + user.MainUsername : fwd.from_id?.ToString())) : "(not forwarded)";
            Console.WriteLine($"\n  #{m.id} from {from}, {m.date:yyyy-MM-dd HH:mm}");
            Console.WriteLine("    text: " + (m.message ?? "").ReplaceLineEndings(" / "));
            foreach (var e in m.entities ?? [])
                Console.WriteLine("    entity " + e.GetType().Name + (e is MessageEntityTextUrl t ? " → " + t.url : " \"" + Slice(m.message, e) + "\""));
            if (m.reply_markup is ReplyInlineMarkup markup)
                foreach (var b in markup.rows.SelectMany(r => r.buttons))
                    Console.WriteLine("    button \"" + b.text + "\" " + b.type?.GetType().Name + (b.type is InlineButtonTypeUrl url ? " → " + url.url : ""));
            else if (m.reply_markup is not null) Console.WriteLine("    keyboard " + m.reply_markup.GetType().Name);
            // A forward keeps no sender flag; as a reply and as a greeting after /start.
            var copy = new Message { id = m.id, message = m.message, entities = m.entities, reply_markup = m.reply_markup, media = m.media };
            foreach (var bot in new[] { TelegramBotSource.Bot, DeezLoadSource.Bot })
            {
                var asReply = BotJoin.Links(copy, bot);
                var asWelcome = BotJoin.Links(copy, bot, welcome: true);
                if (asReply.Count + asWelcome.Count > 0)
                    Console.WriteLine($"    gate for @{bot}: reply [{string.Join(", ", asReply)}], greeting [{string.Join(", ", asWelcome)}]");
            }
        }
    }

    /// <summary>Owner #190: what a public username is (channel, bot), its title and description, and whether this account
    /// is in it. Read-only.</summary>
    public static async Task ResolveAsync(string names, string? store, string? proxy)
    {
        Console.WriteLine("\nResolving " + names);
        if (string.IsNullOrWhiteSpace(store) || !File.Exists(store)) throw new ArgumentException("FMF_TELEGRAM_STORE is required with FMF_LIVE_RESOLVE.");
        using var account = new TelegramAccount(store, () => proxy ?? "", way => Console.WriteLine("  " + way));
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var client = await account.ConnectAsync(patience.Token);
        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var resolved = await client.Contacts_ResolveUsername(name);
            if (resolved.Chat is Channel channel)
            {
                var full = await client.Channels_GetFullChannel(channel);
                Console.WriteLine($"  {name}: {(channel.IsChannel ? "channel" : "group")} \"{channel.title}\", member: {!channel.flags.HasFlag(Channel.Flags.left)}, " +
                    $"about: {((full.full_chat as ChannelFull)?.about ?? "").ReplaceLineEndings(" / ")}");
            }
            else if (resolved.User is { } user) Console.WriteLine($"  {name}: {(user.IsBot ? "bot" : "user")} \"{user.first_name}\"");
            else Console.WriteLine($"  {name}: nothing");
        }
    }

    private static string Slice(string? text, MessageEntity e)
        => text is not null && e.offset >= 0 && e.offset + e.length <= text.Length ? text.Substring(e.offset, e.length) : "?";
}
