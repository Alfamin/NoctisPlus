using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Noctis.Helpers;

namespace Noctis.Mobile.Services;

/// <summary>
/// The phone's theme: an appearance (System / Dark / Light) picks between the base Light
/// dictionary — the desktop's "Light" theme — and one of the dark overlays, merged on top of
/// the base styles as the desktop App.SetThemeCore does; the accent overlay goes last.
/// </summary>
public sealed class MobileTheme
{
    public static IReadOnlyList<string> Appearances { get; } = new[] { "System", "Dark", "Light" };
    public static IReadOnlyList<string> DarkThemes { get; } = new[] { "Ink", "Smoke", "Dark", "Midnight" };
    public const string DefaultAccent = "#E74856";

    private ResourceInclude? _overlay;
    private ResourceDictionary? _lightText;
    private ResourceDictionary? _accent;
    private ResourceDictionary? _palette;

    /// <summary>
    /// The phone's own surface palette (iOS-style grouped pages, cards, hairlines, floating
    /// chrome), keyed per variant so the always-dark scopes (Now Playing, Lyrics) pick the Dark set.
    /// </summary>
    public static ResourceDictionary BuildPalette()
    {
        static SolidColorBrush B(string hex) => new(Color.Parse(hex));
        var palette = new ResourceDictionary();
        palette.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        {
            ["PhoneBackgroundBrush"] = B("#FFFFFF"),
            ["PhoneGroupedBackgroundBrush"] = B("#F2F2F7"),
            ["PhoneCardBrush"] = B("#FFFFFF"),
            ["PhoneSeparatorBrush"] = B("#E3E3E6"),
            ["PhoneFillBrush"] = B("#1F767680"),
            ["PhoneChromeBrush"] = B("#E6FAFAFA"),
            ["PhoneChromeOutlineBrush"] = B("#14000000"),
            ["PhoneChromeSelectedBrush"] = B("#14000000"),
            ["PhoneSecondaryTextBrush"] = B("#8A8A8E"),
            ["PhoneChevronBrush"] = B("#C4C4C7"),
        };
        palette.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
        {
            ["PhoneBackgroundBrush"] = B("#000000"),
            ["PhoneGroupedBackgroundBrush"] = B("#1C1C1E"),
            ["PhoneCardBrush"] = B("#2C2C2E"),
            ["PhoneSeparatorBrush"] = B("#3A3A3C"),
            ["PhoneFillBrush"] = B("#1C1C1E"),
            ["PhoneChromeBrush"] = B("#E6232325"),
            ["PhoneChromeOutlineBrush"] = B("#26FFFFFF"),
            ["PhoneChromeSelectedBrush"] = B("#B3000000"),
            ["PhoneSecondaryTextBrush"] = B("#8D8D93"),
            ["PhoneChevronBrush"] = B("#5A5A5E"),
        };
        return palette;
    }

    /// <summary>The theme last applied ("Light" or a dark theme), or null.</summary>
    public string? ThemeName { get; private set; }

    /// <summary>The theme to run: Light, or the chosen dark theme (Ink when the saved name is a
    /// desktop-only one such as "Gray").</summary>
    public static string Resolve(string? appearance, string? darkTheme, PlatformThemeVariant system)
    {
        var dark = darkTheme != null && DarkThemes.Contains(darkTheme) ? darkTheme : "Ink";
        return appearance switch
        {
            "Light" => "Light",
            "Dark" => dark,
            _ => system == PlatformThemeVariant.Light ? "Light" : dark,
        };
    }

    public void Apply(Application app, string themeName, string? accentHex)
    {
        Remove(app);
        var light = themeName == "Light";
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (!light && DarkThemes.Contains(themeName))
        {
            _overlay = new ResourceInclude(new Uri("avares://Noctis.UI/"))
            {
                Source = new Uri($"avares://Noctis.UI/Assets/Themes/{themeName}.axaml"),
            };
            app.Resources.MergedDictionaries.Add(_overlay);
        }
        if (light)
        {
            // The base Light dictionary is the desktop's legacy Light look, which kept its
            // dark-tuned white secondary text; on the phone's white pages it vanished. Keyed to
            // the Light variant only, so the always-dark overlays (Now Playing, Lyrics, in a
            // Dark ThemeVariantScope) keep their white text.
            _lightText = new ResourceDictionary();
            _lightText.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
            {
                ["SecondaryTextBrush"] = new SolidColorBrush(Color.Parse("#8A000000")),
                ["TertiaryTextBrush"] = new SolidColorBrush(Color.Parse("#61000000")),
            };
            app.Resources.MergedDictionaries.Add(_lightText);
        }

        _palette = BuildPalette();
        app.Resources.MergedDictionaries.Add(_palette);

        var color = !string.IsNullOrWhiteSpace(accentHex) && Color.TryParse(accentHex, out var parsed)
            ? parsed
            : Color.Parse(DefaultAccent);
        // Last: every overlay defines its own AccentColorBrush et al., and the user's accent must win.
        _accent = AccentPalette.Build(color, light);
        app.Resources.MergedDictionaries.Add(_accent);
        ThemeName = themeName;
    }

    public void Remove(Application app)
    {
        if (_overlay != null) app.Resources.MergedDictionaries.Remove(_overlay);
        if (_lightText != null) app.Resources.MergedDictionaries.Remove(_lightText);
        if (_palette != null) app.Resources.MergedDictionaries.Remove(_palette);
        if (_accent != null) app.Resources.MergedDictionaries.Remove(_accent);
        _palette = null;
        _overlay = null;
        _lightText = null;
        _accent = null;
        ThemeName = null;
    }
}
