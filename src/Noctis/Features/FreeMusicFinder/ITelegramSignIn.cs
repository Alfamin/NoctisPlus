namespace FreeMusicFinder;

// The dialog can exercise every login state without opening a Telegram connection.
internal interface ITelegramSignIn
{
    bool IsLoggedIn { get; }
    string SignedInAs { get; }
    int ApiId { get; }
    string ApiHash { get; }
    bool HasCredentials { get; }
    string DefaultsError { get; }
    bool UsesBuiltInApi { get; }
    Task<string?> StartLoginAsync(int apiId, string apiHash, string phone);
    Task<string?> ContinueLoginAsync(string answer);
    Task LogOutAsync();
    Task QrLoginAsync(int apiId, string apiHash, Action<string> show, Action<bool> passwordNeeded, CancellationToken ct);
    void SendQrPassword(string password);
}
