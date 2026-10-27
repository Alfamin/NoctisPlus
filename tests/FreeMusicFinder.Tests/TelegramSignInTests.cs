using Avalonia.Threading;
using FreeMusicFinder;
using Noctis.ViewModels;

namespace FreeMusicFinder.Tests;

internal static class TelegramSignInTests
{
    internal static void Run()
    {
        Check.Section("native Telegram sign-in: synthetic accounts only");
        var account = new Account();
        var logs = new List<string>();
        using var vm = new TelegramSignInViewModel(account, logs.Add);
        Check.True(vm.IsQr && !vm.AdvancedOpen && !vm.ShowContinue, "owner #185: ready credentials open on the QR code");
        vm.UsePhoneCommand.Execute(null);
        Check.True(vm.IsPhone && vm.ShowContinue, "Use phone number keeps the phone steps one click away");
        vm.Phone = "9123456789";
        Run(vm.ContinueCommand.ExecuteAsync(null));
        Check.True(vm.HasError && account.Calls == 0, "invalid phone does not call Telegram");
        vm.ApiHash = "wrong";
        vm.Phone = "+۹۸۹۱۲۳۴۵۶۷۸۹";
        Run(vm.ContinueCommand.ExecuteAsync(null));
        Check.True(vm.AdvancedOpen && account.Calls == 0, "invalid API settings reveal Advanced without a connection");
        vm.ApiHash = account.ApiHash;
        Run(vm.ContinueCommand.ExecuteAsync(null));
        Check.True(vm.IsCode && account.Phone == "+989123456789", "phone normalization advances to code");
        vm.Code = "";
        Run(vm.ContinueCommand.ExecuteAsync(null));
        Check.Equal(1, account.Calls, "empty code sends no request");
        vm.Code = "۱۲۳۴۵";
        account.Next = "password";
        Run(vm.ContinueCommand.ExecuteAsync(null));
        Check.True(vm.IsPassword && account.Answer == "12345", "Persian verification code advances to password");
        vm.Password = "synthetic-secret";
        account.Next = null;
        Run(vm.ContinueCommand.ExecuteAsync(null));
        Check.True(vm.IsSignedIn && vm.Password.Length == 0, "successful password clears the field and shows connected");
        var closed = false;
        vm.CloseRequested += () => closed = true;
        Run(vm.ContinueCommand.ExecuteAsync(null));
        Check.True(closed && account.Logouts == 0, "Done keeps the session");
        Run(vm.StartOverCommand.ExecuteAsync(null));
        Check.Equal(0, account.Logouts, "start over cannot erase a signed-in account");
        Run(vm.DisconnectCommand.ExecuteAsync(null));
        Check.True(vm.IsQr && account.Logouts == 1 && account.QrCalls == 1, "explicit confirmed disconnect returns to the QR code");
        vm.UsePhoneCommand.Execute(null);

        var pending = new TaskCompletionSource<string?>();
        account.Pending = pending.Task;
        vm.Phone = "+989123456789";
        var request = vm.ContinueCommand.ExecuteAsync(null);
        Check.True(vm.IsBusy && !vm.ContinueCommand.CanExecute(null) && !vm.StartOverCommand.CanExecute(null), "pending login disables repeat submit and start over");
        vm.Dispose();
        pending.SetResult("verification_code");
        Run(request);
        Check.True(vm.IsPhone && vm.ApiHash.Length == 0 && vm.Phone.Length == 0 && !vm.ContinueCommand.CanExecute(null), "close clears private fields and ignores a late answer");

        var failing = new Account { Pending = Task.FromException<string?>(new IOException("secret-url=do-not-log")) };
        using var errorVm = new TelegramSignInViewModel(failing, logs.Add) { Phone = "+989123456789" };
        errorVm.UsePhoneCommand.Execute(null);
        Run(errorVm.ContinueCommand.ExecuteAsync(null));
        Check.True(errorVm.IsPhone && errorVm.Error.Contains("Settings → Features") && !errorVm.IsBusy, "network error stays recoverable and points at native settings");
        Check.False(logs.Any(l => l.Contains("secret-url") || l.Contains("synthetic-secret")), "log records exception type without credentials");
        Check.False(TelegramSignInViewModel.Explain(new Exception("private response +989123456789")).Contains("989"), "unexpected errors do not echo private responses");

        var rejected = new Account();
        using var rejectedVm = new TelegramSignInViewModel(rejected, logs.Add) { Phone = "+989123456789" };
        rejectedVm.UsePhoneCommand.Execute(null);
        Run(rejectedVm.ContinueCommand.ExecuteAsync(null));
        rejected.Pending = Task.FromException<string?>(new Exception("API_ID_INVALID"));
        rejectedVm.Code = "12345";
        Run(rejectedVm.ContinueCommand.ExecuteAsync(null));
        Check.True(rejectedVm.IsPhone && rejectedVm.AdvancedOpen && rejectedVm.Code.Length == 0,
            "rejected credentials during a later step reveal editable Advanced fields");
    }

    private static void Run(Task task)
    {
        Check.Until(() => task.IsCompleted, "sign-in operation completes");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Account : ITelegramSignIn
    {
        public bool IsLoggedIn { get; private set; }
        public string SignedInAs => "Test account";
        public int ApiId => 12345;
        public string ApiHash => "0123456789abcdef0123456789abcdef";
        public bool HasCredentials => true;
        public string DefaultsError => "";
        public bool UsesBuiltInApi => false;
        public int QrCalls;
        public Task QrLoginAsync(int id, string hash, Action<string> show, Action<bool> passwordNeeded, CancellationToken ct) { QrCalls++; return Task.Delay(Timeout.Infinite, ct); }
        public void SendQrPassword(string password) { }
        public string? Next = "verification_code";
        public Task<string?>? Pending;
        public int Calls, Logouts;
        public string Phone = "", Answer = "";
        public Task<string?> StartLoginAsync(int id, string hash, string phone) { Calls++; Phone = phone; return Pending ?? Task.FromResult(Next); }
        public Task<string?> ContinueLoginAsync(string answer) { Calls++; Answer = answer; if (Next is null) IsLoggedIn = true; return Pending ?? Task.FromResult(Next); }
        public Task LogOutAsync() { Logouts++; IsLoggedIn = false; return Task.CompletedTask; }
    }
}
