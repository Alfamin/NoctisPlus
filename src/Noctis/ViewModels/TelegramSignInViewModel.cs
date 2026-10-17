using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;

namespace Noctis.ViewModels;

/// <summary>Phone → code → optional password, using the existing private Telegram session store.</summary>
public sealed partial class TelegramSignInViewModel : ObservableObject, IDisposable
{
    public enum LoginStep { Phone, Code, Password, SignedIn }
    private readonly ITelegramSignIn _account;
    private readonly Action<string> _log;
    private bool _disposed;

    internal TelegramSignInViewModel(ITelegramSignIn account, Action<string> log)
    {
        _account = account;
        _log = log;
        ApiId = account.ApiId > 0 ? account.ApiId.ToString(CultureInfo.InvariantCulture) : "";
        ApiHash = account.ApiHash;
        AdvancedOpen = !account.HasCredentials;
        Step = account.IsLoggedIn ? LoginStep.SignedIn : LoginStep.Phone;
        Error = account.DefaultsError;
    }

    public event Action? CloseRequested;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPhone), nameof(IsCode), nameof(IsPassword), nameof(IsSignedIn), nameof(Title), nameof(Description), nameof(ContinueLabel), nameof(CanStartOver))]
    private LoginStep _step;
    [ObservableProperty] private string _apiId = "";
    [ObservableProperty] private string _apiHash = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _advancedOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInteract)), NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(StartOverCommand), nameof(DisconnectCommand))]
    private bool _isBusy;

    public bool CanInteract => !IsBusy && !_disposed;
    public bool IsPhone => Step == LoginStep.Phone;
    public bool IsCode => Step == LoginStep.Code;
    public bool IsPassword => Step == LoginStep.Password;
    public bool IsSignedIn => Step == LoginStep.SignedIn;
    public bool CanStartOver => IsCode || IsPassword;
    public bool HasError => Error.Length > 0;
    [RelayCommand] private void ToggleAdvanced() => AdvancedOpen = !AdvancedOpen;
    public string ContinueLabel => IsSignedIn ? "Done" : IsPhone ? "Send code" : "Continue";
    public string Title => Step switch
    {
        LoginStep.Code => "Check Telegram",
        LoginStep.Password => "Two-step verification",
        LoginStep.SignedIn => "Telegram connected",
        _ => "Connect Telegram",
    };
    public string Description => Step switch
    {
        LoginStep.Code => "Enter the login code sent to your Telegram app or by SMS.",
        LoginStep.Password => "Enter your Telegram password. It is never saved.",
        LoginStep.SignedIn => $"Signed in as {_account.SignedInAs}. You can now download songs.",
        _ => "Use your own phone number to download music. Your session stays on this computer.",
    };

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task Continue()
    {
        if (!CanInteract) return;
        if (IsSignedIn) { CloseRequested?.Invoke(); return; }
        Error = "";
        IsBusy = true;
        try
        {
            string? next;
            if (IsPhone)
            {
                var phone = Digits(Phone.Trim());
                var hash = ApiHash.Trim();
                if (!int.TryParse(Digits(ApiId.Trim()), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !Regex.IsMatch(hash, "\\A[a-fA-F0-9]{32}\\z"))
                {
                    AdvancedOpen = true;
                    Error = "TELEGRAM_API_MISSING: enter a positive API id and a 32-character API hash under Advanced.";
                    return;
                }
                if (!phone.StartsWith('+') || phone[1..].Count(char.IsDigit) is < 7 or > 15 || phone[1..].Any(ch => !char.IsDigit(ch) && ch != ' ' && ch != '-'))
                {
                    Error = "PHONE_NUMBER_INVALID: include your country code, for example +98 followed by your number.";
                    return;
                }
                next = await _account.StartLoginAsync(id, hash, string.Concat(phone.Where(ch => ch == '+' || char.IsDigit(ch))));
            }
            else if (IsCode)
            {
                var code = Digits(Code.Trim());
                if (code.Length == 0 || code.Any(ch => ch is < '0' or > '9')) { Error = "Enter the numeric login code from Telegram."; return; }
                next = await _account.ContinueLoginAsync(code);
            }
            else
            {
                if (Password.Length == 0) { Error = "Enter your two-step verification password."; return; }
                next = await _account.ContinueLoginAsync(Password);
            }
            if (_disposed) return;
            Code = Password = "";
            switch (next)
            {
                case null: Step = LoginStep.SignedIn; break;
                case "verification_code": Step = LoginStep.Code; break;
                case "password": Step = LoginStep.Password; break;
                case "name": Error = "There is no Telegram account for this phone number. Check the number and start over."; break;
                default: Error = "TELEGRAM_LOGIN_UNSUPPORTED: Telegram requested an unsupported sign-in step. Start over or try again later."; break;
            }
        }
        catch (Exception ex)
        {
            _log("Telegram login failed: " + ex.GetType().Name);
            if (!_disposed)
            {
                if (ex.Message.Contains("API_ID", StringComparison.Ordinal) || ex.Message.Contains("API_HASH", StringComparison.Ordinal))
                {
                    Step = LoginStep.Phone;
                    AdvancedOpen = true;
                    Code = "";
                }
                Error = Explain(ex);
            }
        }
        finally { Password = ""; IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task StartOver()
    {
        // This action is only for an incomplete login; closing an existing session never logs out.
        if (!CanInteract || IsSignedIn) return;
        IsBusy = true;
        Error = "";
        try
        {
            await _account.LogOutAsync();
            if (!_disposed) { Code = Password = ""; Step = LoginStep.Phone; }
        }
        catch (Exception ex)
        {
            _log("Telegram login reset failed: " + ex.GetType().Name);
            if (!_disposed) Error = Explain(ex);
        }
        finally { IsBusy = false; }
    }

    // Only called after the view's native confirmation. Cancelling/closing does not invoke this.
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task Disconnect()
    {
        if (!CanInteract || !IsSignedIn) return;
        IsBusy = true;
        try
        {
            await _account.LogOutAsync();
            if (!_disposed) { Error = ""; Step = LoginStep.Phone; }
        }
        catch (Exception ex)
        {
            _log("Telegram logout failed: " + ex.GetType().Name);
            if (!_disposed) Error = Explain(ex);
        }
        finally { IsBusy = false; }
    }

    internal static string Digits(string value) => string.Concat(value.Select(ch => char.IsDigit(ch) ? (char)('0' + (int)char.GetNumericValue(ch)) : ch));
    internal static string Explain(Exception ex) => ex is SocketException or IOException or TimeoutException
        ? "TELEGRAM_CONNECTION_FAILED: Telegram could not be reached. Turn on your VPN or system proxy, or change the proxy in Settings → Features → Free Music Finder. Your login is kept."
        : Explain(ex.Message);
    internal static string Explain(string error) => error switch
    {
        "PHONE_CODE_INVALID" => "That code is not right. Check it and try again.",
        "PHONE_CODE_EXPIRED" => "That code has expired. Start over to get a new one.",
        "PASSWORD_HASH_INVALID" => "That password is not right.",
        "PHONE_NUMBER_INVALID" => "Telegram does not accept that phone number. Include the country code.",
        "API_ID_INVALID" or "API_HASH_INVALID" or "API_ID_PUBLISHED_FLOOD" => "API_CREDENTIALS_REJECTED: Telegram rejected or restricted the API settings. Open Advanced and enter another valid pair.",
        _ when error.StartsWith("FLOOD_WAIT", StringComparison.Ordinal) => "Too many attempts. Telegram asks you to wait before trying again.",
        _ => "TELEGRAM_LOGIN_FAILED: " + (Regex.IsMatch(error, "\\A[A-Z][A-Z0-9_]{2,80}\\z") ? error : "check your connection or try again later."),
    };

    public void Dispose()
    {
        _disposed = true;
        Phone = Code = Password = ApiHash = "";
        ContinueCommand.NotifyCanExecuteChanged();
        StartOverCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }
}
