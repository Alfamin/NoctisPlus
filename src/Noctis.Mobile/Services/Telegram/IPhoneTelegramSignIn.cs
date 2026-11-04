namespace Noctis.Mobile.Services.Telegram;

/// <summary>Phone-side view of the Telegram sign-in (mirrors the desktop ITelegramSignIn, which
/// Noctis.Mobile cannot reach). Start/Continue return what Telegram asks next:
/// "verification_code", "password", or null when signed in.</summary>
public interface IPhoneTelegramSignIn
{
    bool IsLoggedIn { get; }
    string? SignedInAs { get; }
    /// <summary>Only known delivery lengths may auto-submit; null means use Continue.</summary>
    int? ExpectedCodeLength { get; }
    Task<string?> StartLoginAsync(string phone, string? proxyServer, string? proxySecret, CancellationToken ct = default);
    Task<string?> ContinueLoginAsync(string answer, CancellationToken ct = default);
    Task LogOutAsync(CancellationToken ct = default);
    /// <summary>Abandon a pending attempt without logging out an already signed-in account.</summary>
    Task ResetLoginAsync(CancellationToken ct = default);
}
