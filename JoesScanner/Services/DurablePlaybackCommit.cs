namespace JoesScanner.Services;

// Native playback reports confirmed ends in order. Persistence has its own
// gate so a manual selection can play while an earlier completion is saved.
internal sealed class DurablePlaybackCommit(Func<DurableLiveQueue.Run, string, Task> persist)
{
    private readonly Queue<(DurableLiveQueue.Run Run, string Id)> _pending = new();
    private readonly SemaphoreSlim _writer = new(1);

    internal (DurableLiveQueue.Run Run, string Id)? Pending
    {
        get
        {
            lock (_pending)
                return _pending.TryPeek(out var pending) ? pending : null;
        }
    }

    internal Task ConfirmAsync(DurableLiveQueue.Run run, string id)
    {
        lock (_pending)
            _pending.Enqueue((run, id));
        return FlushAsync();
    }

    internal bool IsPending(DurableLiveQueue.Run run, string id)
    {
        lock (_pending)
            return _pending.Contains((run, id));
    }

    internal async Task FlushAsync(CancellationToken token = default, Guid? recorderId = null)
    {
        if (recorderId.HasValue)
        {
            token.ThrowIfCancellationRequested();
            lock (_pending)
                if (!_pending.Any(entry => entry.Run.ServerId == recorderId))
                    return;
        }
        await _writer.WaitAsync(token);
        try
        {
            while (Pending is { } pending)
            {
                await persist(pending.Run, pending.Id);
                lock (_pending)
                    _pending.Dequeue();
            }
        }
        finally { _writer.Release(); }
    }
}
