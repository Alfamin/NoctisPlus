using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Controls;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings sliders (10-12): the stepped ones (Kawarp Warp 0.1, Kawarp Blur and Minimum Line
/// Opacity 1, Play/Pause Fade 50…) snapped their thumb from notch to notch while dragging, and
/// at the minimum the accent fill peeked out left of the thumb as a little red square.
/// </summary>
public partial class SettingsSmoothSliderTests
{
    private sealed partial class Knob : ObservableObject
    {
        [ObservableProperty] private int _blur = 6;
        [ObservableProperty] private double _warp = 1.0;
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 6)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    /// <summary>A Settings-styled slider (the page's own accent-slider styles) in a window.</summary>
    private static (Window win, T slider) Mount<T>(T slider) where T : Slider
    {
        EnsureAppStyles();
        var settings = new SettingsView();
        var styles = settings.Styles.ToList();
        settings.Styles.Clear();
        slider.Classes.Add("accent-slider");
        slider.Width = 600;
        var host = new StackPanel { Margin = new Thickness(40) };
        foreach (var s in styles) host.Styles.Add(s);
        host.Children.Add(slider);
        var win = new Window { Width = 800, Height = 200, Content = host };
        win.Show();
        Pump();
        return (win, slider);
    }

    private static Point ThumbCentre(Slider slider, Window win)
    {
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
        return thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), win)!.Value;
    }

    [AvaloniaFact]
    public void Drag_ThumbFollowsThePointer_WhileTheSettingMovesInSteps()
    {
        var knob = new Knob();
        var slider = new SmoothSlider { Minimum = 1, Maximum = 16, Step = 1, DataContext = knob };
        slider.Bind(SmoothSlider.SteppedValueProperty, new Binding(nameof(Knob.Blur)) { Mode = BindingMode.TwoWay });
        var (win, _) = Mount(slider);
        Assert.Equal(6, slider.Value);

        var start = ThumbCentre(slider, win);
        win.MouseMove(start);
        win.MouseDown(start, MouseButton.Left);
        var values = new List<double>();
        for (var dx = 1; dx <= 60; dx++)
        {
            win.MouseMove(new Point(start.X + dx, start.Y), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            values.Add(slider.Value);
            // The setting only ever sees whole steps, and they track the thumb.
            Assert.Equal(Math.Round(slider.Value), knob.Blur);
        }
        win.MouseUp(new Point(start.X + 60, start.Y), MouseButton.Left);

        // Every pixel moved the thumb: no notch-to-notch jumps, no snapping back.
        Assert.True(values.Zip(values.Skip(1)).All(p => p.Second > p.First), string.Join(",", values));
        Assert.Contains(values, v => v != Math.Round(v));
        Assert.True(knob.Blur > 6);
        win.Close();
    }

    [AvaloniaFact]
    public void SettingChangedElsewhere_MovesTheThumb_ButItsOwnStepDoesNot()
    {
        var knob = new Knob();
        var slider = new SmoothSlider { Minimum = 0, Maximum = 3, Step = 0.1, DataContext = knob };
        slider.Bind(SmoothSlider.SteppedValueProperty, new Binding(nameof(Knob.Warp)) { Mode = BindingMode.TwoWay });
        var (win, _) = Mount(slider);
        Assert.Equal(1.0, slider.Value);

        slider.Value = 1.2345;                     // a drag sample between notches
        Assert.Equal(1.2, knob.Warp);              // the setting gets the step, without binary noise
        Assert.Equal(1.2345, slider.Value);        // and the thumb stays where the pointer put it

        knob.Warp = 2.5;                           // a reset / preset / double-tap default
        Assert.Equal(2.5, slider.Value);
        win.Close();
    }

    [AvaloniaTheory]
    [InlineData(1, 1, 16)]   // Kawarp Blur at its minimum (the reported red square)
    [InlineData(0, 0, 60)]   // Minimum Line Opacity at 0%
    [InlineData(0, 0, 3)]    // Kawarp Warp at 0
    public void AtTheMinimum_NoFillShowsLeftOfTheThumb(double value, double min, double max)
    {
        var (win, slider) = Mount(new SmoothSlider { Minimum = min, Maximum = max, Step = 1, Value = value });
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
        var fill = slider.GetVisualDescendants().OfType<RepeatButton>().First(r => r.Name == "PART_DecreaseButton");
        var thumbLeft = thumb.TranslatePoint(new Point(0, 0), win)!.Value.X;
        var thumbCentre = thumbLeft + thumb.Bounds.Width / 2;

        // Nothing red at all at the minimum, and wherever the fill starts it's under the
        // thumb's centre half, never out at its left edge.
        Assert.True(fill.Bounds.Width <= 0.01, $"fill is {fill.Bounds.Width} wide at the minimum");
        var fillLeft = fill.TranslatePoint(new Point(0, 0), win)!.Value.X;
        Assert.True(fillLeft >= thumbCentre - 0.5, $"fill starts at {fillLeft}, thumb {thumbLeft}..{thumbLeft + thumb.Bounds.Width}");
        win.Close();
    }

    [AvaloniaFact]
    public void AtTheMaximum_NoTrackShowsRightOfTheThumb()
    {
        var (win, slider) = Mount(new SmoothSlider { Minimum = 0, Maximum = 60, Step = 1, Value = 60 });
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
        var track = slider.GetVisualDescendants().OfType<RepeatButton>().First(r => r.Name == "PART_IncreaseButton");
        var thumbLeft = thumb.TranslatePoint(new Point(0, 0), win)!.Value.X;
        var trackRight = track.TranslatePoint(new Point(track.Bounds.Width, 0), win)!.Value.X;

        // No track at all at the maximum; were there any, it would end under the thumb's centre.
        Assert.True(track.Bounds.Width <= 0.01, $"track is {track.Bounds.Width} wide at the maximum");
        Assert.True(track.Bounds.Width <= 0.01 || trackRight <= thumbLeft + thumb.Bounds.Width / 2 + 0.5,
            $"track ends at {trackRight}, thumb {thumbLeft}..{thumbLeft + thumb.Bounds.Width}");
        win.Close();
    }

    [AvaloniaFact]
    public void MidValue_FillReachesTheThumb()
    {
        // The inset is only at the bar's outer ends: mid-slider the fill still runs up to the thumb.
        var (win, slider) = Mount(new SmoothSlider { Minimum = 0, Maximum = 60, Step = 1, Value = 30 });
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
        var fill = slider.GetVisualDescendants().OfType<RepeatButton>().First(r => r.Name == "PART_DecreaseButton");
        var thumbLeft = thumb.TranslatePoint(new Point(0, 0), win)!.Value.X;
        var fillRight = fill.TranslatePoint(new Point(fill.Bounds.Width, 0), win)!.Value.X;

        Assert.InRange(fillRight, thumbLeft, thumbLeft + 2);
        Assert.True(fill.Bounds.Width > 200);
        win.Close();
    }

    [AvaloniaFact]
    public void Snap_RoundsToTheStepFromTheMinimum()
    {
        var slider = new SmoothSlider { Minimum = 100, Maximum = 2000, Step = 50 };
        Assert.Equal(150, slider.Snap(160));
        Assert.Equal(2000, slider.Snap(2100));
        Assert.Equal(100, slider.Snap(0));
        slider.Step = 0;
        Assert.Equal(160.25, slider.Snap(160.25));
    }
}
