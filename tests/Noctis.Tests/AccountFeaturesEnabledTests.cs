using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The account and sync cards are live (<see cref="AccountFeatures"/> on, owner 10-11, to test
/// desktop-to-phone sync): the stored SyncEnabled flag is honoured, the toggle turns sync on and
/// saves it, and Create account makes the primary account in the server's user store.
/// </summary>
public class AccountFeaturesEnabledTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void Switch_IsOn() => Assert.True(AccountFeatures.Enabled);

    [AvaloniaFact]
    public async Task StoredSyncFlag_IsHonoured_AndTheToggleSavesIt()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { SyncEnabled = true });

        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        Assert.True(vm.AccountFeaturesEnabled);
        Assert.True(vm.SyncEnabled);

        vm.SyncEnabled = false;
        await vm.SaveAsync();
        Assert.False((await persistence.LoadSettingsAsync()).SyncEnabled);

        vm.SyncEnabled = true;
        await vm.SaveAsync();
        Assert.True((await persistence.LoadSettingsAsync()).SyncEnabled);
    }

    [AvaloniaFact]
    public async Task CreateAccount_MakesThePrimaryAccount()
    {
        var persistence = new PersistenceService(_root);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();

        Assert.True(vm.CreatePrimaryAccountCommand.CanExecute(null));
        vm.NewServerUserName = "me@example.com";
        vm.NewServerUserPassword = "correct horse";
        vm.CreatePrimaryAccountCommand.Execute(null);

        Assert.True(vm.HasPrimaryAccount);
        var user = Assert.Single(new ServerUserStore(Path.Combine(persistence.DataDirectory, "server", "users.db")).List());
        Assert.Equal("me@example.com", user.Name);
    }
}
