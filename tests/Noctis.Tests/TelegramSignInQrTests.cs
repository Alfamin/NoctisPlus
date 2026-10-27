using Avalonia.Headless.XUnit;
using FreeMusicFinder;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #185: a new user signs in with a QR code and the built-in Telegram app; the phone number stays a fallback.</summary>
public class TelegramSignInQrTests
{
    private sealed class FakeAccount(bool builtIn, bool ownPair = false) : ITelegramSignIn
    {
        public (int Id, string Hash)? QrApi, PhoneApi;
        public Action<string>? Show;
        public Action<bool>? PasswordNeeded;
        public CancellationToken QrToken;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Passwords = [];
        public bool IsLoggedIn { get; set; }
        public string SignedInAs => "@friend";
        public int ApiId => ownPair ? 98765 : builtIn ? 12345 : 0;
        public string ApiHash => ownPair ? "fedcba9876543210fedcba9876543210" : builtIn ? "0123456789abcdef0123456789abcdef" : "";
        public bool HasCredentials => ApiId > 0;
        public string DefaultsError => "";
        public bool UsesBuiltInApi => builtIn && !ownPair;
        public Task<string?> StartLoginAsync(int apiId, string apiHash, string phone) { PhoneApi = (apiId, apiHash); return Task.FromResult<string?>("verification_code"); }
        public Task<string?> ContinueLoginAsync(string answer) => Task.FromResult<string?>(null);
        public Task LogOutAsync() => Task.CompletedTask;
        public async Task QrLoginAsync(int apiId, string apiHash, Action<string> show, Action<bool> passwordNeeded, CancellationToken ct)
        {
            QrApi = (apiId, apiHash); Show = show; PasswordNeeded = passwordNeeded; QrToken = ct;
            show("tg://login?token=AAAA");
            await Done.Task.WaitAsync(ct);
            IsLoggedIn = true;
        }
        public void SendQrPassword(string password) => Passwords.Add(password);
    }

    private static TelegramSignInViewModel Vm(FakeAccount account) => new(account, _ => { });

    [AvaloniaFact]
    public async Task ANewUser_SeesAQrCode_WithTheBuiltInAppFilledIn_ButNotSavedAsTheirOwn()
    {
        var account = new FakeAccount(builtIn: true);
        var vm = Vm(account);
        Assert.True(vm.IsQr);
        Assert.Equal("12345", vm.ApiId);
        Assert.Equal("0123456789abcdef0123456789abcdef", vm.ApiHash);
        Assert.False(vm.AdvancedOpen);
        Assert.False(vm.ShowContinue); // nothing to press while waiting for the scan
        var signIn = vm.StartQrAsync();
        Assert.Equal((0, ""), account.QrApi);
        Assert.NotNull(vm.QrImage);
        Assert.False(vm.QrWaiting);
        account.Done.SetResult();
        await signIn;
        Assert.True(vm.IsSignedIn);
    }

    [AvaloniaFact]
    public async Task ATwoStepPassword_IsAskedInTheDialog_AndAgainWhenWrong()
    {
        var account = new FakeAccount(builtIn: true);
        var vm = Vm(account);
        var signIn = vm.StartQrAsync();
        account.PasswordNeeded!(false);
        Assert.True(vm.IsPassword);
        vm.Password = "secret";
        await vm.ContinueCommand.ExecuteAsync(null);
        Assert.Equal(["secret"], account.Passwords);
        Assert.True(vm.IsBusy);
        account.PasswordNeeded!(true);
        Assert.True(vm.IsPassword);
        Assert.False(vm.IsBusy);
        Assert.Equal("That password is not right.", vm.Error);
        account.Done.SetResult();
        await signIn;
        Assert.True(vm.IsSignedIn);
    }

    [AvaloniaFact]
    public async Task UsePhoneNumber_StopsTheQrCode_AndStillNeedsNoApiPair()
    {
        var account = new FakeAccount(builtIn: true);
        var vm = Vm(account);
        var signIn = vm.StartQrAsync();
        vm.UsePhoneCommand.Execute(null);
        await signIn;
        Assert.True(account.QrToken.IsCancellationRequested);
        Assert.True(vm.IsPhone);
        Assert.Equal("", vm.Error);
        vm.Phone = "+98 912 345 6789";
        await vm.ContinueCommand.ExecuteAsync(null);
        Assert.Equal((0, ""), account.PhoneApi);
        Assert.True(vm.IsCode);
        vm.UseQrCommand.Execute(null);
        Assert.True(vm.IsQr);
        vm.Dispose();
        Assert.True(account.QrToken.IsCancellationRequested);
    }

    [AvaloniaFact]
    public void WithoutABuiltInApp_ThePhonePageOpensWithAdvanced_AndAnOwnPairIsUsedForQr()
    {
        var none = Vm(new FakeAccount(builtIn: false));
        Assert.True(none.IsPhone);
        Assert.True(none.AdvancedOpen);
        none.UseQrCommand.Execute(null);
        Assert.True(none.IsPhone);
        Assert.StartsWith("TELEGRAM_API_MISSING", none.Error);

        var own = new FakeAccount(builtIn: true, ownPair: true);
        var vm = Vm(own);
        Assert.Equal("98765", vm.ApiId);
        _ = vm.StartQrAsync();
        Assert.Equal((98765, "fedcba9876543210fedcba9876543210"), own.QrApi);
        vm.Dispose();
    }

    [Fact]
    public void NewInstalls_DefaultToMp3_EarlierUsersKeepFlac()
    {
        var root = Path.Combine(Path.GetTempPath(), "noctis-quality-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fresh = new Dictionary<string, string>();
            Assert.True(QualityDefault.Settle(fresh, Path.Combine(root, "missing")));
            Assert.Equal("MP3", fresh[QualityDefault.Key]);
            Assert.False(QualityDefault.Settle(fresh, root)); // decided once

            var changedSomething = new Dictionary<string, string> { ["firstBot"] = "DeezLoad" };
            QualityDefault.Settle(changedSomething, Path.Combine(root, "missing"));
            Assert.Equal("FLAC", changedSomething[QualityDefault.Key]);

            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "bot-timings.json"), "{}");
            var usedFinder = new Dictionary<string, string>();
            QualityDefault.Settle(usedFinder, root);
            Assert.Equal("FLAC", usedFinder[QualityDefault.Key]);

            var chose = new Dictionary<string, string> { [QualityDefault.Key] = "MP3" };
            Assert.False(QualityDefault.Settle(chose, root));
            Assert.Equal("MP3", chose[QualityDefault.Key]);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
