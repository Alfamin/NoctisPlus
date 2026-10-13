using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Noctis.Tests;

/// <summary>Phone Settings › Appearance: the nine accents, an accent saved outside them, the
/// swatch layout, and the contrast every theme × accent pair keeps.</summary>
public class MobileAppearanceTests
{
    private sealed class FakeThemeHost : IThemeHost
    {
        public (string Appearance, string DarkTheme, string Accent)? Last { get; private set; }
        public void ApplyTheme(string appearance, string darkTheme, string accentHex) => Last = (appearance, darkTheme, accentHex);
    }

    private static readonly string[] Themes = { "Light", "Ink", "Smoke", "Dark", "Midnight" };

    [Fact]
    public void Accents_AreNineDesktopPresets_CrimsonFirst_AndDistinct()
    {
        var accents = SettingsPageViewModel.Accents;
        Assert.Equal(9, accents.Count);
        Assert.Equal(("Crimson", MobileTheme.DefaultAccent), (accents[0].Name, accents[0].Hex));
        // The desktop's own names and colours, exactly.
        Assert.All(accents, a => Assert.Contains(Noctis.App.AccentPresets, p => p.Name == a.Name && p.Hex == a.Hex));
        Assert.Equal(9, accents.Select(a => a.Hex).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Clearly apart: CIE76 ΔE of at least 25 between any two (the nearest pair, Orchid and
        // Rose, is 28; Indigo and Violet, left out, were 17).
        foreach (var a in accents)
        foreach (var b in accents.Where(b => b != a))
        {
            var d = DeltaE(Color.Parse(a.Hex), Color.Parse(b.Hex));
            Assert.True(d >= 25, $"{a.Name} and {b.Name} are ΔE {d:0} apart");
        }
    }

    private static double DeltaE(Color a, Color b)
    {
        static (double L, double A, double B) Lab(Color c)
        {
            static double Lin(byte v) { var x = v / 255d; return x > 0.04045 ? Math.Pow((x + 0.055) / 1.055, 2.4) : x / 12.92; }
            double r = Lin(c.R), g = Lin(c.G), bl = Lin(c.B);
            var x = (r * 0.4124 + g * 0.3576 + bl * 0.1805) / 0.95047;
            var y = r * 0.2126 + g * 0.7152 + bl * 0.0722;
            var z = (r * 0.0193 + g * 0.1192 + bl * 0.9505) / 1.08883;
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
            return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
        }
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt(Math.Pow(l1 - l2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));
    }

    /// <summary>An accent saved outside the nine (Scarlet, from an earlier build or the desktop)
    /// stays applied, keeps its name on the label and in settings.json, and checks no swatch.</summary>
    [AvaloniaFact]
    public async Task AccentOutsideTheNine_StaysApplied_KeepsItsName_AndChecksNoSwatch()
    {
        var host = new FakeThemeHost();
        using var rig = MobileFixtures.MakeRig(theme: host, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.Theme = "Ink";
            s.AccentColorHex = "#FF3B30";
            s.AccentPresetName = "Scarlet";
            await p.SaveSettingsAsync(s);
        });
        await WithAppearancePane(rig, (page, vm, window) =>
        {
            Assert.Null(vm.SelectedAccent);
            Assert.Equal("Scarlet", vm.AccentName);
            Assert.Equal("Ink · Scarlet", vm.AppearanceValue);
            var list = MobileFixtures.Named<ListBox>(page, "AccentList");
            Assert.Null(list.SelectedItem);
            Assert.All(list.GetVisualDescendants().OfType<Ellipse>().Where(e => e.Classes.Contains("swatch-ring")), r => Assert.Equal(0, r.Opacity));

            // Another change re-applies the theme with the saved accent, not Crimson.
            vm.DarkTheme = "Smoke";
            Assert.Equal(("System", "Smoke", "#FF3B30"), host.Last);
            Assert.Equal("Smoke · Scarlet", vm.AppearanceValue);
        });
        var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await vm.PendingSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("#FF3B30", saved.AccentColorHex);
        Assert.Equal("Scarlet", saved.AccentPresetName);   // was rewritten to "Custom"

        // Picking one of the nine takes over.
        vm.SelectedAccent = SettingsPageViewModel.Accents.Single(a => a.Name == "Teal");
        Assert.Equal("Smoke · Teal", vm.AppearanceValue);
        await vm.PendingSave;
        Assert.Equal("Teal", (await rig.Persistence.LoadSettingsAsync()).AccentPresetName);
    }

    /// <summary>A colour picked on the desktop's custom picker reads "Custom"; a stale saved name
    /// that is one of the nine is never shown for a colour that is not that swatch.</summary>
    [Theory]
    [InlineData("#123456", "Custom", "Custom")]
    [InlineData("#123456", "Crimson", "Custom")]
    [InlineData("#123456", "", "Custom")]
    [InlineData("#B8BCC4", "Silver", "Silver")]
    [InlineData("#12c76f", "Custom", "Emerald")]
    public async Task AccentName_ForSavedColours(string hex, string savedName, string expected)
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.AccentColorHex = hex;
            s.AccentPresetName = savedName;
            await p.SaveSettingsAsync(s);
        });
        rig.Shell.OpenSettingsCommand.Execute(null);
        var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await vm.Loaded;
        Assert.Equal(expected, vm.AccentName);
    }

    /// <summary>Nine swatches: five across, four centred under them, every cell at least a
    /// 44 dp touch target on a 412 dp phone (nine in one row would be 40 dp each).</summary>
    [AvaloniaFact]
    public async Task Swatches_FiveOverFourCentred_WithFullTouchTargets()
    {
        using var rig = MobileFixtures.MakeRig();
        await WithAppearancePane(rig, (page, vm, window) =>
        {
            var list = MobileFixtures.Named<ListBox>(page, "AccentList");
            var cells = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(9, cells.Count);
            var boxes = cells.Select(c => new Rect(c.TranslatePoint(new Point(0, 0), list)!.Value, c.Bounds.Size)).ToList();
            Assert.All(boxes, b => Assert.True(b.Width >= 44 && b.Height >= 44, b.ToString()));
            Assert.Single(boxes.Take(5).Select(b => b.Y).Distinct());
            Assert.Single(boxes.Skip(5).Select(b => b.Y).Distinct());
            Assert.True(boxes[5].Y > boxes[0].Y);
            // Both rows centred on the list: equal space left and right.
            foreach (var row in new[] { boxes.Take(5).ToList(), boxes.Skip(5).ToList() })
                Assert.InRange(row[0].X - (list.Bounds.Width - row[^1].Right), -1.5, 1.5);
            Assert.InRange(boxes[5].X - boxes[0].X - boxes[0].Width / 2, -1.5, 1.5);   // the short row sits half a cell in
        });
    }

    private static async Task WithAppearancePane(MobileFixtures.Rig rig, Action<SettingsPage, SettingsPageViewModel, Window> check)
    {
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            vm.OpenPaneCommand.Execute(SettingsPane.Appearance);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 600)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                System.Threading.Thread.Sleep(5);
            }
            window.UpdateLayout();
            check(MobileFixtures.Find<SettingsPage>(view), vm, window);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }
}
