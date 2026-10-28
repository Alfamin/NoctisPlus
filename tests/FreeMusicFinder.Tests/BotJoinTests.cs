using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #190: a friend's new account was told to join the bot creator's channel before getting songs.</summary>
internal static class BotJoinTests
{
    private static KeyboardInlineButton Url(string text, string url) => new() { text = text, type = new InlineButtonTypeUrl { url = url } };
    private static KeyboardInlineButton Press(string text, byte[] data) => new() { text = text, type = new InlineButtonTypeCallback { data = data } };
    private static Message Bot(string text, params KeyboardInlineButton[] buttons) => new()
    {
        id = 9, message = text,
        reply_markup = buttons.Length == 0 ? null : new ReplyInlineMarkup { rows = [new KeyboardInlineButtonRow { buttons = buttons }] },
    };

    public static void Run()
    {
        Check.Section("Bots that ask for their channel first (#190)");

        var gate = Bot("⚠️ To use this bot you must join our channel first.", Url("📢 Join channel", "https://t.me/HuntersNews"), Press("✅ I joined", [1, 2]));
        Check.Equal("HuntersNews", string.Join(",", BotJoin.Links(gate, "MusicsHuntersbot")), "a join button's channel is read");
        Check.Equal("1,2", string.Join(",", BotJoin.CheckButton(gate) ?? []), "the gate's I joined button is found");
        Check.Equal("HuntersNews", string.Join(",", BotJoin.Links(gate, "MusicsHuntersbot", welcome: true)), "a greeting that says the channel is needed is a gate");

        var mention = Bot("Подпишитесь на канал @deez_news чтобы пользоваться ботом");
        Check.Equal("deez_news", string.Join(",", BotJoin.Links(mention, "deezload2bot")), "a Russian answer naming the channel is read");
        var persian = Bot("برای استفاده از ربات ابتدا در کانال عضو شوید: t.me/+AbCdEf1234");
        Check.Equal("+AbCdEf1234", string.Join(",", BotJoin.Links(persian, "deezload2bot", welcome: true)), "a Persian private invite is read");

        var advert = Bot("Welcome! Join @HuntersNews for updates and new features.");
        Check.Equal(0, BotJoin.Links(advert, "MusicsHuntersbot", welcome: true).Count, "a greeting that only advertises a channel is not a gate");
        var menu = Bot("Audio quality. Join @HuntersNews", Press("FLAC ✅", [7]), Press("MP3 320", [8]));
        Check.Equal(0, BotJoin.Links(menu, "MusicsHuntersbot").Count, "a settings menu that names the channel is not a gate");
        var song = Bot("Join @HuntersNews"); song.media = new MessageMediaDocument { document = new Document() };
        Check.Equal(0, BotJoin.Links(song, "MusicsHuntersbot").Count, "a song's caption is never a gate");
        var bots = Bot("You must join @OtherMusicBot and @MusicsHuntersbot first");
        Check.Equal(0, BotJoin.Links(bots, "MusicsHuntersbot").Count, "bots are not channels to join");
        Check.Equal(0, BotJoin.Links(Bot("Downloading… 45%"), "deezload2bot").Count, "an ordinary answer is not a gate");

        Check.Equal("+AbCdEf1234", BotJoin.Normal("https://t.me/joinchat/AbCdEf1234"), "an old invite link becomes +hash");
        Check.Equal("news_channel", BotJoin.Normal("t.me/news_channel/123"), "a post link names its channel");
        Check.Equal(null, BotJoin.Normal("https://t.me/share/url?url=x"), "a share link is not a channel");
        Check.Equal(null, BotJoin.Normal("https://example.com/t.me/news"), "only Telegram links count");

        string? told = null;
        void Told(string provider, string bot, IReadOnlyList<string> links) => told = provider + "|" + bot + "|" + string.Join(",", links);
        BotJoin.Needed += Told;
        try
        {
            var limit = BotJoin.Gate("DeezLoad", "deezload2bot", ["deez_news"]);
            Check.Equal("DeezLoad|deezload2bot|deez_news", told, "a gate is reported so the app can say what to do");
            Check.True(limit.Provider == "DeezLoad" && limit.Message.Contains(BotJoin.Asks) && limit.Until <= DateTimeOffset.UtcNow.Add(BotJoin.Rest),
                "the bot rests briefly while the other bot takes the songs");
        }
        finally { BotJoin.Needed -= Told; }
    }
}
