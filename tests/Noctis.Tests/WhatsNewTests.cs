using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Features.WhatsNew;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #97: the first start after an update shows what was added and fixed, once.</summary>
public class WhatsNewTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "noctis-whatsnew-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_data, true); } catch (IOException) { } }
    private static WhatsNewNotes Notes(string version) => new(version, [new("SongsIcon", "A", "B")], ["C"]);

    [Fact]
    public void AnUpdate_ShowsTheNotesOnce()
    {
        WhatsNewGate.MarkSeen(_data, new Version(2, 1, 0));
        Assert.NotNull(WhatsNewGate.ToShow(_data, new Version(2, 2, 0, 0), Notes("2.2.0")));
        WhatsNewGate.MarkSeen(_data, new Version(2, 2, 0, 0));
        Assert.Null(WhatsNewGate.ToShow(_data, new Version(2, 2, 0, 0), Notes("2.2.0")));
    }

    [Fact]
    public void AFreshInstall_GetsEveryUpdateOnce_ThenOnlyTheNextOnes()
    {
        // Owner #150: a new user sees everything once.
        Assert.Equal(4, WhatsNewGate.ToShow(_data, new Version(2, 3, 1), History)!.Updates);
        WhatsNewGate.MarkSeen(_data, new Version(2, 3, 1));
        Assert.Null(WhatsNewGate.ToShow(_data, new Version(2, 3, 1), History));
    }

    [Fact]
    public void AnExistingProfileFromBeforeThePopUp_SeesIt()
        => Assert.NotNull(WhatsNewGate.ToShow(_data, new Version(2, 2, 0), Notes("2.2.0")));

    private static WhatsNewNotes Release(string version, params (string Title, bool Top)[] added)
        => new(version, [.. added.Select(a => new WhatsNewItem("SongsIcon", a.Title, "line", Top: a.Top))], ["fix " + version]);
    private static readonly WhatsNewNotes[] History =
    [
        Release("2.3.1", ("small 2.3.1", false), ("big 2.3.1", true)),
        Release("2.3.0", ("small 2.3.0", false), ("big 2.3.0", true)),
        Release("2.2.0", ("big 2.2.0", true)),
        Release("2.1.0", ("small 2.1.0", false)),
    ];

    /// <summary>Owner #149: three updates skipped: one sheet with all of them, every headline first, newest first.</summary>
    [Fact]
    public void SkippedUpdates_ShowEverythingMissed_HeadlinesFirst()
    {
        WhatsNewGate.MarkSeen(_data, new Version(2, 1, 0));
        var notes = WhatsNewGate.ToShow(_data, new Version(2, 3, 1, 0), History)!;
        Assert.Equal(new[] { "big 2.3.1", "big 2.3.0", "big 2.2.0", "small 2.3.1", "small 2.3.0" }, notes.Added.Select(i => i.Title));
        Assert.Equal(new[] { "fix 2.3.1", "fix 2.3.0", "fix 2.2.0" }, notes.Fixed);
        Assert.Equal("Version 2.3.1 · 3 updates", new WhatsNewViewModel(notes, () => { }).VersionLabel);
        WhatsNewGate.MarkSeen(_data, new Version(2, 3, 0));
        var one = WhatsNewGate.ToShow(_data, new Version(2, 3, 1), History)!;
        Assert.Equal(new[] { "big 2.3.1", "small 2.3.1" }, one.Added.Select(i => i.Title));
        Assert.Equal("Version 2.3.1", new WhatsNewViewModel(one, () => { }).VersionLabel);
    }

    [Fact]
    public void AProfileFromBeforeThePopUp_GetsEveryUpdate_UpToThisOne()
    {
        var notes = WhatsNewGate.ToShow(_data, new Version(2, 3, 0), History)!;
        Assert.Equal(3, notes.Updates);
        Assert.DoesNotContain(notes.Added, i => i.Title.EndsWith("2.3.1"));
    }

    [Fact]
    public void TheShippedHistory_IsNewestFirst_AndStartsWithThisBuild()
    {
        var releases = WhatsNewNotes.LoadReleases();
        Assert.True(releases.Count >= 2);
        Assert.Equal(WhatsNewGate.Short(Noctis.Services.UpdateService.CurrentVersion), WhatsNewGate.Short(releases[0].Version));
        for (int i = 1; i < releases.Count; i++)
            Assert.True(WhatsNewGate.Parse(releases[i - 1].Version) > WhatsNewGate.Parse(releases[i].Version), releases[i].Version);
        Assert.All(releases, r => Assert.NotEmpty(r.Added));
        Assert.Equal(releases.Count, WhatsNewNotes.Load()!.Updates); // Settings → About shows them all
    }

    [Fact]
    public void NotesForAnotherVersion_AreNeverShown()
        => Assert.Null(WhatsNewGate.ToShow(_data, new Version(2, 2, 1), Notes("2.2.0")));

    [AvaloniaFact]
    public void TheShippedNotes_Load_AndEveryIconExists()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var notes = WhatsNewNotes.Load();
        Assert.NotNull(notes);
        Assert.NotEmpty(notes!.Added);
        foreach (var item in notes.Added)
            Assert.True(IconKeyConverter.Instance.Convert(item.Icon, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture) is not null, item.Icon);
    }

    [AvaloniaFact]
    public void WordTimingSettingsShortcutClosesTheSheet_ThenOpensSettings()
    {
        var calls = new List<string>();
        var vm = new WhatsNewViewModel(Notes("2.2.0"), () => calls.Add("close"), () => calls.Add("settings"));
        Assert.True(vm.HasWordTimingSettings);
        vm.WordTimingSettingsCommand.Execute(null);
        Assert.Equal(new[] { "close", "settings" }, calls);
    }

    private sealed class NoOpHistory : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => [];
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
    [Fact]
    public void SettingLinksAreAllowlisted_AndCloseBeforeNavigation()
    {
        var notes = WhatsNewNotes.Load()!;
        var calls = new List<string>();
        var vm = new WhatsNewViewModel(notes, () => calls.Add("close"), openSetting: id => calls.Add(id));
        foreach (var feature in notes.Added.Where(i => i.HasSetting))
        {
            calls.Clear(); vm.OpenSettingCommand.Execute(feature.Setting);
            Assert.Equal(new[] { "close", feature.Setting! }, calls);
        }
        calls.Clear(); vm.OpenSettingCommand.Execute("https://example.invalid"); Assert.Empty(calls);
        Assert.All(notes.Added.Where(i => i.Setting is not null), i => Assert.True(i.HasSetting, i.Setting));
        Assert.NotNull(SettingShortcut.Resolve("about"));
        Assert.Equal("OfflineModeRow", SettingShortcut.Resolve("offline")!.Control);
        Assert.Equal("timePlayed", SettingShortcut.Resolve("wordTiming")!.Key);
        Assert.Equal("firstBot", SettingShortcut.Resolve("firstBot")!.Key);
    }

    [AvaloniaFact]
    public void AboutCanReopenNotes_AfterTheAutomaticGateWasDismissed()
    {
        using var data = new TestPersistenceService();
        var settings = new Noctis.ViewModels.SettingsViewModel(data, new FakeLibraryService(), new NoOpHistory());
        WhatsNewGate.MarkSeen(data.DataDirectory, new Version(2, 2, 0));
        var calls = 0; settings.OpenWhatsNewRequested += (_, _) => calls++;
        settings.OpenWhatsNewCommand.Execute(null); settings.OpenWhatsNewCommand.Execute(null);
        Assert.Equal(2, calls);
        settings.NavigateToSetting("offline");
        Assert.Equal(Noctis.ViewModels.SettingsViewModel.TabGeneral, settings.SelectedSettingsTab);
        Assert.Equal("OfflineModeRow", settings.PendingSettingShortcut!.Control);
        settings.NavigateToSetting("wordTiming");
        Assert.Equal(Noctis.ViewModels.SettingsViewModel.TabFeatures, settings.SelectedSettingsTab);
        Assert.Equal("dev.moshi.wordlyrics", settings.PendingSettingShortcut!.Feature);
    }

    [AvaloniaFact]
    public void NativeSettingsLinksReachTheExactControl_AndOfflineHasNoDivider()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        using var data = new TestPersistenceService();
        var settings = new Noctis.ViewModels.SettingsViewModel(data, new FakeLibraryService(), new NoOpHistory());
        settings.NavigateToSetting("offline");
        var view = new Views.SettingsView { DataContext = settings };
        var window = new Window { Width = 1100, Height = 820, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Null(settings.PendingSettingShortcut);
            Assert.Equal(new Avalonia.Thickness(0), view.FindControl<Border>("OfflineModeRow")!.BorderThickness);
            settings.NavigateToSetting("wordTiming");
            var feature = new Noctis.Services.Plugins.LoadedPlugin("dev.moshi.wordlyrics", null) { IsBundled = true };
            feature.SettingItems.Add(new(new("timePlayed", "Time every song you play", Noctis.Services.Plugins.PluginSettingType.Bool, "true", [], null, null, null), "true", _ => { }));
            settings.BuiltInFeatures.Add(feature);
            for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Assert.Null(settings.PendingSettingShortcut);
            Assert.Equal(Noctis.ViewModels.SettingsViewModel.TabFeatures, settings.SelectedSettingsTab);
            settings.NavigateToSetting("about");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Null(settings.PendingSettingShortcut);
            Assert.True(view.FindControl<Button>("WhatsNewButton")!.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ThePanel_ShowsEveryFeature_AndGotItCloses()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var closed = false; var settings = false;
        var notes = WhatsNewNotes.Load()!;
        var panel = new Views.WhatsNewPanel { DataContext = new WhatsNewViewModel(notes, () => closed = true, openSetting: _ => settings = true) };
        var window = new Window { Width = 900, Height = 900, Content = panel };
        window.Show();
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        var rows = panel.GetVisualDescendants().OfType<StackPanel>().Count(p => p.Classes.Contains("feature-row"));
        Assert.Equal(notes.Added.Count, rows);
        var shortcut = panel.GetVisualDescendants().OfType<Button>().Single(b => b.CommandParameter as string == notes.Added.First(i => i.HasSetting).Setting);
        Assert.True(shortcut.IsVisible);
        Assert.True(shortcut.IsEnabled);
        shortcut.Command!.Execute(shortcut.CommandParameter);
        Assert.True(settings);
        Assert.True(closed);
        closed = false;
        var gotIt = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Got it");
        gotIt.Command!.Execute(null);
        Assert.True(closed);
        window.Close();
    }
}
