using System.Collections.Concurrent;
using System.Diagnostics;

namespace FreeMusicFinder;

/// <summary>
/// Owner 2026-10-20: a bot that has just sent a song sometimes ignores the next link when it comes at once (pasted again by
/// hand it works), and the song was then taken for one the bot lacks. The next message to a bot waits until a moment after
/// its last file.
/// </summary>
internal static class BotPacing
{
    internal static TimeSpan Gap { get; set; } = TimeSpan.FromSeconds(1);
    private static readonly ConcurrentDictionary<string, long> Sent = new(StringComparer.Ordinal);

    /// <summary>The bot just sent a file.</summary>
    public static void Answered(string bot) => Sent[bot] = Stopwatch.GetTimestamp();

    /// <summary>Waits, if needed, so this message reaches the bot at least <see cref="Gap"/> after its last file.</summary>
    public static async Task TurnAsync(string bot, CancellationToken ct)
    {
        if (!Sent.TryGetValue(bot, out var at)) return;
        var wait = Gap - Stopwatch.GetElapsedTime(at);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
    }
}
