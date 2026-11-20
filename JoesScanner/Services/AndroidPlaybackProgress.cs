namespace JoesScanner.Services;

// Time is monotonic and relative to one audible attempt. Time spent waiting for
// focus, a private route, or foreground eligibility is outside this watchdog.
internal sealed class AndroidPlaybackProgress(int position)
{
    private TimeSpan _lastProgress;
    private TimeSpan? _bufferingSince;

    internal void BufferingChanged(bool buffering, TimeSpan elapsed)
    {
        if (buffering)
            _bufferingSince ??= elapsed;
        else
            _bufferingSince = null;
    }

    internal bool IsStalled(int currentPosition, TimeSpan elapsed)
    {
        if (currentPosition != position)
        {
            position = currentPosition;
            _lastProgress = elapsed;
        }

        if (_bufferingSince is { } since)
            return elapsed - (since > _lastProgress ? since : _lastProgress) >= TimeSpan.FromSeconds(4);
        return elapsed - _lastProgress >= TimeSpan.FromSeconds(8);
    }
}
