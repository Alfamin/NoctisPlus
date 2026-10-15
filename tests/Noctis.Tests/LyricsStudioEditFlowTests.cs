using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Hand edits in the Studio review (Discord "Manual lyrics editing", 10-14): drafts keep them
/// as they are made, tap mode does not outlive the lines it was tapping, and the nudge bar's
/// selection, steps and keys act on the line or word chosen.
/// </summary>
public class LyricsStudioEditFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-edit-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioEditFlowTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string TwoLines = "[00:05.00]First line here\n[00:12.50]Second line here\n";
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private Track SongWithLrc(string name, string lrc = TwoLines)
    {
        var path = Path.Combine(_root, name + ".mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), lrc);
        return new Track { Title = name, Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
    }

    private LyricsStudioDraftStore Drafts() => new(Path.Combine(_root, "drafts"));

    private LyricsStudioViewModel Studio(LyricsStudioDraftStore? drafts, params Track[] tracks)
    {
        var vm = new LyricsStudioViewModel(tracks, new Engine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), null,
            () => new AppSettings(), _ => { }, drafts);
        vm.Confirm = _ => Task.FromResult(true);
        return vm;
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition());
    }

    // ── Drafts keep edits as they are made ─────────────────────────────────

    /// <summary>
    /// The page's Studio is never closed: a draft was written only on leaving the song, so a
    /// fix made on the song on screen was gone after quitting the app.
    /// </summary>
    [AvaloniaFact]
    public async Task AnEditOnTheSongOnScreen_IsInItsDraft_WithoutLeavingTheSong()
    {
        var drafts = Drafts();
        var song = SongWithLrc("song");
        var vm = Studio(drafts, song);
        vm.Selected = vm.Queue[0];
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Loaded, vm.Queue[0].Status);

        vm.ReviewLines[0].Nudge(null, NudgeEdge.Whole, Ms(100), null, vm.ReviewLines[1].Start, song.Duration);
        Assert.False(drafts.TryLoad(song.Id, out _)); // not on every keystroke: after a pause
        await Until(() => drafts.TryLoad(song.Id, out _));

        // The app restarts: the song comes back with the nudge.
        var next = Studio(drafts, song);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, next.Queue[0].Status);
        next.Selected = next.Queue[0];
        Assert.Equal(S(5.1), next.ReviewLines[0].Start);
        Assert.Equal(S(12.5), next.ReviewLines[1].Start);
    }

    [AvaloniaFact]
    public async Task ASavedSong_IsNotWrittenBackToADraft_ByAPendingEdit()
    {
        var drafts = Drafts();
        var song = SongWithLrc("song");
        var writer = new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_root, "registry.json")), Path.Combine(_root, "cache"))
        {
            TrashFile = path => { File.Delete(path); return true; },
        };
        var vm = new LyricsStudioViewModel(new[] { song }, new Engine(_root), writer, new FakeLibraryService(), null,
            () => new AppSettings { LyricsStudioWordTimings = false }, _ => { }, drafts);
        vm.Confirm = _ => Task.FromResult(true);
        vm.Selected = vm.Queue[0];

        vm.ReviewLines[0].Nudge(null, NudgeEdge.Whole, Ms(100), null, null, song.Duration);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LyricsStudioViewModel.StudioStatus.Saved, vm.Queue[0].Status);
        await Task.Delay(LyricsStudioViewModel.DraftSaveDelayMs + 200);
        Dispatcher.UIThread.RunJobs();

        Assert.False(drafts.TryLoad(song.Id, out _));
        Assert.StartsWith("[00:05.10]First line here", File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".lrc")));
    }

    // ── Tap mode does not outlive its lines ────────────────────────────────

    /// <summary>
    /// Tap mode (and the selected word) belonged to the lines on screen: after a Re-sync replaced
    /// them, the tap bar stayed up and Space stamped times on a line no longer shown.
    /// </summary>
    [AvaloniaFact]
    public async Task ReSync_EndsTapMode_AndDropsTheSelectedWord()
    {
        var vm = Studio(null, SongWithLrc("song"));
        vm.Selected = vm.Queue[0];
        await vm.StartTapCommand.ExecuteAsync(vm.ReviewLines[0]);
        Assert.True(vm.IsTapping);
        Assert.NotNull(vm.SelectedWord);

        await vm.StartCommand.ExecuteAsync(null); // "Re-sync": nothing queued, the song on screen re-runs

        Assert.Equal(LyricsStudioViewModel.StudioStatus.Ready, vm.Queue[0].Status);
        Assert.False(vm.IsTapping);
        Assert.Null(vm.SelectedWord);
    }

    // ── The nudge bar ──────────────────────────────────────────────────────

    private const string WordLrc = "[00:05.00]<00:05.00>First <00:05.50>line <00:06.00>here<00:06.50>\n[00:12.50]<00:12.50>Second <00:13.00>line<00:13.50>\n";

    [AvaloniaFact]
    public async Task TheTimePill_PicksTheLine_AndTheStepsMoveIt_StoppingBeforeTheNextLine()
    {
        var vm = Studio(null, SongWithLrc("song"));
        vm.Selected = vm.Queue[0];
        Assert.False(vm.HasNudgeTarget);
        Assert.False(vm.NudgeSelectionBy(Ms(10)));

        await vm.PlayFromLineCommand.ExecuteAsync(vm.ReviewLines[0]);
        Assert.Same(vm.ReviewLines[0], vm.SelectedLine);
        Assert.True(vm.ReviewLines[0].IsSelected);
        Assert.Contains("00:05.00", vm.NudgeTargetText);

        vm.NudgeSelectionCommand.Execute("100");
        vm.NudgeSelectionCommand.Execute("-10");
        Assert.Equal(S(5.09), vm.ReviewLines[0].Start);
        Assert.Contains("00:05.09", vm.NudgeTargetText);
        Assert.Equal(S(12.5), vm.ReviewLines[1].Start); // only the chosen line moved

        for (var i = 0; i < 100; i++) vm.NudgeSelectionCommand.Execute("100");
        Assert.Equal(S(12.49), vm.ReviewLines[0].Start); // clamped 10 ms before the next line
    }

    [AvaloniaFact]
    public async Task AWord_PicksItsLine_AndTheEndEdgeMovesOnlyItsEnd()
    {
        var vm = Studio(null, SongWithLrc("song", WordLrc));
        vm.WordTimings = true;
        vm.Selected = vm.Queue[0];
        var line = vm.ReviewLines[0];
        await vm.SelectWordCommand.ExecuteAsync(line.Words[1]);
        Assert.Same(line, vm.SelectedLine);

        vm.NudgeEnd = true;
        vm.NudgeSelectionCommand.Execute("100");

        Assert.Equal(new[] { S(5), S(5.5), S(6.1) }, line.Words.Select(w => w.Start));
        Assert.Equal(S(6.5), line.End);

        // The time pill goes back to the whole line.
        await vm.PlayFromLineCommand.ExecuteAsync(line);
        Assert.Null(vm.SelectedWord);
        vm.NudgeWhole = true;
        vm.NudgeSelectionCommand.Execute("-100");
        Assert.Equal(new[] { S(4.9), S(5.4), S(6) }, line.Words.Select(w => w.Start));
    }

    [AvaloniaFact]
    public async Task PickingAnotherSong_LetsTheLineGo()
    {
        var vm = Studio(null, SongWithLrc("one"), SongWithLrc("two"));
        vm.Selected = vm.Queue[0];
        await vm.PlayFromLineCommand.ExecuteAsync(vm.ReviewLines[0]);
        var line = vm.SelectedLine!;

        vm.Selected = vm.Queue[1];

        Assert.Null(vm.SelectedLine);
        Assert.False(line.IsSelected);
        Assert.False(vm.NudgeSelectionBy(Ms(10)));
    }

    [AvaloniaFact]
    public async Task NudgedTimes_AreWhatSaveWrites_InElrcLrcAndTtml()
    {
        var song = SongWithLrc("song", WordLrc);
        var writer = new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_root, "registry.json")), Path.Combine(_root, "cache"))
        {
            TrashFile = path => { File.Delete(path); return true; },
        };
        var vm = new LyricsStudioViewModel(new[] { song }, new Engine(_root), writer, new FakeLibraryService(), null,
            () => new AppSettings { LyricsStudioWordTimings = true, LyricsStudioSaveTtml = true }, _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.Selected = vm.Queue[0];
        await vm.SelectWordCommand.ExecuteAsync(vm.ReviewLines[1].Words[1]);
        vm.NudgeStart = true;
        vm.NudgeSelectionCommand.Execute("100"); // "line" 13.00 → 13.10

        await vm.SaveCommand.ExecuteAsync(null);

        var elrc = File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".elrc"));
        Assert.Contains("[00:12.50]<00:12.50>Second <00:13.10>line<00:13.50>", elrc);
        Assert.Contains("[00:12.50]Second line", File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".lrc")));
        var ttml = File.ReadAllText(Path.ChangeExtension(song.FilePath!, ".ttml"));
        Assert.Contains("<span begin=\"00:00:13.100\"", ttml);
    }

    // ── Engine ─────────────────────────────────────────────────────────────

    private sealed class Engine : ILyricsStudioEngine
    {
        public Engine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }

        public IDisposable OpenSession(WhisperModelSize model) => new Handle();

        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            var src = options.SourceLines ?? new List<string> { "heard" };
            var lines = src.Select((t, i) => new AlignedLine(t, TimeSpan.FromSeconds(20 + i), TimeSpan.FromSeconds(21 + i),
                Array.Empty<AlignedWord>(), 0.8, false)).ToList();
            return Task.FromResult(new LyricsStudioResult(track, lines, LyricsStudioSource.ExistingLyrics, 0.8, "en", lines.Count));
        }

        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
