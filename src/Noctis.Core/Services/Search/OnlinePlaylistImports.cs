namespace Noctis.Services.Search;

/// <summary>Native playlist import data. Reading/importing metadata never requests audio.</summary>
public interface IOnlinePlaylistImports
{
    event Action? Changed;
    IReadOnlyList<OnlineImportJob> Snapshot();
    OnlineImportedPlaylist? Read(Guid playlistId);
    Task ImportAsync(string links, CancellationToken ct);
    Task CheckLibraryAsync(CancellationToken ct);
    Task<int> DownloadMissingAsync(Guid playlistId, CancellationToken ct);
    /// <summary>Stop managing the import, keeping the native playlist and music.</summary>
    Task RemoveAsync(Guid playlistId, CancellationToken ct);
}

public sealed record OnlineImportJob(Guid Id, string Name, string Url, int Found, int Total, string Status,
    bool Complete, bool Stopped, string ArtworkUrl = "")
{
    public bool HasMissing => Complete && !Stopped && Found < Total;
}

/// <summary>Song may be null for an unavailable position; positions and duplicate entries are retained.</summary>
public sealed record OnlinePlaylistEntry(int Position, OnlineSong? Song, string Issue);
public sealed record OnlineImportedPlaylist(OnlineImportJob Import, IReadOnlyList<OnlinePlaylistEntry> Entries);
