namespace JoesScanner.Services;

// One marker per native player attempt. Cancellation after a confirmed end
// can still stop the operation's UI, but cannot revoke the end of the clip.
public sealed class PlaybackCompletion
{
    private int _result;
    private Action? _started;
    internal bool Confirmed => Volatile.Read(ref _result) == 1;

    internal void OnStarted(Action callback) => _started = callback;

    internal void NotifyStarted(CancellationToken token)
    {
        if (token.IsCancellationRequested || Volatile.Read(ref _result) != 0)
            return;
        try { Interlocked.Exchange(ref _started, null)?.Invoke(); }
        catch (Exception ex) { AppLog.DebugWriteLine(() => $"Playback start observer failed ({ex.GetType().Name})."); }
    }

    internal bool Confirm(CancellationToken token) =>
        !token.IsCancellationRequested && Interlocked.CompareExchange(ref _result, 1, 0) == 0;

    internal bool Reject() => Interlocked.CompareExchange(ref _result, 2, 0) == 0;
}
