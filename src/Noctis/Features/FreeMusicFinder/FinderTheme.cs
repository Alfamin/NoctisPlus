using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace FreeMusicFinder;

/// <summary>Shared window-scoped presentation; never alters Noctis's own controls.</summary>
internal static class FinderTheme
{
    public static void Apply(Window window)
    {
        window.Resources.ThemeDictionaries[ThemeVariant.Dark] = Palette("#111216", "#1A1C22", "#292C34", "#F3F3F6", "#A5A7B3", "#E74856", "#252830");
        window.Resources.ThemeDictionaries[ThemeVariant.Light] = Palette("#F6F6F8", "#FFFFFF", "#E3E3E9", "#202128", "#646575", "#D93747", "#EBEBF0");
        var surface = new Style(s => s.OfType<Window>());
        surface.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Resource("FinderBackground")));
        surface.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Resource("FinderText")));
        surface.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 13d));
        window.Styles.Add(surface);
        var button = new Style(s => s.OfType<Button>());
        button.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)));
        button.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 8)));
        button.Setters.Add(new Setter(TemplatedControl.MinHeightProperty, 36d));
        button.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Resource("FinderButton")));
        button.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Resource("FinderText")));
        window.Styles.Add(button);
        var primary = new Style(s => s.OfType<Button>().Class("finder-primary"));
        primary.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Resource("FinderAccent")));
        primary.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brushes.White));
        primary.Setters.Add(new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold));
        window.Styles.Add(primary);
        // The theme paints a hovered or pressed button's inside itself, which hid the chosen chip's colour until the
        // pointer left it: the chosen one keeps its colour under the pointer too.
        foreach (var state in new[] { ":pointerover", ":pressed" })
        {
            // Keep both layers consistent when a host theme adds button state styles.
            var heldButton = new Style(s => s.OfType<Button>().Class("finder-primary").Class(state));
            heldButton.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Resource("FinderAccent")));
            heldButton.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brushes.White));
            window.Styles.Add(heldButton);
            var held = new Style(s => s.OfType<Button>().Class("finder-primary").Class(state).Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"));
            held.Setters.Add(new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Resource("FinderAccent")));
            held.Setters.Add(new Setter(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Brushes.White));
            window.Styles.Add(held);
        }
        var card =new Style(s => s.OfType<Border>().Class("finder-card"));
        card.Setters.Add(new Setter(Border.BackgroundProperty, Resource("FinderPanel")));
        card.Setters.Add(new Setter(Border.BorderBrushProperty, Resource("FinderLine")));
        card.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        card.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(12)));
        window.Styles.Add(card);
        // A card that can be clicked says so under the pointer; the one already picked keeps its own colour.
        var pick = new Style(s => s.OfType<Border>().Class("finder-card").Class("finder-pick").Class(":pointerover").Not(x => x.Class("finder-selected")));
        pick.Setters.Add(new Setter(Border.BorderBrushProperty, Resource("FinderMuted")));
        window.Styles.Add(pick);
        var selected = new Style(s => s.OfType<Border>().Class("finder-card").Class("finder-selected"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, Resource("FinderAccent")));
        window.Styles.Add(selected);
        var input = new Style(s => s.OfType<TextBox>());
        input.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)));
        input.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 10)));
        input.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Resource("FinderLine")));
        window.Styles.Add(input);
        var muted = new Style(s => s.OfType<TextBlock>().Class("finder-muted"));
        muted.Setters.Add(new Setter(TextBlock.ForegroundProperty, Resource("FinderMuted")));
        window.Styles.Add(muted);
        var chip = new Style(s => s.OfType<Button>().Class("finder-chip"));
        chip.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(24)));
        chip.Setters.Add(new Setter(TemplatedControl.MinHeightProperty, 32d));
        chip.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(14, 6)));
        chip.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 12d)); window.Styles.Add(chip);
        var tile = new Style(s => s.OfType<Button>().Class("finder-tile"));
        tile.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        tile.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(10)));
        tile.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, Avalonia.Layout.HorizontalAlignment.Stretch)); window.Styles.Add(tile);
        var tileHover = new Style(s => s.OfType<Button>().Class("finder-tile").Class(":pointerover"));
        tileHover.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Resource("FinderButton"))); window.Styles.Add(tileHover);
        var songHover = new Style(s => s.OfType<Border>().Class("finder-song").Class(":pointerover"));
        songHover.Setters.Add(new Setter(Border.BackgroundProperty, Resource("FinderPanel"))); window.Styles.Add(songHover);
        var quiet = new Style(s => s.OfType<Button>().Class("finder-quiet"));
        quiet.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        quiet.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Resource("FinderMuted"))); window.Styles.Add(quiet);
        var search = new Style(s => s.OfType<TextBox>().Class("finder-search"));
        search.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(25)));
        search.Setters.Add(new Setter(TemplatedControl.MinHeightProperty, 50d));
        search.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 15d));
        search.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(18, 12)));
        search.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Resource("FinderPanel"))); window.Styles.Add(search);
        var tab = new Style(s => s.OfType<TabItem>());
        tab.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 14d));
        tab.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 8))); window.Styles.Add(tab);
    }
    private static ResourceDictionary Palette(string background, string panel, string line, string text, string muted, string accent, string button)
    {
        var resources = new ResourceDictionary();
        foreach (var (key, color) in new[] { ("FinderBackground", background), ("FinderPanel", panel), ("FinderLine", line), ("FinderText", text), ("FinderMuted", muted), ("FinderAccent", accent), ("FinderButton", button) })
            resources[key] = new SolidColorBrush(Color.Parse(color));
        foreach (var key in new[] { "SystemControlHighlightAccentBrush", "SystemControlHighlightAccentBrush2", "TextControlBorderBrushFocused" }) resources[key] = new SolidColorBrush(Color.Parse(accent));
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver" }) resources[key] = new SolidColorBrush(Color.Parse(panel));
        resources["TextControlForegroundFocused"] = new SolidColorBrush(Color.Parse(text));
        resources["FinderHero"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(Color.Parse(background == "#111216" ? "#32242F" : "#F3E8EF"), 0), new(Color.Parse(panel), 1) },
        };
        return resources;
    }
    internal static DynamicResourceExtension Resource(string key) => new(key);
    public static Border Card(Control content, double padding = 14)
    {
        var card = new Border { Child = content, Padding = new Thickness(padding) }; card.Classes.Add("finder-card"); return card;
    }
    public static void Primary(Button button) => button.Classes.Add("finder-primary");
    public static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 }; block.Classes.Add("finder-muted"); return block;
    }
}
