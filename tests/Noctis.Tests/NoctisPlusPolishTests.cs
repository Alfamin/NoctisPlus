using Noctis.Features.WordLyrics;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>The owner's 2026-10-18 consistency sweep: settings that open something are buttons, word timing speaks in
/// a few words, and pages opened from Search are detail pages without a "Search" title.</summary>
public class NoctisPlusPolishTests
{
    [Fact]
    public void ActionSetting_IsAButton_AndEveryPressIsOneChange()
    {
        var manifest = PluginManifest.Parse(PluginSandbox.Manifest(settingsJson:
            """[{ "key": "open", "label": "Open something…", "type": "action" }]"""));
        var definition = Assert.Single(manifest.Settings);
        Assert.Equal(PluginSettingType.Action, definition.Type);
        var changes = new List<string>();
        var item = new PluginSettingItem(definition, definition.Default, i => changes.Add(i.Value));
        Assert.True(item.IsAction);
        Assert.False(item.IsBool);
        item.PressCommand.Execute(null);
        item.PressCommand.Execute(null);
        Assert.Equal(["true", "false"], changes); // the feature hears both presses
    }

    [Fact]
    public void WordTimingPill_SaysItInAFewWords()
    {
        var working = TimingPill.From(new ProgressState("Timing words", "Artist · Song", 1, 5, .3))!;
        Assert.Equal("Timing words · 2 of 5", working.Text);
        Assert.True(working.IsDeterminate);
        Assert.Equal(.3, working.Percent, 3);

        Assert.Equal("Word timing paused", TimingPill.From(new("Paused", "", 1, 5, Paused: true))!.Text);
        Assert.Equal("WordLyrics needs setup", TimingPill.From(new("Setup is needed", "long reason", Error: true, Finished: true))!.Text);
        var done = TimingPill.From(new("3 songs received word timestamps", "details", 3, 3, Finished: true))!;
        Assert.Equal("Words timed · 3 songs", done.Text);
        Assert.True(done.IsDone && done.Lingers);
        Assert.Null(TimingPill.From(new("Ready", "", Finished: true)));
        Assert.All(new[] { working, done }, p => Assert.True(p.Text.Length <= 32, p.Text));
    }

    [Fact]
    public void PagesOpenedFromSearch_HaveNoSearchTitle_WhileSearchKeepsIt()
    {
        var bar = new TopBarViewModel { CurrentTabName = "Search", IsSearchPageShown = true, IsBackButtonVisible = true };
        Assert.True(bar.IsPageTitleVisible); // Search itself after Back: title and tabs stay above results
        bar.IsSearchPageShown = false;       // an artist or album page opened from Search
        Assert.False(bar.IsPageTitleVisible);
    }
}
