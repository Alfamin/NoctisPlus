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
    Task<string?> StartLoginAsync(int apiId, string apiHash, string phone);
    Task<string?> ContinueLoginAsync(string answer);
    Task LogOutAsync();
}
