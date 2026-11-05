using Avalonia.Headless.XUnit;
using Avalonia.Controls.Primitives;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class MobileOfflineTests
{
    [AvaloniaFact]
    public async Task NativeToggle_PersistsWithoutStoppingLocalPlayback_AndRestoresTheGate()
    {
        var network = new OnlineAccess();
        var song = MobileFixtures.Song("Local song");
        using var rig = MobileFixtures.MakeRig(tracks: [song], onlineAccess: network);
        rig.Shell.Player.PlayTracks([song], 0);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var settings = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
            await settings.Loaded;
            window.UpdateLayout();
            var toggle = MobileFixtures.Named<ToggleButton>(MobileFixtures.Find<SettingsPage>(view), "OfflineModeSwitch");
            toggle.IsChecked = true;
            await settings.PendingSave;
            Assert.True(network.IsOffline);
            Assert.True(rig.Shell.Player.IsPlaying);
            Assert.Same(song, rig.Shell.Player.CurrentTrack);
            Assert.True((await rig.Persistence.LoadSettingsAsync()).OfflineMode);

            network.SetOffline(false);
            var reopened = new SettingsPageViewModel(rig.Shell);
            await reopened.LoadAsync();
            Assert.True(reopened.OfflineMode);
            Assert.True(network.IsOffline);
            reopened.OfflineMode = false;
            await reopened.PendingSave;
            Assert.False(network.IsOffline);
            Assert.False((await rig.Persistence.LoadSettingsAsync()).OfflineMode);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task EarlyOfflineChoice_IsKept_WithoutRewritingThemeOrLogin()
    {
        var network = new OnlineAccess();
        using var rig = MobileFixtures.MakeRig(onlineAccess: network, seed: p => p.UpdateSettingsAsync(s =>
        {
            s.Theme = "Gray";
            s.LastFmSessionKey = "synthetic-session";
            s.LastFmScrobblingEnabled = true;
        }));
        var settings = new SettingsPageViewModel(rig.Shell) { OfflineMode = true };
        await settings.LoadAsync();
        await settings.PendingSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.True(settings.OfflineMode);
        Assert.True(network.IsOffline);
        Assert.True(saved.OfflineMode);
        Assert.Equal("Gray", saved.Theme);
        Assert.Equal("synthetic-session", saved.LastFmSessionKey);
        Assert.True(saved.LastFmScrobblingEnabled);
    }

    [Fact]
    public async Task IntentionalOffline_KeepsCachedLyrics_AndDoesNotCallProviderForAMiss()
    {
        using var rig = MobileFixtures.MakeRig();
        var network = new OnlineAccess(); network.SetOffline(true);
        var provider = new EmptyLyrics();
        var source = new LrcLibOnlineLyricsSource(provider, rig.Root, access: network);
        var song = MobileFixtures.Song("Uncached");
        Assert.Null(await source.FindAsync(song, default));
        Assert.Equal(0, provider.Calls);
        var cache = Path.Combine(rig.Root, song.Id.ToString("N") + ".lrc");
        await File.WriteAllTextAsync(cache, "[00:01.00]Saved words");
        Assert.Equal("[00:01.00]Saved words", (await source.FindAsync(song, default))?.Text);
        Assert.Equal(0, provider.Calls);
        File.Delete(cache);
        network.SetOffline(false);
        Assert.Null(await source.FindAsync(song, default));
        Assert.True(provider.Calls > 0);
    }

    private sealed class EmptyLyrics : ILrcLibService
    {
        public int Calls;
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        { Calls++; return Task.FromResult<LrcLibResult?>(null); }
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new List<LrcLibResult>()); }
    }
}
