using System.Text;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public sealed class PlusOnlineLyricsSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task WordSourcesAreAskedInOrder_AndLineSyncDoesNotWin()
    {
        var order = new List<string>();
        var providers = new[]
        {
            MakeProvider("lrc.red", order, WordResult("line one\nline two\nline three")),
            MakeProvider("Unison", order, WordResult("line one\nline two\nline three")),
            MakeProvider("Kugou", order, WordResult(WordLrc)),
        };
        var source = MakeSource(providers);

        var result = await source.FindAsync(Track(), CancellationToken.None);

        Assert.Equal(new[] { "lrc.red", "Unison", "Kugou" }, order);
        Assert.Equal("Kugou", result?.Provider);
        Assert.False(result!.AudioFitVerified); // metadata match is not audio-fit proof
        Assert.Equal(35, LyricsLoader.FromOnlineText(result.Text)!.Lines.Sum(l => l.Words?.Count ?? 0));
    }

    /// <summary>Owner #290: LRCLIB is the last resort, as on the PC. With no word timing anywhere, a provider's line
    /// timing is used before LRCLIB is asked; LRCLIB answers only when no provider had anything.</summary>
    [Fact]
    public async Task LineTimingFromAProvider_ComesBeforeLrclib_WhichIsAskedLast()
    {
        const string LineLrc = "[00:01.00]line one\n[00:03.00]line two\n[00:05.00]line three";
        var order = new List<string>();
        var fallback = new FakeFallback { Answer = (_, _) => Task.FromResult<OnlineLyrics?>(new OnlineLyrics("[00:01.00]from lrclib", false)) };
        var source = MakeSource(new[]
        {
            MakeProvider("lrc.red", order, null),
            MakeProvider("Unison", order, WordResult(LineLrc)),
            MakeProvider("Kugou", order, null),
        }, fallback);

        var result = await source.FindAsync(Track(), CancellationToken.None);

        Assert.Equal(new[] { "lrc.red", "Unison", "Kugou" }, order);   // word timing was looked for everywhere first
        Assert.Equal("Unison", result?.Provider);
        Assert.Equal(LineLrc, result!.Text);
        Assert.Equal(0, fallback.Calls);

        var none = MakeSource(new[] { MakeProvider("lrc.red", new List<string>(), null) }, fallback);
        var last = await none.FindAsync(new Track { Id = Guid.NewGuid(), Title = "Other", Artist = "Test artist", Duration = TimeSpan.FromSeconds(60) }, CancellationToken.None);
        Assert.Equal("LRCLIB", last?.Provider);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task ConvertedWordLyricsWinOverRawTtmlProvenance()
    {
        var withTtml = WordResult(WordLrc);
        withTtml.OriginalTtml = "<tt><body><div><p begin=\"1s\">raw provider TTML</p></div></body></tt>";
        var provider = new PlusOnlineLyricsSource.Provider("lrc.red", (_, _) => Task.FromResult<LrcLibResult?>(withTtml));

        var result = await MakeSource(new[] { provider }).FindAsync(Track(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(WordLrc, result.Text);
        Assert.True(LyricsLoader.FromOnlineText(result.Text)!.Lines.Any(line => line.Words?.Count >= 2));
    }

    [Fact]
    public async Task FailureFallsThroughButAllFailuresAreNotReportedAsDefinitiveMiss()
    {
        var order = new List<string>();
        var providers = new[]
        {
            new PlusOnlineLyricsSource.Provider("lrc.red", (_, _) => throw new IOException("network down")),
            MakeProvider("Unison", order, null),
            new PlusOnlineLyricsSource.Provider("Kugou", (_, _) => throw new TimeoutException()),
        };
        var fallback = new FakeFallback { Answer = (_, _) => Task.FromResult<OnlineLyrics?>(null) };

        var result = await MakeSource(providers, fallback).FindAsync(Track(), CancellationToken.None);

        Assert.True(result?.Failed);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task WordTimingCacheIsReusableAfterRestart_ButBoundToTrackMetadata()
    {
        var order = new List<string>();
        var calls = 0;
        var providers = new[]
        {
            new PlusOnlineLyricsSource.Provider("lrc.red", (_, _) =>
            {
                calls++;
                return Task.FromResult<LrcLibResult?>(WordResult(WordLrc));
            }),
        };
        var first = MakeSource(providers);
        var track = Track();
        Assert.Equal("lrc.red", (await first.FindAsync(track, CancellationToken.None))?.Provider);

        var restarted = MakeSource(new[] { MakeProvider("lrc.red", order, null) });
        var cached = await restarted.FindAsync(track, CancellationToken.None);
        Assert.Equal("lrc.red", cached?.Provider);
        Assert.Empty(order);

        track.Title = "Different recording";
        var changed = await restarted.FindAsync(track, CancellationToken.None);
        Assert.Null(changed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProviderCancellationIsNotTurnedIntoFailureOrFallback()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fallback = new FakeFallback();
        var source = MakeSource(new[]
        {
            new PlusOnlineLyricsSource.Provider("lrc.red", async (_, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            }),
        }, fallback);
        using var stop = new CancellationTokenSource();
        var request = source.FindAsync(Track(), stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, fallback.Calls);
    }

    private PlusOnlineLyricsSource MakeSource(IEnumerable<PlusOnlineLyricsSource.Provider> providers, FakeFallback? fallback = null) =>
        new(providers, fallback ?? new FakeFallback(), Path.Combine(_root, "cache"), new OnlineAccess());

    private static PlusOnlineLyricsSource.Provider MakeProvider(string name, List<string> order, LrcLibResult? answer) =>
        new(name, (_, _) =>
        {
            order.Add(name);
            return Task.FromResult(answer);
        });

    private static LrcLibResult WordResult(string? text) => new()
    {
        TrackName = "Test song", ArtistName = "Test artist", Duration = 60, SyncedLyrics = text,
    };

    private Track Track() => new()
    {
        Id = Guid.Parse("92ced611-083d-4e8c-a1f3-74065c3ce114"), Title = "Test song", Artist = "Test artist",
        FilePath = "synthetic://track", Duration = TimeSpan.FromSeconds(60),
    };

    private sealed class FakeFallback : IOnlineLyricsSource
    {
        public int Calls { get; private set; }
        public Func<Track, CancellationToken, Task<OnlineLyrics?>> Answer { get; set; } = (_, _) => Task.FromResult<OnlineLyrics?>(null);
        public Task<OnlineLyrics?> FindAsync(Track track, CancellationToken ct)
        {
            Calls++;
            return Answer(track, ct);
        }
    }

    private static readonly string WordLrc = BuildWordLrc();
    private static string BuildWordLrc()
    {
        var text = new StringBuilder("[00:01.00]");
        for (var i = 0; i < 35; i++)
        {
            var stamp = TimeSpan.FromSeconds(1 + i * 0.5).ToString(@"mm\:ss\.ff", System.Globalization.CultureInfo.InvariantCulture);
            text.Append($"<{stamp}>word{i} ");
        }
        return text.ToString();
    }
}
