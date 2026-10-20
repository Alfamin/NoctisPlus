namespace Noctis.Services;

/// <summary>User-selected offline mode is independent of OS connectivity. Network
/// changes are retry hints; only successful service traffic proves that route works.</summary>
public sealed class OnlineAccess
{
    public static OnlineAccess Shared { get; } = new(watchNetwork: true);
    public OnlineAccess(bool watchNetwork = false)
    {
        if (!watchNetwork) return;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Hint();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => Hint();
    }
    private readonly object _gate = new();
    private bool _offline;
    private TaskCompletionSource _online = Completed();
    private TaskCompletionSource _hint = NewSignal();
    private CancellationTokenSource _requests = new();
    private int _failed;
    public event Action<bool>? Changed;
    public event Action? Recovered;
    public event Action? RetryHint;
    public bool IsOffline { get { lock (_gate) return _offline; } }
    public CancellationToken RequestToken { get { lock (_gate) return _requests.Token; } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource Completed() { var signal = NewSignal(); signal.SetResult(); return signal; }

    public void SetOffline(bool value)
    {
        CancellationTokenSource? cancel = null; TaskCompletionSource? online = null;
        lock (_gate)
        {
            if (_offline == value) return;
            _offline = value;
            if (value) { _online = NewSignal(); cancel = _requests; }
            else { _requests = new(); online = _online; }
        }
        cancel?.Cancel(); online?.TrySetResult(); Hint();
        if (Changed is { } changed) foreach (Action<bool> listener in changed.GetInvocationList())
            try { listener(value); } catch { /* independent feature observers */ }
    }
    public Task WaitAsync(CancellationToken ct)
    { lock (_gate) return _online.Task.WaitAsync(ct); }
    public void Hint()
    {
        TaskCompletionSource signal;
        lock (_gate) { signal = _hint; _hint = NewSignal(); }
        signal.TrySetResult();
        if (RetryHint is { } hinted) foreach (Action listener in hinted.GetInvocationList())
            try { listener(); } catch { }
    }
    public async Task RetryAsync(CancellationToken ct, TimeSpan? delay = null)
    {
        Task hint; lock (_gate) hint = _hint.Task;
        await Task.WhenAny(hint, Task.Delay(delay ?? TimeSpan.FromSeconds(2), ct)).WaitAsync(ct).ConfigureAwait(false);
        await WaitAsync(ct).ConfigureAwait(false);
    }
    public void Failed() => Interlocked.Exchange(ref _failed, 1);
    public void Succeeded()
    {
        if (IsOffline || Interlocked.Exchange(ref _failed, 0) == 0) return;
        if (Recovered is { } recovered) foreach (Action listener in recovered.GetInvocationList())
            try { listener(); } catch { }
    }
    public void ThrowIfOffline()
    { if (IsOffline) throw new OfflineModeException(); }
}

public sealed class OfflineModeException() : OperationCanceledException("Offline mode");

/// <summary>Stops new HTTP traffic and cancels in-flight HTTP calls when offline
/// is selected. Cached/local reads happen above this handler and stay available.</summary>
public sealed class OnlineAccessHandler(HttpMessageHandler inner, OnlineAccess? access = null) : DelegatingHandler(inner)
{
    private readonly OnlineAccess _access = access ?? OnlineAccess.Shared;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _access.ThrowIfOffline();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, _access.RequestToken);
        try
        {
            var response = await base.SendAsync(request, attempt.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) _access.Succeeded();
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _access.IsOffline) { throw new OfflineModeException(); }
        catch (HttpRequestException) { _access.Failed(); throw; }
    }
}
