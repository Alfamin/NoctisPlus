using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>Synthetic protocol replies, not a claim of a live bot-account test.</summary>
internal static class BotQualityTests
{
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
        public AudioQuality? Requested;
        public Task SetQualityAsync(AudioQuality quality, CancellationToken ct) { Set = quality; return Configure?.Invoke(quality) ?? Task.CompletedTask; }
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MusicRequest>>([]);
        public async Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
        { Fetched = true; Requested = request.Quality; await output.WriteAsync("ID3test MP3"u8.ToArray(), ct); return Extension; }
    }

    public static async Task RunAsync()
    {
        Check.Section("preferred quality configures both bots, with verified settings replies");
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
