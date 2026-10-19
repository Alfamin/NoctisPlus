using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Minimum Line Opacity slider (Settings › Lyrics) ships at a non-zero default and
/// double-tapping its thumb snaps back to it, the same affordance the pre-amp and
/// crossfade sliders have. The handler is wired in XAML, so this drives the real view.
/// </summary>
public class LyricsMinLineOpacitySliderTests
{
    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>Raises the routed event the gesture recognizer raises on a double click
    /// (mirrors RowDoubleClickToPlayTests), so the XAML-wired handler runs for real.</summary>
    private static void DoubleTap(Control target, Visual root)
    {
        var pointer = new Pointer(1, PointerType.Mouse, true);
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? default;
        var press = new PointerPressedEventArgs(
            target, pointer, root, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 2);
        target.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, press));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task FreshSettings_StartAtTheDefault_AndDoubleTapRestoresIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Window? window = null;
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            Assert.Equal(AppSettings.LyricsMinLineOpacityDefault, vm.LyricsMinLineOpacity);

            vm.SelectedSettingsTab = SettingsViewModel.TabLyrics;
            var view = new SettingsView { DataContext = vm };
            window = new Window { Width = 1000, Height = 720, Content = view };
            window.Show();
            window.UpdateLayout();

            var slider = view.FindControl<Slider>("LyricsMinLineOpacitySlider");
            Assert.NotNull(slider);
            // 1% steps for the int setting, but the thumb itself glides (10-12: the snapped
            // thumb jumped notch to notch); SmoothSlider rounds only what the setting sees.
            var smooth = Assert.IsType<Noctis.Controls.SmoothSlider>(slider);
            Assert.Equal(1, smooth.Step);
            Assert.False(slider!.IsSnapToTickEnabled);

            // User dragged it somewhere else …
            vm.LyricsMinLineOpacity = 42;
            window.UpdateLayout();
            Assert.Equal(42, slider.Value, 3);

            // … a double-tap on the slider brings the default back, view model and thumb alike.
            DoubleTap(slider, window);
            Assert.Equal(AppSettings.LyricsMinLineOpacityDefault, vm.LyricsMinLineOpacity);
            window.UpdateLayout();
            Assert.Equal(AppSettings.LyricsMinLineOpacityDefault, slider.Value, 3);
        }
        finally
        {
            window?.Close();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
