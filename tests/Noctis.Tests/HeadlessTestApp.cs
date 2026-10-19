using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(Noctis.Tests.HeadlessTestApp))]
// Test classes run one at a time: plain [Fact] tests that post to Dispatcher.UIThread or
// time a window raced the headless session's thread when classes ran in parallel, so a
// loaded CI runner failed a different probe on every run ("a different thread owns it",
// a streamed slice landing after its clear, a 308px window). Costs ~30 s locally.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Noctis.Tests;

/// <summary>
/// Minimal Avalonia application for headless view tests. Loads only the Fluent
/// theme (control templates); views under test carry their own local styles.
/// </summary>
public class HeadlessTestApp : Application
{
    /// <summary>
    /// NOCTIS_TEST_SKIA=1 swaps the headless drawing stubs for real Skia rendering, so a
    /// probe can save what a control actually draws (Window.CaptureRenderedFrame) and the
    /// PNG can be eyeballed. Off by default: the suite's layout assertions are measured
    /// against the stubs.
    /// </summary>
    public static bool RealRendering =>
        Environment.GetEnvironmentVariable("NOCTIS_TEST_SKIA") == "1";

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = BuildAvaloniaAppCore();
        // The headless session resets the UI dispatcher before every test and leaves it to the
        // first thread that asks for one. The session thread used to ask only when the platform
        // built its compositor, so a timer an earlier test left running (Motion.After, a
        // settings save posting back) could ask first from the thread pool and become the UI
        // thread; the compositor then refused the session thread and the test failed before it
        // started ("Case Cleanup Failure ... a different thread owns it"). The runtime platform
        // is the first step of setup, so the session thread takes the dispatcher there.
        var runtime = builder.RuntimePlatformServicesInitializer;
        return builder.UseRuntimePlatformSubsystem(() =>
        {
            ClaimUiDispatcher();
            runtime?.Invoke();
        }, builder.RuntimePlatformServicesName ?? "");
    }

    private static AppBuilder BuildAvaloniaAppCore() => RealRendering
        ? AppBuilder
            .Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        : AppBuilder
            .Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    // The reset the session ran just before this setup (internal to Avalonia).
    private static readonly Action? ResetDispatcher = typeof(Dispatcher)
        .GetMethod("ResetBeforeUnitTests", BindingFlags.NonPublic | BindingFlags.Static)
        ?.CreateDelegate<Action>();

    /// <summary>
    /// Makes the calling (session) thread the UI dispatcher's. A stray thread that asked in
    /// the instant between the session's reset and this call gets it reset away again.
    /// </summary>
    private static void ClaimUiDispatcher()
    {
        for (var attempt = 0; !Dispatcher.UIThread.CheckAccess(); attempt++)
        {
            if (ResetDispatcher is null || attempt == 100)
                throw new InvalidOperationException(
                    $"Thread {Dispatcher.UIThread.Thread.ManagedThreadId} holds the UI dispatcher the headless session needs.");
            ResetDispatcher();
        }
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}
