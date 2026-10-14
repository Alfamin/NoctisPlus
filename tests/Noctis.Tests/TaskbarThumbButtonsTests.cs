using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;
using Glyph = Noctis.Services.TaskbarIntegrationService.ThumbGlyph;

namespace Noctis.Tests;

/// <summary>
/// The Previous / Play-Pause / Forward / Favorite buttons under the Windows taskbar thumbnail.
/// The COM calls and what Explorer paints are not visible headlessly; this pins what each
/// button is told to show, and the Settings toggle that hides them (Discord request: the
/// user looked for them in Settings).
/// </summary>
public class TaskbarThumbButtonsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "taskbar-" + Guid.NewGuid().ToString("N"));

    public TaskbarThumbButtonsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SettingsViewModel NewVm() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    [Fact]
    public void Buttons_AreInPlaybackBarOrder_WithDistinctIds()
    {
        var buttons = TaskbarIntegrationService.DescribeButtons(isPlaying: false, isFavorite: false);
        Assert.Equal(new[] { Glyph.Previous, Glyph.Play, Glyph.Next, Glyph.Heart }, buttons.Select(b => b.Glyph));
        Assert.Equal(buttons.Length, buttons.Select(b => b.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Tooltips_AreLocalizationKeys_NotHardCodedEnglish(bool isPlaying, bool isFavorite)
    {
        // They used to be English literals ("Previous", "Forward", "Unfavorite") whatever
        // language the app was in. Every key must exist, or the tooltip shows the key.
        foreach (var b in TaskbarIntegrationService.DescribeButtons(isPlaying, isFavorite))
            Assert.NotNull(Loc.English(b.TipKey));
    }

    [Fact]
    public void PlayButton_FollowsThePlaybackState()
    {
        var paused = TaskbarIntegrationService.DescribeButtons(isPlaying: false, isFavorite: false)[1];
        var playing = TaskbarIntegrationService.DescribeButtons(isPlaying: true, isFavorite: false)[1];
        Assert.Equal(paused.Id, playing.Id);
        Assert.Equal((Glyph.Play, "PlaybackBar.Play"), (paused.Glyph, paused.TipKey));
        Assert.Equal((Glyph.Pause, "PlaybackBar.Pause"), (playing.Glyph, playing.TipKey));
    }

    [Fact]
    public void FavoriteButton_FollowsTheCurrentTrack()
    {
        var off = TaskbarIntegrationService.DescribeButtons(isPlaying: false, isFavorite: false)[3];
        var on = TaskbarIntegrationService.DescribeButtons(isPlaying: false, isFavorite: true)[3];
        Assert.Equal(off.Id, on.Id);
        Assert.Equal((Glyph.Heart, "PlaybackBar.FavoriteTip"), (off.Glyph, off.TipKey));
        Assert.Equal((Glyph.HeartFilled, "PlaybackBar.RemoveFromFavorites"), (on.Glyph, on.TipKey));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void SwitchedOff_EveryButtonIsHidden_SoSingleUpdatesCannotBringOneBack(bool isPlaying, bool isFavorite)
    {
        // Thumbnail buttons can't be removed once added, only hidden. Play/pause and the
        // heart are later updated one at a time, and THB_FLAGS replaces the flags, so each
        // description must carry the hidden flag itself.
        Assert.All(TaskbarIntegrationService.DescribeButtons(isPlaying, isFavorite, visible: false),
            b => Assert.Equal(TaskbarIntegrationService.HiddenFlag, b.Flags & TaskbarIntegrationService.HiddenFlag));
        Assert.All(TaskbarIntegrationService.DescribeButtons(isPlaying, isFavorite, visible: true),
            b => Assert.Equal(0u, b.Flags & TaskbarIntegrationService.HiddenFlag));
    }

    [Fact]
    public void FreshInstall_HasTheButtonsOn()
        => Assert.True(new AppSettings().TaskbarMediaControlsEnabled);

    [AvaloniaFact]
    public async Task Toggle_SurvivesSaveAndReload_AndResetTurnsItBackOn()
    {
        var vm = NewVm();
        await vm.LoadAsync();
        Assert.True(vm.TaskbarMediaControlsEnabled);

        vm.TaskbarMediaControlsEnabled = false;
        await vm.SaveAsync();

        var reloaded = NewVm();
        await reloaded.LoadAsync();
        Assert.False(reloaded.TaskbarMediaControlsEnabled);
        Assert.False(reloaded.GetSettings().TaskbarMediaControlsEnabled);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.True(reloaded.TaskbarMediaControlsEnabled);
    }

    [AvaloniaFact]
    public async Task SettingsFromBeforeTheToggle_KeepTheButtonsOn()
    {
        // Every existing settings.json lacks the key; those users had the buttons, and an
        // update must not take them away.
        await new PersistenceService(_root).SaveSettingsAsync(new AppSettings());
        var path = Path.Combine(_root, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(json.Remove("taskbarMediaControlsEnabled"), "the key is written camelCase");
        File.WriteAllText(path, json.ToJsonString());

        var vm = NewVm();
        await vm.LoadAsync();
        Assert.True(vm.TaskbarMediaControlsEnabled);
    }
}
