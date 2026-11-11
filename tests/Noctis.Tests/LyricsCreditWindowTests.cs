using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner #294 (Noctis Plus, after the 1.6.9 merge): the centred no-lyrics page keeps saying what is happening under the
/// player (searching, WordLyrics' state, why there are none) instead of only in a tooltip; and the lyrics credit opens
/// the Search Lyrics window with its word timing choices above the sources.
/// </summary>
public class LyricsCreditWindowTests : IDisposable
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Func<Task<LrcLibResult?>> GetImpl = () => Task.FromResult<LrcLibResult?>(null);
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => GetImpl();
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default) => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class SettingsPersistence : TestPersistenceService
    {
        public AppSettings Settings { get; } = new() { NetEaseEnabled = false };
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "creditwindow-" + Guid.NewGuid().ToString("N"));
    public LyricsCreditWindowTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (LyricsViewModel Vm, PlayerViewModel Player, StubLrcLib LrcLib, List<LyricsSearchViewModel> Shown) Mount()
    {
        var lrcLib = new StubLrcLib();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService())
        { LyricsAutoDownload = true, LyricsCenterWhenNoLyrics = true };
        var shown = new List<LyricsSearchViewModel>();
        var vm = new LyricsViewModel(player, lrcLib, new StubNetEase(), new MetadataService(), new SettingsPersistence(), new FakeLibraryService())
        {
            ShowLyricsSearchDialog = s => { shown.Add(s); s.StopSearch(); return Task.CompletedTask; },
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };
        vm.SetLyricsSurfaceVisible(true);
        return (vm, player, lrcLib, shown);
    }

    private Track NewTrack() => new()
    {
        Title = "Test Song", Artist = "Test Artist", Duration = TimeSpan.FromSeconds(200),
        FilePath = Path.Combine(_dir, $"nofile-{Guid.NewGuid():N}.mp3"), SourceType = SourceType.Local,
    };

    private static async Task PumpUntilAsync(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task CentredWithNoLyrics_TheSearchShowsUnderThePlayer_ThenLyricsMoveThePageBack()
    {
        var rig = Mount();
        rig.Player.CurrentTrack = NewTrack();
        rig.Vm.EnsureLyricsForCurrentTrack();
        await PumpUntilAsync(() => rig.Vm.IsNoLyricsCentered && !rig.Vm.IsSearching);
        Assert.True(rig.Vm.IsNoLyricsCentered);
        Assert.Equal(rig.Vm.SearchFailedMessage, rig.Vm.NoLyricsStatus);   // why there are none, under the player

        // The next song has none on disk either: the page stays centred while it searches, and says so.
        var answer = new TaskCompletionSource<LrcLibResult?>();
        rig.LrcLib.GetImpl = () => answer.Task;
        rig.Player.CurrentTrack = NewTrack();
        rig.Vm.EnsureLyricsForCurrentTrack();
        await PumpUntilAsync(() => rig.Vm.IsSearching);
        Assert.True(rig.Vm.IsNoLyricsCentered);
        Assert.Equal(Localization.Loc.T("Lyrics.SearchingLyrics"), rig.Vm.NoLyricsStatus);

        answer.SetResult(new LrcLibResult { TrackName = "Test Song", ArtistName = "Test Artist", Duration = 200, SyncedLyrics = "[00:01.00]found", PlainLyrics = "found" });
        await PumpUntilAsync(() => !rig.Vm.IsNoLyricsCentered);
        Assert.False(rig.Vm.IsNoLyricsCentered);   // lines arrived: back to the split page (the view slides it)
    }

    [AvaloniaFact]
    public async Task TheCredit_OpensTheSearchLyricsWindow()
    {
        var rig = Mount();
        rig.Player.CurrentTrack = NewTrack();
        rig.Vm.EnsureLyricsForCurrentTrack();
        await PumpUntilAsync(() => !rig.Vm.IsSearching);
        await rig.Vm.OpenCreditChoicesCommand.ExecuteAsync(null);
        var window = Assert.Single(rig.Shown);
        Assert.Equal(rig.Vm.CreditChoices().Count(), window.TimingChoices.Count);
        Assert.Equal(window.TimingChoices.Count > 0, window.HasTimingChoices);
    }

    [Fact]
    public void AWordTimingRow_DoesItsChoice_AndClosesTheWindow_ADisabledOneDoesNothing()
    {
        var track = new Track { Title = "Song", Artist = "Artist" };
        var window = new LyricsSearchViewModel(track, ["LRCLIB"], () => Task.FromResult<IReadOnlyList<string>>(["LRCLIB"]),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<Services.Lyrics.LyricsSourceHit>>([]), (_, _) => { });
        var picked = new List<string>();
        var closed = 0;
        window.Closed += (_, _) => closed++;
        Assert.False(window.HasTimingChoices);
        window.TimingChoices.Add(new("Word timing from Kugou", IsCurrent: true, IsEnabled: true, () => picked.Add("Kugou")));
        window.TimingChoices.Add(new("Time with WordLyrics", IsCurrent: false, IsEnabled: false, () => picked.Add("WordLyrics")));
        Assert.True(window.HasTimingChoices);

        window.PickTimingChoiceCommand.Execute(window.TimingChoices[1]);
        Assert.Empty(picked); Assert.Equal(0, closed);
        window.PickTimingChoiceCommand.Execute(window.TimingChoices[0]);
        Assert.Equal(["Kugou"], picked); Assert.Equal(1, closed);
    }
}
