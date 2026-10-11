using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Phone Settings, the theme that follows the system, the shared accent palette and log export.</summary>
public class MobileSettingsTests
{
    private sealed class FakeThemeHost : IThemeHost
    {
        public (string Appearance, string DarkTheme, string Accent)? Last { get; private set; }
        public void ApplyTheme(string appearance, string darkTheme, string accentHex) => Last = (appearance, darkTheme, accentHex);
    }

    private sealed class FakeLogExporter : ILogExporter
    {
        public string? FileName { get; private set; }
        public string? Text { get; private set; }
        public Task<bool> ExportAsync(string suggestedFileName, string text)
        {
            FileName = suggestedFileName;
            Text = text;
            return Task.FromResult(true);
        }
    }

    private static async Task<SettingsPageViewModel> OpenSettings(ShellViewModel shell)
    {
        shell.OpenSettingsCommand.Execute(null);
        var page = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
        await page.Loaded;
        return page;
    }

    [AvaloniaFact]
    public void AccentPalette_Build_CarriesTheAccentAndItsLightThemeOutline()
    {
        var crimson = Color.Parse("#E74856");
        var dark = AccentPalette.Build(crimson, isLightTheme: false);
        Assert.Equal(crimson, (Color)dark["SystemAccentColor"]!);
        Assert.Equal(crimson, ((ISolidColorBrush)dark["AccentColorBrush"]!).Color);
        Assert.Equal(0, ((ISolidColorBrush)dark["AccentBorderBrush"]!).Color.A);          // no outline on dark

        var white = AccentPalette.Build(Colors.White, isLightTheme: true);
        Assert.NotEqual(0, ((ISolidColorBrush)white["AccentBorderBrush"]!).Color.A);      // a white pill on a light page needs one
        Assert.Equal(Colors.Black, ((ISolidColorBrush)white["AccentForegroundBrush"]!).Color);
    }

    [Theory]
    [InlineData("System", "Smoke", PlatformThemeVariant.Dark, "Smoke")]
    [InlineData("System", "Smoke", PlatformThemeVariant.Light, "Light")]
    [InlineData("Dark", "Midnight", PlatformThemeVariant.Light, "Midnight")]
    [InlineData("Light", "Ink", PlatformThemeVariant.Dark, "Light")]
    [InlineData("System", "Gray", PlatformThemeVariant.Dark, "Ink")]       // a desktop-only name falls back to Ink
    [InlineData(null, null, PlatformThemeVariant.Dark, "Ink")]
    public void MobileTheme_Resolve_FollowsTheSystemUnlessOverridden(string? appearance, string? dark, PlatformThemeVariant system, string expected)
    {
        Assert.Equal(expected, MobileTheme.Resolve(appearance, dark, system));
    }

    [AvaloniaFact]
    public void MobileTheme_Apply_SwapsTheOverlayAndTheAccent_AndSetsTheVariant()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        try
        {
            theme.Apply(app, "Ink", "#12C76F");
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.True(app.Resources.TryGetResource("AppMainBackgroundColor", ThemeVariant.Dark, out var ink));
            Assert.Equal(Color.Parse("#0F0F0F"), (Color)ink!);                                      // Ink's surface
            Assert.True(app.Resources.TryGetResource("AccentColorBrush", ThemeVariant.Dark, out var accent));
            Assert.Equal(Color.Parse("#12C76F"), ((ISolidColorBrush)accent!).Color);               // the accent beats Ink's own

            theme.Apply(app, "Light", "#12C76F");
            Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
            Assert.False(app.Resources.TryGetResource("AppMainBackgroundColor", ThemeVariant.Light, out _));   // overlay gone
            Assert.Equal("Light", theme.ThemeName);
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
    }

    /// <summary>The base Light dictionary is the desktop's legacy Light look and keeps white
    /// (#80FFFFFF) secondary text, which vanished on the phone's white pages (device, 09-23).
    /// The phone's override is Light-variant only, so the always-dark overlays keep theirs.</summary>
    [AvaloniaFact]
    public void MobileTheme_Apply_Light_GivesDarkSecondaryText_OnlyForTheLightVariant()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        try
        {
            theme.Apply(app, "Light", null);
            Assert.True(app.Resources.TryGetResource("SecondaryTextBrush", ThemeVariant.Light, out var text));
            var color = ((ISolidColorBrush)text!).Color;
            Assert.True(color.R < 0x40 && color.G < 0x40 && color.B < 0x40, color.ToString());
            Assert.False(app.Resources.TryGetResource("SecondaryTextBrush", ThemeVariant.Dark, out _));

            theme.Apply(app, "Ink", null);                                                       // the override goes with Light
            Assert.True(app.Resources.TryGetResource("SecondaryTextBrush", ThemeVariant.Dark, out var ink));
            Assert.Equal(Color.Parse("#A3A3A3"), ((ISolidColorBrush)ink!).Color);
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
    }

    [Fact]
    public async Task Settings_AppearanceThemeAndAccent_ApplyAndPersist()
    {
        using var rig = MobileFixtures.MakeRig();
        var host = new FakeThemeHost();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Theme = host };
        var page = await OpenSettings(shell);
        Assert.Equal("System", page.Appearance);
        Assert.Equal("Ink", page.DarkTheme);                       // AppSettings.Theme defaults to the desktop's "Gray"
        Assert.Null(host.Last);                                    // loading applies nothing

        page.Appearance = "Light";
        Assert.Equal(("Light", "Ink", "#E74856"), host.Last);
        page.DarkTheme = "Midnight";
        page.SelectedAccent = SettingsPageViewModel.Accents.Single(a => a.Name == "Emerald");
        Assert.Equal(("Light", "Midnight", "#12C76F"), host.Last);
        await page.PendingSave;

        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("Light", saved.MobileAppearance);
        Assert.Equal("Midnight", saved.Theme);
        Assert.True(saved.ThemeV2Migrated);
        Assert.Equal("#12C76F", saved.AccentColorHex);
        Assert.Equal("Emerald", saved.AccentPresetName);
    }

    [Fact]
    public async Task Settings_LyricsTextSize_AppliesAndPersists()
    {
        using var rig = MobileFixtures.MakeRig();
        var page = await OpenSettings(rig.Shell);

        page.LyricsTextScale = 1.4;
        await page.PendingSave;

        Assert.Equal(LyricsPageViewModel.BaseFontSize * 1.4, rig.Shell.Lyrics.LineFontSize, 6);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal(1.4, saved.MobileLyricsTextScale, 6);

        var fresh = new LyricsPageViewModel(rig.Player, rig.Shell.Player, new FakeTrackFiles(), rig.Persistence, work => Task.FromResult(work()));
        await fresh.InitializeAsync();
        Assert.Equal(1.4, fresh.TextScale, 6);
    }

    /// <summary>Owner 10-11: gapless is always on on the phone. A settings.json that says off
    /// (written by the desktop, or by an older phone build that had the switch) changes nothing:
    /// the player is gapless from the moment the shell is built, as the Android startup builds
    /// it, and stays so through Settings; the saved desktop value is left alone.</summary>
    [Fact]
    public async Task Gapless_IsOnFromStartup_EvenWhenSettingsSayOff()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var persistence = new PersistenceService(root);
            var seeded = await persistence.LoadSettingsAsync();
            seeded.GaplessPlaybackEnabled = false;
            await persistence.SaveSettingsAsync(seeded);

            var tracks = new[] { MobileFixtures.Song("One"), MobileFixtures.Song("Two") };
            var library = new FakeLibraryService();
            library.TrackList.AddRange(tracks);
            var player = new FakeAudioPlayer();
            player.SetGapless(false);                               // whatever the player came up with
            var history = new FakeHistoryLog();
            var nowPlaying = new NowPlayingViewModel(player, library, persistence, history, marshal: a => a());
            var shell = new ShellViewModel(
                new LibraryViewModel(library, persistence, new MobileFixtures.NoPicker(), history, marshal: a => a()),
                nowPlaying,
                new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())));
            await shell.Library.InitializeAsync();
            Assert.True(player.GaplessEnabled);

            var page = await OpenSettings(shell);
            page.LyricsTextScale = 1.2;                             // a save through Settings
            await page.PendingSave;
            Assert.True(player.GaplessEnabled);
            Assert.False((await persistence.LoadSettingsAsync()).GaplessPlaybackEnabled);

            nowPlaying.PlayTracks(tracks, 0);
            Assert.Equal(new[] { tracks[1].FilePath }, player.PreparedPaths);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExportLogs_HandsTheLogToTheExporter()
    {
        using var rig = MobileFixtures.MakeRig();
        var exporter = new FakeLogExporter();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Logs = exporter, VersionText = "Noctis 9.9.9" };
        var page = await OpenSettings(shell);
        DebugLog.Write("Test", "export-marker-5f1c");

        await page.ExportLogsCommand.ExecuteAsync(null);

        Assert.StartsWith("noctis-log-", exporter.FileName);
        Assert.Contains("export-marker-5f1c", exporter.Text);
        Assert.Equal("Log saved", page.ExportStatus);
        Assert.Equal("Noctis 9.9.9", page.VersionText);
    }

    /// <summary>S23 2026-10-05: the folder's grant was dropped and every song failed with a bare
    /// "Source error"; Settings now names the folder whose access was lost.</summary>
    [AvaloniaFact]
    public async Task AFolderWithoutAccess_SaysSo()
    {
        const string tones = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones";
        const string beats = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FBeats";
        var picker = new MobileFixtures.NoPicker();
        picker.Lost.Add(beats);
        using var rig = MobileFixtures.MakeRig(picker: picker, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.Add(tones);
            s.MusicFolders.Add(beats);
            await p.SaveSettingsAsync(s);
        });

        rig.Shell.OpenSettingsCommand.Execute(null);
        var page = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await page.Loaded;

        Assert.Equal(new[] { new SettingsFolder("Music/Tones", false), new SettingsFolder("Music/Beats", true) }, page.FolderRows);
    }

    [AvaloniaFact]
    public async Task ProfileButton_OpensSettings_WithReadableFolders()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.Add("content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones");
            await p.SaveSettingsAsync(s);
        });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        var profile = MobileFixtures.Named<Button>(view, "ProfileButtonRoot");   // Library's header ProfileButton
        profile.Command!.Execute(profile.CommandParameter);
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).Loaded;
        window.UpdateLayout();

        var page = MobileFixtures.Find<SettingsPage>(view);
        Assert.Equal(new[] { new SettingsFolder("Music/Tones", false) }, ((SettingsPageViewModel)page.DataContext!).FolderRows);
        Assert.Contains(MobileFixtures.Named<ItemsControl>(page, "FolderList").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Music/Tones");
        Assert.True(MobileFixtures.Named<Button>(page, "ExportLogsButton").IsEffectivelyVisible);
        window.Close();
    }

    /// <summary>Now Playing and Lyrics are always dark (ruling 8): under the Light theme they
    /// must still resolve dark resources, or Fluent's slider tracks and theme-brushed icons turn
    /// light-on-dark-scrim. The Queue follows the app theme (MobileQueueSheetTests).</summary>
    [AvaloniaFact]
    public void AlwaysDarkOverlays_ResolveDarkResources_UnderTheLightTheme()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
            var window = MobileFixtures.Mount(rig.Shell, out var view);
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
            rig.Shell.OpenNowPlayingCommand.Execute(null);
            window.UpdateLayout();

            Assert.Equal(ThemeVariant.Light, view.ActualThemeVariant);
            var nowPlaying = MobileFixtures.Find<NowPlayingPage>(view);
            Assert.Equal(ThemeVariant.Dark, nowPlaying.ActualThemeVariant);
            Assert.Equal(ThemeVariant.Dark, MobileFixtures.Named<Slider>(nowPlaying, "SeekBar").ActualThemeVariant);
            Assert.Equal(ThemeVariant.Dark, MobileFixtures.Find<LyricsPage>(view).ActualThemeVariant);
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = before;
        }
    }

    /// <summary>Mounts Settings with the desktop icon set merged as the device merges it
    /// (AndroidApp.axaml), so the section badges resolve their Rail icons.</summary>
    private static async Task WithMountedSettings(MobileFixtures.Rig rig, Action<SettingsPage, SettingsPageViewModel, Window> check)
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
            window.UpdateLayout();
            check(MobileFixtures.Find<SettingsPage>(view), vm, window);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    /// <summary>Owner 10-11: every section is a rounded card headed by the desktop Settings
    /// rail's icon for it, and the gapless switch is gone (the phone is always gapless).</summary>
    [AvaloniaFact]
    public async Task Settings_IsRoundedCards_EachHeadedByItsDesktopIcon_WithNoGaplessSwitch()
    {
        using var rig = MobileFixtures.MakeRig();
        await WithMountedSettings(rig, (page, _, _) =>
        {
            var cards = page.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("settings-card") && b.IsEffectivelyVisible).ToList();
            var heads = cards.Select(c =>
            {
                var title = c.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("section-title")).Text;
                var badge = c.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("section-badge"));
                return (Title: title, Icon: badge.GetVisualDescendants().OfType<PathIcon>().Single().Data, Corner: c.CornerRadius.TopLeft);
            }).ToList();

            // No account service in this rig: the Account card stays hidden.
            Assert.Equal(new[] { "Library", "Appearance", "Lyrics", "Advanced", "About" }, heads.Select(h => h.Title));
            var keys = new[] { "RailFolderIcon", "RailPaletteIcon", "RailLyricsIcon", "RailWrenchIcon", "RailInfoIcon" };
            for (var i = 0; i < keys.Length; i++)
            {
                Assert.True(Application.Current!.TryGetResource(keys[i], null, out var geometry), keys[i]);
                Assert.Same(geometry, heads[i].Icon);
            }
            Assert.All(heads, h => Assert.Equal(20, h.Corner));

            Assert.DoesNotContain(page.GetVisualDescendants().OfType<Control>(), c => c.Name == "GaplessSwitch");
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Gapless", StringComparison.OrdinalIgnoreCase) == true);
        });
    }

    /// <summary>Rows are touch-sized (each at least 52 dp tall, each button at least 44 dp)
    /// and each leads with an icon.</summary>
    [AvaloniaFact]
    public async Task Settings_Rows_AreTouchSized_AndLeadWithAnIcon()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.Add("content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones");
            await p.SaveSettingsAsync(s);
        });
        await WithMountedSettings(rig, (page, _, _) =>
        {
            var rows = page.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsEffectivelyVisible && (c.Classes.Contains("setting-row") || c.Classes.Contains("switch-row"))).ToList();
            Assert.True(rows.Count >= 9, $"{rows.Count} rows");
            Assert.All(rows, r =>
            {
                Assert.True(r.Bounds.Height >= 52, $"{r.Name ?? r.GetType().Name} is {r.Bounds.Height} tall");
                var icon = r.GetVisualDescendants().OfType<Control>().First(c => c is PathIcon or Ellipse);
                Assert.True(icon.Bounds.Width >= 20, $"{r.Name ?? r.GetType().Name} has no leading icon");
            });
            Assert.All(page.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.TemplatedParent == null),  // not a slider's parts
                b => Assert.True(b.Bounds.Height >= 44, $"{b.Name} is {b.Bounds.Height} tall"));
        });
    }

    /// <summary>The library card lists each folder by name, says in place when its access was
    /// lost, and offers Add folder and Rescan as icon pills.</summary>
    [AvaloniaFact]
    public async Task Settings_LibraryCard_ShowsTheLostFolder_AndTheTwoActionPills()
    {
        const string tones = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones";
        const string beats = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FBeats";
        var picker = new MobileFixtures.NoPicker();
        picker.Lost.Add(beats);
        using var rig = MobileFixtures.MakeRig(picker: picker, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.Add(tones);
            s.MusicFolders.Add(beats);
            await p.SaveSettingsAsync(s);
        });
        await WithMountedSettings(rig, (page, _, _) =>
        {
            var warnings = MobileFixtures.Named<ItemsControl>(page, "FolderList").GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("folder-lost")).ToList();
            Assert.Equal(new[] { false, true }, warnings.Select(w => w.IsEffectivelyVisible));
            Assert.False(MobileFixtures.Named<Grid>(page, "NoFoldersRow").IsEffectivelyVisible);

            foreach (var (name, label) in new[] { ("AddFolderButton", "Add folder"), ("RescanButton", "Rescan") })
            {
                var pill = MobileFixtures.Named<Button>(page, name);
                Assert.Contains("pill", pill.Classes);
                Assert.NotNull(pill.GetVisualDescendants().OfType<PathIcon>().Single().Data);
                Assert.Contains(pill.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == label);
            }
        });
    }

    /// <summary>System / Dark / Light leads Appearance; the dark style hides under Light,
    /// where it cannot apply, and comes back for System and Dark.</summary>
    [AvaloniaFact]
    public async Task Settings_Appearance_LeadsWithTheThemeChoice_AndHidesTheDarkStyleUnderLight()
    {
        using var rig = MobileFixtures.MakeRig();
        await WithMountedSettings(rig, (page, vm, window) =>
        {
            var lists = MobileFixtures.Named<Border>(page, "AppearanceCard").GetVisualDescendants().OfType<ListBox>().ToList();
            Assert.Equal("AppearanceList", lists[0].Name);
            Assert.Equal(new[] { "System", "Dark", "Light" }, lists[0].Items.Cast<string>());
            Assert.True(MobileFixtures.Named<StackPanel>(page, "DarkThemeSection").IsEffectivelyVisible);

            vm.Appearance = "Light";
            window.UpdateLayout();
            Assert.False(vm.ShowsDarkThemes);
            Assert.False(MobileFixtures.Named<StackPanel>(page, "DarkThemeSection").IsEffectivelyVisible);
            Assert.Equal("Light", lists[0].SelectedItem);

            vm.Appearance = "Dark";
            window.UpdateLayout();
            Assert.True(MobileFixtures.Named<StackPanel>(page, "DarkThemeSection").IsEffectivelyVisible);
        });
        await ((SettingsPageViewModel)rig.Shell.CurrentPage!).PendingSave;
    }
}
