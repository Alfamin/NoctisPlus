using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Mobile.Services.Telegram;

namespace Noctis.Mobile.ViewModels.Settings;

public enum TelegramStep { Phone, Code, Password, Done }

/// <summary>#255 part 1: Telegram sign-in on the phone. Phone -> code -> password (only if asked)
/// -> done. Errors are one short line in <see cref="ErrorText"/>. Advanced holds an optional MTProto proxy.</summary>
public sealed partial class TelegramPaneViewModel : ObservableObject, IDisposable
{
    private readonly IPhoneTelegramSignIn _signIn;
    private CancellationTokenSource? _attempt;

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
    public bool IsPhoneStep => Step == TelegramStep.Phone;
    public bool IsCodeStep => Step == TelegramStep.Code;
    public bool IsPasswordStep => Step == TelegramStep.Password;
    partial void OnStepChanged(TelegramStep value)
    {
        OnPropertyChanged(nameof(IsSignedIn)); OnPropertyChanged(nameof(IsPhoneStep));
        OnPropertyChanged(nameof(IsCodeStep)); OnPropertyChanged(nameof(IsPasswordStep));
    }

    /// <summary>Do not guess a delivery's code length.</summary>
    partial void OnCodeChanged(string value)
    {
        ErrorText = null;
        if (Step == TelegramStep.Code && !IsBusy && _signIn.ExpectedCodeLength is > 0 and <= 12 and var length && value.Count(char.IsDigit) == length)
            _ = SubmitCodeAsync();
    }
    partial void OnPhoneChanged(string value) => ErrorText = null;
    partial void OnPasswordChanged(string value) => ErrorText = null;

    [RelayCommand]
    private async Task SendCodeAsync()
    {
        var phone = new string(Phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (phone.Count(char.IsDigit) < 7) { ErrorText = "Enter your number"; return; }
        if (UseProxy && string.IsNullOrWhiteSpace(ProxyServer)) { ErrorText = "Enter the proxy server"; return; }
        await RunAsync(ct => _signIn.StartLoginAsync(phone,
            UseProxy ? ProxyServer.Trim() : null, UseProxy ? ProxySecret.Trim() : null, ct));
    }

    [RelayCommand]
    private async Task SubmitCodeAsync()
    {
        var code = new string(Code.Where(char.IsDigit).ToArray());
        if (code.Length == 0) { ErrorText = "Enter the code"; return; }
        await RunAsync(ct => _signIn.ContinueLoginAsync(code, ct));
    }

    [RelayCommand]
    private async Task SubmitPasswordAsync()
    {
        if (string.IsNullOrEmpty(Password)) { ErrorText = "Enter your password"; return; }
        await RunAsync(ct => _signIn.ContinueLoginAsync(Password, ct));
    }

    [RelayCommand]
    private async Task ChangeNumberAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _signIn.ResetLoginAsync();
            Step = TelegramStep.Phone; Code = ""; Password = ""; ErrorText = null;
        }
        catch (Exception ex) { ErrorText = ShortError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (IsBusy) return;
        IsBusy = true; ErrorText = null;
        try { await _signIn.LogOutAsync(); Step = TelegramStep.Phone; SignedInAs = null; Code = ""; Password = ""; }
        catch (Exception ex) { ErrorText = ShortError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CancelSignIn() => _attempt?.Cancel();

    public void Dispose() => CancelSignIn();

    private async Task RunAsync(Func<CancellationToken, Task<string?>> call)
    {
        if (IsBusy) return;
        IsBusy = true; ErrorText = null; StatusText = "Connecting…";
        using var attempt = new CancellationTokenSource();
        _attempt = attempt;
        try
        {
            var next = await call(attempt.Token).WaitAsync(attempt.Token);
            if (next is not ("verification_code" or "password" or null))
            {
                await _signIn.ResetLoginAsync(attempt.Token);
                Step = TelegramStep.Phone;
                ErrorText = "Sign in in the Telegram app first";
                return;
            }
            Step = next switch
            {
                "verification_code" => TelegramStep.Code,
                "password" => TelegramStep.Password,
                null => TelegramStep.Done,
                _ => Step,
            };
            if (Step == TelegramStep.Done) { SignedInAs = _signIn.SignedInAs; Code = ""; Password = ""; }
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
        catch (Exception ex) { ErrorText = ShortError(ex); }
        finally { _attempt = null; IsBusy = false; StatusText = null; }
    }

    /// <summary>Maps Telegram errors to one short line.</summary>
    public static string ShortError(Exception ex)
    {
        var m = ex.Message ?? "";
        if (m.Contains("TELEGRAM_APP_MISSING")) return "Telegram setup is missing from this build";
        if (ex is FormatException or UriFormatException) return "Check the proxy address";
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
