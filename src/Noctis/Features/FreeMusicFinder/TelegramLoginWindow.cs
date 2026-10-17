using System.Globalization;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Noctis.Plugins;

namespace FreeMusicFinder;

/// <summary>
/// Logs the user's own Telegram account in: API id and hash and phone number, then the code
/// Telegram sends, then the two-step password if the account has one. Shows who is logged in
/// and offers to log out when a session already exists.
/// </summary>
internal sealed class TelegramLoginWindow : Window
{
    private enum Step { Phone, Code, Password, LoggedIn }

    private readonly TelegramAccount _account;
    private readonly IPluginHost _host;
    private Step _step;
    private bool _closed;

    private readonly TextBlock _intro;
    private readonly StackPanel _phonePanel;
    private readonly TextBox _apiId;
    private readonly TextBox _apiHash;
    private readonly Expander _advanced;
    private readonly TextBox _phone;
    private readonly TextBox _code;
    private readonly TextBox _password;
    private readonly TextBlock _error;
    private readonly Button _back;
    private readonly Button _next;

    public TelegramLoginWindow(TelegramAccount account, IPluginHost host)
    {
        _account = account;
        _host = host;

        Title = "Telegram login";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _intro = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _apiId = new TextBox { PlaceholderText = "API id (a number)", Text = account.ApiId > 0 ? account.ApiId.ToString(CultureInfo.InvariantCulture) : "" };
        _apiHash = new TextBox { PlaceholderText = "API hash", Text = account.ApiHash, PasswordChar = '•' };
        _phone = new TextBox { PlaceholderText = "Phone number with country code (+98…)" };
        _phonePanel = new StackPanel { Spacing = 8 };
        _phonePanel.Children.Add(_phone);
        var apiFields = new StackPanel { Spacing = 8 };
        apiFields.Children.Add(new TextBlock { Text = "Use your own API id/hash to override the private defaults. These identify the Telegram application, not your account.", TextWrapping = TextWrapping.Wrap });
        apiFields.Children.Add(_apiId); apiFields.Children.Add(_apiHash);
        _advanced = new Expander { Header = "Advanced: Telegram API settings", Content = apiFields, IsExpanded = !account.HasCredentials };
        _phonePanel.Children.Add(_advanced);
        _code = new TextBox { PlaceholderText = "Login code" };
        _password = new TextBox { PlaceholderText = "Two-step verification password", PasswordChar = '•' };
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, IsVisible = false };

        _back = new Button { Margin = new Thickness(0, 0, 8, 0) };
        _back.Click += (_, _) => Back();
        _next = new Button { MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        _next.Click += (_, _) => Next();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);

        var root = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        root.Children.Add(_intro);
        root.Children.Add(_phonePanel);
        root.Children.Add(_code);
        root.Children.Add(_password);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !_next.IsEnabled) return;
            e.Handled = true;
            Next();
        };
        Closed += (_, _) => _closed = true;

        Show(account.IsLoggedIn ? Step.LoggedIn : Step.Phone);
        if (account.DefaultsError.Length > 0) Fail(account.DefaultsError);
    }

    private void Show(Step step)
    {
        _step = step;
        _phonePanel.IsVisible = step == Step.Phone;
        _code.IsVisible = step == Step.Code;
        _password.IsVisible = step == Step.Password;
        _back.IsVisible = step != Step.Phone;
        _back.Content = step == Step.LoggedIn ? "Log out" : "Start over";
        _next.Content = step == Step.LoggedIn ? "Close" : "Continue";
        _intro.Text = step switch
        {
            Step.Phone => _account.HasCredentials
                ? "Sign in with your own Telegram phone number. Telegram app settings are already filled in; you can change them under Advanced. Your login session is kept privately on this computer."
                : "Sign in with your own Telegram account. No private app defaults were supplied. Enter an API id/hash from my.telegram.org → API development tools under Advanced, then your phone number.",
            Step.Code => "Telegram sent a login code to your Telegram app (or by SMS). Enter it here.",
            Step.Password => "This account has two-step verification. Enter its password; it is not stored.",
            _ => $"Logged in as {_account.SignedInAs}.",
        };
        (step switch { Step.Phone => _apiId.Text is { Length: > 0 } ? _phone : _apiId, Step.Code => _code, Step.Password => _password, _ => (Control)_next }).Focus();
    }

    private async void Next()
    {
        try
        {
            if (_step == Step.LoggedIn)
            {
                Close();
                return;
            }

            _error.IsVisible = false;
            string? wanted;
            SetBusy(true);
            try
            {
                switch (_step)
                {
                    case Step.Phone:
                        var phone = Digits((_phone.Text ?? "").Trim());
                        var hash = (_apiHash.Text ?? "").Trim();
                        if (!int.TryParse(Digits((_apiId.Text ?? "").Trim()), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(hash, "\\A[a-fA-F0-9]{32}\\z"))
                        {
                            _advanced.IsExpanded = true;
                            Fail("TELEGRAM_API_MISSING: enter a positive API id and a 32-character API hash under Advanced.");
                            return;
                        }
                        if (!phone.StartsWith('+') || phone[1..].Count(char.IsDigit) < 7 || phone[1..].Any(ch => !char.IsDigit(ch) && ch != ' ' && ch != '-'))
                        {
                            Fail("PHONE_NUMBER_INVALID: include your country code, for example +98 followed by your number.");
                            return;
                        }
                        wanted = await _account.StartLoginAsync(id, hash, string.Concat(phone.Where(ch => ch == '+' || char.IsDigit(ch))));
                        break;
                    case Step.Code:
                        wanted = await _account.ContinueLoginAsync(Digits((_code.Text ?? "").Trim()));
                        break;
                    default:
                        wanted = await _account.ContinueLoginAsync(_password.Text ?? "");
                        break;
                }
            }
            finally
            {
                SetBusy(false);
            }
            if (_closed) return;

            _code.Text = "";
            _password.Text = "";
            switch (wanted)
            {
                case null: Show(Step.LoggedIn); break;
                case "verification_code": Show(Step.Code); break;
                case "password": Show(Step.Password); break;
                case "name": Fail("There is no Telegram account for this phone number."); break;
                default: Fail($"Telegram asked for \"{wanted}\", which this window cannot provide."); break;
            }
        }
        catch (Exception ex)
        {
            _host.Log("Telegram login failed: " + ex.GetType().Name);
            if (ex.Message.Contains("API_ID", StringComparison.Ordinal) || ex.Message.Contains("API_HASH", StringComparison.Ordinal)) _advanced.IsExpanded = true;
            if (!_closed) Fail(Explain(ex));
        }
    }

    private async void Back()
    {
        try
        {
            _error.IsVisible = false;
            SetBusy(true);
            try { await _account.LogOutAsync(); }
            finally { SetBusy(false); }
            if (!_closed) Show(Step.Phone);
        }
        catch (Exception ex)
        {
            _host.Log("Telegram logout failed: " + ex.GetType().Name);
            if (!_closed) Fail(Explain(ex));
        }
    }

    private void SetBusy(bool busy)
    {
        _next.IsEnabled = !busy;
        _back.IsEnabled = !busy;
    }

    private void Fail(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private static string Explain(Exception ex) => ex is SocketException or IOException or TimeoutException
        // Not an answer from Telegram: the connection itself did not work.
        ? "TELEGRAM_CONNECTION_FAILED (" + ex.GetType().Name + "). Telegram could not be reached. Where Telegram is blocked, turn on a VPN "
          + "or its system proxy, or set a proxy in the plugin's settings (Settings → Plugins → Free Music Finder)."
        : Explain(ex.Message);

    private static string Explain(string error) => error switch
    {
        "PHONE_CODE_INVALID" => "That code is not right. Check it and try again.",
        "PHONE_CODE_EXPIRED" => "That code has expired. Start over to get a new one.",
        "PASSWORD_HASH_INVALID" => "That password is not right.",
        "PHONE_NUMBER_INVALID" => "Telegram does not accept that phone number. Include the country code.",
        "API_ID_INVALID" or "API_HASH_INVALID" or "API_ID_PUBLISHED_FLOOD" => "API_CREDENTIALS_REJECTED: Telegram rejected or restricted the default API id/hash. Open Advanced and enter another valid pair, then try again.",
        _ when error.StartsWith("FLOOD_WAIT", StringComparison.Ordinal) => "Too many attempts. Telegram asks you to wait before trying again.",
        _ => "TELEGRAM_LOGIN_FAILED: " + (System.Text.RegularExpressions.Regex.IsMatch(error, "\\A[A-Z][A-Z0-9_]{2,80}\\z") ? error : "the login could not be completed. Check your connection or try again later."),
    };

    private static string Digits(string value) => string.Concat(value.Select(ch => char.IsDigit(ch) ? (char)('0' + (int)char.GetNumericValue(ch)) : ch));
}
