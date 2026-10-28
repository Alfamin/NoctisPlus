using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;

namespace Noctis.ViewModels;

/// <summary>QR code (owner #185), or phone → code → optional password, using the existing private Telegram session store.</summary>
public sealed partial class TelegramSignInViewModel : ObservableObject, IDisposable
{
    public enum LoginStep { Qr, Phone, Code, Password, SignedIn }
    private readonly ITelegramSignIn _account;
    private readonly Action<string> _log;
    private bool _disposed;
    private CancellationTokenSource? _qr;
    // The password step belongs to the QR sign-in, which waits for it inside Telegram's own login.
    private bool _qrPassword;

    internal TelegramSignInViewModel(ITelegramSignIn account, Action<string> log)
    {
        _account = account;
        _log = log;
        // Owner #186: the built-in pair is never shown or copyable; the fields hold only a pair of the user's own.
        ApiId = !account.UsesBuiltInApi && account.ApiId > 0 ? account.ApiId.ToString(CultureInfo.InvariantCulture) : "";
        ApiHash = account.UsesBuiltInApi ? "" : account.ApiHash;
        AdvancedOpen = !account.HasCredentials;
        Step = account.IsLoggedIn ? LoginStep.SignedIn : account.HasCredentials ? LoginStep.Qr : LoginStep.Phone;
        Error = account.DefaultsError;
    }

    public event Action? CloseRequested;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsQr), nameof(IsPhone), nameof(IsCode), nameof(IsPassword), nameof(IsSignedIn), nameof(Title), nameof(Description), nameof(ContinueLabel), nameof(CanStartOver), nameof(ShowContinue), nameof(QrWaiting), nameof(ShowBotsChecking), nameof(NeedsJoin))]
    private LoginStep _step;

    // Owner #190: once signed in, the music bots are started and asked what they need; a bot that wants its channel
    // joined first gets one Join press here instead of silently sending no songs.
    private CancellationTokenSource? _bots;
    private IReadOnlyList<BotChannel> _channels = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowBotsChecking))] private bool _botsChecking;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsJoin))] private string _joinText = "";
    public bool ShowBotsChecking => IsSignedIn && BotsChecking;
    public bool NeedsJoin => IsSignedIn && JoinText.Length > 0;

    partial void OnStepChanged(LoginStep value)
    {
        if (value == LoginStep.SignedIn) _ = CheckBotsAsync();
        else { _bots?.Cancel(); JoinText = ""; BotsChecking = false; }
    }

    internal async Task CheckBotsAsync()
    {
        if (_disposed || !IsSignedIn) return;
        _bots?.Cancel();
        var run = _bots = new CancellationTokenSource();
        BotsChecking = true;
        try
        {
            var channels = await _account.BotChannelsAsync(run.Token);
            if (_bots != run || _disposed) return;
            _channels = channels;
            JoinText = JoinLine(channels);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log("Telegram bot check failed: " + ex.GetType().Name); }
        catch (OperationCanceledException) { }
        finally { if (_bots == run) BotsChecking = false; }
    }

    internal static string JoinLine(IReadOnlyList<BotChannel> channels)
    {
        if (channels.Count == 0) return "";
        var bots = channels.Select(c => c.Provider).Distinct().ToList();
        // Channel names can be anything (">DEDSEC:/"); the bots are what the user knows.
        return bots.Count == 1 ? $"{bots[0]} needs you to join its channel first." : $"{string.Join(" and ", bots)} need you to join their channels first.";
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task Join()
    {
        if (!CanInteract || _channels.Count == 0) return;
        IsBusy = true;
        try
        {
            await _account.JoinAsync(_channels, CancellationToken.None);
            if (!_disposed) { JoinText = ""; Error = ""; }
        }
        catch (Exception ex)
        {
            _log("Telegram join failed: " + ex.GetType().Name);
            if (!_disposed) Error = Explain(ex);
        }
        finally { IsBusy = false; }
        if (!_disposed) await CheckBotsAsync();
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(QrWaiting), nameof(HasQr))] private Avalonia.Media.IImage? _qrImage;
    [ObservableProperty] private string _apiId = "";
    [ObservableProperty] private string _apiHash = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _advancedOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError), nameof(ShowContinue), nameof(QrWaiting), nameof(HasQr))] private string _error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInteract)), NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(StartOverCommand), nameof(DisconnectCommand), nameof(JoinCommand))]
    private bool _isBusy;

    public bool CanInteract => !IsBusy && !_disposed;
    public bool IsQr => Step == LoginStep.Qr;
    public bool QrWaiting => IsQr && QrImage is null && !HasError;
    public bool HasQr => QrImage is not null && !HasError;
    /// <summary>The QR page has no main button until something went wrong ("Try again").</summary>
    public bool ShowContinue => !IsQr || HasError;
    public bool IsPhone => Step == LoginStep.Phone;
    public bool IsCode => Step == LoginStep.Code;
    public bool IsPassword => Step == LoginStep.Password;
    public bool IsSignedIn => Step == LoginStep.SignedIn;
    public bool CanStartOver => IsCode || IsPassword;
    public bool HasError => Error.Length > 0;
    [RelayCommand] private void ToggleAdvanced() => AdvancedOpen = !AdvancedOpen;
    /// <summary>Owner #183: a first-time user has no API id/hash yet; this opens the page that makes them.</summary>
    internal const string AppsPage = "https://my.telegram.org/apps";
    [RelayCommand] private void OpenAppsPage() => Helpers.PlatformHelper.OpenUrl(AppsPage);
    public string ContinueLabel => IsSignedIn ? "Done" : IsQr ? "Try again" : IsPhone ? "Send code" : "Continue";
    /// <summary>Owner #185: a user without the built-in pair is told the fields are optional only when they are.</summary>
    public string AdvancedHint => _account.UsesBuiltInApi
        ? "Optional: your own Telegram app from my.telegram.org."
        : "Telegram app settings from my.telegram.org. Existing settings are kept.";

    [RelayCommand]
    private void UsePhone()
    {
        StopQr();
        Error = "";
        Step = LoginStep.Phone;
    }

    /// <summary>Shows a QR code to scan with Telegram on the phone; started when the dialog opens and by "Use a QR code".</summary>
    [RelayCommand] private void UseQr() => _ = StartQrAsync();

    internal async Task StartQrAsync()
    {
        if (_disposed || IsSignedIn) return;
        StopQr();
        Error = "";
        QrImage = null;
        Code = Password = "";
        Step = LoginStep.Qr;
        if (!TryApi(out var id, out var hash)) return;
        var run = _qr = new CancellationTokenSource();
        try
        {
            await _account.QrLoginAsync(id, hash,
                link => OnUi(() => { if (_qr == run) QrImage = Helpers.QrCodeBitmap.TryRender(link, 6); }),
                again => OnUi(() =>
                {
                    if (_qr != run || _disposed) return;
                    _qrPassword = true;
                    IsBusy = false;
                    Password = "";
                    Step = LoginStep.Password;
                    Error = again ? Explain("PASSWORD_HASH_INVALID") : "";
                }), run.Token);
            if (_qr == run && !_disposed) { Error = ""; Step = LoginStep.SignedIn; }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log("Telegram QR login failed: " + ex.GetType().Name);
            if (_qr == run && !_disposed)
            {
                _qrPassword = false;
                if (ex.Message.Contains("API_ID", StringComparison.Ordinal) || ex.Message.Contains("API_HASH", StringComparison.Ordinal))
                {
                    Step = LoginStep.Phone;
                    AdvancedOpen = true;
                }
                else if (IsPassword) Step = LoginStep.Qr;
                QrImage = null;
                Error = Explain(ex);
            }
        }
        finally { if (_qr == run) { IsBusy = false; } }
    }

    private void StopQr()
    {
        _qrPassword = false;
        var run = Interlocked.Exchange(ref _qr, null);
        run?.Cancel();
    }

    private static void OnUi(Action act)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) act();
        else Avalonia.Threading.Dispatcher.UIThread.Post(act);
    }

    /// <summary>The API pair to sign in with: the user's own when typed, else the built-in one (0 and "").</summary>
    private bool TryApi(out int id, out string hash)
    {
        hash = ApiHash.Trim();
        id = 0;
        var typed = Digits(ApiId.Trim());
        if (typed.Length == 0 && hash.Length == 0 && _account.UsesBuiltInApi) return true;
        if (int.TryParse(typed, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0 && Regex.IsMatch(hash, "\\A[a-fA-F0-9]{32}\\z"))
        {
            // The pre-filled built-in pair is not saved as the user's own, so a newer release's pair still applies.
            if (_account.UsesBuiltInApi && id == _account.ApiId && string.Equals(hash, _account.ApiHash, StringComparison.OrdinalIgnoreCase)) { id = 0; hash = ""; }
            return true;
        }
        Step = LoginStep.Phone;
        AdvancedOpen = true;
        Error = "TELEGRAM_API_MISSING: enter a positive API id and a 32-character API hash under Advanced.";
        return false;
    }

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
        LoginStep.Qr => "Scan in Telegram on your phone: Settings › Devices › Link Desktop Device.",
        _ => "Use your own phone number to download music. Your session stays on this computer.",
    };

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task Continue()
    {
        if (!CanInteract) return;
        if (IsSignedIn) { CloseRequested?.Invoke(); return; }
        if (IsQr) { _ = StartQrAsync(); return; }
        if (IsPassword && _qrPassword)
        {
            if (Password.Length == 0) { Error = "Enter your two-step verification password."; return; }
            Error = "";
            IsBusy = true; // until Telegram accepts it (signed in) or asks again
            _account.SendQrPassword(Password);
            Password = "";
            return;
        }
        Error = "";
        IsBusy = true;
        try
        {
            string? next;
            if (IsPhone)
            {
                var phone = Digits(Phone.Trim());
                if (!TryApi(out var id, out var hash)) return;
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
        if (_qrPassword) { _ = StartQrAsync(); return; }
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
            if (!_disposed) { Error = ""; Step = LoginStep.Phone; if (_account.HasCredentials) _ = StartQrAsync(); }
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
        StopQr();
        _bots?.Cancel();
        Phone = Code = Password = ApiHash = "";
        ContinueCommand.NotifyCanExecuteChanged();
        StartOverCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }
}
