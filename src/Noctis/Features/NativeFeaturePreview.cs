#if DEBUG
using Avalonia.Controls;
using FreeMusicFinder;
using Noctis.Helpers;
using Noctis.ViewModels;
using Noctis.Views;

namespace Noctis.Features;

// A real-app visual check with synthetic data. Never reads credentials, opens a network
// connection, or writes a Telegram store. Not compiled into Release builds.
internal static class NativeFeaturePreview
{
    internal static async Task ShowAsync(Window owner, string preview)
    {
        if (!preview.StartsWith("telegram-", StringComparison.Ordinal)) return;
        var dialog = new TelegramSignInDialog(new PreviewAccount(), _ => { });
        var vm = (TelegramSignInViewModel)dialog.DataContext!;
        vm.Step = preview switch
        {
            "telegram-code" => TelegramSignInViewModel.LoginStep.Code,
            "telegram-password" => TelegramSignInViewModel.LoginStep.Password,
            "telegram-connected" => TelegramSignInViewModel.LoginStep.SignedIn,
            _ => TelegramSignInViewModel.LoginStep.Phone,
        };
        if (preview == "telegram-error") vm.Error = TelegramSignInViewModel.Explain(new IOException());
        if (preview == "telegram-busy") vm.IsBusy = true;
        DialogHelper.SizeToOwner(dialog, owner);
        await dialog.ShowDialog(owner);
    }

    private sealed class PreviewAccount : ITelegramSignIn
    {
        public bool IsLoggedIn => false;
        public string SignedInAs => "Test account";
        public int ApiId => 12345;
        public string ApiHash => "0123456789abcdef0123456789abcdef";
        public bool HasCredentials => true;
        public string DefaultsError => "";
        public Task<string?> StartLoginAsync(int id, string hash, string phone) => Task.FromResult<string?>("verification_code");
        public Task<string?> ContinueLoginAsync(string answer) => Task.FromResult<string?>(null);
        public Task LogOutAsync() => Task.CompletedTask;
    }
}
#endif
