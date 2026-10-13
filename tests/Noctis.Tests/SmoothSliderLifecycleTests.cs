using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>SmoothSlider (shared by the desktop and phone Settings, 10-13): attaching or detaching
/// it re-runs its styles, and the thumb position it had set with SetCurrentValue fell back to the
/// coerced default, the Minimum. It then reported that Minimum as the setting: opening the phone's
/// Settings set the lyrics text size to 80 % for a moment (and saved it), closing it saved 80 %.</summary>
public partial class SmoothSliderLifecycleTests
{
    private sealed partial class Knob : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private double _scale = 1.4;
    }

    [AvaloniaFact]
    public void BoundSetting_SurvivesAttachAndDetach()
    {
        var knob = new Knob();
        var seen = new List<double>();
        knob.PropertyChanged += (_, _) => seen.Add(knob.Scale);
        var slider = new SmoothSlider { Minimum = 0.8, Maximum = 1.6, Step = 0.1, DataContext = knob };
        slider.Bind(SmoothSlider.SteppedValueProperty, new Avalonia.Data.Binding(nameof(Knob.Scale)));
        var host = new StackPanel();
        var window = new Window { Width = 400, Height = 200, Content = host };
        window.Show();

        host.Children.Add(slider);
        window.UpdateLayout();
        Assert.Equal(1.4, slider.Value, 6);
        host.Children.Remove(slider);
        window.UpdateLayout();
        host.Children.Add(slider);
        window.UpdateLayout();

        Assert.Empty(seen);
        Assert.Equal(1.4, knob.Scale, 6);
        Assert.Equal(1.4, slider.Value, 6);

        slider.SetCurrentValue(RangeBase.ValueProperty, 1.23);   // a drag (as the Track moves it) still reaches the setting, stepped
        Assert.Equal(1.2, knob.Scale, 6);
        Assert.Equal(1.23, slider.Value, 6);    // and the thumb stays under the finger

        // A drag's value survives the next detach too (it must not fall back to the 1.4 placed before).
        seen.Clear();
        host.Children.Remove(slider);
        window.UpdateLayout();
        host.Children.Add(slider);
        window.UpdateLayout();
        Assert.Empty(seen);
        Assert.Equal(1.2, knob.Scale, 6);
        Assert.Equal(1.23, slider.Value, 6);
        window.Close();
    }
}
