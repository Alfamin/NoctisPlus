using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-18: the Noctis Plus defaults (shuffle + skip buttons, Liquid Glass, Dark) are applied to an
/// existing profile ONCE. The level used to be pulled back from disk on every save, so "once" ran on
/// every launch and undid the user's later choices.
/// </summary>
public class PlusDefaultsOnceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private async Task<SettingsViewModel> Launch(PersistenceService persistence)
    {
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        return vm;
    }

    [AvaloniaFact]
    public async Task ExistingProfile_GetsThePlusDefaultsOnce_ThenKeepsTheUsersChoices()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings
        {
            Theme = "Gray", LiquidGlassEnabled = false, PlaybackBarShowShuffle = false, PlusDefaultsLevel = 0, LrcRedEnabled = false,
        });

        var first = await Launch(persistence);
        Assert.True(first.IsDarkTheme);          // "full default at dark"
        Assert.True(first.LiquidGlassEnabled);
        Assert.True(first.LrcRedEnabled);        // #200: lrc.red on
        first.SetGrayThemeCommand.Execute(null); // the user changes their mind afterwards
        first.LiquidGlassEnabled = false;
        first.LrcRedEnabled = false;
        await first.SaveAsync();
        Assert.Equal(3, (await persistence.LoadSettingsAsync()).PlusDefaultsLevel);

        var second = await Launch(persistence);
        Assert.True(second.IsGrayTheme);
        Assert.False(second.LiquidGlassEnabled);
        Assert.False(second.LrcRedEnabled);
    }

    [AvaloniaFact]
    public async Task ALevel2Profile_GetsOnlyLrcRed_ItsThemeStays()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { Theme = "Gray", PlusDefaultsLevel = 2, LrcRedEnabled = false, LiquidGlassEnabled = false });
        var vm = await Launch(persistence);
        Assert.True(vm.IsGrayTheme);
        Assert.False(vm.LiquidGlassEnabled);
        Assert.True(vm.LrcRedEnabled);
    }
}
