using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Features.WordLyrics;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class WordLyricsDialog : Window
{
    public WordLyricsDialog() => InitializeComponent();
    internal WordLyricsDialog(WordLyricsService service, bool library, Track? track) : this()
    {
        var vm = new WordLyricsViewModel(service, library, track, text => ConfirmationDialog.ShowAsync(this, text));
        DataContext = vm; Closed += (_, _) => vm.Dispose();
    }
    internal static Task ShowAsync(Window owner, WordLyricsService service, bool library, Track? track)
    {
        var dialog = new WordLyricsDialog(service, library, track);
        DialogHelper.SizeToOwner(dialog, owner); return dialog.ShowDialog(owner);
    }
    private void OnDone(object? sender, RoutedEventArgs e) => Close();
    protected override void OnKeyDown(KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; Close(); } else base.OnKeyDown(e); }
}
