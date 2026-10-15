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
