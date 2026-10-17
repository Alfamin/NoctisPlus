using System.Reflection;
using Avalonia.Headless.XUnit;
using DiscordRPC;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord ask (2026-10-14): "switch the Discord presence from showing the artist to the
/// title". Settings ▸ Integrations ▸ Status on Discord picks what the one-line status reads
/// after "Listening to": the artist (default, as before), the song title or the app name.
/// </summary>
public class DiscordStatusDisplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "discord-status-" + Guid.NewGuid().ToString("N"));

    public DiscordStatusDisplayTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Connected presence service that records what it was asked to publish.</summary>
    private sealed class RecordingDiscord : IDiscordPresenceService
    {
        public List<DiscordPresenceTrack> Updates { get; } = new();
        public bool IsConnected => true;
        public Func<bool>? IsEnabled { get; set; }
        public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task UpdateAsync(DiscordPresenceTrack track, TimeSpan position, TimeSpan? duration, bool isPlaying)
        {
            Updates.Add(track);
            return Task.CompletedTask;
        }
        public Task ClearAsync() => Task.CompletedTask;
        public void InvalidateArtworkCache() { }
        public void Dispose() { }
    }

    private SettingsViewModel NewVm() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    [Theory]
    [InlineData(DiscordStatusDisplay.Artist, StatusDisplayType.State)]
    [InlineData(DiscordStatusDisplay.Title, StatusDisplayType.Details)]
    [InlineData(DiscordStatusDisplay.AppName, StatusDisplayType.Name)]
    public void EachChoice_MapsToTheFieldThatCarriesIt(DiscordStatusDisplay choice, StatusDisplayType expected)
    {
        // Details carries the title and State the artist (DiscordPresenceService.UpdateAsync).
        Assert.Equal(expected, DiscordPresenceService.MapStatusDisplay(choice));
    }

    [Fact]
    public void Default_IsTheArtist_LikeBeforeTheSettingExisted()
    {
        Assert.Equal(DiscordStatusDisplay.Artist, new DiscordPresenceTrack("Song", "Artist", null).StatusDisplay);
        Assert.Equal(DiscordStatusDisplay.Artist, DiscordPresenceOptions.ParseStatusDisplay(new AppSettings().DiscordStatusDisplay));
        Assert.Equal(DiscordStatusDisplay.Artist, DiscordPresenceOptions.ParseStatusDisplay(null));
        Assert.Equal(DiscordStatusDisplay.Artist, DiscordPresenceOptions.ParseStatusDisplay("Karaoke"));
        Assert.Equal(DiscordStatusDisplay.Artist, DiscordPresenceOptions.ParseStatusDisplay("7"));
        Assert.Equal(DiscordStatusDisplay.Title, DiscordPresenceOptions.ParseStatusDisplay("title"));
    }

    /// <summary>
    /// DiscordRpcClient.SetPresence skips a presence that Matches the last one sent
    /// (SkipIdenticalPresence), and Matches never reads StatusDisplay. A paused song has no
    /// timestamps to differ, so changing only the status line was dropped until the next
    /// song. The service turns the skip off for exactly that case.
    /// </summary>
    [Fact]
    public void LibrarySkip_IgnoresTheStatusLine_SoTheServiceForcesThatSend()
    {
        var sent = new RichPresence { Type = ActivityType.Listening, Details = "Song", State = "Artist", StatusDisplay = StatusDisplayType.State };
        var next = new RichPresence { Type = ActivityType.Listening, Details = "Song", State = "Artist", StatusDisplay = StatusDisplayType.Details };

        var matches = typeof(RichPresence).GetMethod("Matches", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(RichPresence) }, null);
        Assert.NotNull(matches);
        Assert.True((bool)matches!.Invoke(sent, new object[] { next })!,
            "DiscordRPC now compares StatusDisplay; the forced send in DiscordPresenceService.UpdateAsync can go");

        Assert.True(DiscordPresenceService.StatusDisplayChanged(sent, next));
        Assert.False(DiscordPresenceService.StatusDisplayChanged(sent, sent));
        Assert.False(DiscordPresenceService.StatusDisplayChanged(null, next));
    }

    [AvaloniaFact]
    public async Task Choice_SurvivesSaveAndReload_AndResetPutsTheArtistBack()
    {
        var vm = NewVm();
        await vm.LoadAsync();

        Assert.Equal("Artist", vm.SelectedDiscordStatusDisplayOption?.Value);
        Assert.Equal(new[] { "Artist", "Title", "AppName" }, vm.DiscordStatusDisplayOptions.Select(o => o.Value));
        Assert.Equal(new[] { Loc.T("Settings.DiscordStatusDisplay.Artist"), Loc.T("Settings.DiscordStatusDisplay.Title"), Loc.T("Settings.DiscordStatusDisplay.AppName") },
            vm.DiscordStatusDisplayOptions.Select(o => o.Label));

        vm.SelectedDiscordStatusDisplayOption = vm.DiscordStatusDisplayOptions.Single(o => o.Value == "Title");
        await vm.SaveAsync();

        var reloaded = NewVm();
        await reloaded.LoadAsync();
        Assert.Equal("Title", reloaded.SelectedDiscordStatusDisplayOption?.Value);
        Assert.Equal("Title", reloaded.GetSettings().DiscordStatusDisplay);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.Equal("Artist", reloaded.SelectedDiscordStatusDisplayOption?.Value);
    }

    [AvaloniaFact]
    public async Task HandEditedUnknownValue_ReadsAsTheArtist()
    {
        await new PersistenceService(_root).SaveSettingsAsync(new AppSettings { DiscordStatusDisplay = "Album" });

        var vm = NewVm();
        await vm.LoadAsync();

        Assert.Equal("Artist", vm.SelectedDiscordStatusDisplayOption?.Value);
    }

    [AvaloniaFact]
    public async Task ChangingTheChoice_RepublishesTheCurrentSongAtOnce()
    {
        var vm = NewVm();
        await vm.LoadAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SetPlayer(player);
        var discord = new RecordingDiscord();
        vm.SetDiscordPresence(discord);

        var song = new Track { Title = "Never Gonna Give You Up", Artist = "Rick Astley", FilePath = "a.flac" };
        player.ReplaceQueueAndPlay(new List<Track> { song }, 0);
        Assert.Equal(PlaybackState.Playing, player.State);

        vm.SelectedDiscordStatusDisplayOption = vm.DiscordStatusDisplayOptions.Single(o => o.Value == "Title");
        var update = Assert.Single(discord.Updates);
        Assert.Equal(DiscordStatusDisplay.Title, update.StatusDisplay);
        Assert.Equal("Never Gonna Give You Up", update.Title);

        vm.SelectedDiscordStatusDisplayOption = vm.DiscordStatusDisplayOptions.Single(o => o.Value == "AppName");
        Assert.Equal(DiscordStatusDisplay.AppName, discord.Updates[^1].StatusDisplay);

        // The album toggle's re-publish carries the status choice along instead of resetting it.
        vm.DiscordShowAlbum = false;
        Assert.Equal(DiscordStatusDisplay.AppName, discord.Updates[^1].StatusDisplay);
        Assert.False(discord.Updates[^1].ShowAlbum);
    }
}
