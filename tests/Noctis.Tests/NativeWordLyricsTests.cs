using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Features.WordLyrics;
using Noctis.Models;
using Noctis.Services.Plugins;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

[Collection("MetadataServiceStatics")] // shares the process-wide saved-download event with importer/pipeline checks
public class NativeWordLyricsTests
{
    private sealed class Rig : IDisposable
    {
        internal readonly TestPersistenceService Data = new();
        internal readonly FakeLibraryService Library = new();
        internal readonly AppSettings Settings = new() { CommunityPluginsEnabled = false };
        internal readonly WordLyricsService Service;
        internal readonly PluginHost Host;
        internal Rig(bool off = false, bool initialSong = true, bool finderOff = false)
        {
            Settings.MusicFolders = [Data.DataDirectory];
            Settings.PluginSettingValues = new() { ["dev.moshi.wordlyrics"] = new() { ["auto"] = off ? "false" : "true", ["speed"] = "Light", ["folder"] = Path.Combine(Data.DataDirectory, "absent backend") } };
            if (finderOff)
            {
                Settings.DisabledPlugins = ["dev.moshi.freemusicfinder"];
                Settings.PluginSettingValues["dev.moshi.freemusicfinder"] = new() { ["proxy"] = "socks5://127.0.0.1:9" };
            }
            if (initialSong) Library.TrackList.Add(Song("Existing"));
            Service = new(Library, () => Settings);
            Host = new(null, Data.DataDirectory, () => Settings, () => { }, "1.6.3", Library);
            Noctis.Features.BuiltInFeatures.Register(Host, words: Service); Host.LoadAll();
        }
        internal Track Song(string title)
        {
            var path = Path.Combine(Data.DataDirectory, title + ".mp3"); File.WriteAllText(path, "synthetic fixture");
            return new() { Title = title, Artist = "Synthetic", FilePath = path };
        }
        public void Dispose() { Host.UnloadAll(); Data.Dispose(); }
    }
    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(6)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Assert.True(condition());
    }
    [AvaloniaFact]
    public void ExplicitRetryReplacesOldErrorBeforeOpeningThePanel_RealNewFailureStillShows()
    {
        using var rig = new Rig();
        rig.Service.Presenter = (_, _) => { };
        rig.Service.SetPreviewState(new("Old failure", "Earlier attempt failed", Error: true, Finished: true, Asked: true));
        Assert.True(rig.Service.State.Error);
        var states = new List<ProgressState>(); rig.Service.Changed += () => states.Add(rig.Service.State);
        var song = rig.Song("Retry now");
        var queue = rig.Service.QueueAsync([song.FilePath]);
        Until(() => queue.IsCompleted); queue.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(states, s => s.Message == "Queued" && !s.Error && !s.Finished);
        Assert.DoesNotContain(states.SkipWhile(s => s.Message != "Queued"), s => s.Message == "Old failure");
        rig.Service.SetPreviewState(new("New failure", "A real failure is not suppressed", Error: true, Finished: true, Asked: true));
        Assert.True(rig.Service.State.Error);
        Assert.True(rig.Service.Pill?.IsProblem);
    }
    [AvaloniaFact]
    public void AutomaticTimingStatus_IsPerSong_AndCancelClearsItWithoutDownloading()
    {
        using var rig = new Rig();
        var song = rig.Song("New download");
        rig.Service.Presenter = (_, _) => { };
        var other = rig.Song("Other song");
        OnlineMusicSearch.PublishSavedFile(song.FilePath);
        Until(() => rig.Service.StatusFor(song.FilePath) == SongTimingStatus.SetupNeeded);
        Assert.Equal(SongTimingStatus.None, rig.Service.StatusFor(other.FilePath));
        var duplicate = rig.Service.TimeSongAsync(song);
        Until(() => duplicate.IsCompleted); duplicate.GetAwaiter().GetResult();
        Assert.Equal(1, rig.Service.WaitingCount);
        var cancel = rig.Service.CancelAsync(); Until(() => cancel.IsCompleted);
        Until(() => rig.Service.StatusFor(song.FilePath) == SongTimingStatus.None);
        Assert.False(Directory.Exists(Path.Combine(rig.Data.DataDirectory, "absent backend")));
    }
    [AvaloniaFact]
    public void SongOptOutIsReversibleInNativePanel_AndUsesOnlyTheOuterScrim()
    {
        using var rig = new Rig(); var track = rig.Library.TrackList[0];
        using var vm = new WordLyricsViewModel(rig.Service, false, track, _ => Task.FromResult(true));
        Assert.True(vm.CanStart); Assert.Contains("Ignore", vm.IgnoreLabel);
        vm.ToggleIgnoreCommand.Execute(null);
        Assert.True(vm.IsIgnored); Assert.False(vm.CanStart); Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains("Enable", vm.IgnoreLabel);
        vm.ToggleIgnoreCommand.Execute(null);
        Assert.False(vm.IsIgnored); Assert.True(vm.CanStart);
        var panel = new WordLyricsPanel { DataContext = vm };
        Assert.False(panel.FindControl<Noctis.Controls.PillDialogHost>("TimingHost")!.ShowBackdrop);
        panel.UseWindowBackdrop();
        Assert.True(panel.FindControl<Noctis.Controls.PillDialogHost>("TimingHost")!.ShowBackdrop);
    }

    /// <summary>Owner #206: Word timing on a song already word-timed said "already exist, right-click the song…", which opens this
    /// same window. It opens on Retime existing words, and Start times it again.</summary>
    [AvaloniaFact]
    public void ASongAlreadyWordTimed_OpensOnRetime_AndStartQueuesIt()
    {
        using var rig = new Rig(); var track = rig.Library.TrackList[0];
        using (var fresh = new WordLyricsViewModel(rig.Service, false, track, _ => Task.FromResult(true)))
            Assert.Equal(0, fresh.ActionIndex);   // no word timing yet: Time missing words
        File.WriteAllText(Path.ChangeExtension(track.FilePath, ".elrc"), "[00:01.00]<00:01.00>Hello <00:01.50>you <00:02.00>");
        using var vm = new WordLyricsViewModel(rig.Service, false, track, _ => Task.FromResult(true));
        Assert.Equal(1, vm.ActionIndex);
        Assert.Equal("Retime existing words", vm.Actions[vm.ActionIndex]);
        Assert.True(vm.CanStart);
        vm.StartCommand.Execute(null);
        Until(() => rig.Service.WaitingCount == 1);
        Assert.DoesNotContain("right-click", vm.Error ?? "");
    }

    /// <summary>Owner #194: the word timing window lists the songs in line; Remove takes one off for good, and only Time
    /// words on that song brings it back.</summary>
    [AvaloniaFact]
    public void TheSongsInLine_AreListed_AndRemoveMeansNeverUntilTimeWords()
    {
        using var rig = new Rig(); var track = rig.Library.TrackList[0];
        var ask = rig.Service.TimeSongAsync(track); Until(() => ask.IsCompleted); ask.GetAwaiter().GetResult();
        using var vm = new WordLyricsViewModel(rig.Service, true, null, _ => Task.FromResult(true));
        var row = Assert.Single(vm.Queue);
        Assert.Equal("Existing", row.Name);
        Assert.Equal("Waiting", row.Line);
        Assert.False(row.Failing);
        Assert.Equal("1 song in line", vm.QueueTitle);
        row.Remove.Execute(row);
        Assert.Empty(vm.Queue);
        Assert.True(rig.Service.IsIgnored(track.FilePath));
        Assert.Equal(0, rig.Service.WaitingCount);
        // Playback and new-download timing leave it alone; Time words on its lyrics brings it back.
        var again = rig.Service.TimeSongAsync(track); Until(() => again.IsCompleted); again.GetAwaiter().GetResult();
        Assert.False(rig.Service.IsIgnored(track.FilePath));
        Until(() => rig.Service.WaitingCount == 1);
    }

    [Fact]
    public void ASongThatKeepsFailing_SaysSo_AndWhenItIsTriedNext()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Timing now", WordLyricsViewModel.QueueLine(new("a.mp3", true, null, true, 2), now));
        Assert.Equal("Didn't work · tried 2 times · next try in 3 h 0 min", WordLyricsViewModel.QueueLine(new("a.mp3", false, now.AddHours(3), true, 2), now));
        Assert.Equal("Didn't work · next try in 10 days", WordLyricsViewModel.QueueLine(new("a.mp3", false, now.AddDays(10), true, 1), now));
        Assert.Equal("Waiting", WordLyricsViewModel.QueueLine(new("a.mp3", false, null, false, 0), now));
        Assert.Equal("Waiting · next try in 2 min", WordLyricsViewModel.QueueLine(new("a.mp3", false, now.AddMinutes(2), false, 0), now));
    }

    [AvaloniaFact] // Owner #183: a first-time user saw Pause/Stop/Restart and a red Start timing before WordLyrics was installed.
    public void BeforeSetup_ThePanelOffersOnlySetUpAsTheMainAction()
    {
        using var rig = new Rig(); var track = rig.Library.TrackList[0];
        using var vm = new WordLyricsViewModel(rig.Service, false, track, _ => Task.FromResult(true));
        Assert.False(vm.IsSetUp); Assert.Equal("Set up WordLyrics", vm.SetupLabel);
        Assert.False(vm.ShowStart); Assert.False(vm.ShowTimingActions); Assert.False(vm.ShowActions);
        var panel = new WordLyricsPanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 900, Height = 800 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var buttons = panel.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            var setup = Assert.Single(buttons, b => Equals(b.Content, "Set up WordLyrics"));
            Assert.Contains("pill-primary", setup.Classes);
            Assert.DoesNotContain(buttons, b => Equals(b.Content, "Start timing") || Equals(b.Content, "Stop") || Equals(b.Content, "Restart"));
            Assert.Contains(buttons, b => Equals(b.Content, vm.IgnoreLabel));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ExplicitTimeWordsReenablesAnIgnoredSong_AndQueuesOnlyOneRequestWithoutDownloadingModels()
    {
        using var rig = new Rig(off: true);
        var song = rig.Library.TrackList[0];
        rig.Service.Presenter = (_, _) => { };
        rig.Service.SetIgnored(song.FilePath, true);
        rig.Service.PlayerMoved(song, TimeSpan.FromSeconds(12), true);
        Thread.Sleep(100); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, rig.Service.WaitingCount); Assert.True(rig.Service.IsIgnored(song.FilePath));
        var request = rig.Service.TimeSongAsync(song); Until(() => request.IsCompleted); request.GetAwaiter().GetResult();
        Assert.False(rig.Service.IsIgnored(song.FilePath));
        Assert.Equal(1, rig.Service.WaitingCount);
        var again = rig.Service.TimeSongAsync(song); Until(() => again.IsCompleted); again.GetAwaiter().GetResult();
        Assert.Equal(1, rig.Service.WaitingCount);
        Assert.False(Directory.Exists(Path.Combine(rig.Data.DataDirectory, "absent backend")));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.Service.DataDirectory, "song-rules.json")));
        Assert.Empty(state.RootElement.GetProperty("Rules").EnumerateObject());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayerLyricsMenuIncludesTheSameActions_AndRebindsIgnoreToTheNewSong(bool lyricsPage)
    {
        using var rig = new Rig(off: true);
        var first = rig.Library.TrackList[0]; var second = rig.Song("Second menu target");
        var player = new PlayerViewModel(new FakeAudioPlayer(), rig.Library, rig.Data, new FakeAnimatedCoverService())
            { CurrentTrack = first, IsLyricsPageActive = lyricsPage };
        var bar = new PlaybackBarView { DataContext = player };
        var window = new Window { Content = bar, Width = 1000, Height = 700 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var menu = Assert.IsType<MenuFlyout>(bar.FindControl<Button>("OptionsButton")!.Flyout);
        Track? repaired = null; rig.Service.Presenter = (_, song) => repaired = song;
        try
        {
            menu.ShowAt(bar); Dispatcher.UIThread.RunJobs();
            var timing = bar.FindControl<MenuItem>("CurrentWordTimingMenuItem")!;
            var ignore = bar.FindControl<MenuItem>("CurrentIgnoreWordLyricsMenuItem")!;
            Assert.True(timing.IsVisible); Assert.True(ignore.IsVisible);
            Assert.Same(first, timing.CommandParameter); Assert.Same(first, ignore.CommandParameter);
            Assert.Same(player.FetchCurrentTrackLyricsCommand, bar.FindControl<MenuItem>("FetchCurrentLyricsMenuItem")!.Command);
            Assert.True(bar.FindControl<MenuItem>("RemoveCurrentLyricsMenuItem")!.IsVisible);
            Assert.Same(player.OpenCurrentTrackLyricsStudioCommand, bar.FindControl<MenuItem>("CurrentLyricsStudioMenuItem")!.Command);
            timing.Command!.Execute(timing.CommandParameter); Assert.Same(first, repaired);
            ignore.Command!.Execute(ignore.CommandParameter); Assert.True(rig.Service.IsIgnored(first.FilePath));
            menu.Hide(); player.CurrentTrack = second;
            menu.ShowAt(bar); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ignore WordLyrics for this song", ignore.Header);
            Assert.Same(second, ignore.CommandParameter);
            ignore.Command!.Execute(ignore.CommandParameter);
            Assert.True(rig.Service.IsIgnored(second.FilePath)); Assert.True(rig.Service.IsIgnored(first.FilePath));
            menu.Hide(); menu.ShowAt(bar); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Enable WordLyrics for this song", ignore.Header);
        }
        finally { menu.Hide(); window.Close(); }
    }

    [AvaloniaFact]
    public void NativeMigrationRunsBeforeDefaults_AndNewChoiceKeepsRollbackKeys()
    {
        using var rig = new Rig(off: true);
        var plugin = rig.Host.FindById("dev.moshi.wordlyrics")!;
        var mode = Assert.Single(plugin.SettingItems, s => s.Key == "defaultMode");
        Assert.Equal("Off", mode.Value); Assert.Null(plugin.AssemblyPath);
        mode.SelectedChoice = "Medium";
        var raw = rig.Settings.PluginSettingValues!["dev.moshi.wordlyrics"];
        Assert.Equal("true", raw["auto"]); Assert.Equal("Half", raw["speed"]);
        Assert.Equal("Medium", raw["defaultMode"]);
    }

    [AvaloniaFact]
    public void LyricsPageTimeWords_QueuesOnlyThatSong_AndMissingSetupNeverDownloads()
    {
        using var rig = new Rig(off: true);
        var song = rig.Library.TrackList[0];
        Track? shown = null;
        rig.Service.Presenter = (library, track) => { Assert.False(library); shown = track; };
        var request = rig.Service.TimeSongAsync(song);
        Until(() => request.IsCompleted); request.GetAwaiter().GetResult();
        Assert.Equal(1, rig.Service.WaitingCount);
        Assert.Same(song, shown);
        Assert.False(Directory.Exists(Path.Combine(rig.Data.DataDirectory, "absent backend")));
        Assert.False(File.Exists(Path.Combine(rig.Service.DataDirectory, "setup.cmd")));
        Assert.True(File.Exists(song.FilePath));
    }

    [AvaloniaFact]
    public void LyricsPageTimeWords_RejectsRemoteSongsWithoutOpeningSetup()
    {
        using var rig = new Rig(off: true);
        var shown = false;
        rig.Service.Presenter = (_, _) => shown = true;
        var request = rig.Service.TimeSongAsync(new Track { SourceType = SourceType.Navidrome, FilePath = "https://example.invalid/song" });
        Until(() => request.IsCompleted); request.GetAwaiter().GetResult();
        Assert.Equal(0, rig.Service.WaitingCount);
        Assert.False(shown);
    }
    [AvaloniaFact]
    public void TimingAheadUsesOnlyTheNextTwoPositions_CompletedFilesOnly_AndClearsOldPriorities()
    {
        using var rig = new Rig();
        var now = rig.Song("Playing local");
        var second = rig.Song("Second ready");
        var third = rig.Song("Third outside");
        var source = new QueueStreamingTests.Source();
        var pending = PlayerViewModel.StandIn(source, new OnlineSong("missing", "Still arriving", "Synthetic"), false);
        rig.Service.PlayerMoved(now, TimeSpan.Zero, true); // local songs now get timing too
        rig.Service.UpdateAhead([pending, second, third], true);
        Until(() => rig.Service.WaitingCount == 2); // current + ready position2; no backfill for missing position1
        Assert.Equal(SongTimingStatus.None, rig.Service.StatusFor(third.FilePath));
        Assert.Empty(source.Asked); // timing never starts an audio download
        rig.Service.UpdateAhead([], true);
        Until(() => rig.Service.WaitingCount == 1); // obsolete ahead-only work removed, current retained
    }

    [AvaloniaFact]
    public void AddingAnAlreadyTimedPlayingSong_PublishesItsSidecarsToTheCacheWithoutAnotherModelRun()
    {
        using var rig = new Rig();
        var saved = rig.Song("Already timed copy");
        var cache = Path.Combine(rig.Data.DataDirectory, "cache", Path.GetFileName(saved.FilePath));
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "synthetic cache audio");
        var words = "[00:01.00]<00:01.00>timed <00:02.00>words";
        File.WriteAllText(Path.ChangeExtension(saved.FilePath, ".elrc"), words);
        IReadOnlyList<string>? written = null;
        rig.Service.LyricsWritten += paths => written = paths;
        rig.Service.PlayerMoved(new Track { FilePath = cache }, TimeSpan.Zero, true);
        OnlineMusicSearch.PublishSavedFile(saved.FilePath);
        Until(() => written?.Contains(cache) == true);
        Assert.Equal(words, File.ReadAllText(Path.ChangeExtension(cache, ".elrc")));
        Until(() => rig.Service.WaitingCount == 0);
    }

    [AvaloniaFact]
    public void PlayingSavedCopyShowsTimingStatus_AndNewPlayedDefaultKeepsAnExplicitOffChoice()
    {
        using var rig = new Rig();
        Assert.True(rig.Service.TimePlayed);
        rig.Host.FindById("dev.moshi.wordlyrics")!.SettingItems.Single(s => s.Key == "timePlayed").BoolValue = false;
        Assert.False(rig.Service.TimePlayed);
        var saved = rig.Song("Copy status");
        var cache = Path.Combine(rig.Data.DataDirectory, "cache", Path.GetFileName(saved.FilePath));
        rig.Service.PlayerMoved(new Track { FilePath = cache }, TimeSpan.Zero, true);
        OnlineMusicSearch.PublishSavedFile(saved.FilePath);
        Until(() => rig.Service.StatusFor(cache) == SongTimingStatus.SetupNeeded);
        Assert.Equal(rig.Service.StatusFor(saved.FilePath), rig.Service.StatusFor(cache));
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "synthetic cache copy");
        var manual = rig.Service.TimeSongAsync(new Track { FilePath = cache });
        Until(() => manual.IsCompleted); manual.GetAwaiter().GetResult();
        Assert.Equal(1, rig.Service.WaitingCount); // manual action promotes the saved copy, never queues its cache twin too
    }

    [AvaloniaFact]
    public void AddingThePlayingCopy_RespectsAutomaticTimingOff()
    {
        using var rig = new Rig(off: true);
        var saved = rig.Song("Playing song");
        var cache = new Track { FilePath = Path.Combine(rig.Data.DataDirectory, "stream-cache", Path.GetFileName(saved.FilePath)) };
        rig.Service.PlayerMoved(cache, TimeSpan.Zero, true);
        OnlineMusicSearch.PublishSavedFile(saved.FilePath);
        Thread.Sleep(100); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, rig.Service.WaitingCount);
    }

    [AvaloniaFact]
    public void ExistingLibraryIsNotBackfilled_AndNewDownloadsAreQueuedOnce()
    {
        using var rig = new Rig();
        Assert.Equal(0, rig.Service.WaitingCount);
        var song = rig.Song("Downloaded");
        OnlineMusicSearch.PublishSavedFile(song.FilePath);
        OnlineMusicSearch.PublishSavedFile(song.FilePath);
        Until(() => rig.Service.WaitingCount == 1);
        rig.Library.TrackList.Add(song); rig.Library.RaiseLibraryUpdated(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, rig.Service.WaitingCount);
    }
    [AvaloniaFact]
    public void FirstNewSongInAnInitiallyEmptyLibraryIsNotLost_AndOffDoesNoAutomaticWork()
    {
        using (var rig = new Rig(initialSong: false))
        {
            var song = rig.Song("First new song");
            File.SetCreationTimeUtc(song.FilePath, DateTime.UtcNow.AddMilliseconds(-500)); // coarse filesystem timestamp
            rig.Library.TrackList.Add(song); rig.Library.RaiseLibraryUpdated();
            Until(() => rig.Service.WaitingCount == 1);
        }
        using (var rig = new Rig(off: true))
        {
            var song = rig.Song("New"); OnlineMusicSearch.PublishSavedFile(song.FilePath);
            Thread.Sleep(100); Dispatcher.UIThread.RunJobs(); Assert.Equal(0, rig.Service.WaitingCount);
        }
    }
    [AvaloniaFact]
    public void ProgressDialogCannotAccidentallyQueueTheLibrary_AndCancelKeepsAudio()
    {
        using var rig = new Rig();
        using var vm = new WordLyricsViewModel(rig.Service, false, null, _ => Task.FromResult(true));
        Assert.False(vm.CanQueue); Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.Indeterminate);
        var path = rig.Library.TrackList[0].FilePath;
        var queued = rig.Service.QueueAsync([path]); Until(() => queued.IsCompleted); queued.GetAwaiter().GetResult();
        Until(() => rig.Service.WaitingCount == 1);
        var cancel = rig.Service.CancelAsync(); Until(() => cancel.IsCompleted);
        Assert.Equal(0, rig.Service.WaitingCount); Assert.True(File.Exists(path));
    }
    [AvaloniaFact]
    public void NativeDialogLoadsAndCustomDraftPreservesLineGuides()
    {
        using var rig = new Rig();
        var song = rig.Library.TrackList[0];
        File.WriteAllText(Path.ChangeExtension(song.FilePath, ".elrc"), "[00:01.00]<00:01.00>hello <00:02.00>world");
        var dialog = new WordLyricsDialog(rig.Service, false, song);
        var vm = Assert.IsType<WordLyricsViewModel>(dialog.DataContext);
        dialog.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            vm.ActionIndex = 3; vm.Words = "\n[00:01.00]hello world\n\n.done\n";
            Assert.True(vm.IsCustom); Assert.True(vm.CanQueue);
            var draft = LyricDrafts.Save(rig.Service.DataDirectory, vm.Words);
            Assert.Equal("[00:01.00]hello world\n", File.ReadAllText(draft));
            Assert.Contains("<00:01.00>", File.ReadAllText(Path.ChangeExtension(song.FilePath, ".elrc")));
        }
        finally { dialog.Close(); }
    }
    [Fact]
    public void EmbeddedWordReaderAndDraftsUseNativeTypesAndRejectEmptyText()
    {
        Assert.True(EmbeddedWordLyrics.HasWords("fixture", _ => "[00:01.00]<00:01.00>hello"));
        Assert.False(EmbeddedWordLyrics.HasWords("fixture", _ => "[00:01.00]hello"));
        Assert.Equal("[00:02.00]hello", LyricDrafts.Normalize("\r\n [00:02.00]hello \r\n\r\n.done\r\n"));
        using var data = new TestPersistenceService();
        Assert.Throws<InvalidOperationException>(() => LyricDrafts.Save(data.DataDirectory, "\n .done \n"));
    }
    [AvaloniaFact]
    public void WordLyricsKeepsFindersProxyEvenWhenFinderIsDisabled()
    {
        using var rig = new Rig(finderOff: true);
        Assert.Equal("socks5://127.0.0.1:9", rig.Service.MetadataProxy);
        var proxy = rig.Host.FindById("dev.moshi.freemusicfinder")!.SettingItems.Single(s => s.Key == "proxy");
        proxy.TextValue = "direct";
        Assert.Equal("direct", rig.Service.MetadataProxy);
    }
    [AvaloniaFact]
    public void SetupRequiresExplicitConsent_AndCancelCreatesNoInstaller()
    {
        using var rig = new Rig(); string? prompt = null;
        var request = rig.Service.SetupAsync(text => { prompt = text; return Task.FromResult(false); });
        Until(() => request.IsCompleted); request.GetAwaiter().GetResult();
        Assert.Contains("1.33 GB", prompt); Assert.Contains("optional", prompt);
        Assert.Equal(2, prompt!.Split('\n').Length);
        Assert.All(prompt.Split('\n'), line => Assert.True(line.Length <= 50));
        Assert.False(File.Exists(Path.Combine(rig.Service.DataDirectory, "setup.cmd")));
        Assert.False(Directory.Exists(Path.Combine(rig.Data.DataDirectory, "absent backend")));
    }
    [AvaloniaFact]
    public void LongErrorsWrapInsideTheNativeDialog()
    {
        using var rig = new Rig();
        rig.Service.SetPreviewState(new("Waiting", string.Join(" ", Enumerable.Repeat("Detailed permission error and a long folder path", 40)), Error: true, Finished: true));
        Dispatcher.UIThread.RunJobs();
        var dialog = new WordLyricsDialog(rig.Service, true, null); dialog.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var scroll = dialog.WordPanel.FindControl<ScrollViewer>("WordBodyScroll")!;
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"{scroll.Extent.Width} > {scroll.Viewport.Width}");
        }
        finally { dialog.Close(); }
    }
    [AvaloniaFact]
    public void LockedFileGetsAVisibleRetryStateInsteadOfSilentWaiting()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing semantics, including Windows 10.
        using var data = new TestPersistenceService(); var path = Path.Combine(data.DataDirectory, "locked.mp3");
        File.WriteAllText(path, "synthetic fixture"); var states = new ConcurrentQueue<ProgressState>();
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var worker = new Worker(Path.Combine(data.DataDirectory, "work"), () => [data.DataDirectory],
            new(true, "Light", false, Path.Combine(data.DataDirectory, "absent backend"), ""), _ => { }, _ => { }, states.Enqueue);
        worker.Ask(path); Until(() => states.Any(s => s.Detail.Contains("WORDLYRICS_FILE_ACCESS")));
        Assert.Equal(1, worker.WaitingCount); Assert.True(File.Exists(path));
    }
    [Fact]
    public void GeniusParserReadsOnlyCompleteContainersAndValidatesPages()
    {
        var html = "<h1>Page header</h1><div data-lyrics-container=\"true\">One &amp; two<br/>Three <a>four</a></div><div class='annotation'>Ignore me</div><div data-lyrics-container='true'>Last line</div>";
        Assert.Equal("One & two\nThree four\nLast line", LyricSources.GeniusText(html));
        Assert.Equal("", LyricSources.GeniusText("<div data-lyrics-container='true'>Incomplete"));
        Assert.Equal("genius.com", LyricSources.GeniusUrl("https://genius.com/Song-lyrics?utm=test").Host);
        foreach (var bad in new[] { "http://genius.com/Song-lyrics", "https://genius.com.evil.test/Song-lyrics", "https://user:pass@genius.com/Song-lyrics", "https://127.0.0.1/Song-lyrics", "https://genius.com/artist", "https://genius.com:444/Song-lyrics" })
            Assert.Throws<InvalidOperationException>(() => LyricSources.GeniusUrl(bad));
    }
    [Fact]
    public async Task BackendProxyRequiresAuthentication_AndTunnelsBytesWithoutTlsInterception()
    {
        var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        int connections = 0;
        using var proxy = new BackendProxy(() => "direct", async (host, port, ct) =>
        {
            Assert.Equal("example.test", host); Assert.Equal(443, port); Interlocked.Increment(ref connections);
            var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)upstream.LocalEndpoint).Port, ct); return tcp;
        });
        var uri = new Uri(proxy.Address);
        try
        {
            using (var denied = new TcpClient())
            {
                await denied.ConnectAsync(uri.Host, uri.Port);
                await denied.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT example.test:443 HTTP/1.1\r\n\r\n"));
                var data = new byte[200]; var read = await denied.GetStream().ReadAsync(data);
                Assert.Contains("407", Encoding.ASCII.GetString(data, 0, read)); Assert.Equal(0, connections);
            }
            var accepting = upstream.AcceptTcpClientAsync();
            using var client = new TcpClient(); await client.ConnectAsync(uri.Host, uri.Port);
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes(Uri.UnescapeDataString(uri.UserInfo)));
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT example.test:443 HTTP/1.1\r\nProxy-Authorization: Basic " + auth + "\r\n\r\n"));
            using var server = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            var header = new byte[100]; var count = await client.GetStream().ReadAsync(header);
            Assert.Contains("200 Connection Established", Encoding.ASCII.GetString(header, 0, count));
            await client.GetStream().WriteAsync("synthetic encrypted bytes"u8.ToArray());
            var payload = new byte[25]; var got = await server.GetStream().ReadAsync(payload);
            Assert.Equal("synthetic encrypted bytes", Encoding.ASCII.GetString(payload, 0, got));
            proxy.Dispose();
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { upstream.Stop(); }
    }
}
