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
        Assert.NotNull(WhatsNewGate.ToShow(_data, new Version(2, 2, 0, 0), existingProfile: true, Notes("2.2.0")));
        WhatsNewGate.MarkSeen(_data, new Version(2, 2, 0, 0));
        Assert.Null(WhatsNewGate.ToShow(_data, new Version(2, 2, 0, 0), existingProfile: true, Notes("2.2.0")));
    }

    [Fact]
    public void AFreshInstall_ShowsNothing_ButTheNextUpdateDoes()
    {
        Assert.Null(WhatsNewGate.ToShow(_data, new Version(2, 2, 0), existingProfile: false, Notes("2.2.0")));
        Assert.NotNull(WhatsNewGate.ToShow(_data, new Version(2, 3, 0), existingProfile: true, Notes("2.3.0")));
    }

    [Fact]
    public void AnExistingProfileFromBeforeThePopUp_SeesIt()
        => Assert.NotNull(WhatsNewGate.ToShow(_data, new Version(2, 2, 0), existingProfile: true, Notes("2.2.0")));

    [Fact]
    public void NotesForAnotherVersion_AreNeverShown()
        => Assert.Null(WhatsNewGate.ToShow(_data, new Version(2, 2, 1), existingProfile: true, Notes("2.2.0")));

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

    [AvaloniaFact]
    public void ThePanel_ShowsEveryFeature_AndGotItCloses()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var closed = false; var settings = false;
        var notes = WhatsNewNotes.Load()!;
        var panel = new Views.WhatsNewPanel { DataContext = new WhatsNewViewModel(notes, () => closed = true, () => settings = true) };
        var window = new Window { Width = 900, Height = 900, Content = panel };
        window.Show();
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        var rows = panel.GetVisualDescendants().OfType<StackPanel>().Count(p => p.Classes.Contains("feature-row"));
        Assert.Equal(notes.Added.Count, rows);
        var shortcut = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Word timing settings");
        Assert.True(shortcut.IsVisible);
        Assert.Contains("pill-secondary", shortcut.Classes);
        shortcut.Command!.Execute(null);
        Assert.True(settings);
        Assert.True(closed);
        closed = false;
        var gotIt = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Got it");
        gotIt.Command!.Execute(null);
        Assert.True(closed);
        window.Close();
    }
}
