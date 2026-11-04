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
        var (id, hash) = LoginPair(finder.Telegram.UsesBuiltInApi, finder.Telegram.ApiId, finder.Telegram.ApiHash);
        return await finder.Telegram.StartLoginAsync(id, hash, phone, ct).ConfigureAwait(false);
    }

    /// <summary>Owner #185 on the phone too: the build's packed pair is used but never saved as the user's own
    /// (0/"" reads it at connect time, so a newer build's pair applies); a pair of the user's own stays theirs.</summary>
    internal static (int Id, string Hash) LoginPair(bool usesBuiltIn, int id, string hash)
        => usesBuiltIn ? (0, "") : (id, hash);

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
