using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Studio's live preview pane (Discord "Manual lyrics editing", 10-14): the review shown by
/// the lyrics view beside the editor, updated as it is edited, without switching to Preview.
/// </summary>
public class LyricsStudioLivePreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-live-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioLivePreviewTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string TwoLines = "[00:05.00]First line here\n[00:12.50]Second line here\n";

    private Track SongWithLrc(string name)
    {
        var path = Path.Combine(_root, name + ".mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), TwoLines);
        return new Track { Title = name, Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
    }

    private LyricsStudioViewModel Studio(PlayerViewModel? player, bool livePreview, List<LyricsStudioPrefs>? saved, params Track[] tracks)
    {
        var vm = new LyricsStudioViewModel(tracks, new NoRunEngine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), player,
            () => new AppSettings { LyricsStudioLivePreview = livePreview, LyricsStudioWordTimings = false }, p => saved?.Add(p));
        vm.Confirm = _ => Task.FromResult(true);
        return vm;
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition(), "timed out waiting for: " + what);
    }

    // ── The view model's side ──────────────────────────────────────────────

    [AvaloniaFact]
    public async Task AnOpenPane_GetsTheReview_AndEveryEdit_ThenLetsGoWhenClosed()
    {
        var pushes = new List<(Track Track, string Text)>();
        var clears = 0;
        var saved = new List<LyricsStudioPrefs>();
        var vm = Studio(null, livePreview: true, saved, SongWithLrc("song"));
        vm.UpdateLivePreview = (t, text) => pushes.Add((t, text));
        vm.ClearLivePreview = () => clears++;
        Assert.False(vm.IsLivePreviewOpen); // no review yet

        vm.Selected = vm.Queue[0];
        Assert.True(vm.IsLivePreviewOpen);
        Assert.True(vm.ShowLivePreviewIdle); // no player: the song is not playing
        await Until(() => pushes.Count == 1, "the review to reach the pane");
        Assert.Equal("[00:05.00]First line here\n[00:12.50]Second line here", pushes[0].Text);

        vm.SelectLine(vm.ReviewLines[0]);
        for (var i = 0; i < 5; i++) vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(10));
        await Until(() => pushes.Count >= 2 && pushes[^1].Text.StartsWith("[00:05.05]"), "the nudges to reach the pane");
        Assert.True(pushes.Count <= 3, $"a burst of nudges is coalesced, got {pushes.Count} pushes");

        vm.ToggleLivePreviewCommand.Execute(null);
        Assert.False(vm.IsLivePreviewOpen);
        await Until(() => clears == 1, "closing the pane to drop the preview");
        Assert.False(saved[^1].LivePreview); // remembered with the Studio's options
    }

    [AvaloniaFact]
    public async Task AClosedPane_PushesNothing()
    {
        var pushes = 0;
        var vm = Studio(null, livePreview: false, null, SongWithLrc("song"));
        vm.UpdateLivePreview = (_, _) => pushes++;
        vm.Selected = vm.Queue[0];
        vm.ReviewLines[0].Text = "First line fixed";
        await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs * 3);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.CanLivePreview);
        Assert.False(vm.IsLivePreviewOpen);
        Assert.Equal(0, pushes);
    }

    [AvaloniaFact]
    public async Task ClosingTheDialog_DropsWhatThePaneShowed()
    {
        var clears = 0;
        var vm = Studio(null, livePreview: true, null, SongWithLrc("song"));
        var shown = false;
        vm.UpdateLivePreview = (_, _) => shown = true;
        vm.ClearLivePreview = () => clears++;
        vm.Selected = vm.Queue[0];
        await Until(() => shown, "the review to reach the pane");

        await vm.CloseCommand.ExecuteAsync(null);

        Assert.Equal(1, clears);
    }

    [AvaloniaFact]
    public async Task AnEditJustBeforeClosing_IsNotPushedAfterTheDialogClosed()
    {
        var pushes = 0;
        var clears = 0;
        var vm = Studio(null, livePreview: true, null, SongWithLrc("song"));
        vm.UpdateLivePreview = (_, _) => pushes++;
        vm.ClearLivePreview = () => clears++;
        vm.Selected = vm.Queue[0];
        await Until(() => pushes == 1, "the review to reach the pane");

        // A nudge, then Close inside the pane's 120 ms coalescing window.
        vm.SelectLine(vm.ReviewLines[0]);
        vm.NudgeSelectionBy(TimeSpan.FromMilliseconds(10));
        await vm.CloseCommand.ExecuteAsync(null);
        Assert.Equal(1, clears);

        await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs * 3);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, pushes); // the closed dialog's review must not come back on the lyrics page
    }

    [AvaloniaFact]
    public async Task ThePreviewButton_KeepsItsPreview_WhenThePageItOpensDetachesTheStudio()
    {
        // Preview hands the lyrics page its own preview and navigates there, which detaches the
        // Studio page: dropping the pane's preview then must not drop the one Preview just set.
        var clears = 0;
        var vm = Studio(null, livePreview: true, null, SongWithLrc("song"));
        vm.UpdateLivePreview = (_, _) => { };
        vm.ClearLivePreview = () => clears++;
        vm.ShowOnLyricsPage = (_, _) => Task.CompletedTask;
        vm.Selected = vm.Queue[0];
        await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs * 3);
        Dispatcher.UIThread.RunJobs();

        await vm.PreviewOnLyricsPageCommand.ExecuteAsync(null);
        vm.SuspendLivePreview();
        await Task.Delay(LyricsStudioViewModel.LivePreviewDelayMs * 3);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, clears);
    }

    // ── On screen, with the app's lyrics view model ────────────────────────

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

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("ChevronDownThinIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
    }

    private static void Settle(Window win)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
        }
    }

    [AvaloniaFact]
    public async Task ThePane_ShowsTheReviewBesideTheEditor_AndFollowsAnEdit()
    {
        EnsureAppResources();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), new FakeLibraryService());
        player.Duration = TimeSpan.FromSeconds(30);
        var track = SongWithLrc("song");
        var vm = Studio(player, livePreview: true, null, track);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false, PreviewLyrics = lyrics };
        var win = new Window { Width = 1400, Height = 800, Content = panel };
        win.Show();
        try
        {
            vm.Selected = vm.Queue[0];
            Settle(win);
            var pane = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("studio-preview"));
            Assert.True(pane.IsEffectivelyVisible);
            Assert.True(vm.ShowLivePreviewIdle); // another song (none) is playing: Play is offered
            var toggle = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("studio-live-preview"));
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Same(vm.ToggleLivePreviewCommand, toggle.Command);

            // The song under review plays: the pane shows the review, not the song's file.
            player.CurrentTrack = track;
            lyrics.EnsureLyricsForCurrentTrack();
            Assert.False(vm.ShowLivePreviewIdle);
            await Until(() => lyrics.IsPreviewActive && lyrics.LyricLines.Any(l => l.Text == "First line here"), "the review in the pane");
            var view = panel.GetVisualDescendants().OfType<LyricsPanelView>().Single();
            Assert.True(view.IsEffectivelyVisible);

            vm.ReviewLines[1].Text = "Second line fixed";
            await Until(() => lyrics.LyricLines.Any(l => l.Text == "Second line fixed"), "the edit in the pane");
            Assert.False(File.Exists(Path.ChangeExtension(track.FilePath!, ".elrc"))); // nothing saved

            // Its X hides the pane (not the main window's side panel) and the song's own lyrics return.
            vm.ShowLivePreview = false;
            Settle(win);
            Assert.False(pane.IsEffectivelyVisible);
            await Until(() => !lyrics.IsPreviewActive, "the preview to be dropped");
        }
        finally { await CloseQuietly(win, lyrics); }
    }

    /// <summary>
    /// Closes the window and lets the lyrics reload it starts (or one still in flight) land before
    /// the test ends, then disposes the lyrics view model: a probe that finishes after the test
    /// posts its swap into whichever headless test runs next.
    /// </summary>
    private static async Task CloseQuietly(Window win, LyricsViewModel lyrics)
    {
        win.Close();
        try { await Until(() => !lyrics.IsPreviewActive && lyrics.LyricLines.Any(l => l.Text == "Second line here"), "the song's own lyrics to reload"); }
        finally { lyrics.Dispose(); }
    }

    [AvaloniaFact]
    public async Task LeavingTheStudioPage_PutsTheSongsOwnLyricsBack()
    {
        // The sidebar page's Studio is never closed: navigating away detaches the panel. The
        // pane's unsaved preview lives in the app's lyrics view model, so it stayed on the lyrics
        // page and side panel (with Save / Search off) until the song changed.
        EnsureAppResources();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), new FakeLibraryService());
        player.Duration = TimeSpan.FromSeconds(30);
        var track = SongWithLrc("song");
        var vm = Studio(player, livePreview: true, null, track);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false, PreviewLyrics = lyrics };
        var win = new Window { Width = 1400, Height = 800, Content = panel };
        win.Show();
        try
        {
            vm.Selected = vm.Queue[0];
            player.CurrentTrack = track;
            lyrics.EnsureLyricsForCurrentTrack();
            Settle(win);
            vm.ReviewLines[1].Text = "Second line fixed";
            await Until(() => lyrics.IsPreviewActive && lyrics.LyricLines.Any(l => l.Text == "Second line fixed"), "the edit in the pane");

            win.Content = null; // another page
            Settle(win);
            await Until(() => !lyrics.IsPreviewActive, "the pane's preview to be dropped when the page is left");

            // Back on the page: the pane shows the review again.
            win.Content = panel;
            Settle(win);
            await Until(() => lyrics.IsPreviewActive && lyrics.LyricLines.Any(l => l.Text == "Second line fixed"), "the pane back on return");
        }
        finally { await CloseQuietly(win, lyrics); }
    }

    private sealed class NoRunEngine : ILyricsStudioEngine
    {
        public NoRunEngine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => throw new InvalidOperationException("no runs here");
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("no runs here");
    }
}
