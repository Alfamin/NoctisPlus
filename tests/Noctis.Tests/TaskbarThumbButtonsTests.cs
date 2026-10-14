using Noctis.Localization;
using Noctis.Services;
using Xunit;
using Glyph = Noctis.Services.TaskbarIntegrationService.ThumbGlyph;

namespace Noctis.Tests;

/// <summary>
/// The Previous / Play-Pause / Forward / Favorite buttons under the Windows taskbar thumbnail.
/// The COM calls and what Explorer paints are not visible headlessly; this pins what each
/// button is told to show.
/// </summary>
public class TaskbarThumbButtonsTests
{
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
}
