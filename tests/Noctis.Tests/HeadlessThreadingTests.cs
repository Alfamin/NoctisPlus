using System.Reflection;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Avalonia 12 hands the UI dispatcher to whichever thread touches it first after a reset, and
/// every AvaloniaObject (a SolidColorBrush too) belongs to the thread that built it. CI on 10-16
/// failed a headless test's setup because a leftover background thread won that race; a local
/// run failed 14 lyrics tests because static brushes were built on a test thread.
/// </summary>
public class HeadlessThreadingTests
{
    private static readonly MethodInfo ResetDispatcher =
        typeof(Dispatcher).GetMethod("ResetBeforeUnitTests", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void SetupClaim_TakesTheDispatcherBack_FromALeftoverBackgroundThread()
    {
        Assert.NotNull(ResetDispatcher);
        try
        {
            // The per-test reset, then a leftover callback on another thread touching the
            // dispatcher before the session's setup did.
            ResetDispatcher.Invoke(null, null);
            // A real thread: waiting on a queued Task.Run can run it inline on this thread.
            Dispatcher? leftover = null;
            var other = new Thread(() => leftover = Dispatcher.UIThread) { IsBackground = true };
            other.Start();
            other.Join();
            Assert.NotNull(leftover);
            Assert.Throws<InvalidOperationException>(() => Dispatcher.UIThread.VerifyAccess());

            HeadlessTestApp.ClaimUiThread();

            Assert.True(Dispatcher.UIThread.CheckAccess());
            Assert.NotSame(leftover, Dispatcher.UIThread);
        }
        finally
        {
            // Leave it as the end of a headless test does: unclaimed.
            ResetDispatcher.Invoke(null, null);
        }
    }

    [Fact]
    public void SharedBrushes_AreNotTiedToTheThreadThatBuiltThem()
    {
        // Declared types only: reading the values would run static constructors on this thread,
        // which is the very thing that goes wrong.
        var assemblies = new[]
        {
            typeof(Noctis.App).Assembly,
            typeof(Noctis.Services.DominantColorExtractor).Assembly,
            typeof(Noctis.Mobile.ViewModels.ArtistIndex).Assembly,
        };

        var offenders = new List<string>();
        foreach (var assembly in assemblies)
        foreach (var type in assembly.GetTypes())
        foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var declared = field.FieldType.IsArray ? field.FieldType.GetElementType()! : field.FieldType;
            if (typeof(Brush).IsAssignableFrom(declared) && !field.IsLiteral)
                offenders.Add($"{type.FullName}.{field.Name}");
        }

        Assert.True(offenders.Count == 0,
            "Use Immutable*Brush for static brushes: " + string.Join(", ", offenders));
    }
}
