using FreeMusicFinder;

namespace Noctis.Mobile.Services.Telegram;

/// <summary>Uses this installation's own account; never imports a desktop session.</summary>
internal sealed class PhoneTelegramSignIn(FinderSession finder) : IPhoneTelegramSignIn
{
    public bool IsLoggedIn => finder.Telegram.IsLoggedIn;
    public string? SignedInAs => finder.Telegram.SignedInAs;
    // WTelegram's Login API does not expose the delivery length. Keep a Continue action.
    public int? ExpectedCodeLength => null;

    public async Task<string?> StartLoginAsync(string phone, string? proxyServer, string? proxySecret, CancellationToken ct = default)
    {
        if (IsLoggedIn) return null;
        if (!finder.Telegram.HasCredentials) throw new InvalidOperationException("TELEGRAM_APP_MISSING");
        finder.SetProxy(proxyServer, proxySecret);
        return await finder.Telegram.StartLoginAsync(finder.Telegram.ApiId, finder.Telegram.ApiHash, phone, ct).ConfigureAwait(false);
    }

    public Task<string?> ContinueLoginAsync(string answer, CancellationToken ct = default)
        => finder.Telegram.ContinueLoginAsync(answer, ct);
    public Task ResetLoginAsync(CancellationToken ct = default) => finder.Telegram.ResetPendingLoginAsync(ct);
    public async Task LogOutAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        finder.Downloads.SetPaused(true);
        await finder.Telegram.LogOutAsync().ConfigureAwait(false);
    }
}
