using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A scan Settings didn't start (Scan on Startup, the folder watcher) still reports its
/// progress to Settings' status line, but only RunScanCoreAsync ever replaced that text,
/// so "Scanning Library 7,200" stayed beside the Scan Library button for the rest of the
/// session after the startup scan had long finished (seen live 10-12).
/// </summary>
public class SettingsScanProgressStatusTests
{
    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task StartupScanProgress_ClearsOnceTheLibraryStopsScanning()
    {
        using var persistence = new TestPersistenceService();
        var lib = new FakeLibraryService { IsPublishingPartial = true };
        var vm = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());

        lib.RaiseScanProgress(7200);
        await PumpUntil(() => vm.ScanStatusText.Length > 0);
        Assert.Equal("Scanning Library 7,200", vm.ScanStatusText);

        // Still scanning: the count stays up.
        await PumpUntil(() => false, budgetMs: 1500);
        Assert.Equal("Scanning Library 7,200", vm.ScanStatusText);

        // The startup scan ends without Settings hearing about it.
        lib.IsPublishingPartial = false;
        await PumpUntil(() => vm.ScanStatusText.Length == 0, budgetMs: 4000);
        Assert.Equal("", vm.ScanStatusText);
    }

    [AvaloniaFact]
    public async Task SettingsOwnScan_StillEndsWithItsTracksFoundLine()
    {
        using var persistence = new TestPersistenceService();
        var lib = new FakeLibraryService();
        var vm = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());

        await vm.RescanCommand.ExecuteAsync(null);
        await PumpUntil(() => false, budgetMs: 200);

        Assert.Equal("No tracks found.", vm.ScanStatusText);
    }
}
