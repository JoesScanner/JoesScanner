namespace JoesScanner.Services;

// One per monitoring run so foreground events cannot carry a backlog into a
// later connection or survive an explicit disconnect.
public sealed class LiveStreamRecovery
{
    private readonly object _gate = new();
    private TaskCompletionSource _pending = NewSignal();
    private DateTimeOffset? _since;

    internal Task Pending
    {
        get
        {
            lock (_gate)
                return _pending.Task;
        }
    }

    internal void Request(DateTimeOffset since)
    {
        lock (_gate)
        {
            if (_since == null || since < _since)
                _since = since;
            _pending.TrySetResult();
        }
    }

    internal DateTimeOffset? Take()
    {
        lock (_gate)
        {
            if (_since is not { } since)
                return null;

            _since = null;
            _pending = NewSignal();
            return DateTimeOffset.FromUnixTimeSeconds(since.ToUnixTimeSeconds());
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
