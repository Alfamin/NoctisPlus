using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Eyeball probe for the phone's grouped Settings and Account pages, signed in (a state the
/// emulator cannot reach without a desktop server): renders both pages in Light and every dark
/// style to PNGs. Runs only with NOCTIS_TEST_SKIA=1 and NOCTIS_GROUPED_SHOTS=&lt;dir&gt;.
/// </summary>
public class MobileGroupedPagesShotsTests
{
    private readonly ITestOutputHelper _o;
    public MobileGroupedPagesShotsTests(ITestOutputHelper o) => _o = o;

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Motion.FinishAll();   // a shot shows the sheet landed, not on its way
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Shots_SettingsAndAccount_SignedIn_InEveryTheme()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_GROUPED_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        var app = Application.Current!;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        // The phone registers Inter (WithInterFont); the grouped pages ask for it by weight.
        try { FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection()); }
        catch (ArgumentException) { }   // already added by an earlier run in this process
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        var theme = new MobileTheme();
        try
        {
            foreach (var name in new[] { "Light", "Ink", "Smoke", "Dark", "Midnight" })
            {
                theme.Apply(app, name, MobileTheme.DefaultAccent);
                using var rig = MobileFixtures.MakeRig();
                var account = new MobileAccountTests.FakeAccount
                {
                    Account = MobileAccountTests.FakeAccount.SignedInAs("ferlin@example.com") with
                    {
                        DisplayName = "Ferlin Mejia",
                        LastSyncUtc = DateTime.UtcNow.AddMinutes(-5),
                    },
                };
                var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
                {
                    Account = account,
                    Marshal = a => a(),
                    TintFactory = rig.Shell.TintFactory,
                };
                var window = MobileFixtures.Mount(shell, out _, 412, 1320);
                window.Background = Brushes.Transparent;
                try
                {
                    shell.OpenSettingsCommand.Execute(null);
                    var settings = (SettingsPageViewModel)shell.CurrentPage!;
                    await settings.Loaded;
                    Pump(300);
                    Save(window, Path.Combine(dir, $"settings-{name}.png"));
                    // The pages pushed inside the sheet, each once its slide has settled.
                    foreach (var pane in new[] { SettingsPane.Folders, SettingsPane.Appearance, SettingsPane.Lyrics })
                    {
                        settings.Pane = pane;
                        Pump(600);
                        Save(window, Path.Combine(dir, $"settings-{pane}-{name}.png"));
                    }
                    settings.Pane = SettingsPane.Main;
                    Pump(600);

                    shell.OpenAccountCommand.Execute(null);
                    var page = (AccountPageViewModel)shell.CurrentPage!;
                    page.SyncText = string.Empty;
                    Pump(300);
                    Save(window, Path.Combine(dir, $"account-{name}.png"));

                    page.AskSignOutCommand.Execute(null);
                    Pump(200);
                    Save(window, Path.Combine(dir, $"account-signout-{name}.png"));
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            theme.Remove(app);
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    private void Save(Window window, string path)
    {
        window.CaptureRenderedFrame()!.Save(path);
        _o.WriteLine(path);
    }
}
