using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

public class ProgressRingLifecycleTests
{
    [AvaloniaFact]
    public async Task LyricsRingStartsWhenItsParentAppearsAfterTheChild()
    {
        var ring = new ProgressRing { Width = 20, Height = 20, IsIndeterminate = true, IsVisible = false };
        var parent = new Button { Content = ring, IsVisible = false, IsEnabled = false };
        var window = new Window { Content = parent, Width = 200, Height = 150 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            for (var next = 0; next < 3; next++)
            {
                ring.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(40); // lyrics load can finish after the deferred visibility check
                parent.IsVisible = true;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(40);
                Assert.False(ring.SpinTask.IsCompleted);
                var turns = new HashSet<double>();
                for (var frame = 0; frame < 12; frame++)
                {
                    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    turns.Add(ring.SpinAngle);
                    await Task.Delay(16);
                }
                Assert.True(turns.Count > 1, "Visible lyrics spinner must actually rotate, even inside a disabled status button.");
                parent.IsVisible = false;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await ring.SpinTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally { window.Close(); }
    }

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
