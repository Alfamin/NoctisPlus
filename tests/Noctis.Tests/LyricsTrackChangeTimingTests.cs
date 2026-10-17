using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Timeline of a track change on the lyrics page, on the real path: queue play →
/// TrackStarted → sidecar probe → LyricsSwapPending (fade-out) → LyricsSwapped
/// (apply + jump) → host fade-in. Prints when each stage lands relative to the
/// play call so the "lyrics take a while after the track starts" report can be
/// judged against numbers instead of a feeling.
/// </summary>
public class LyricsTrackChangeTimingTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-lyrics-timing-" + Guid.NewGuid().ToString("N"));

    public LyricsTrackChangeTimingTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private Track MakeTrack(string name, string elrc)
    {
        var audio = Path.Combine(_dir, name + ".flac");
        File.WriteAllText(audio, "x");
        File.WriteAllText(Path.Combine(_dir, name + ".elrc"), elrc);
        return new Track
        {
            Id = Guid.NewGuid(),
            Title = name,
            Artist = "Artist",
            FilePath = audio,
            SourceType = SourceType.Local,
            Duration = TimeSpan.FromMinutes(3),
        };
    }

    private static string Elrc(string tag) =>
        "[00:00.30]<00:00.30>" + tag + " <00:00.80>one <00:01.20>two<00:01.60>\n" +
        "[00:03.00]<00:03.00>" + tag + " <00:03.40>three <00:03.80>four<00:04.20>\n" +
        "[00:06.00]<00:06.00>" + tag + " <00:06.40>five <00:06.80>six<00:07.20>\n";

    private static void Tick()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Tick();
            await Task.Delay(8);
        }
    }

    [AvaloniaFact]
    public async Task CompletedWordTimingReloadsOnlyItsCurrentSong_AndPreservesStudioPreview()
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        using var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), persistence, new FakeLibraryService());
        var song = MakeTrack("repair", Elrc("before"));
        player.ReplaceQueueAndPlay([song], 0); await Pump(300);
        Assert.Contains("before", vm.LyricLines[0].Text);
        File.WriteAllText(Path.ChangeExtension(song.FilePath, ".elrc"), Elrc("after"));
        vm.ReloadWrittenLyrics([Path.Combine(_dir, "different.flac")]); await Pump(100);
        Assert.Contains("before", vm.LyricLines[0].Text);
        vm.ReloadWrittenLyrics([song.FilePath]); await Pump(300);
        Assert.Contains("after", vm.LyricLines[0].Text);
        vm.ShowPreview(song, Elrc("unsaved")); await Pump(300);
        vm.ReloadWrittenLyrics([song.FilePath]); await Pump(100);
        Assert.Contains("unsaved", vm.LyricLines[0].Text);
    }

    [AvaloniaFact]
    public async Task TrackChange_Timeline()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());

        var a = MakeTrack("A", Elrc("alpha"));
        var b = MakeTrack("B", Elrc("bravo"));

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 900, Content = view };
        var log = new List<string>();
        var t0 = 0L;
        string Stamp() => $"+{Environment.TickCount64 - t0,4}ms";
        vm.LyricsSwapPending += (_, _) => log.Add($"{Stamp()} LyricsSwapPending (fade-out starts)");
        vm.LyricsSwapped += (_, _) => log.Add($"{Stamp()} LyricsSwapped (apply + jump, fade-in starts) first line='{(vm.LyricLines.Count > 0 ? vm.LyricLines[0].Text : "")}'");
        // The VM logs "LocalProbe" the moment the disk probe returns; the fade-out
        // must already be running by then (it used to wait for the probe).
        var wasLoggerEnabled = DebugLogger.IsEnabled;
        DebugLogger.IsEnabled = true;
        void OnEntry(DebugLogger.LogEntry e)
        {
            if (e.Category == DebugLogger.Category.Lyrics && e.Action == "LocalProbe")
                log.Add($"{Stamp()} probe returned ({e.Metadata})");
        }
        DebugLogger.EntryAdded += OnEntry;

        try
        {
            win.Show();
            await Pump(150);
            var host = view.FindControl<Border>("LyricsContentHost");
            Assert.NotNull(host);

            // Warm: first track.
            t0 = Environment.TickCount64;
            player.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);
            await Pump(900);
            // A slow machine (the CI runner) can need longer for the first load.
            for (var waited = 0; waited < 5000 && !vm.LyricLines.Any(l => l.Text.Contains("alpha")); waited += 100) await Pump(100);
            _output.WriteLine("--- first track (cold) ---");
            foreach (var l in log) _output.WriteLine(l);
            log.Clear();
            Assert.Contains(vm.LyricLines, l => l.Text.Contains("alpha"));

            // The change under test: A → B while A's lyrics are on screen.
            t0 = Environment.TickCount64;
            log.Add($"{Stamp()} ReplaceQueueAndPlay(B) called; CurrentTrack={player.CurrentTrack?.Title}");
            player.ReplaceQueueAndPlay(new List<Track> { a, b }, 1);
            log.Add($"{Stamp()} returned; CurrentTrack={player.CurrentTrack?.Title}");

            double? lastOpacity = null;
            string? lastFirst = null;
            long? fadedOutAt = null, newLinesAt = null, fadedInAt = null;
            // Watch at least 1.2 s (the whole fade on a normal machine), and on a slow one keep
            // watching until the new lines have faded back in, up to 6 s.
            var minEnd = Environment.TickCount64 + 1200;
            var maxEnd = Environment.TickCount64 + 6000;
            while (Environment.TickCount64 < minEnd || (fadedInAt is null && Environment.TickCount64 < maxEnd))
            {
                Tick();
                var op = host!.Opacity;
                var first = vm.LyricLines.Count > 0 ? vm.LyricLines[0].Text : "";
                if (lastOpacity is null || Math.Abs(op - lastOpacity.Value) > 0.001)
                {
                    log.Add($"{Stamp()} host opacity {op:F3}");
                    lastOpacity = op;
                }
                if (first != lastFirst)
                {
                    log.Add($"{Stamp()} first line now '{first}'");
                    lastFirst = first;
                }
                if (fadedOutAt is null && op <= 0.01) fadedOutAt = Environment.TickCount64 - t0;
                if (newLinesAt is null && first.Contains("bravo")) newLinesAt = Environment.TickCount64 - t0;
                if (fadedInAt is null && newLinesAt is not null && op >= 0.99) fadedInAt = Environment.TickCount64 - t0;
                await Task.Delay(8);
            }

            _output.WriteLine("--- A → B ---");
            foreach (var l in log) _output.WriteLine(l);
            _output.WriteLine($"SUMMARY faded-out at {fadedOutAt}ms, new lines applied at {newLinesAt}ms, faded back in at {fadedInAt}ms");
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "noctis-lyrics-timing.txt"),
                log.Append($"SUMMARY faded-out at {fadedOutAt}ms, new lines applied at {newLinesAt}ms, faded back in at {fadedInAt}ms"));

            Assert.NotNull(newLinesAt);
            Assert.NotNull(fadedInAt);

            // Fade-out is raised before the probe returns, not after it.
            var pendingAt = log.FindIndex(l => l.Contains("LyricsSwapPending"));
            var probeAt = log.FindIndex(l => l.Contains("probe returned"));
            Assert.True(pendingAt >= 0, "no LyricsSwapPending");
            Assert.True(probeAt >= 0, "no LocalProbe log entry");
            Assert.True(pendingAt < probeAt,
                $"fade-out (log #{pendingAt}) must start before the disk probe returns (log #{probeAt})");
        }
        finally
        {
            DebugLogger.EntryAdded -= OnEntry;
            DebugLogger.IsEnabled = wasLoggerEnabled;
            win.Close();
        }
    }
}
