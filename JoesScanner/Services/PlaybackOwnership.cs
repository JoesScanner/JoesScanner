namespace JoesScanner.Services;

// Page transitions happen on the UI dispatcher. A native player can be used
// by only one attempt, including its teardown, even across page transitions.
public sealed class PlaybackOwnership
{
    private Lease _current = new(isLive: true);
    private Lease? _nativeOwner;
    private readonly SemaphoreSlim _nativeGate = new(1);

    internal event Action? Changed;
    internal Lease? Live => Volatile.Read(ref _current) is { IsLive: true } lease ? lease : null;
    internal bool CanPublishLivePlayback => Volatile.Read(ref _nativeOwner) is { IsLive: true } owner && IsCurrent(owner);
    internal bool IsCurrent(Lease lease) => ReferenceEquals(Volatile.Read(ref _current), lease);

    internal Lease EnterHistory()
    {
        if (!_current.IsLive)
            return _current;
        var history = new Lease(isLive: false);
        Replace(history);
        return history;
    }

    internal void LeaveHistory(Lease lease)
    {
        if (!lease.IsLive && IsCurrent(lease))
            Replace(new Lease(isLive: true));
    }

    private void Replace(Lease next)
    {
        var prior = Interlocked.Exchange(ref _current, next);
        try { prior.Retire(); }
        finally { Changed?.Invoke(); }
    }

    internal async Task RunNativeAsync(Lease lease, CancellationToken token, Func<CancellationToken, Task> play)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(lease.Token, token);
        await _nativeGate.WaitAsync(lifetime.Token);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(lease))
                throw new OperationCanceledException(lease.Token);

            Volatile.Write(ref _nativeOwner, lease);
            await play(lifetime.Token);
        }
        finally
        {
            Volatile.Write(ref _nativeOwner, null);
            _nativeGate.Release();
        }
    }

    internal sealed class Lease
    {
        private readonly CancellationTokenSource _lifetime = new();

        internal Lease(bool isLive)
        {
            IsLive = isLive;
            Token = _lifetime.Token;
        }

        internal bool IsLive { get; }
        internal CancellationToken Token { get; }

        internal void Retire()
        {
            try { _lifetime.Cancel(); }
            finally { _lifetime.Dispose(); }
        }
    }
}
