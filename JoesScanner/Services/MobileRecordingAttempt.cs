namespace JoesScanner.Services;

// A recording can outlive a native player. Only a started, uninterrupted
// player instance may confirm the recording's natural end.
internal sealed class MobileRecordingAttempt(PlaybackCompletion completion) : IDisposable
{
    private const int Preparing = 0;
    private const int Playing = 1;
    private const int Interrupted = 2;
    private const int Finished = 3;
    private const int Failed = 4;
    private int _state;

    internal bool WasInterrupted => Volatile.Read(ref _state) == Interrupted;
    internal bool HasStarted => Volatile.Read(ref _state) == Playing;

    internal void Start() => Interlocked.CompareExchange(ref _state, Playing, Preparing);

    internal bool Interrupt() => Interlocked.CompareExchange(ref _state, Interrupted, Playing) == Playing;

    internal bool Confirm(CancellationToken token) => !token.IsCancellationRequested &&
        Interlocked.CompareExchange(ref _state, Finished, Playing) == Playing && completion.Confirm(token);

    internal bool Reject()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is not (Preparing or Playing))
                return false;
            if (Interlocked.CompareExchange(ref _state, Failed, state) == state)
                return true;
        }
    }

    public void Dispose() => Reject();
}
