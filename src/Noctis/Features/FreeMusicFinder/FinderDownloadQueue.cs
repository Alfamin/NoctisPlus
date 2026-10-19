using Noctis.Services.Search;

namespace FreeMusicFinder;

/// <summary>Finder's download queue as the app's Downloads page sees it.</summary>
internal sealed class FinderDownloadQueue(Downloads downloads) : IDownloadQueue
{
    public event EventHandler? Changed
    {
        add { if (_changed is null) downloads.Changed += OnChanged; _changed += value; }
        remove { _changed -= value; if (_changed is null) downloads.Changed -= OnChanged; }
    }
    private EventHandler? _changed;
    private void OnChanged(Download _) => _changed?.Invoke(this, EventArgs.Empty);

    public bool IsPaused => downloads.Paused;

    public IReadOnlyList<DownloadEntry> Snapshot()
    {
        var active = downloads.Active;
        var history = downloads.History;
        var rows = new List<DownloadEntry>(active.Count + 16);
        foreach (var d in active.OrderByDescending(d => d.State == DownloadState.Running)) rows.Add(Entry(d));
        foreach (var d in history.Where(d => d.State == DownloadState.Failed && !active.Contains(d))) rows.Add(Entry(d));
        foreach (var d in history.Where(d => d.State is DownloadState.Saved or DownloadState.AlreadyThere).Reverse().Take(100)) rows.Add(Entry(d));
        return rows;
    }

    private static DownloadEntry Entry(Download d)
    {
        var (state, detail) = d.State switch
        {
            DownloadState.Running => (OnlineDownloadState.Downloading, d.Stage.Length > 0 ? d.Stage : d.Progress > 0 ? $"Downloading · {d.Progress:P0}" : "Asking for the file…"),
            DownloadState.Waiting => (OnlineDownloadState.Queued, (d.Error.Length > 0 ? d.Error : "Waiting for the service") +
                (d.RetryAt is { } at ? $" · retry at {at.ToLocalTime():HH:mm}" : "")),
            DownloadState.Failed => (OnlineDownloadState.Failed, d.Error.Length > 0 ? d.Error : "Could not download"),
            DownloadState.Saved => (OnlineDownloadState.Saved, "Downloaded"),
            DownloadState.AlreadyThere => (OnlineDownloadState.Saved, "Already in the folder"),
            _ => (OnlineDownloadState.Queued, d.Held ? "Ready · start the queued downloads" : d.Priority ? "Next" : "Queued"),
        };
        return new DownloadEntry(d, d.Track.Title, d.Track.Artist, state, Math.Clamp(d.Progress, 0, 1), detail, d.Priority, d.Held,
            d.SavedPath, d.Track.Duration?.TotalSeconds, d.State == DownloadState.Running ? d.Step : OnlineDownloadStep.None);
    }

    public void SetPaused(bool paused) => downloads.SetPaused(paused);
    public void RetryFailed() => downloads.RetryFailed();
    public void StartHeld() => downloads.ReleaseHeld();
    public void Clear() => downloads.Clear();
    public void DownloadNext(DownloadEntry entry) { if (entry.Handle is Download d) downloads.DownloadNext(d); }
    public void Remove(DownloadEntry entry) { if (entry.Handle is Download d) downloads.Remove(d); }
}
