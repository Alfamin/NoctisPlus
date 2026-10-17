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

    public static AppBuilder BuildAvaloniaApp() => ClaimUiThreadFirst(RealRendering
        ? AppBuilder
            .Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        : AppBuilder
            .Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions()));

    /// <summary>
    /// Avalonia 12 makes whichever thread first touches <see cref="Dispatcher.UIThread"/>, or
    /// builds any AvaloniaObject, after the per-test reset the UI thread. A previous test's
    /// leftover background callback (a search result posting back, a timer) could win that
    /// race while the session was still setting up, and the next test then failed in setup
    /// with "The calling thread cannot access this object because a different thread owns it"
    /// (CI, 10-16: LyricsSourceAutoSearchTests). Claim the dispatcher on the session thread
    /// before any other platform setup; if a leftover got there first anyway, reset and claim
    /// again. Headless tests always get the default managed dispatcher, so claiming it early
    /// changes nothing else.
    /// </summary>
    private static AppBuilder ClaimUiThreadFirst(AppBuilder builder)
    {
        var runtimeSetup = builder.RuntimePlatformServicesInitializer;
        return builder.UseRuntimePlatformSubsystem(() =>
        {
            ClaimUiThread();
            runtimeSetup?.Invoke();
        }, builder.RuntimePlatformServicesName);
    }

    private static readonly MethodInfo? ResetDispatcherForTests =
        typeof(Dispatcher).GetMethod("ResetBeforeUnitTests", BindingFlags.NonPublic | BindingFlags.Static);

    internal static void ClaimUiThread()
    {
        for (var attempt = 0; !Dispatcher.UIThread.CheckAccess(); attempt++)
        {
            if (ResetDispatcherForTests == null || attempt == 20)
                throw new InvalidOperationException(
                    "Another thread kept taking Avalonia's UI dispatcher during headless test setup.");
            ResetDispatcherForTests.Invoke(null, null);
        }
    }

    public override void Initialize()
    {
        // Headless view tests must never launch the developer's installed models. Backend
        // protocol tests inject their own readiness check and an isolated synthetic runner.
        Noctis.Features.WordLyrics.Tool.SuppressModelLaunchForTests = true;
        Styles.Add(new FluentTheme());
    }
}
