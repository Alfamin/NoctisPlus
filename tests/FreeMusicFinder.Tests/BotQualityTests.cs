using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>Synthetic protocol replies, not a claim of a live bot-account test.</summary>
internal static class BotQualityTests
{
    private sealed class HungChat : IQualityConversation
    {
        public Task<QualityReply> OpenAsync(CancellationToken ct) => new TaskCompletionSource<QualityReply>().Task;
        public Task<QualityReply> PressAsync(QualityReply reply, QualityButton button, CancellationToken ct) => new TaskCompletionSource<QualityReply>().Task;
    }
    private sealed class Chat(params QualityReply[] replies) : IQualityConversation
    {
        private int _next;
        public List<string> Pressed = [];
        public Task<QualityReply> OpenAsync(CancellationToken ct) => Task.FromResult(replies[_next++]);
        public Task<QualityReply> PressAsync(QualityReply reply, QualityButton button, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Pressed.Add(button.Text); return Task.FromResult(replies[_next++]); }
    }
    private sealed class Source(string id) : IMusicSource, IQualityMusicSource
    {
        public string Id => id;
        public AudioQuality? Set;
        public bool Fetched;
        public Func<AudioQuality, Task>? Configure;
        public string Extension = "mp3";
        public Queue<string>? Extensions;          // one format per request, then Extension
        public AudioQuality? Requested;
        public AudioQuality? Confirmed;            // as kept with the login
        public bool Changes;                       // what the bot's menu answers: had to change the setting
        public int Asked, Forgotten, Fetches;
        public async Task<bool> SetQualityAsync(AudioQuality quality, CancellationToken ct)
        { Asked++; Set = quality; if (Configure is not null) await Configure(quality); Confirmed = quality; return Changes; }
        public bool QualityConfirmed(AudioQuality quality) => Confirmed == quality;
        public void ForgetQuality() { Forgotten++; Confirmed = null; }
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MusicRequest>>([]);
        public async Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
        {
            Fetched = true; Fetches++; Requested = request.Quality; await output.WriteAsync("ID3test MP3"u8.ToArray(), ct);
            return Extensions is { Count: > 0 } next ? next.Dequeue() : Extension;
        }
    }

    /// <summary>The owner's description of the two bots' real menus (2026-10-18), replayed.</summary>
    private static async Task RealMenusAsync()
    {
        Check.Section("bot settings menus: navigate like a person, read the tick, press only when needed");
        var deezLoad = new Chat(new QualityReply(1, "Settings", [new("Audio quality", [1]), new("Naming format", [2]), new("Zip", [3])]),
            new(1, "Audio quality", [new("✅ MP3 320", [4]), new("FLAC", [5]), new("« Back", [6])]),
            new(1, "Audio quality", [new("MP3 320", [4]), new("✅ FLAC", [5]), new("« Back", [6])]));
        Check.True(await BotQuality.ApplyAsync("DeezLoad", deezLoad, AudioQuality.FLAC, default), "DeezLoad: a changed setting is reported");
        Check.Equal("Audio quality,FLAC", string.Join(",", deezLoad.Pressed), "DeezLoad: Audio quality, not Naming format (both are not \"quality\"), then FLAC");

        var already = new Chat(new QualityReply(1, "Settings", [new("Audio quality", [1]), new("Naming format", [2]), new("Zip", [3])]),
            new(1, "Audio quality", [new("MP3 320", [4]), new("✅ FLAC", [5])]));
        Check.False(await BotQuality.ApplyAsync("DeezLoad", already, AudioQuality.FLAC, default), "an already-ticked FLAC is not changed");
        Check.Equal("Audio quality", string.Join(",", already.Pressed), "it only opens the menu to read the tick");

        var hunters = new Chat(new QualityReply(1, "Open settings to configure quality, downloads, captions, language and default search behaviour.", [new("Audio quality", [1]), new("Info", [2])]),
            new(1, "Per-service quality", [new("Deezer", [3]), new("Tidal", [4]), new("Qobuz", [5]), new("« Back", [6])]),
            new(1, "Deezer quality", [new("FLAC", [7]), new("✔️ MP3 320", [8])]),
            new(1, "Deezer quality", [new("✔️ FLAC", [7]), new("MP3 320", [8])]));
        Check.True(await BotQuality.ApplyAsync("Music Hunters", hunters, AudioQuality.FLAC, default), "Music Hunters: set through its per-service menu");
        Check.Equal("Audio quality,Deezer,FLAC", string.Join(",", hunters.Pressed), "Music Hunters: Audio quality → Deezer → FLAC, never Info");

        var webOnly = new Chat(new QualityReply(1, "Open settings", [new("Open settings", Web: true)]));
        var web = await Check.CatchAsync<BotAnswerException>(() => BotQuality.ApplyAsync("Music Hunters", webOnly, AudioQuality.FLAC, default), "a web-page-only settings menu is reported, not guessed");
        Check.True(web?.Message.Contains("BOT_QUALITY_WEB_PAGE") == true, "it says the settings open as a web page");

        var unknown = new Chat(new QualityReply(1, "Settings", [new("Naming format", [1]), new("Zip", [2])]));
        var unclear = await Check.CatchAsync<BotAnswerException>(() => BotQuality.ApplyAsync("DeezLoad", unknown, AudioQuality.FLAC, default), "a menu without a quality entry presses nothing");
        Check.True(unclear?.Message.Contains("\"Naming format\" | \"Zip\"") == true, "the menu it saw is in the message, for the log");
        Check.Equal(0, unknown.Pressed.Count, "Naming format is never pressed");

        Check.Section("bot settings are sent once, not before every song");
        var first = new Source("Music Hunters") { Confirmed = AudioQuality.FLAC, Extension = "flac" };
        var backup = new Source("DeezLoad") { Confirmed = AudioQuality.FLAC, Extension = "flac" };
        var sources = new MusicSources(first, backup, _ => { }, quality: () => AudioQuality.FLAC);
        using (var output = new MemoryStream())
            await sources.FetchAsync(new("Song", "Artist"), output, null, default);
        Check.Equal(0, first.Asked + backup.Asked, "both bots already confirmed FLAC: no settings message at all");
        first.Confirmed = backup.Confirmed = null;
        using (var output = new MemoryStream())
            await sources.FetchAsync(new("Song 2", "Artist"), output, null, default);
        using (var output = new MemoryStream())
            await sources.FetchAsync(new("Song 3", "Artist"), output, null, default);
        Check.Equal(2, first.Asked + backup.Asked, "not confirmed yet: each bot is asked once, then never again");
        await sources.SynchronizeQualityAsync(default);
        Check.True(first.Forgotten > 0 && first.Asked == 2 && backup.Asked == 2, "changing the preference in the app checks each bot once more");

        Check.Section("a format changed inside the bot itself is noticed");
        var moved = new Source("Music Hunters") { Confirmed = AudioQuality.FLAC, Changes = true, Extensions = new(["mp3", "flac"]) };
        var other = new Source("DeezLoad") { Confirmed = AudioQuality.FLAC, Extension = "flac" };
        var notices = new List<string>();
        using (var output = new MemoryStream())
        {
            var format = await new MusicSources(moved, other, notices.Add, quality: () => AudioQuality.FLAC).FetchAsync(new("Song", "Artist"), output, null, default);
            Check.Equal("flac", format, "the bot is set back to FLAC and the song asked again");
            Check.True(moved.Forgotten == 1 && moved.Asked == 1 && moved.Fetches == 2 && !other.Fetched, "one check of the bot's menu, one more request, the backup is not needed");
            Check.True(notices.Any(n => n.Contains("switched away from FLAC")), "the change is told in the log");
        }
        var leak = new Source("Music Hunters") { Confirmed = AudioQuality.FLAC, Changes = false, Extension = "mp3" };
        var leakBackup = new Source("DeezLoad") { Confirmed = AudioQuality.FLAC, Extension = "flac" };
        var clock = DateTimeOffset.Parse("2026-10-18T08:00:00Z");
        var leakSources = new MusicSources(leak, leakBackup, _ => { }, now: () => clock, quality: () => AudioQuality.FLAC);
        using (var output = new MemoryStream())
        {
            var format = await leakSources.FetchAsync(new("Song", "Artist"), output, null, default);
            Check.True(format == "flac" && leak.Fetches == 1 && leakBackup.Fetched, "setting unchanged (only an MP3 of this song): no repeat request, the backup supplies FLAC");
        }
        leak.Confirmed = AudioQuality.FLAC;
        for (var song = 2; song <= 4; song++)
            using (var output = new MemoryStream())
                await leakSources.FetchAsync(new("Song " + song, "Artist"), output, null, default);
        Check.Equal(1, leak.Asked, "an album of MP3-only songs opens the bot's settings once, not for every song");
        clock = clock.AddMinutes(11);
        using (var output = new MemoryStream())
            await leakSources.FetchAsync(new("Song 5", "Artist"), output, null, default);
        Check.Equal(2, leak.Asked, "ten minutes later the bot's menu may be checked again");

        Check.Section("the confirmed format is kept with the login");
        var folder = Path.Combine(Path.GetTempPath(), "bot-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var account = new TelegramAccount(Path.Combine(folder, "telegram.dat"), () => "");
            File.WriteAllText(Path.Combine(folder, "bot-quality.json"), "{\"|deezload2bot\":{\"Quality\":\"FLAC\",\"At\":\"2026-10-18T00:00:00+00:00\"}}");
            Check.True(account.QualityConfirmed("deezload2bot", AudioQuality.FLAC), "a confirmation from an earlier session is read back");
            Check.False(await account.SetQualityAsync("deezload2bot", "DeezLoad", AudioQuality.FLAC, default), "confirmed: returns without connecting or sending anything");
            Check.False(account.QualityConfirmed("deezload2bot", AudioQuality.MP3), "another format is not confirmed by it");
            account.ForgetQuality("deezload2bot");
            Check.False(account.QualityConfirmed("deezload2bot", AudioQuality.FLAC), "forgetting makes the next song check the bot");
            File.WriteAllText(Path.Combine(folder, "bot-quality.json"), "{\"|deezload2bot\":{\"Quality\":\"FLAC\",\"At\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\",\"Problem\":\"BOT_QUALITY_UNVERIFIED: test\"}}");
            await Check.ThrowsAsync<BotAnswerException>(() => account.SetQualityAsync("deezload2bot", "DeezLoad", AudioQuality.FLAC, default), "a menu that failed today is not retried for every song");
            File.WriteAllText(Path.Combine(folder, "bot-quality.json"), "{\"|deezload2bot\":{\"Quality\":\"FLAC\",\"At\":\"" + DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O") + "\",\"Problem\":\"BOT_QUALITY_TIMEOUT: test\"}}");
            var started = DateTime.UtcNow;
            await Check.ThrowsAsync<TimeoutException>(() => account.SetQualityAsync("deezload2bot", "DeezLoad", AudioQuality.FLAC, default), "a bot that did not answer minutes ago is not waited for again (it may be down)");
            Check.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "the song goes on at once instead of waiting out the timeout");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    public static async Task RunAsync()
    {
        await RealMenusAsync();
        Check.Section("preferred quality configures both bots, with verified settings replies");
        await Check.ThrowsAsync<TimeoutException>(() => BotQuality.ApplyAsync("offline provider", new HungChat(), AudioQuality.MP3, default, TimeSpan.FromMilliseconds(40)), "a non-answering settings request cannot stall the queue forever");
        foreach (var quality in new[] { AudioQuality.FLAC, AudioQuality.MP3 })
        {
            var opposite = quality == AudioQuality.FLAC ? "MP3 320" : "FLAC";
            var chat = new Chat(new QualityReply(1, "Settings", [new("Quality 🎧: " + opposite, [1])]),
                new(1, "Choose quality", [new("FLAC", [2]), new("MP3 128", [3]), new("MP3 320", [4])]),
                new(1, "Quality: " + quality, []));
            await BotQuality.ApplyAsync("provider", chat, quality, default);
            Check.Equal(2, chat.Pressed.Count, "opens quality then selects the user's preferred format");
            Check.Equal(quality == AudioQuality.MP3 ? "MP3 320" : "FLAC", chat.Pressed.Last(), "MP3 uses the explicit 320 choice and FLAC stays lossless");
        }
        var unchanged = new Chat(new QualityReply(1, "Choose quality", [new("✅ FLAC", [1]), new("MP3 320", [2])]));
        await BotQuality.ApplyAsync("provider", unchanged, AudioQuality.FLAC, default);
        Check.Equal(0, unchanged.Pressed.Count, "already-confirmed preference is not toggled off");
        Check.False(BotQuality.Confirmed(new(1, "Choose quality: FLAC / MP3", [new("FLAC"), new("MP3")]), AudioQuality.FLAC), "offered option alone is not confirmation");
        Check.False(BotQuality.Confirmed(new(1, "Quality: FLAC", [new("✅ FLAC"), new("✅ MP3")]), AudioQuality.FLAC), "conflicting selected states are not confirmed");
        var ambiguous = new Chat(new QualityReply(1, "Choose quality", [new("FLAC", [1]), new("FLAC", [2])]));
        await Check.ThrowsAsync<BotAnswerException>(() => BotQuality.ApplyAsync("provider", ambiguous, AudioQuality.FLAC, default), "duplicate format choices fail without an arbitrary click");
        Check.Equal(0, ambiguous.Pressed.Count, "ambiguous callbacks are not sent");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            var cancelled = new Chat(new QualityReply(1, "Choose quality", [new("FLAC", [1])]));
            await Check.ThrowsAsync<OperationCanceledException>(() => BotQuality.ApplyAsync("provider", cancelled, AudioQuality.FLAC, stop.Token), "cancelled quality change sends no selection");
            Check.Equal(0, cancelled.Pressed.Count, "cancellation leaves the bot's format alone");
        }
        var parsed = TelegramQualityConversation.Read(new Message { id = 3, message = "Settings", reply_markup = new ReplyInlineMarkup
        { rows = [new KeyboardInlineButtonRow { buttons = [new KeyboardInlineButton { text = "FLAC", type = new InlineButtonTypeCallback { data = [7, 8] } }] }] } });
        Check.True(parsed.Buttons.Single().Callback!.SequenceEqual(new byte[] { 7, 8 }), "uses the provider's actual callback bytes rather than another bot's protocol");
        var first = new Source("Music Hunters"); var backup = new Source("DeezLoad");
        first.Configure = q => { Check.False(first.Fetched || backup.Fetched, "quality is set before requesting a recording"); return Task.CompletedTask; };
        backup.Configure = first.Configure;
        using (var output = new MemoryStream())
        {
            await new MusicSources(first, backup, _ => { }, allowMp3: () => true, preferFlac: () => false, quality: () => AudioQuality.MP3)
                .FetchAsync(new("Song", "Artist"), output, null, default);
            Check.True(first.Set == AudioQuality.MP3 && backup.Set == AudioQuality.MP3, "both bots receive the preferred format, including the unused backup");
            Check.True(first.Fetched && !backup.Fetched, "MP3 preference saves the primary MP3 immediately");
        }
        first = new("Music Hunters") { Configure = _ => throw new BotAnswerException("BOT_QUALITY_UNVERIFIED") }; backup = new("DeezLoad");
        using (var output = new MemoryStream())
        {
            await new MusicSources(first, backup, _ => { }, allowMp3: () => true, preferFlac: () => false, quality: () => AudioQuality.MP3)
                .FetchAsync(new("Song", "Artist"), output, null, default);
            Check.True(!first.Fetched && backup.Fetched, "unverified provider is skipped; confirmed backup still works");
        }
        first = new("Music Hunters") { Extension = "flac" }; backup = new("DeezLoad");
        using (var output = new MemoryStream())
        {
            var format = await new MusicSources(first, backup, _ => { }, quality: () => AudioQuality.MP3)
                .FetchAsync(new("Song", "Artist"), output, null, default);
            Check.Equal("mp3", format, "wrong-format FLAC is not relabelled as the requested MP3");
            Check.True(first.Fetched && backup.Fetched, "backup supplies MP3 when the confirmed primary sends FLAC");
        }
        first = new("Music Hunters") { Configure = _ => throw new BotAnswerException("BOT_QUALITY_UNVERIFIED") };
        backup = new("DeezLoad") { Configure = first.Configure };
        using (var output = new MemoryStream())
        {
            var format = await new MusicSources(first, backup, _ => { }, quality: () => AudioQuality.MP3)
                .FetchAsync(new("Song", "Artist"), output, null, default);
            Check.Equal("mp3", format, "changed settings menus on BOTH bots do not block valid default MP3 audio");
        }
        first = new("Music Hunters") { Configure = _ => throw new BotAnswerException("BOT_QUALITY_UNVERIFIED") };
        backup = new("DeezLoad") { Configure = first.Configure };
        using (var output = new MemoryStream())
            await Check.ThrowsAsync<TrackUnavailableException>(() => new MusicSources(first, backup, _ => { }, quality: () => AudioQuality.FLAC)
                .FetchAsync(new("Song", "Artist"), output, null, default), "unconfirmed settings never bypass FLAC-only preference");
        var preference = AudioQuality.MP3;
        first = new("Music Hunters") { Configure = _ => { preference = AudioQuality.FLAC; return Task.CompletedTask; } };
        backup = new("DeezLoad");
        using (var output = new MemoryStream())
        {
            await new MusicSources(first, backup, _ => { }, quality: () => preference)
                .FetchAsync(new("Song", "Artist"), output, null, default);
            Check.True(first.Set == AudioQuality.MP3 && backup.Set == AudioQuality.MP3 && first.Requested == AudioQuality.MP3,
                "one song uses one captured preference even when the user changes it mid-operation");
        }
        first = new("Music Hunters") { Configure = _ => throw new SourceLimitException("Music Hunters", "daily limit reached", DateTimeOffset.UtcNow.AddHours(1)) };
        backup = new("DeezLoad"); var notices = new List<string>();
        var sources = new MusicSources(first, backup, notices.Add, quality: () => AudioQuality.MP3);
        await Check.ThrowsAsync<BotAnswerException>(() => sources.SynchronizeQualityAsync(default), "a cooling bot cannot confirm quality");
        await Check.ThrowsAsync<BotAnswerException>(() => sources.SynchronizeQualityAsync(default), "rechecking a cooling bot still does not claim confirmation");
        Check.False(notices.Any(n => n.Contains("Both bots confirmed")), "quality status is truthful while either provider is waiting");
        var mp3 = new Document { mime_type = "audio/mpeg" }; var flac = new Document { mime_type = "audio/flac" };
        foreach (var quality in new[] { AudioQuality.MP3, AudioQuality.FLAC })
        {
            var request = new MusicRequest("Song", "Artist", Quality: quality);
            Check.True(TelegramAudioCache.AcceptsQuality(request, quality == AudioQuality.MP3 ? mp3 : flac), "matching cached format can be reused");
            Check.False(TelegramAudioCache.AcceptsQuality(request, quality == AudioQuality.MP3 ? flac : mp3), "old opposite-format cache cannot bypass a fresh request in the preferred format");
        }
    }
}
