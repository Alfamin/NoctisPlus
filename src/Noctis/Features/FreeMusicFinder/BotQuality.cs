using System.Text.RegularExpressions;
using TL;
using WTelegram;

namespace FreeMusicFinder;

internal enum AudioQuality { FLAC, MP3 }

internal interface IQualityMusicSource
{
    /// <summary>Makes sure the bot sends this format. Nothing is sent when it was already confirmed for this account
    /// (owner, 2026-10-18: not "/settings" before every song). True when a setting had to be changed.</summary>
    Task<bool> SetQualityAsync(AudioQuality quality, CancellationToken ct);
    /// <summary>True when this format is already confirmed, so asking costs no message.</summary>
    bool QualityConfirmed(AudioQuality quality);
    /// <summary>The bot sent another format than confirmed (say its setting was changed in the bot itself):
    /// check it again next time.</summary>
    void ForgetQuality();
}

/// <param name="Web">Opens a link or a web page (a Telegram mini app): an app cannot press it like a person.</param>
internal sealed record QualityButton(string Text, byte[]? Callback = null, bool Web = false);
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
    private static bool QualityLabel(string value) => Regex.IsMatch(Words(value), @"\b(quality|calidad|qualité|qualita|qualidade|качество|کیفیت)\b");
    private static AudioQuality? Format(string value)
    {
        var words = Words(value);
        var flac = Regex.IsMatch(words, @"\b(flac|lossless)\b");
        var mp3 = Regex.IsMatch(words, @"\b(mp3|320|320kbps|320k)\b");
        return flac == mp3 ? null : flac ? AudioQuality.FLAC : AudioQuality.MP3;
    }
    // How bots mark the option in use: a tick, a filled dot, or words.
    private static bool Ticked(string text) =>
        text.IndexOfAny(['✅', '✓', '✔', '☑', '●', '◉']) >= 0 || text.Contains("🔘") || Regex.IsMatch(text, @"\[x\]|\((current|selected|active)\)|«|»", RegexOptions.IgnoreCase);
    // Menu entries that never lead to the audio quality (owner, 2026-10-18: DeezLoad shows "Audio quality",
    // "Naming format" and "Zip"; Music Hunters shows "Audio quality" and "Info").
    private static readonly Regex NotQuality = new(@"\b(naming|name|names|filename|zip|caption|captions|language|lang|info|about|help|back|close|cancel|search|lyrics|cover|covers|playlist|donate|support|referral|stats)\b");
    private static readonly Regex Service = new(@"\b(deezer|tidal|qobuz|spotify|apple|youtube|soundcloud|amazon|napster|beatport)\b");

    internal static bool Confirmed(QualityReply reply, AudioQuality wanted)
    {
        // An unselected FLAC option in a list of choices is not an acknowledgement.
        var selected = reply.Buttons.Where(b => Ticked(b.Text))
            .Select(b => Format(b.Text)).Where(q => q is not null).Distinct().ToList();
        if (selected.Count > 0) return selected.Count == 1 && selected[0] == wanted;
        foreach (var line in reply.Text.Split('\n'))
            if ((QualityLabel(line) || Regex.IsMatch(Words(line), @"\bformat\b")) && Format(line) == wanted &&
                (line.Contains(':') || Regex.IsMatch(Words(line), @"\b(set|selected|changed|current|установлено|выбрано|انتخاب)\b"))) return true;
        return false;
    }

    /// <returns>True when an option had to be changed; false when the bot already used this format.</returns>
    public static async Task<bool> ApplyAsync(string provider, IQualityConversation chat, AudioQuality wanted, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        try { return await ApplyCoreAsync(provider, chat, wanted, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException($"BOT_QUALITY_TIMEOUT: {provider} did not finish confirming {wanted}. The supplied audio format will be checked; no quality change is assumed."); }
    }

    /// <summary>
    /// Walks the bot's own menus the way a person would: settings → audio quality → (a per-service list: Deezer) →
    /// the format, reading which option is ticked before pressing anything. Only buttons the bot actually showed
    /// are pressed, and only when exactly one fits.
    /// </summary>
    private static async Task<bool> ApplyCoreAsync(string provider, IQualityConversation chat, AudioQuality wanted, CancellationToken ct)
    {
        var reply = await chat.OpenAsync(ct).WaitAsync(ct).ConfigureAwait(false);
        var pressed = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        for (var step = 0; step < 6; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (Confirmed(reply, wanted)) return changed;
            var pressable = reply.Buttons.Where(b => !b.Web).ToList();
            var choices = pressable.Where(b => Format(b.Text) == wanted).ToList();
            // For MP3 prefer an explicit 320 choice. Never select the first arbitrary bitrate.
            if (wanted == AudioQuality.MP3 && choices.Any(b => Words(b.Text).Contains("320")))
                choices = choices.Where(b => Words(b.Text).Contains("320")).ToList();
            // A per-service list that shows each service's format ("Deezer: FLAC", "Tidal: FLAC"): Deezer's.
            if (choices.Count > 1 && choices.Count(b => Words(b.Text).Contains("deezer")) == 1)
                choices = choices.Where(b => Words(b.Text).Contains("deezer")).ToList();
            if (choices.Count > 1) throw Unverified(provider, wanted, reply);
            var next = choices.Count == 1 ? choices[0] : Navigation(pressable, pressed);
            if (next is null)
            {
                if (reply.Buttons.Any(b => b.Web) && pressable.Count == 0)
                    throw new BotAnswerException($"BOT_QUALITY_WEB_PAGE: {provider}'s settings open as a web page, which Noctis cannot press. Set it to {wanted} in the bot yourself; every file's actual format is still checked. Menu: {Menu(reply)}");
                throw Unverified(provider, wanted, reply);
            }
            if (!pressed.Add(next.Text)) throw Unverified(provider, wanted, reply); // the same press again would loop
            if (choices.Count == 1) changed = true;
            reply = await chat.PressAsync(reply, next, ct).WaitAsync(ct).ConfigureAwait(false);
        }
        if (!Confirmed(reply, wanted)) throw Unverified(provider, wanted, reply);
        return changed;
    }

    /// <summary>The one menu entry that leads towards the audio quality, or null when none or several fit.</summary>
    private static QualityButton? Navigation(IReadOnlyList<QualityButton> buttons, IReadOnlySet<string> pressed)
    {
        var services = buttons.Count(b => Service.IsMatch(Words(b.Text)));
        int Score(QualityButton b)
        {
            var words = Words(b.Text);
            if (pressed.Contains(b.Text)) return 0;
            // Music Hunters (live, 2026-10-18) puts its format selector beside a Quality Info
            // caption toggle. Both contain "quality"; only the per-service entry changes audio.
            if (Regex.IsMatch(words, @"\bper service quality\b")) return 5;
            if (Regex.IsMatch(words, @"\baudio quality\b")) return 4;           // "Audio Quality & Info" opens the submenu
            if (NotQuality.IsMatch(words)) return 0;
            if (QualityLabel(b.Text)) return 4;                                    // "Audio quality", "Quality"
            if (services >= 2 && Regex.IsMatch(words, @"\bdeezer\b")) return 3;   // "per service quality": Deezer
            if (Regex.IsMatch(words, @"\b(format|bitrate|codec)\b")) return 2;
            if (Regex.IsMatch(words, @"\b(settings?|preferences|options|настройки|ajustes|تنظیمات)\b")) return 1;
            return 0;
        }
        var best = buttons.Select(b => (Button: b, Score: Score(b))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        return best.Count == 0 || best.Count > 1 && best[1].Score == best[0].Score ? null : best[0].Button;
    }

    private static string Menu(QualityReply reply)
        => reply.Buttons.Count == 0 ? "(no buttons)" : string.Join(" | ", reply.Buttons.Select(b => "\"" + b.Text + "\"" + (b.Web ? " (web page)" : "")));

    private static BotAnswerException Unverified(string provider, AudioQuality quality, QualityReply? reply = null)
        => new($"BOT_QUALITY_UNVERIFIED: {provider} did not confirm {quality}. Its settings may have changed or this quality may be unavailable. No arbitrary option was selected; the other provider will be tried." + (reply is null ? "" : " Menu: " + Menu(reply)));
}

/// <summary>Uses the app's one proxy-aware Telegram client. No new login/session and no guessed callback bytes.</summary>
internal sealed class TelegramQualityConversation(Client client, User user, string provider) : IQualityConversation
{
    private InputPeer Peer => user;
    private long _lastSent = long.MinValue;

    /// <summary>
    /// The shortest time between two things sent to the bot. Music Hunters (live, 2026-10-18) ignored "Audio Quality
    /// &amp; Info" all three times it was pressed in the same second as /settings, and took it when pressed 2 s later:
    /// a press that follows the command too fast is dropped as flooding. DeezLoad does not mind; settings are rare.
    /// </summary>
    internal static readonly TimeSpan MinimumStep = TimeSpan.FromSeconds(2);

    internal static TimeSpan PaceDelay(TimeSpan sinceLastSent)
        => sinceLastSent >= MinimumStep ? TimeSpan.Zero : MinimumStep - (sinceLastSent < TimeSpan.Zero ? TimeSpan.Zero : sinceLastSent);

    // One press, sent at a person's pace: waits, never repeats.
    private async Task PaceAsync(CancellationToken ct)
    {
        if (_lastSent != long.MinValue)
        {
            var wait = PaceDelay(System.Diagnostics.Stopwatch.GetElapsedTime(_lastSent));
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        }
        _lastSent = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private void Trace(string stage, QualityReply? reply = null)
    {
        if (!Noctis.Services.DebugLogger.IsEnabled) return;
        // Local troubleshooting only: menu labels/types, never callback bytes, credentials or account details.
        var menu = reply is null ? "" : " | " + string.Join(" / ", reply.Buttons.Take(12)
            .Select(b => b.Text.ReplaceLineEndings(" ") + (b.Web ? " [web]" : b.Callback is null ? " [text]" : " [callback]")));
        Noctis.Services.DebugLog.Write("Finder.Quality", provider + ": " + stage + menu);
    }

    public async Task<QualityReply> OpenAsync(CancellationToken ct)
    {
        var info = await client.Users_GetFullUser(user).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        var commands = info.full_user.bot_info?.commands ?? [];
        var command = commands.FirstOrDefault(c => c.command is "settings" or "setting")?.command
            ?? commands.FirstOrDefault(c => c.command is "quality" or "setquality" or "set_quality")?.command
            ?? "settings";
        await PaceAsync(ct).ConfigureAwait(false);
        var sent =await client.SendMessageAsync(Peer, "/" + command).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        Trace("sent /" + command);
        return await WaitAsync(sent.id, null, ct).ConfigureAwait(false);
    }

    public async Task<QualityReply> PressAsync(QualityReply reply, QualityButton button, CancellationToken ct)
    {
        var latest = await client.Messages_GetHistory(Peer, limit: 1).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        var after = latest.Messages.FirstOrDefault()?.ID ?? reply.Id;
        await PaceAsync(ct).ConfigureAwait(false);
        Trace("press " + button.Text, reply);
        if (button.Callback is { } data)
        {
            // A menu edit is the reply too. Some bots edit it immediately but never acknowledge the callback
            // (Telegram then waits ~15 s). Four serial acknowledgement waits exceed the entire settings deadline.
            var callback = ReadCallbackAsync();
            return await AfterPressAsync(provider, reply.Id, callback, token => WaitAsync(after, reply, token, button.Text), ct).ConfigureAwait(false);
            async Task<string?> ReadCallbackAsync()
                => (await client.Messages_GetBotCallbackAnswer(Peer, reply.Id, data)
                    .WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false))?.message;
        }
        else after = (await client.SendMessageAsync(Peer, button.Text).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false)).id;
        return await WaitAsync(after, reply, ct, button.Text).ConfigureAwait(false);
    }

    internal static async Task<QualityReply> AfterPressAsync(string provider, int id, Task<string?> callback,
        Func<CancellationToken, Task<QualityReply>> readMenu, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var menu = readMenu(stop.Token);
        try
        {
            var first = await Task.WhenAny(callback, menu).WaitAsync(ct).ConfigureAwait(false);
            if (first == menu) return await menu.ConfigureAwait(false);
            string? answer = null;
            try { answer = await callback.ConfigureAwait(false); }
            catch (RpcException ex) when (ex.Message.Contains("BOT_RESPONSE_TIMEOUT", StringComparison.Ordinal)) { }
            if (BotLimits.Read(provider, answer ?? "", DateTimeOffset.UtcNow) is { } limited) throw limited;
            var ack = new QualityReply(id, answer ?? "", []);
            if (BotQuality.Confirmed(ack, AudioQuality.FLAC) || BotQuality.Confirmed(ack, AudioQuality.MP3)) return ack;
            return await menu.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            // Neither task sends another press. A late acknowledgement cannot change our selected menu/quality,
            // but its exception still needs observing if the menu won or the user cancelled.
            _ = ObserveAsync(callback);
            _ = ObserveAsync(menu);
        }
        static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { /* owned, bounded RPC/read; result no longer needed */ } }
    }

    private async Task<QualityReply> WaitAsync(int after, QualityReply? previous, CancellationToken ct, string? pressed = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            // Callbacks often EDIT the previous message. Keep it in the read, but require a changed body/keyboard.
            var history = await client.Messages_GetHistory(Peer, limit: 20).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            // A song arriving for a transfer under way (they no longer wait for menu changes) is never a menu.
            foreach (var message in history.Messages.OfType<Message>().Where(m => !m.flags.HasFlag(Message.Flags.out_) && m.media is not MessageMediaDocument && (m.id > after || m.id == previous?.Id)).OrderByDescending(m => m.id))
            {
                if (BotLimits.Read(provider, message.message ?? "", DateTimeOffset.UtcNow) is { } limited) throw limited;
                var reply = Read(message);
                if (previous is not null && reply.Id == previous.Id && Fingerprint(reply) == Fingerprint(previous)) continue;
                if (reply.Buttons.Count > 0 || BotQuality.Confirmed(reply, AudioQuality.FLAC) || BotQuality.Confirmed(reply, AudioQuality.MP3))
                { Trace("read menu", reply); return reply; }
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        Trace("menu wait timed out" + (pressed is null ? "" : " after " + pressed), previous);
        var step = pressed is null ? "opening settings" : "pressing “" + pressed + "”";
        throw new TimeoutException($"BOT_QUALITY_TIMEOUT: {provider} did not update its menu within 20 seconds after {step}. Your queue is kept and the other bot can continue. If this repeats, set audio quality in the bot's chat, then retry.");
    }

    private static string Fingerprint(QualityReply reply) => reply.Text + "\n" + string.Join("|", reply.Buttons.Select(b => b.Text + ":" + (b.Callback is null ? "text" : Convert.ToHexString(b.Callback))));

    internal static QualityReply Read(Message message)
    {
        IEnumerable<KeyboardInlineButton> inline = message.reply_markup is ReplyInlineMarkup markup
            ? markup.rows.SelectMany(r => r.buttons) : [];
        // Link and web-page (mini app) buttons are kept so a menu that only offers those can be reported as such.
        var buttons = inline.Select(b => b.type is InlineButtonTypeCallback callback
            ? new QualityButton(b.text, callback.data) : new QualityButton(b.text, Web: true)).ToList();
        if (message.reply_markup is ReplyKeyboardMarkup keyboard)
            buttons.AddRange(keyboard.rows.SelectMany(r => r.buttons).Select(b => new QualityButton(b.text)));
        return new(message.id, message.message ?? "", buttons);
    }
}
