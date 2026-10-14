using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace Noctis.Mobile.Services;

/// <summary>
/// Puts the UI dispatcher's timers on its platform loop's clock. Avalonia's Android platform
/// builds the loop (AndroidDispatcherImpl, which starts its own stopwatch) before anything has
/// touched <see cref="Dispatcher.UIThread"/>, so the dispatcher is first created bare with a
/// second stopwatch and only then handed the loop; its time provider still reads that second
/// stopwatch. Timers are due on the dispatcher's clock but armed on the loop's (due - loop.Now),
/// and the loop's clock is ahead by however long that start-up took (30 ms on the emulator,
/// Debug). Any timer due sooner than that gap is posted to run at once, finds itself not yet
/// due, re-arms "at once" again, and so on: a busy loop on the main thread until the dispatcher's
/// clock catches up. MediaContext's 16 ms animation timer is always inside a 30 ms gap, so
/// whenever an animation clock was subscribed and nothing was being drawn, the UI thread sat at
/// a full core (about 6,500 wake-ups a second, measured 2026-10-14).
/// </summary>
internal static class DispatcherClock
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_timeProvider")]
    private static extern ref Func<long> TimeProvider(Dispatcher dispatcher);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_impl")]
    private static extern ref IDispatcherImpl Impl(Dispatcher dispatcher);

    /// <summary>
    /// Points <paramref name="dispatcher"/>'s clock at its platform loop's. Timers already
    /// armed fire at most the old gap early, once. False when this Avalonia no longer has the
    /// fields (an upgrade renamed them; the regression test says so).
    /// </summary>
    public static bool Align(Dispatcher dispatcher)
    {
        try
        {
            // IDispatcherImpl.Now is hidden from the reference assembly (private API), so bind its
            // getter by name; the delegate dispatches to the loop's own implementation.
            var impl = Impl(dispatcher);
            if (impl?.GetType().GetInterfaceMap(typeof(IDispatcherImpl)) is not { } map) return false;
            var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == "get_Now");
            if (index < 0) return false;
            TimeProvider(dispatcher) = map.TargetMethods[index].CreateDelegate<Func<long>>(impl);
            return true;
        }
        catch (MissingFieldException)
        {
            return false;
        }
    }
}
