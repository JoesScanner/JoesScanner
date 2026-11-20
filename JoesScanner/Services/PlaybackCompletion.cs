namespace JoesScanner.Services;

// One marker per native player attempt. Cancellation after a confirmed end
// can still stop the operation's UI, but cannot revoke the end of the clip.
public sealed class PlaybackCompletion
{
    private int _result;
    internal bool Confirmed => Volatile.Read(ref _result) == 1;

    internal bool Confirm(CancellationToken token) =>
        !token.IsCancellationRequested && Interlocked.CompareExchange(ref _result, 1, 0) == 0;

    internal bool Reject() => Interlocked.CompareExchange(ref _result, 2, 0) == 0;
}
