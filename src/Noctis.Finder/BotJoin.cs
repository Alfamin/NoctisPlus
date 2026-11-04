using System.Text.RegularExpressions;
using TL;

namespace FreeMusicFinder;

/// <summary>A channel a bot asks its users to join before it answers (owner #190). <paramref name="Link"/> is a public
/// username ("name") or a private invite ("+hash").</summary>
internal sealed record BotChannel(string Bot, string Provider, string Link, string Title)
{
    /// <summary>The channel as Telegram gave it while checking, so joining needs no second lookup.</summary>
    public Channel? Peer { get; init; }
}

/// <summary>Owner #190: a new account's bot may answer "join our channel first" instead of sending songs. Reading that
/// answer; no Telegram calls in here.</summary>
internal static partial class BotJoin
{
    [GeneratedRegex(@"\b(join|joined|subscribe|subscribed|subscription|member)\b|подпи|вступ|عضو|اشترک|اشترك|انضم|únete|unete|suscr|katıl|abone|inscreva|abonn|beitr", RegexOptions.IgnoreCase)]
    private static partial Regex JoinWords();
    [GeneratedRegex(@"\b(must|need|needs|required|requires|first|before|to use|in order|only)\b|ابتدا|باید|لطفا|сначала|необходимо|нужно|должн|يجب|أولا|primero|önce|zuerst|obligatoire", RegexOptions.IgnoreCase)]
    private static partial Regex Required();
    // The buttons a gate has besides its links: "Refresh" (Music Hunters, live 2026-10-28), "I joined", "Check".
    [GeneratedRegex(@"\b(refresh|retry|reload|joined|done|check|verify|confirm|continue|ready)\b|✅|🔄|تایید|تأیید|عضو شدم|بروز|провер|обнов|подписал|تحقق|تحديث|اشتركت|verificar|kontrol|yenile", RegexOptions.IgnoreCase)]
    private static partial Regex AfterJoin();
    // What a bot says to the first message once its channel was joined (live 2026-10-28: "Thanks for joining! You can use
    // the bot now.", "Welcome to @DeezLoad2bot"). It answers no request.
    /// <summary>Owner #203: text (a saved quality problem with its menu) that is a channel gate: join words plus a
    /// refresh/check step. Such a problem is retried soon, like a bot that did not answer, never kept for a day.</summary>
    internal static bool LooksLikeGate(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains(Asks, StringComparison.Ordinal) || JoinWords().IsMatch(text) && AfterJoin().IsMatch(text));

    [GeneratedRegex(@"thanks? (you )?for joining|you can (now )?use (the|this) bot|\A\W*welcome to\b", RegexOptions.IgnoreCase)]
    private static partial Regex Unlocked();
    [GeneratedRegex(@"(?:https?://)?(?:www\.)?(?:t|telegram)\.me/(\+[\w-]{6,}|joinchat/[\w-]{6,}|[A-Za-z]\w{3,31})", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
    [GeneratedRegex(@"(?<![\w@./])@([A-Za-z]\w{3,31})")]
    private static partial Regex Mention();
    [GeneratedRegex(@"\b(channel|channels)\b|канал|کانال|قناة|canal|kanal", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelWord();
    // Owner (2026-10-28): each bot's channel, for a gate whose link cannot be read (its button gone or changed).
    // Music Hunters' says "Bot link - @MusicsHuntersbot"; DeezLoad's is the other one the owner gave.
    internal static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["MusicsHuntersbot"] = "MusicsHunter", ["deezload2bot"] = "DEDSEClulz" };
    // t.me paths that are never a channel.
    private static readonly HashSet<string> NotChannels = new(StringComparer.OrdinalIgnoreCase)
        { "share", "addstickers", "addemoji", "addlist", "proxy", "socks", "setlanguage", "login", "iv", "boost", "contact", "invoice", "joinchat" };

    /// <summary>How long a bot that asked rests while the app joins its channel (about 7 s live); the other bot takes
    /// its songs meanwhile.</summary>
    internal static readonly TimeSpan Rest = TimeSpan.FromSeconds(10);
    internal const string Asks = " asks for its channel first";

    /// <summary>Raised with the provider, the bot's username and the links, whenever a bot answers with a channel to join.</summary>
    internal static event Action<string, string, IReadOnlyList<string>>? Needed;

    /// <summary>What a download or settings check throws on such an answer: the bot rests briefly and the other one is asked.</summary>
    internal static SourceLimitException Gate(string provider, string bot, IReadOnlyList<string> links)
    {
        try { Needed?.Invoke(provider, bot, links); } catch { /* the notice is best effort; the download carries on */ }
        return new SourceLimitException(provider, provider + Asks + "; joining it.",
            DateTimeOffset.UtcNow.Add(Rest));
    }

    /// <summary>The channels a bot's message asks to join; empty when it is not such a message.</summary>
    /// <param name="welcome">The bot's greeting after /start, which often only advertises a channel: it must also say the
    /// channel is needed. An answer to a request that names a channel to join is a gate by itself.</param>
    internal static IReadOnlyList<string> Links(Message message, string bot, bool welcome = false)
    {
        if (message.flags.HasFlag(Message.Flags.out_) || message.media is MessageMediaDocument) return [];
        var text = message.message ?? "";
        var buttons = message.reply_markup is ReplyInlineMarkup markup ? markup.rows.SelectMany(r => r.buttons).ToList() : [];
        // A menu (quality choices, numbered results) that also names the bot's channel is not a gate: a gate's only
        // buttons are links and an "I joined" check.
        if (buttons.Any(b => b.type is InlineButtonTypeCallback && !AfterJoin().IsMatch(b.text))) return [];
        var words = text + "\n" + string.Join("\n", buttons.Select(b => b.text));
        if (!JoinWords().IsMatch(words) || welcome && !Required().IsMatch(words)) return [];
        var found = new List<string>();
        foreach (var button in buttons) if (button.type is InlineButtonTypeUrl url) Add(url.url);
        foreach (var entity in message.entities ?? [])
        {
            if (entity is MessageEntityTextUrl linked) Add(linked.url);
            else if (entity is MessageEntityUrl or MessageEntityMention && entity.offset >= 0 && entity.offset + entity.length <= text.Length)
                Add(text.Substring(entity.offset, entity.length));
        }
        foreach (Match match in Link().Matches(text)) Add(match.Value);
        foreach (Match match in Mention().Matches(text)) Add(match.Value);
        // No link to read, but plainly a gate ("Join our channel to use this bot", "Only Channel Subscribers can use the
        // Bot"): the bot's known channel.
        if (found.Count == 0 && Known.TryGetValue(bot, out var known) && Required().IsMatch(words) && ChannelWord().IsMatch(words)) found.Add(known);
        return found;

        void Add(string? raw)
        {
            if (Normal(raw) is not { } link) return;
            // The bot itself, or another bot, is not a channel to join.
            if (!link.StartsWith('+') && (string.Equals(link, bot, StringComparison.OrdinalIgnoreCase) || link.EndsWith("bot", StringComparison.OrdinalIgnoreCase))) return;
            if (!found.Contains(link, StringComparer.OrdinalIgnoreCase)) found.Add(link);
        }
    }

    /// <summary>The greeting a bot sends once its channel was joined, instead of answering the request.</summary>
    internal static bool IsUnlocked(Message message)
        => !message.flags.HasFlag(Message.Flags.out_) && message.media is not MessageMediaDocument && Unlocked().IsMatch(message.message ?? "");

    /// <summary>"@name", "t.me/name" or "https://t.me/name/12" → "name"; "t.me/+hash" or "t.me/joinchat/hash" → "+hash";
    /// null for anything else.</summary>
    internal static string? Normal(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.StartsWith('@')) return Regex.IsMatch(raw, @"\A@[A-Za-z]\w{3,31}\z") ? raw[1..] : null;
        var match = Link().Match(raw);
        if (!match.Success || match.Index != 0) return null;
        var path = match.Groups[1].Value;
        if (path.StartsWith("joinchat/", StringComparison.OrdinalIgnoreCase)) return "+" + path["joinchat/".Length..];
        return NotChannels.Contains(path) ? null : path;
    }
}
