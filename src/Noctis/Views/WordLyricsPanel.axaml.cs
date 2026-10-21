using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Noctis.Views;

/// <summary>Word timing progress and actions, hosted in the main window or in <see cref="WordLyricsDialog"/>.</summary>
public partial class WordLyricsPanel : UserControl
{
    public WordLyricsPanel() => InitializeComponent();
    internal void UseWindowBackdrop() => TimingHost.ShowBackdrop = true;

    /// <summary>Done was pressed: the host closes the panel. Bubbles, so a host can listen from above a template.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CloseRequestedEvent =
        RoutedEvent.Register<WordLyricsPanel, RoutedEventArgs>(nameof(CloseRequested), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs>? CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    private void OnDone(object? sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(CloseRequestedEvent));
}
