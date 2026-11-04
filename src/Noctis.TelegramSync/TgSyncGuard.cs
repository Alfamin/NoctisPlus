namespace Noctis.TelegramSync;

/// <summary>The channel was deleted or the account left it (CHANNEL_INVALID / CHANNEL_PRIVATE).</summary>
public sealed class TelegramChannelGoneException(long channel) : Exception($"CHANNEL_GONE {channel}")
{
    public long Channel { get; } = channel;
}

/// <summary>AUTH_KEY_UNREGISTERED / SESSION_REVOKED: the session is signed out; stop, ask the user to sign in.</summary>
public sealed class TelegramSessionRevokedException() : Exception("SESSION_REVOKED");

public enum TgGuardStatus { Ok, WrongAccount, SignedOut, ChannelRecreated, ChannelGoneAgain }

/// <summary>Creator-tagged markers: <c>noctis-plus:&lt;role&gt;:v1:&lt;guid&gt;:u&lt;userId&gt;</c>.</summary>
public static class TgCreatorMarker
{
    public static string For(string role, Guid id, long creatorUserId) => $"{TgMarkers.For(role, id)}:u{creatorUserId}";

    public static long? Creator(string? marker)
    {
        if (marker is null) return null;
        var i = marker.LastIndexOf(":u", StringComparison.Ordinal);
        return i >= 0 && long.TryParse(marker.AsSpan(i + 2), out var id) ? id : null;
    }
}

/// <summary>
/// Safety checks around one sync pass: refuse a different account than the channel's creator,
/// stop cleanly when signed out, and recreate a deleted/left channel from local state once.
/// </summary>
public sealed class TgSyncGuard(ITelegramDrive drive)
{
    /// <summary>Ok when the signed-in account created <paramref name="channel"/>; WrongAccount / SignedOut otherwise.</summary>
    public async Task<TgGuardStatus> CheckAccountAsync(TgChannel channel, CancellationToken ct = default)
    {
        long self;
        try { self = await drive.GetSelfUserIdAsync(ct); }
        catch (TelegramSessionRevokedException) { return TgGuardStatus.SignedOut; }
        var creator = TgCreatorMarker.Creator(channel.Marker);
        return creator is > 0 && creator == self ? TgGuardStatus.Ok : TgGuardStatus.WrongAccount;
    }

    /// <summary>
    /// Run <paramref name="pass"/> on <paramref name="channel"/>. If the channel is gone, call
    /// <paramref name="recreate"/> (find-or-create + repost a snapshot from local state) and retry once.
    /// </summary>
    public async Task<(TgGuardStatus Status, TgChannel Channel)> RunAsync(
        TgChannel channel, Func<TgChannel, Task> pass, Func<Task<TgChannel>> recreate, CancellationToken ct = default)
        => await RunAsync(channel, (c, _) => pass(c), _ => recreate(), ct);

    public async Task<(TgGuardStatus Status, TgChannel Channel)> RunAsync(
        TgChannel channel, Func<TgChannel, CancellationToken, Task> pass,
        Func<CancellationToken, Task<TgChannel>> recreate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var account = await CheckAccountAsync(channel, ct);
        if (account != TgGuardStatus.Ok) return (account, channel);
        try { await pass(channel, ct); return (TgGuardStatus.Ok, channel); }
        catch (TelegramSessionRevokedException) { return (TgGuardStatus.SignedOut, channel); }
        catch (TelegramChannelGoneException e) when (e.Channel == channel.Id) { }
        var fresh = channel;
        try
        {
            ct.ThrowIfCancellationRequested();
            fresh = await recreate(ct);
            account = await CheckAccountAsync(fresh, ct);
            if (account != TgGuardStatus.Ok) return (account, fresh);
            ct.ThrowIfCancellationRequested();
            await pass(fresh, ct);
        }
        catch (TelegramSessionRevokedException) { return (TgGuardStatus.SignedOut, fresh); }
        catch (TelegramChannelGoneException e) when (e.Channel == fresh.Id) { return (TgGuardStatus.ChannelGoneAgain, fresh); }
        return (TgGuardStatus.ChannelRecreated, fresh);
    }
}
