namespace JoesScanner.Services;

// Serialized by the Live native playback gate. A failed commit is retried
// before another recording can play or another run can acquire the journal.
internal sealed class DurablePlaybackCommit(Func<DurableLiveQueue.Run, string, Task> persist)
{
    internal (DurableLiveQueue.Run Run, string Id)? Pending { get; private set; }

    internal async Task ConfirmAsync(DurableLiveQueue.Run run, string id)
    {
        Pending = (run, id);
        await FlushAsync();
    }

    internal async Task FlushAsync()
    {
        if (Pending is { } pending)
        {
            await persist(pending.Run, pending.Id);
            Pending = null;
        }
    }
}
