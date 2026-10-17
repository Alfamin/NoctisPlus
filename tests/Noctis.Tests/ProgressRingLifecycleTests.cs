using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

public class ProgressRingLifecycleTests
{
    [AvaloniaFact]
    public async Task WaitingRingDoesNotFault_AndStopsWhenHiddenOrDetached()
    {
        var ring = new ProgressRing { Width = 20, Height = 20, IsIndeterminate = true };
        var window = new Window { Content = ring, Width = 200, Height = 150 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            await Task.Delay(40);
            Assert.False(ring.SpinTask.IsCompleted); // Infinite RunAsync used to fault immediately.
            var first = ring.SpinTask;
            ring.IsIndeterminate = false;
            await first.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(first.IsCompletedSuccessfully);
            ring.IsIndeterminate = true; Dispatcher.UIThread.RunJobs();
            var second = ring.SpinTask; Assert.NotSame(first, second);
            window.Content = null;
            await second.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(second.IsCompletedSuccessfully);
        }
        finally { window.Close(); }
    }
}
