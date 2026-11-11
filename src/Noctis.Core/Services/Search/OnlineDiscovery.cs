namespace Noctis.Services.Search;

public sealed record RecommendationPage(string Id, IReadOnlyList<OnlineSong> Songs, string Notice = "");
public enum RecommendationAction { Play, Download, Queue }

/// <summary>Portable recommendations from the existing Plus engine. Metadata/cache only;
/// loading a page never requests audio or signs the user in.</summary>
public interface IOnlineDiscovery
{
    Task<IReadOnlyList<RecommendationPage>> LoadAsync(bool refresh, CancellationToken ct);
    Task RecordAsync(string page, OnlineSong song, RecommendationAction action);
    Task ShownAsync(string page, IReadOnlyList<string> songIds);
    /// <summary>An opaque, short-lived undo receipt; null if this was already hidden.</summary>
    Task<string?> HideAsync(string page, OnlineSong song, bool artist = false);
    Task<bool> UndoAsync(string receipt);
    /// <summary>Owner #283 (the computer's #248): takes a page off the shelf; its songs are not disliked. An undo receipt, or null.</summary>
    Task<string?> RemovePageAsync(string page) => Task.FromResult<string?>(null);
    /// <summary>Owner #283: every page off the shelf (what was learnt and disliked stays). An undo receipt, or null.</summary>
    Task<string?> ClearPagesAsync() => Task.FromResult<string?>(null);
}
