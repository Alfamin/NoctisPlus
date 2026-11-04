using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Mobile.Services.Telegram;

namespace Noctis.Mobile.ViewModels.Settings;

public enum TelegramStep { Phone, Code, Password, Done }

/// <summary>#255 part 1: Telegram sign-in on the phone. Phone -> code -> password (only if asked)
/// -> done. Errors are one short line in <see cref="ErrorText"/>. Advanced holds an optional MTProto proxy.</summary>
public sealed partial class TelegramPaneViewModel : ObservableObject
{
    private readonly IPhoneTelegramSignIn _signIn;

    public TelegramPaneViewModel(IPhoneTelegramSignIn signIn)
    {
        _signIn = signIn;
        if (signIn.IsLoggedIn) { Step = TelegramStep.Done; SignedInAs = signIn.SignedInAs; }
    }

    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private TelegramStep _step = TelegramStep.Phone;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _signedInAs;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private bool _useProxy;
    [ObservableProperty] private string _proxyServer = "";
    [ObservableProperty] private string _proxySecret = "";

    public bool IsSignedIn => Step == TelegramStep.Done;
    partial void OnStepChanged(TelegramStep value) => OnPropertyChanged(nameof(IsSignedIn));

    /// <summary>Code field auto-submits on 5 digits.</summary>
    partial void OnCodeChanged(string value)
    {
        if (Step == TelegramStep.Code && !IsBusy && value.Count(char.IsDigit) == 5)
            _ = SubmitCodeAsync();
    }

    [RelayCommand]
    private async Task SendCodeAsync()
    {
        var phone = new string(Phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (phone.Count(char.IsDigit) < 7) { ErrorText = "Enter your number"; return; }
        if (UseProxy && string.IsNullOrWhiteSpace(ProxyServer)) { ErrorText = "Enter the proxy server"; return; }
        await RunAsync(() => _signIn.StartLoginAsync(phone,
            UseProxy ? ProxyServer.Trim() : null, UseProxy ? ProxySecret.Trim() : null));
    }

    [RelayCommand]
    private async Task SubmitCodeAsync()
    {
        var code = new string(Code.Where(char.IsDigit).ToArray());
        if (code.Length == 0) { ErrorText = "Enter the code"; return; }
        await RunAsync(() => _signIn.ContinueLoginAsync(code));
    }

    [RelayCommand]
    private async Task SubmitPasswordAsync()
    {
        if (string.IsNullOrEmpty(Password)) { ErrorText = "Enter your password"; return; }
        await RunAsync(() => _signIn.ContinueLoginAsync(Password));
    }

    [RelayCommand]
    private void ChangeNumber()
    {
        Step = TelegramStep.Phone; Code = ""; Password = ""; ErrorText = null;
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        IsBusy = true; ErrorText = null;
        try { await _signIn.LogOutAsync(); Step = TelegramStep.Phone; SignedInAs = null; Code = ""; Password = ""; }
        catch (Exception ex) { ErrorText = ShortError(ex); }
        finally { IsBusy = false; }
    }

    private async Task RunAsync(Func<Task<string?>> call)
    {
        if (IsBusy) return;
        IsBusy = true; ErrorText = null; StatusText = "Connecting…";
        try
        {
            var next = await call();
            Step = next switch
            {
                "verification_code" => TelegramStep.Code,
                "password" => TelegramStep.Password,
                null => TelegramStep.Done,
                _ => Step,
            };
            if (Step == TelegramStep.Done) { SignedInAs = _signIn.SignedInAs; Code = ""; Password = ""; }
        }
        catch (Exception ex) { ErrorText = ShortError(ex); }
        finally { IsBusy = false; StatusText = null; }
    }

    /// <summary>Maps Telegram errors to one short line.</summary>
    public static string ShortError(Exception ex)
    {
        var m = ex.Message ?? "";
        var flood = Regex.Match(m, @"FLOOD_WAIT_?(\d+)");
        if (flood.Success && int.TryParse(flood.Groups[1].Value, out var s))
            return s >= 60 ? $"Too many tries. Wait {(s + 59) / 60} min" : $"Too many tries. Wait {s} s";
        if (m.Contains("PHONE_CODE_INVALID")) return "Wrong code";
        if (m.Contains("PHONE_CODE_EXPIRED")) return "Code expired. Send again";
        if (m.Contains("PASSWORD_HASH_INVALID")) return "Wrong password";
        if (m.Contains("PHONE_NUMBER_INVALID")) return "Check the number";
        if (ex is System.Net.Sockets.SocketException or TimeoutException or HttpRequestException)
            return "Can't reach Telegram";
        return "Sign-in failed. Try again";
    }
}
