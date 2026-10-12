using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Noctis.Views;

/// <summary>
/// The lyrics page's Background Color pop-up (Artwork / Solid / Gradient and their swatches),
/// in the shared rounded pill shell. It works on the page's own LyricsViewModel: every pick
/// runs the same command the old strip under the player bar ran, so the page behind repaints
/// at once and the choice is saved as before. Nothing is pending, so Done, Escape and a click
/// outside the card all just close.
/// </summary>
public partial class LyricsBackgroundColorDialog : Window
{
    public LyricsBackgroundColorDialog()
    {
        InitializeComponent();
        // On the window, not the host: outside the card the host draws nothing hit-testable,
        // so a press there reaches the window alone (as in SongsViewOptionsDialog).
        AddHandler(PointerPressedEvent, OnBackdropPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Handled here so the key never reaches anything else; PillDialogHost turns the
        // Close into the animated one.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e) => Close();

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Inside the card (its rounded corners included) is the card's business.
        if (new Rect(PickerCard.Bounds.Size).Contains(e.GetPosition(PickerCard))) return;
        e.Handled = true;
        Close();
    }
}
