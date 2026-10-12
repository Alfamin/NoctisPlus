using System.Security.Cryptography;
using Noctis.Services;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// The desktop's profile picture (getAvatar) for the Account page and the profile button,
/// cached in the account directory so it shows from the first frame after a restart. Fetched
/// in the background after sign-in and inline at the end of each sync. Best effort throughout:
/// no picture, a desktop older than getAvatar or a failed download is logged and never fails
/// sign-in or a sync (a failed download keeps the last picture; "none" clears it).
///
/// A picture is stored under a name made from its bytes (<c>avatar-&lt;hash&gt;.img</c>): the
/// image cache is keyed by path, so a new picture needs a new path or the old one stays on
/// screen (the desktop's SetProfileAvatarAsync hit the same), and an unchanged one keeps its
/// path so nothing reloads.
/// </summary>
public sealed partial class NoctisAccountService
{
    private const string AvatarPrefix = "avatar-";
    private const string AvatarSuffix = ".img";

    private readonly SemaphoreSlim _avatarGate = new(1, 1);
    private volatile string? _avatarPath;

    public string? AvatarPath => _avatarPath;

    /// <summary>The background fetch started by the last sign-in; tests await it.</summary>
    internal Task AvatarRefresh { get; private set; } = Task.CompletedTask;

    private string AvatarDownloadPath => Path.Combine(_store.Directory, AvatarPrefix + "download");

    /// <summary>Constructor: the picture kept from the last run while still signed in; anything else
    /// (a sign-out's leftovers, an interrupted download) is removed.</summary>
    private void LoadCachedAvatar()
    {
        try
        {
            var files = Directory.EnumerateFiles(_store.Directory, AvatarPrefix + "*").Select(f => new FileInfo(f)).ToList();
            var keep = _account is null ? null : files
                .Where(f => f.Name.EndsWith(AvatarSuffix, StringComparison.Ordinal) && f.Length > 0)
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            foreach (var f in files)
                if (f != keep) NoctisServerClient.TryDelete(f.FullName);
            // Spelled like a fetched one (RefreshAvatarAsync compares paths to tell a change).
            _avatarPath = keep is null ? null : Path.Combine(_store.Directory, keep.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("Account", $"avatar cache unreadable ({ex.GetType().Name})");
        }
    }

    /// <summary>Asks the desktop for the picture and swaps it in. Throws only when
    /// <paramref name="ct"/> is cancelled (a cancelled sync stops here like anywhere else).</summary>
    private async Task RefreshAvatarAsync(CancellationToken ct)
    {
        // One fetch at a time: a sync right after sign-in finds the sign-in's fetch running,
        // and its answer would be the same.
        if (!await _avatarGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        var download = AvatarDownloadPath;
        try
        {
            NoctisAccount account;
            NoctisServerClient client;
            lock (_gate)
            {
                if (_account is null) return;
                account = _account;
                client = GetClientLocked();
            }

            string? next = null;
            if (await client.GetAvatarAsync(account.UserName, download, ct).ConfigureAwait(false))
            {
                var hash = await Task.Run(() =>
                {
                    using var file = File.OpenRead(download);
                    return Convert.ToHexString(SHA256.HashData(file), 0, 8).ToLowerInvariant();
                }, ct).ConfigureAwait(false);
                next = Path.Combine(_store.Directory, AvatarPrefix + hash + AvatarSuffix);
            }

            bool changed;
            lock (_gate)
            {
                // Signed out (or in again) while it downloaded: not this account's picture.
                // Compared by device key: a sync replaces the account record, not the key.
                if (_account?.DeviceKey != account.DeviceKey) return;
                if (next is not null && !File.Exists(next)) File.Move(download, next);
                changed = !string.Equals(next, _avatarPath, StringComparison.Ordinal);
                _avatarPath = next;
            }
            if (!changed) return;
            DeleteAvatarFiles(except: next);
            DebugLog.Write("Account", next is null ? "avatar: the desktop has none" : "avatar: updated");
            RaiseStateChanged();
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Unreachable, signed out mid-way (the client disposed), a full disk: keep what is shown.
            DebugLog.Write("Account", $"avatar not fetched: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
        }
        finally
        {
            NoctisServerClient.TryDelete(download);
            NoctisServerClient.TryDelete(download + ".part");
            _avatarGate.Release();
        }
    }

    /// <summary>Sign-in: fetch in the background so signing in never waits on the picture.</summary>
    private void StartAvatarRefresh() => AvatarRefresh = Task.Run(() => RefreshAvatarAsync(CancellationToken.None));

    /// <summary>Forgets the picture (sign-out, a revoked key). Call with the account already gone,
    /// so a fetch finishing now drops its download instead of bringing the picture back.</summary>
    private void ClearAvatar()
    {
        var had = _avatarPath is not null;
        _avatarPath = null;
        DeleteAvatarFiles(except: null);
        if (had) DebugLog.Write("Account", "avatar removed");
    }

    private void DeleteAvatarFiles(string? except)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_store.Directory, AvatarPrefix + "*" + AvatarSuffix))
                if (!string.Equals(f, except, StringComparison.Ordinal)) NoctisServerClient.TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("Account", $"removing the avatar failed ({ex.GetType().Name})");
        }
    }
}
