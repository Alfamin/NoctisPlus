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
}
