using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Services.ProfileImport;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The first-launch import window: hands over to the player when the copy is done, waits while Noctis is open,
/// and after a failure offers Try again or an empty start (remembered). Synthetic profiles only.
/// </summary>
public sealed class ProfileImportWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis import window " + Guid.NewGuid().ToString("N")[..8]);
    private string Source => Path.Combine(_root, "Noctis");
    private string Dest => Path.Combine(_root, "Noctis Plus");

    public ProfileImportWindowTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "settings.json"), "{ \"webRemotePort\": 9420 }");
        File.WriteAllText(Path.Combine(Source, "library.json"), "[]");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp folder */ }
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            var frame = new DispatcherFrame();
            using (DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(10), DispatcherPriority.Send))
                Dispatcher.UIThread.PushFrame(frame);
        }
        return condition();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void FinishedImport_HandsOverToThePlayer()
    {
        var window = new ProfileImportWindow(new ProfileImporter(Source, Dest, freeSpace: _ => long.MaxValue));
        var finished = 0;
        window.Finished += (_, _) => finished++;
        window.Show();

        Assert.True(PumpUntil(() => finished == 1), "the player never got the go-ahead");
        Assert.True(File.Exists(Path.Combine(Dest, "settings.json")));
        PumpUntil(() => false, 200);
        Assert.Equal(1, finished);
        window.Close();
    }

    [AvaloniaFact]
    public void WhileNoctisIsOpen_ItWaits_ThenContinuesByItself()
    {
        var open = true;
        var window = new ProfileImportWindow(new ProfileImporter(Source, Dest, () => open, _ => long.MaxValue))
        { WaitPoll = TimeSpan.FromMilliseconds(20) };
        var finished = false;
        window.Finished += (_, _) => finished = true;
        window.Show();

        var title = window.FindControl<TextBlock>("TitleText")!;
        Assert.True(PumpUntil(() => title.Text == "Close Noctis to continue"));
        Assert.False(window.FindControl<Button>("RetryButton")!.IsVisible);
        Assert.False(File.Exists(Path.Combine(Dest, "settings.json")));

        open = false;
        Assert.True(PumpUntil(() => finished));
        Assert.True(File.Exists(Path.Combine(Dest, "settings.json")));
        window.Close();
    }

    [AvaloniaFact]
    public void Failure_OffersTryAgain_AndStartFreshIsRemembered()
    {
        var attempts = 0; // every attempt finds the disk full
        var window = new ProfileImportWindow(new ProfileImporter(Source, Dest, freeSpace: _ => { attempts++; return 0; }));
        var finished = false;
        window.Finished += (_, _) => finished = true;
        window.Show();

        var title = window.FindControl<TextBlock>("TitleText")!;
        var message = window.FindControl<TextBlock>("MessageText")!;
        Assert.True(PumpUntil(() => title.Text == "Couldn't bring over your library"));
        Assert.Contains("Not enough free space", message.Text);
        Assert.True(window.FindControl<Button>("RetryButton")!.IsVisible);

        Assert.Equal(1, attempts);
        Click(window.FindControl<Button>("RetryButton")!);
        Assert.True(PumpUntil(() => attempts == 2 && title.Text == "Couldn't bring over your library"));
        Assert.False(finished);

        Click(window.FindControl<Button>("FreshButton")!);
        Assert.True(PumpUntil(() => finished));
        Assert.False(File.Exists(Path.Combine(Dest, "settings.json")));
        Assert.Equal(ProfileImportDecisionKind.NotNeeded, ProfileImporter.Evaluate(Source, Dest).Kind);
        window.Close();
    }

    [AvaloniaFact]
    public void ClosingTheWindow_StopsTheCopy_WithoutFinishing()
    {
        var window = new ProfileImportWindow(new ProfileImporter(Source, Dest, () => true, _ => long.MaxValue))
        { WaitPoll = TimeSpan.FromMilliseconds(20) };
        var finished = false;
        window.Finished += (_, _) => finished = true;
        window.Show();
        var title = window.FindControl<TextBlock>("TitleText")!;
        Assert.True(PumpUntil(() => title.Text == "Close Noctis to continue"));

        window.Close();
        Assert.False(PumpUntil(() => finished, 300));
        Assert.Equal(ProfileImportDecisionKind.Start, ProfileImporter.Evaluate(Source, Dest).Kind);
    }
}
