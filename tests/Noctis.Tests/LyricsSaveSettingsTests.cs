using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #135: Settings ▸ Lyrics ▸ Saving lyrics — "Save lyrics to", "Automatically download
/// lyrics" and "Preferred lyrics type" save, reload, reach the player (which the lyrics page
/// reads), and reset to the behaviour from before the settings existed.
/// </summary>
public class LyricsSaveSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "settings135-" + Guid.NewGuid().ToString("N"));

    public LyricsSaveSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SettingsViewModel NewVm() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    [AvaloniaFact]
    public async Task Choices_SurviveSaveAndReload_ReachThePlayer_AndResetPutsThemBack()
    {
        var vm = NewVm();
        await vm.LoadAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SetPlayer(player);

        Assert.Equal("Sidecar", vm.SelectedLyricsSaveTargetOption?.Value);
        Assert.False(vm.LyricsAutoDownload);
        Assert.Equal("WordSynced", vm.SelectedLyricsPreferredTypeOption?.Value);
        Assert.Equal(new[] { "Sidecar", "Embedded", "Both" }, vm.LyricsSaveTargetOptions.Select(o => o.Value));
        Assert.Equal(new[] { Loc.T("Settings.LyricsSaveTarget.Sidecar"), Loc.T("Settings.LyricsSaveTarget.Embedded"), Loc.T("Settings.LyricsSaveTarget.Both") },
            vm.LyricsSaveTargetOptions.Select(o => o.Label));
        Assert.Equal(new[] { "WordSynced", "LineSynced", "Plain" }, vm.LyricsPreferredTypeOptions.Select(o => o.Value));

        vm.SelectedLyricsSaveTargetOption = vm.LyricsSaveTargetOptions.Single(o => o.Value == "Embedded");
        vm.LyricsAutoDownload = true;
        vm.SelectedLyricsPreferredTypeOption = vm.LyricsPreferredTypeOptions.Single(o => o.Value == "LineSynced");

        Assert.Equal("Embedded", player.LyricsSaveTarget);
        Assert.True(player.LyricsAutoDownload);
        Assert.Equal("LineSynced", player.LyricsPreferredType);
        await vm.SaveAsync();

        var reloaded = NewVm();
        await reloaded.LoadAsync();
        Assert.Equal("Embedded", reloaded.SelectedLyricsSaveTargetOption?.Value);
        Assert.True(reloaded.LyricsAutoDownload);
        Assert.Equal("LineSynced", reloaded.SelectedLyricsPreferredTypeOption?.Value);
        Assert.Equal(LyricsSaveTarget.Embedded, LyricsSaveOptions.ParseTarget(reloaded.GetSettings().LyricsSaveTarget));

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.Equal("Sidecar", reloaded.SelectedLyricsSaveTargetOption?.Value);
        Assert.False(reloaded.LyricsAutoDownload);
        Assert.Equal("WordSynced", reloaded.SelectedLyricsPreferredTypeOption?.Value);
    }

    [AvaloniaFact]
    public async Task HandEditedUnknownValue_ReadsAsTheDefault()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { LyricsSaveTarget = "Cloud", LyricsPreferredType = "Karaoke" });

        var vm = NewVm();
        await vm.LoadAsync();

        Assert.Equal("Sidecar", vm.SelectedLyricsSaveTargetOption?.Value);
        Assert.Equal("WordSynced", vm.SelectedLyricsPreferredTypeOption?.Value);
    }
}
