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
        return creator is null || creator == self ? TgGuardStatus.Ok : TgGuardStatus.WrongAccount;
    }

    /// <summary>
    /// Run <paramref name="pass"/> on <paramref name="channel"/>. If the channel is gone, call
    /// <paramref name="recreate"/> (find-or-create + repost a snapshot from local state) and retry once.
    /// </summary>
    public async Task<(TgGuardStatus Status, TgChannel Channel)> RunAsync(
        TgChannel channel, Func<TgChannel, Task> pass, Func<Task<TgChannel>> recreate, CancellationToken ct = default)
    {
        var account = await CheckAccountAsync(channel, ct);
        if (account != TgGuardStatus.Ok) return (account, channel);
        try { await pass(channel); return (TgGuardStatus.Ok, channel); }
        catch (TelegramSessionRevokedException) { return (TgGuardStatus.SignedOut, channel); }
        catch (TelegramChannelGoneException) { }
        TgChannel fresh;
        try { fresh = await recreate(); await pass(fresh); }
        catch (TelegramSessionRevokedException) { return (TgGuardStatus.SignedOut, channel); }
        catch (TelegramChannelGoneException) { return (TgGuardStatus.ChannelGoneAgain, channel); }
        return (TgGuardStatus.ChannelRecreated, fresh);
    }
}
