namespace JoesScanner.Services;

// Owned by the UI dispatcher. The run token also crosses into the stream worker.
internal sealed class MonitoringLifecycle
{
    private long _nextRunId = Random.Shared.NextInt64(1, long.MaxValue / 2);
    private Task _cleanup = Task.CompletedTask;

    internal Run? Current { get; private set; }
    internal long StopRevision { get; private set; }
    internal bool ExplicitlyStopped { get; private set; }

    internal Run? TryBeginStart(long? automaticStopRevision = null)
    {
        if (Current != null || (automaticStopRevision.HasValue &&
            (ExplicitlyStopped || automaticStopRevision.Value != StopRevision)))
            return null;

        ExplicitlyStopped = false;
        return Current = new Run(++_nextRunId, _cleanup);
    }

    internal bool Activate(Run run)
    {
        if (Current != run)
            return false;
        run.IsActive = true;
        return true;
    }

    internal bool CanRecover(Run? run) =>
        run is { IsActive: true } && Current == run && !run.Token.IsCancellationRequested;

    internal Task StopAsync(Action<Run?> recordIntent, Action<Run> retire, Func<Run, Task> cleanup)
    {
        ++StopRevision;
        var firstStop = !ExplicitlyStopped;
        ExplicitlyStopped = true;
        if (Current is { } run)
            return RetireAsync(run, () =>
            {
                try { recordIntent(run); }
                finally { retire(run); }
            }, cleanup);

        if (firstStop)
            recordIntent(null);
        return _cleanup;
    }

    internal Task AbortStartAsync(Run run, Action<Run> retire, Func<Run, Task> cleanup) =>
        Current == run && !run.IsActive ? RetireAsync(run, () => retire(run), cleanup) : Task.CompletedTask;

    private async Task RetireAsync(Run run, Action retire, Func<Run, Task> cleanup)
    {
        var previousCleanup = _cleanup;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cleanup = finished.Task;
        Current = null;
        try
        {
            try { run.Cancel(); }
            finally
            {
                try { retire(); }
                finally
                {
                    await previousCleanup;
                    await cleanup(run);
                }
            }
        }
        finally
        {
            run.Dispose();
            finished.TrySetResult();
        }
    }

    internal sealed class Run
    {
        private readonly CancellationTokenSource _lifetime = new();

        internal Run(long id, Task previousCleanup)
        {
            Id = id;
            PreviousCleanup = previousCleanup;
            Token = _lifetime.Token;
        }

        internal long Id { get; }
        internal Task PreviousCleanup { get; }
        internal CancellationToken Token { get; }
        internal bool IsActive { get; set; }

        // An OS permission prompt cannot be canceled. Its completion must not
        // unblock a retired startup or keep a replacement waiting on teardown.
        internal Task WaitForInitializationAsync(Task task) => task.WaitAsync(Token);

        internal void Cancel() => _lifetime.Cancel();
        internal void Dispose() => _lifetime.Dispose();
    }
}
