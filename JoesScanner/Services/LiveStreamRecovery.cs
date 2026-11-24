namespace JoesScanner.Services;

// One coordinator per run. SQLite owns the scoped window. The run owns the
// wake signal and the lifetime of the HTTP work that can reconcile it.
public sealed class LiveStreamRecovery
{
    internal sealed record Window(DateTimeOffset Since, string Id, long Revision);

    private readonly object _gate = new();
    private readonly DurableLiveQueue? _queue;
    private readonly DurableLiveQueue.Run? _run;
    private readonly CancellationToken _lifetime;
    private Task _published = Task.CompletedTask;
    private TaskCompletionSource _pending = NewSignal();
    private Window? _outstanding;
    private TaskCompletionSource? _reconciled;
    private string? _inheritedWindowId;
    private bool _hasPollingBaseline;

    public LiveStreamRecovery() { }

    private LiveStreamRecovery(DurableLiveQueue queue, DurableLiveQueue.Run run, CancellationToken token)
    {
        _queue = queue;
        _run = run;
        _lifetime = token;
    }

    internal static async Task<LiveStreamRecovery> OpenAsync(DurableLiveQueue queue, DurableLiveQueue.Run run,
        CancellationToken token)
    {
        var recovery = new LiveStreamRecovery(queue, run, token);
        if (await queue.ReadRecoveryWindowAsync(run, token).ConfigureAwait(false) is { } saved)
            recovery.PublishRequest(saved);
        return recovery;
    }

    internal static async Task<LiveStreamRecovery> StartListeningAsync(DurableLiveQueue queue, DurableLiveQueue.Run run,
        DateTimeOffset since, CancellationToken token)
    {
        await queue.StartListeningAsync(run, since, token).ConfigureAwait(false);
        var recovery = await OpenAsync(queue, run, token).ConfigureAwait(false);
        recovery.ListeningSince = since;
        recovery._inheritedWindowId = recovery._outstanding?.Id;
        return recovery;
    }

    internal DateTimeOffset? ListeningSince { get; private set; }
    internal bool NeedsPollingBaseline => ListeningSince.HasValue && !_hasPollingBaseline;
    internal void CompletePollingBaseline() => _hasPollingBaseline = true;
    internal bool IsInherited(Window? window) => window != null && window.Id == _inheritedWindowId;

    internal bool IsDurable => _queue != null;

    internal Window? OutstandingWindow
    {
        get
        {
            lock (_gate)
                return _outstanding;
        }
    }

    internal Task ReconciledForPlayback
    {
        get
        {
            lock (_gate)
                return _reconciled?.Task ?? Task.CompletedTask;
        }
    }

    internal Task Pending
    {
        get
        {
            lock (_gate)
                return _pending.Task;
        }
    }

    internal void Request(DateTimeOffset since) => _ = RequestAsync(since);

    internal Task<bool> RequestAsync(DateTimeOffset since)
    {
        lock (_gate)
        {
            EnsureReconciliationSignal();
            if (_queue == null)
            {
                var window = _outstanding;
                if (window != null && window.Since < since)
                    since = window.Since;
                PublishRequest(new(DateTimeOffset.FromUnixTimeSeconds(since.ToUnixTimeSeconds()),
                    window?.Id ?? Guid.NewGuid().ToString("N"), (window?.Revision ?? 0) + 1));
                return Task.FromResult(true);
            }

            // Reserve the SQLite writer in the same order as Jump and ingestion.
            // A request can wake HTTP only once its window is durable.
            return PublishInOrder(_queue.RequestRecoveryAsync(_run!, since, ReportStorageFailure, _lifetime), saved =>
            {
                if (saved != null)
                    PublishRequest(saved);
                return saved != null;
            });
        }
    }

    private void PublishRequest(Window window)
    {
        _outstanding = window;
        EnsureReconciliationSignal();
        _pending.TrySetResult();
    }

    private void EnsureReconciliationSignal()
    {
        if (_reconciled == null || _reconciled.Task.IsCompleted)
            _reconciled = NewSignal();
    }

    internal (Window? Window, bool NewRequest) BeginAttempt()
    {
        lock (_gate)
        {
            var newRequest = _pending.Task.IsCompleted;
            if (newRequest)
                _pending = NewSignal();
            return (_outstanding, newRequest);
        }
    }

    internal Task CompleteAsync(Window window, CancellationToken token)
    {
        lock (_gate)
        {
            if (_queue == null)
            {
                CompleteIfCurrent(window);
                return Task.CompletedTask;
            }

            return PublishInOrder(_queue.CompleteRecoveryAsync(_run!, window, ReportStorageFailure, token), completed =>
            {
                if (completed)
                    CompleteIfCurrent(window);
                return completed;
            });
        }
    }

    private void CompleteIfCurrent(Window window)
    {
        // A new request may cover additional time even when it shares a window.
        if (ReferenceEquals(_outstanding, window))
        {
            _outstanding = null;
            _reconciled?.TrySetResult();
        }
    }

    // SQLite operations are reserved immediately. Publish their outcomes in the
    // same order even if thread-pool continuations are scheduled out of order.
    private Task<bool> PublishInOrder<T>(Task<T> operation, Func<T, bool> publish)
    {
        var previous = _published;
        var current = ApplyAsync();
        _published = current;
        return current;

        async Task<bool> ApplyAsync()
        {
            await previous.ConfigureAwait(false);
            try
            {
                var result = await operation.ConfigureAwait(false);
                lock (_gate)
                    return publish(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                ReportStorageFailure(error);
            }
            return false;
        }
    }

    private static void ReportStorageFailure(Exception error) =>
        AppLog.Add(() => $"Live HTTP recovery checkpoint could not be saved ({error.GetType().Name}).");

    internal void AllowPlaybackWithoutHistory(Window window)
    {
        lock (_gate)
            if (ReferenceEquals(_outstanding, window))
                _reconciled?.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
