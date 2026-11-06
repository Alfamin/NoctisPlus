using Avalonia.Controls;
using Avalonia.Interactivity;
namespace Noctis.Mobile.Views;
public partial class TelegramPane : UserControl
{
    public TelegramPane() => InitializeComponent();

    /// <summary>#281: closes the keyboard so the progress line and any error under the button are seen.</summary>
    private void OnSubmitClick(object? sender, RoutedEventArgs e) => TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
}
