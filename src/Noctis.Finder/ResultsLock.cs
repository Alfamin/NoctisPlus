namespace FreeMusicFinder;

/// <summary>
/// Owner #159: one conversation with Music Hunters at a time, for searches and the press that follows them. The bot deletes
/// its earlier results when another search reaches it, so after a search the lock is kept for its results until their
/// button is pressed (<see cref="Hold"/> at most: the song may not be taken), and another song's search waits meanwhile.
/// Live: "Stuck In A Dream" lost its results to "Umbrella"'s search in between and went to DeezLoad.
/// </summary>
internal sealed class ResultsLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private int _keptFor; // the results message the lock is kept for; 0: none
    private CancellationTokenSource? _timer;

    internal TimeSpan Hold { get; init; } = TimeSpan.FromSeconds(3);

    public Task WaitAsync(CancellationToken ct) => _gate.WaitAsync(ct);
    public void Release() => _gate.Release();

    /// <summary>Instead of releasing: kept for these results until they are pressed (<see cref="TryTake"/>) or the hold passes.</summary>
    public void KeepFor(int results)
    {
        var timer = new CancellationTokenSource();
        lock (_sync) { _keptFor = results; _timer = timer; }
        _ = Task.Delay(Hold, timer.Token).ContinueWith(_ => Expire(timer), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>The press for these results takes over the kept lock (and releases it later); false when it is not kept for them.</summary>
    public bool TryTake(int results)
    {
        lock (_sync)
        {
            if (_keptFor == 0 || _keptFor != results) return false;
            _keptFor = 0; _timer?.Cancel(); _timer = null;
            return true;
        }
    }

    private void Expire(CancellationTokenSource timer)
    {
        lock (_sync) { if (!ReferenceEquals(_timer, timer)) return; _keptFor = 0; _timer = null; }
        _gate.Release();
    }
}
