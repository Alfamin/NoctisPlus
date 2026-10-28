using System.Text.RegularExpressions;
using TL;

namespace FreeMusicFinder;

/// <summary>A channel a bot asks its users to join before it answers (owner #190). <paramref name="Link"/> is a public
/// username ("name") or a private invite ("+hash").</summary>
internal sealed record BotChannel(string Bot, string Provider, string Link, string Title);

/// <summary>Owner #190: a new account's bot may answer "join our channel first" instead of sending songs. Reading that
/// answer; no Telegram calls in here.</summary>
internal static partial class BotJoin
{
    [GeneratedRegex(@"\b(join|joined|subscribe|subscribed|subscription|member)\b|подпи|вступ|عضو|اشترک|اشترك|انضم|únete|unete|suscr|katıl|abone|inscreva|abonn|beitr", RegexOptions.IgnoreCase)]
    private static partial Regex JoinWords();
    [GeneratedRegex(@"\b(must|need|needs|required|requires|first|before|to use|in order|only)\b|ابتدا|باید|لطفا|сначала|необходимо|нужно|должн|يجب|أولا|primero|önce|zuerst|obligatoire", RegexOptions.IgnoreCase)]
    private static partial Regex Required();
    [GeneratedRegex(@"\b(joined|done|check|verify|confirm|continue|ready)\b|✅|تایید|تأیید|عضو شدم|провер|подписал|تحقق|اشتركت|verificar|kontrol", RegexOptions.IgnoreCase)]
    private static partial Regex CheckWords();
    [GeneratedRegex(@"(?:https?://)?(?:www\.)?(?:t|telegram)\.me/(\+[\w-]{6,}|joinchat/[\w-]{6,}|[A-Za-z]\w{3,31})", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
    [GeneratedRegex(@"(?<![\w@./])@([A-Za-z]\w{3,31})")]
    private static partial Regex Mention();
    // t.me paths that are never a channel.
    private static readonly HashSet<string> NotChannels = new(StringComparer.OrdinalIgnoreCase)
        { "share", "addstickers", "addemoji", "addlist", "proxy", "socks", "setlanguage", "login", "iv", "boost", "contact", "invoice", "joinchat" };

    /// <summary>How long a bot that asked rests before it is tried again; the other bot takes its songs meanwhile.</summary>
    internal static readonly TimeSpan Rest = TimeSpan.FromMinutes(2);
    internal const string Asks = " asks you to join its channel first";

    /// <summary>Raised with the provider, the bot's username and the links, whenever a bot answers with a channel to join.</summary>
    internal static event Action<string, string, IReadOnlyList<string>>? Needed;

    /// <summary>What a download or settings check throws on such an answer: the bot rests briefly and the other one is asked.</summary>
    internal static SourceLimitException Gate(string provider, string bot, IReadOnlyList<string> links)
    {
        try { Needed?.Invoke(provider, bot, links); } catch { /* the notice is best effort; the download carries on */ }
        return new SourceLimitException(provider, provider + Asks + ". Join it from Telegram account in Settings; the other bot takes the songs meanwhile.",
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
        if (buttons.Any(b => b.type is InlineButtonTypeCallback && !CheckWords().IsMatch(b.text))) return [];
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
        return found;

        void Add(string? raw)
        {
            if (Normal(raw) is not { } link) return;
            // The bot itself, or another bot, is not a channel to join.
            if (!link.StartsWith('+') && (string.Equals(link, bot, StringComparison.OrdinalIgnoreCase) || link.EndsWith("bot", StringComparison.OrdinalIgnoreCase))) return;
            if (!found.Contains(link, StringComparer.OrdinalIgnoreCase)) found.Add(link);
        }
    }

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

    /// <summary>The button a gate offers to press once joined ("I joined", "Check"), if any.</summary>
    internal static byte[]? CheckButton(Message message)
        => message.reply_markup is ReplyInlineMarkup markup
            ? markup.rows.SelectMany(r => r.buttons).Where(b => CheckWords().IsMatch(b.text))
                .Select(b => b.type is InlineButtonTypeCallback callback ? callback.data : null).FirstOrDefault(data => data is not null)
            : null;
}
