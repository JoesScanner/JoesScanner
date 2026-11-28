namespace JoesScanner.Services;

// One marker per native player attempt. Cancellation after a confirmed end
// can still stop the operation's UI, but cannot revoke the end of the clip.
public sealed class PlaybackCompletion
{
    private int _result;
    private Action? _started;
    private Action? _ended;
    private RecorderOperation? _recorder;
    internal bool RecorderRetired => _recorder?.Token.IsCancellationRequested == true;
    internal void Bind(RecorderOperation? recorder) => _recorder = recorder;
    internal bool Confirmed => Volatile.Read(ref _result) == 1;
    internal bool IsFinished => Volatile.Read(ref _result) != 0;

    internal void OnStarted(Action callback) => _started = callback;
    internal void OnEnded(Action callback) => _ended = callback;

    internal void NotifyStarted(CancellationToken token)
    {
        if (token.IsCancellationRequested || Volatile.Read(ref _result) != 0)
            return;
        try { Interlocked.Exchange(ref _started, null)?.Invoke(); }
        catch (Exception ex) { AppLog.DebugWriteLine(() => $"Playback start observer failed ({ex.GetType().Name})."); }
    }

    internal bool Confirm(CancellationToken token)
    {
        if (_recorder is not { } recorder)
            return !token.IsCancellationRequested && Finish(1);
        bool confirmed = false;
        recorder.TryApply(() =>
        {
            if (!token.IsCancellationRequested)
                confirmed = Interlocked.CompareExchange(ref _result, 1, 0) == 0;
        });
        if (confirmed)
            NotifyEnded();
        return confirmed;
    }

    internal bool Reject() => Finish(2);

    private bool Finish(int result)
    {
        if (Interlocked.CompareExchange(ref _result, result, 0) != 0)
            return false;
        NotifyEnded();
        return true;
    }

    private void NotifyEnded()
    {
        Interlocked.Exchange(ref _started, null);
        try { Interlocked.Exchange(ref _ended, null)?.Invoke(); }
        catch (Exception ex) { AppLog.DebugWriteLine(() => $"Playback end observer failed ({ex.GetType().Name})."); }
    }
}
