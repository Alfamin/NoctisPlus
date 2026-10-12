using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Settings switch took mouse presses only: not focusable, no keyboard toggle, and its
/// automation peer was a stateless Custom element, so neither the keyboard nor a screen
/// reader could flip "Exclusive Mode" and the other ~90 switches.
/// </summary>
public class LottieToggleAccessibilityTests
{
    private static (Window Win, LottieToggle Toggle) Show()
    {
        var toggle = new LottieToggle();
        var win = new Window { Content = toggle, Width = 200, Height = 100 };
        win.Show();
        return (win, toggle);
    }

    [AvaloniaFact]
    public void Space_And_Enter_Flip_The_Focused_Switch()
    {
        var (win, toggle) = Show();
        Assert.True(toggle.Focusable);
        Assert.True(toggle.Focus());

        win.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.True(toggle.IsChecked);
        win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.False(toggle.IsChecked);
        win.Close();
    }

    [AvaloniaFact]
    public void Modified_Keys_Leave_The_Switch_Alone()
    {
        var (win, toggle) = Show();
        toggle.Focus();
        win.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Assert.False(toggle.IsChecked);
        win.Close();
    }

    [AvaloniaFact]
    public void Automation_Peer_Is_A_Toggle_With_State()
    {
        var (win, toggle) = Show();
        var peer = ControlAutomationPeer.CreatePeerForElement(toggle);
        Assert.Equal(AutomationControlType.CheckBox, peer.GetAutomationControlType());

        var provider = Assert.IsAssignableFrom<IToggleProvider>(peer);
        Assert.Equal(ToggleState.Off, provider.ToggleState);
        provider.Toggle();
        Assert.True(toggle.IsChecked);
        Assert.Equal(ToggleState.On, provider.ToggleState);
        win.Close();
    }

    [AvaloniaFact]
    public void Disabled_Switch_Refuses_Automation_Toggle()
    {
        var (win, toggle) = Show();
        toggle.IsEnabled = false;
        var provider = (IToggleProvider)ControlAutomationPeer.CreatePeerForElement(toggle);
        Assert.Throws<ElementNotEnabledException>(() => provider.Toggle());
        Assert.False(toggle.IsChecked);
        win.Close();
    }
}
