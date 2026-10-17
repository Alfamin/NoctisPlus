using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Settings rail after the 2026-09 revamp: 14 pages in 5 groups (Noctis Plus adds
/// Features), every page with an icon.</summary>
public class SettingsRailTests
{
    private static SettingsViewModel NewVm()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        return new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
    }

    [Fact]
    public void Rail_HasFourteenPagesInFiveGroups_InOrder()
    {
        var vm = NewVm();

        Assert.Equal(new[] { "App", "Playback", "Library", "Connect", "More" }, vm.SectionGroups.Select(g => g.Name));
        Assert.Equal(
            new[] { "General", "Appearance", "Player", "Lyrics", "Features", "Shortcuts", "Audio", "Library", "Advanced", "Account & Devices", "Integrations", "Plugins", "Statistics", "About" },
            vm.Sections.Select(s => s.Key));
        Assert.Equal(vm.Sections.Count, vm.SectionGroups.Sum(g => g.Sections.Count));
        Assert.All(vm.Sections, s => Assert.False(string.IsNullOrEmpty(s.IconKey)));
        Assert.All(vm.SectionGroups, g => Assert.All(g.Sections, s => Assert.Equal(g.Name, s.Group)));
    }

    [Fact]
    public void SelectingATab_FlipsVisibility_AndDescription()
    {
        var vm = NewVm();

        vm.SelectedSettingsTab = SettingsViewModel.TabPlayer;
        Assert.True(vm.IsPlayerTabVisible);
        Assert.False(vm.IsGeneralTabVisible);
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedTabDescription));

        vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
        Assert.True(vm.IsAccountDevicesTabVisible);
        Assert.False(vm.IsPlayerTabVisible);
        Assert.Single(vm.Sections, s => s.IsSelected);
        Assert.Equal(SettingsViewModel.TabAccountDevices, vm.Sections.Single(s => s.IsSelected).Key);
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
