using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Noctis.Controls;

public partial class LottieToggle : UserControl
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<LottieToggle, bool>(
            nameof(IsChecked),
            defaultBindingMode: BindingMode.TwoWay);

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public LottieToggle()
    {
        InitializeComponent();
        Focusable = true;
    }

    private static IBrush ResolveResourceBrush(string key, string fallbackHex)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var b) == true && b is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    private EventHandler? _accentHandler;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateVisualState();

        _accentHandler = (_, _) => UpdateVisualState();
        App.AccentApplied += _accentHandler;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_accentHandler != null)
        {
            App.AccentApplied -= _accentHandler;
            _accentHandler = null;
        }
        base.OnUnloaded(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsCheckedProperty)
            UpdateVisualState();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            IsChecked = !IsChecked;
            e.Handled = true;
        }
    }

    // Keyboard: Space / Enter flip the switch like a ToggleButton. It used to take mouse
    // presses only — not focusable, so Settings' switches were unreachable by keyboard.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is Key.Space or Key.Enter)
        {
            IsChecked = !IsChecked;
            e.Handled = true;
        }
    }

    // Screen readers / UI Automation: a toggle with an on/off state. The default
    // UserControl peer exposed a nameless Custom element with no state and no way to flip it.
    protected override AutomationPeer OnCreateAutomationPeer() => new LottieTogglePeer(this);

    private sealed class LottieTogglePeer : ControlAutomationPeer, IToggleProvider
    {
        public LottieTogglePeer(LottieToggle owner) : base(owner)
        {
            owner.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsCheckedProperty)
                    RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                        ToState(e.GetOldValue<bool>()), ToState(e.GetNewValue<bool>()));
            };
        }

        private LottieToggle Toggle_ => (LottieToggle)Owner;

        public ToggleState ToggleState => ToState(Toggle_.IsChecked);

        public void Toggle()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            Toggle_.IsChecked = !Toggle_.IsChecked;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;

        private static ToggleState ToState(bool on) => on ? ToggleState.On : ToggleState.Off;
    }

    private void UpdateVisualState()
    {
        if (Track is null || Knob is null) return;

        if (IsChecked)
        {
            Track.Background = ResolveResourceBrush("AccentColorBrush", "#E74856");
            Knob.Background = ResolveResourceBrush("AccentForegroundBrush", "#FFFFFF");
            Knob.Margin = new Thickness(29, 0, 0, 0);
        }
        else
        {
            Track.Background = ResolveResourceBrush("ToggleTrackOffBrush", "#484C54");
            Knob.Background = ResolveResourceBrush("ToggleKnobOffBrush", "#FFFFFF");
            Knob.Margin = new Thickness(3, 0, 0, 0);
        }
    }
}
