namespace JoesScanner.Services;

// One coordinator per run. SQLite owns the scoped window. The run owns the
// wake signal and the lifetime of the HTTP work that can reconcile it.
public sealed class LiveStreamRecovery
{
    internal sealed record Window(DateTimeOffset Since, string Id, long Revision);
    internal sealed record TransportGap(Window Window, long Version, PlaybackLagGeneration.Lease Audio);
    internal sealed record LagObservation(TransportGap Gap, PlaybackLagEvidence? Evidence);

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
    private Func<PlaybackLagGeneration.Lease>? _captureLagAudio;
    private Action<LagObservation>? _reportLag;
    private DateTimeOffset? _lastTransportSuccess;
    private TransportGap? _transportGap;
    private long _recoveryVersion;
    private long _reportedIndeterminate;

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

    internal void ObserveTransportRecovery(Func<PlaybackLagGeneration.Lease> captureAudio, Action<LagObservation> report)
    {
        _captureLagAudio = captureAudio;
        _reportLag = report;
    }

    internal void TransportSucceeded(DateTimeOffset observed)
    {
        lock (_gate)
            _lastTransportSuccess = observed;
    }

    internal Task TransportLostAsync()
    {
        lock (_gate)
        {
            if (!IsDurable || _captureLagAudio == null || _lastTransportSuccess is not { } since)
                return Task.CompletedTask;

            _lastTransportSuccess = null;
            // Do not turn an outstanding foreground or inherited checkpoint
            // into evidence about a different transport gap.
            if (_outstanding != null && _transportGap == null)
                return Task.CompletedTask;
            return RequestAsync(since, _captureLagAudio());
        }
    }

    internal void TransportAuthenticationChanged()
    {
        lock (_gate)
        {
            _lastTransportSuccess = null;
            ++_recoveryVersion;
        }
    }

    internal TransportGap? GetTransportGap(Window? window)
    {
        lock (_gate)
            return ReferenceEquals(_transportGap?.Window, window) ? _transportGap : null;
    }

    internal void ReportIndeterminate(TransportGap gap)
    {
        lock (_gate)
        {
            if (gap.Version != _recoveryVersion || _reportedIndeterminate == gap.Version)
                return;
            _reportedIndeterminate = gap.Version;
            _reportLag?.Invoke(new(gap, null));
        }
    }

    internal bool CommitLagIfCurrent(TransportGap gap, Action commit)
    {
        lock (_gate)
            return !_lifetime.IsCancellationRequested && gap.Version == _recoveryVersion &&
                _outstanding == null && gap.Audio.TryCommit(commit);
    }

    internal void Request(DateTimeOffset since) => _ = RequestAsync(since);

    internal Task<bool> RequestAsync(DateTimeOffset since) => RequestAsync(since, null);

    private Task<bool> RequestAsync(DateTimeOffset since, PlaybackLagGeneration.Lease? lagAudio)
    {
        lock (_gate)
        {
            var version = ++_recoveryVersion;
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
                {
                    if (version == _recoveryVersion)
                        _transportGap = lagAudio != null ? new(saved, version, lagAudio) : null;
                    PublishRequest(saved);
                }
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

    internal Task CompleteAsync(Window window, CancellationToken token, PlaybackLagEvidence? evidence = null)
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
                if (completed && CompleteIfCurrent(window) && GetTransportGap(window) is { } gap &&
                    gap.Version == _recoveryVersion)
                {
                    if (evidence == null)
                        ReportIndeterminate(gap);
                    else
                        _reportLag?.Invoke(new(gap, evidence));
                }
                return completed;
            });
        }
    }

    private bool CompleteIfCurrent(Window window)
    {
        // A new request may cover additional time even when it shares a window.
        if (!ReferenceEquals(_outstanding, window))
            return false;

        _outstanding = null;
        _reconciled?.TrySetResult();
        return true;
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
