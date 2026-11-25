namespace JoesScanner.Services;

// Android delivers starts and service destruction asynchronously on the main
// looper. A run gets readiness only after its own start has promoted an instance.
internal sealed class AndroidForegroundOwnership
{
    private bool _requested;
    private bool _topActivity;
    private object? _readyInstance;
    private TaskCompletionSource _changed = NewSignal();
    internal long RunId { get; private set; }
    internal bool ActivityVisible { get; private set; }
    internal bool IsReady => RunId != 0 && _readyInstance != null;
    internal bool StartFailed { get; private set; }
    internal Task Changed => _changed.Task;
    internal event Action? StateChanged;

    internal bool CanPlay(bool requiresForegroundService, bool requiresTopActivity = false) =>
        !requiresForegroundService || IsReady || (ActivityVisible && (!requiresTopActivity || _topActivity));

    internal void SetActivityVisible(bool visible)
    {
        if (ActivityVisible == visible)
            return;
        ActivityVisible = visible;
        if (!visible)
            _topActivity = false;
        Publish();
    }

    internal void SetTopActivity(bool top)
    {
        if (_topActivity == top)
            return;
        _topActivity = top;
        Publish();
    }

    internal void Begin(long runId)
    {
        RunId = runId;
        _requested = false;
        _readyInstance = null;
        StartFailed = false;
        Publish();
    }

    internal bool End(long runId)
    {
        if (RunId != runId || runId == 0)
            return false;
        Begin(0);
        return true;
    }

    internal bool TryRequest()
    {
        if (!ActivityVisible || RunId == 0 || _requested || IsReady)
            return false;
        _requested = true;
        if (StartFailed)
        {
            StartFailed = false;
            Publish();
        }
        return true;
    }

    internal bool IsCurrent(long runId) => runId != 0 && RunId == runId;

    internal bool CanUpdateNotification(long runId) => IsCurrent(runId) && IsReady;

    internal bool OwnsService(long runId, object service) =>
        IsCurrent(runId) && ReferenceEquals(_readyInstance, service);

    internal bool Promote(long runId, object service)
    {
        if (!IsCurrent(runId))
            return false;
        _readyInstance = service;
        _requested = false;
        StartFailed = false;
        Publish();
        return true;
    }

    internal void RequestFailed(long runId)
    {
        if (!IsCurrent(runId) || !_requested)
            return;
        _requested = false;
        StartFailed = true;
        Publish();
    }

    internal void Destroy(long runId, object service)
    {
        if (OwnsService(runId, service))
        {
            _readyInstance = null;
            StartFailed = true;
            Publish();
        }
    }

    private void Publish()
    {
        var prior = _changed;
        _changed = NewSignal();
        prior.TrySetResult();
        StateChanged?.Invoke();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
