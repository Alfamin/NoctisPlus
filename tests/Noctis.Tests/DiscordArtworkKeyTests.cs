using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DiscordArtworkKeyTests
{
    [Fact]
    public void LargeImageText_ShowsAlbumByDefault_AndDropsItWhenOff()
    {
        Assert.Equal("Memoirs of an Imperfect Angel",
            DiscordPresenceService.BuildLargeImageText("https://relay/x", "Memoirs of an Imperfect Angel", "Mariah Carey", showAlbum: true));
        // Falls back to the artist when the file has no album, so the hover text isn't blank.
        Assert.Equal("Mariah Carey",
            DiscordPresenceService.BuildLargeImageText("https://relay/x", "", "Mariah Carey", showAlbum: true));
        // "Show album" off removes the line entirely rather than repeating the artist.
        Assert.Null(DiscordPresenceService.BuildLargeImageText("https://relay/x", "Memoirs of an Imperfect Angel", "Mariah Carey", showAlbum: false));
        // Text on a missing image is never sent.
        Assert.Null(DiscordPresenceService.BuildLargeImageText(null, "Album", "Artist", showAlbum: true));
    }

    [Fact]
    public void PrefersFreshUrlWhenAvailable()
    {
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: "https://relay/new", identity: "a", lastKey: "https://relay/old", lastIdentity: "a");
        Assert.Equal("https://relay/new", key);
    }

    [Fact]
    public void ReusesLastKeyWhenUrlMissingForSameTrack()
    {
        // Relay transiently down (null URL) but Discord already shows the cached cover —
        // keep the same key instead of flipping to the app icon.
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: null, identity: "a", lastKey: "https://relay/old", lastIdentity: "a");
        Assert.Equal("https://relay/old", key);
    }

    [Fact]
    public void OmitsImageWhenUrlMissingForDifferentTrack()
    {
        // New track with no art must not inherit the previous track's cover. It also must
        // not name an asset the application doesn't have (it has none) — that rendered a
        // broken-image placeholder instead of no image.
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: null, identity: "b", lastKey: "https://relay/old", lastIdentity: "a");
        Assert.Null(key);
    }

    [Fact]
    public void OmitsImageWhenNoPriorArt()
    {
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: "   ", identity: "a", lastKey: null, lastIdentity: null);
        Assert.Null(key);
    }

    [Fact]
    public void Truncate_NeverSplitsAnEmojiInHalf()
    {
        // A 130-char title whose emoji straddles the 125-char cut: the old cut kept only the
        // emoji's high surrogate, which UTF-8 can't encode and Discord shows as U+FFFD.
        var title = new string('a', 124) + "\U0001F600" + "tail";
        var cut = DiscordPresenceService.Truncate(title, 128);

        Assert.True(cut.Length <= 128);
        Assert.EndsWith("...", cut);
        Assert.Equal(new string('a', 124) + "...", cut);
        Assert.Equal(cut, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(cut)));

        // Emoji wholly inside the kept part survive; short titles are untouched.
        var fits = new string('a', 123) + "\U0001F600" + "tail";
        Assert.Equal(new string('a', 123) + "\U0001F600...", DiscordPresenceService.Truncate(fits, 128));
        Assert.Equal("\U0001F600 Song", DiscordPresenceService.Truncate("\U0001F600 Song", 128));
    }
}
