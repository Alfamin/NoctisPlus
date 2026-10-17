using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>Owner, 2026-10-17: Music Hunters should hand over to DeezLoad soon after its usual
/// answer time, not after a fixed 20 seconds.</summary>
internal static class BotTimingTests
{
    internal static void Run()
    {
        Check.Section("bot timings: waits follow the bot's usual answer time");
        var folder = Check.NewFolder("bot-timings");
        var path = Path.Combine(folder, "bot-timings.json");
        var timings = new BotTimings(path);
        var fallback = TimeSpan.FromSeconds(20);
        Check.Equal(fallback, timings.Wait("s", fallback, TimeSpan.FromSeconds(8), fallback), "until enough answers are known the old wait is kept");
        foreach (var seconds in new[] { 1.5, 2, 2, 2.5, 3 }) timings.Record("s", TimeSpan.FromSeconds(seconds));
        Check.Equal(TimeSpan.FromSeconds(8), timings.Wait("s", fallback, TimeSpan.FromSeconds(8), fallback), "a ~2 s bot gets 8 s (the minimum), not 20");
        foreach (var seconds in new[] { 9.0, 10, 11, 12, 12 }) timings.Record("s", TimeSpan.FromSeconds(seconds));
        var slow = timings.Wait("s", fallback, TimeSpan.FromSeconds(8), fallback);
        Check.True(slow > TimeSpan.FromSeconds(8) && slow <= fallback, "a slower bot gets a longer wait, never above the old limit");
        Check.Equal(10, new BotTimings(path).Count("s"), "answer times survive a restart");

        var asked = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
        var history = new[]
        {
            Out(1, asked, "/search drake"), Answer(2, asked.AddSeconds(2)),
            Out(3, asked.AddMinutes(1), "/search sza"), Answer(4, asked.AddMinutes(1)),               // same second
            Out(5, asked.AddMinutes(2), "/search nobody"), Out(6, asked.AddMinutes(2).AddSeconds(1), "/start"),
            Answer(7, asked.AddMinutes(2).AddSeconds(30)),                                              // another request came between
        };
        var times = TelegramBotSource.SearchAnswerTimes(history.Reverse());
        Check.Equal(2, times.Count, "history pairs each search with the bot's next result message only");
        Check.Equal(TimeSpan.FromSeconds(2), times[0], "answer time is the gap between the two messages");
        Check.Equal(TimeSpan.FromSeconds(0.5), times[1], "a same-second answer counts as half a second");
    }

    private static Message Out(int id, DateTime at, string text) => new() { id = id, date = at, message = text, flags = Message.Flags.out_ };

    private static Message Answer(int id, DateTime at) => new()
    {
        id = id, date = at, message = "1. Song - Artist",
        reply_markup = new ReplyInlineMarkup { rows = [new KeyboardInlineButtonRow { buttons = [new KeyboardInlineButton { text = "1", type = new InlineButtonTypeCallback { data = [1] } }] }] },
    };
}
