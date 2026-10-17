using System.Text.RegularExpressions;
using TL;
using WTelegram;

namespace FreeMusicFinder;

internal enum AudioQuality { FLAC, MP3 }

internal interface IQualityMusicSource
{
    Task SetQualityAsync(AudioQuality quality, CancellationToken ct);
}

internal sealed record QualityButton(string Text, byte[]? Callback = null);
internal sealed record QualityReply(int Id, string Text, IReadOnlyList<QualityButton> Buttons);

/// <summary>Provider chat transport, separate from the decision logic so real replies can be replayed in tests.</summary>
internal interface IQualityConversation
{
    Task<QualityReply> OpenAsync(CancellationToken ct);
    Task<QualityReply> PressAsync(QualityReply reply, QualityButton button, CancellationToken ct);
}

internal static class BotQuality
{
    private static string Words(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    private static bool QualityLabel(string value) => Regex.IsMatch(Words(value), @"\b(quality|format|calidad|качество|формат|کیفیت)\b");
    private static AudioQuality? Format(string value)
    {
        var words = Words(value);
        var flac = Regex.IsMatch(words, @"\b(flac|lossless)\b");
        var mp3 = Regex.IsMatch(words, @"\b(mp3|320|320kbps|320k)\b");
        return flac == mp3 ? null : flac ? AudioQuality.FLAC : AudioQuality.MP3;
    }

    internal static bool Confirmed(QualityReply reply, AudioQuality wanted)
    {
        // An unselected FLAC option in a list of choices is not an acknowledgement.
        var selected = reply.Buttons.Where(b => b.Text.Contains('✅') || b.Text.Contains('✓') || b.Text.Contains('☑'))
            .Select(b => Format(b.Text)).Where(q => q is not null).Distinct().ToList();
        if (selected.Count > 0) return selected.Count == 1 && selected[0] == wanted;
        foreach (var line in reply.Text.Split('\n'))
            if (QualityLabel(line) && Format(line) == wanted &&
                (line.Contains(':') || Regex.IsMatch(Words(line), @"\b(set|selected|changed|current|установлено|выбрано|انتخاب)\b"))) return true;
        return false;
    }

    public static async Task ApplyAsync(string provider, IQualityConversation chat, AudioQuality wanted, CancellationToken ct)
    {
        var reply = await chat.OpenAsync(ct).ConfigureAwait(false);
        for (var step = 0; step < 3; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (Confirmed(reply, wanted)) return;
            var choices = reply.Buttons.Where(b => Format(b.Text) == wanted).ToList();
            // For MP3 prefer an explicit 320 choice. Never select the first arbitrary bitrate.
            if (wanted == AudioQuality.MP3 && choices.Any(b => Words(b.Text).Contains("320")))
                choices = choices.Where(b => Words(b.Text).Contains("320")).ToList();
            if (choices.Count == 0) choices = reply.Buttons.Where(b => QualityLabel(b.Text)).ToList();
            if (choices.Count != 1) throw Unverified(provider, wanted);
            reply = await chat.PressAsync(reply, choices[0], ct).ConfigureAwait(false);
        }
        if (!Confirmed(reply, wanted)) throw Unverified(provider, wanted);
    }

    private static BotAnswerException Unverified(string provider, AudioQuality quality)
        => new($"BOT_QUALITY_UNVERIFIED: {provider} did not confirm {quality}. Its settings may have changed or this quality may be unavailable. No arbitrary option was selected; the other provider will be tried.");
}

/// <summary>Uses the app's one proxy-aware Telegram client. No new login/session and no guessed callback bytes.</summary>
internal sealed class TelegramQualityConversation(Client client, User user, string provider) : IQualityConversation
{
    private InputPeer Peer => user;

    public async Task<QualityReply> OpenAsync(CancellationToken ct)
    {
        var info = await client.Users_GetFullUser(user).WaitAsync(ct).ConfigureAwait(false);
        var commands = info.full_user.bot_info?.commands ?? [];
        var command = commands.FirstOrDefault(c => c.command is "settings" or "setting")?.command
            ?? commands.FirstOrDefault(c => c.command is "quality" or "setquality" or "set_quality")?.command
            ?? "settings";
        var sent = await client.SendMessageAsync(Peer, "/" + command).WaitAsync(ct).ConfigureAwait(false);
        return await WaitAsync(sent.id, null, ct).ConfigureAwait(false);
    }

    public async Task<QualityReply> PressAsync(QualityReply reply, QualityButton button, CancellationToken ct)
    {
        var latest = await client.Messages_GetHistory(Peer, limit: 1).WaitAsync(ct).ConfigureAwait(false);
        var after = latest.Messages.FirstOrDefault()?.ID ?? reply.Id;
        string? answer = null;
        if (button.Callback is { } data)
            answer = (await client.Messages_GetBotCallbackAnswer(Peer, reply.Id, data).WaitAsync(ct).ConfigureAwait(false))?.message;
        else after = (await client.SendMessageAsync(Peer, button.Text).WaitAsync(ct).ConfigureAwait(false)).id;
        if (BotLimits.Read(provider, answer ?? "", DateTimeOffset.UtcNow) is { } limited) throw limited;
        // A callback can acknowledge the change in an alert rather than editing its message.
        var ack = new QualityReply(reply.Id, answer ?? "", []);
        if (BotQuality.Confirmed(ack, AudioQuality.FLAC) || BotQuality.Confirmed(ack, AudioQuality.MP3)) return ack;
        return await WaitAsync(after, reply, ct).ConfigureAwait(false);
    }

    private async Task<QualityReply> WaitAsync(int after, QualityReply? previous, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            // Callbacks often EDIT the previous message. Keep it in the read, but require a changed body/keyboard.
            var history = await client.Messages_GetHistory(Peer, limit: 20).WaitAsync(ct).ConfigureAwait(false);
            foreach (var message in history.Messages.OfType<Message>().Where(m => !m.flags.HasFlag(Message.Flags.out_) && (m.id > after || m.id == previous?.Id)).OrderByDescending(m => m.id))
            {
                if (BotLimits.Read(provider, message.message ?? "", DateTimeOffset.UtcNow) is { } limited) throw limited;
                var reply = Read(message);
                if (previous is not null && reply.Id == previous.Id && Fingerprint(reply) == Fingerprint(previous)) continue;
                if (reply.Buttons.Count > 0 || BotQuality.Confirmed(reply, AudioQuality.FLAC) || BotQuality.Confirmed(reply, AudioQuality.MP3)) return reply;
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"BOT_QUALITY_TIMEOUT: {provider} did not answer the audio-quality setting within 20 seconds. Downloads are kept; check its chat or retry.");
    }

    private static string Fingerprint(QualityReply reply) => reply.Text + "\n" + string.Join("|", reply.Buttons.Select(b => b.Text + ":" + (b.Callback is null ? "text" : Convert.ToHexString(b.Callback))));

    internal static QualityReply Read(Message message)
    {
        IEnumerable<KeyboardInlineButton> inline = message.reply_markup is ReplyInlineMarkup markup
            ? markup.rows.SelectMany(r => r.buttons) : [];
        var buttons = inline.Where(b => b.type is InlineButtonTypeCallback)
            .Select(b => new QualityButton(b.text, ((InlineButtonTypeCallback)b.type).data)).ToList();
        if (message.reply_markup is ReplyKeyboardMarkup keyboard)
            buttons.AddRange(keyboard.rows.SelectMany(r => r.buttons).Select(b => new QualityButton(b.text)));
        return new(message.id, message.message ?? "", buttons);
    }
}
