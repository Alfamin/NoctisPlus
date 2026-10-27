using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreeMusicFinder;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class TelegramSignInDialog : Window
{
    private readonly TelegramSignInViewModel _vm = null!;

    public TelegramSignInDialog() => InitializeComponent();

    internal TelegramSignInDialog(ITelegramSignIn account, Action<string> log) : this()
    {
        DataContext = _vm = new TelegramSignInViewModel(account, log);
        _vm.CloseRequested += Close;
        _vm.PropertyChanged += OnStateChanged;
        // Owner #185: asked for while the dialog opens, so the code is there the moment it shows.
        if (_vm.IsQr) _ = _vm.StartQrAsync();
        Opened += (_, _) => FocusStep();
        Closed += (_, _) =>
        {
            _vm.CloseRequested -= Close;
            _vm.PropertyChanged -= OnStateChanged;
            _vm.Dispose();
        };
    }

    internal static Task ShowAsync(Window owner, ITelegramSignIn account, Action<string> log)
    {
        var dialog = new TelegramSignInDialog(account, log);
        DialogHelper.SizeToOwner(dialog, owner);
        return dialog.ShowDialog(owner);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(_vm.Step) or nameof(_vm.IsBusy)) FocusStep();
    }

    private void FocusStep() => Dispatcher.UIThread.Post(() =>
    {
        if (!IsVisible || _vm.IsBusy) return;
        (_vm.Step switch
        {
            TelegramSignInViewModel.LoginStep.Phone => (Control)PhoneField,
            TelegramSignInViewModel.LoginStep.Code => CodeField,
            TelegramSignInViewModel.LoginStep.Password => PasswordField,
            _ => ContinueButton,
        }).Focus();
    }, DispatcherPriority.Loaded);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanInteract) return;
        try
        {
            if (await ConfirmationDialog.ShowAsync(this, "Disconnect Telegram on this computer? Your downloaded songs and queue are kept."))
                await _vm.DisconnectCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { _vm.Error = TelegramSignInViewModel.Explain(ex); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_vm is null) { base.OnKeyDown(e); return; }
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.Key == Key.Enter && _vm.ShowContinue && _vm.ContinueCommand.CanExecute(null))
        {
            e.Handled = true;
            _vm.ContinueCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
