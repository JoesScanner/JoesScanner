namespace JoesScanner.Services;

internal sealed class RecorderOperation
{
    private readonly CancellationTokenSource _lifetime = new();
    internal CancellationToken Token => _lifetime.Token;

    internal void Validate()
    {
        if (_lifetime.IsCancellationRequested)
            throw new RecordingAccessException("The recorder configuration changed during the operation.");
    }

    internal void Apply(Action commit, CancellationToken token)
    {
        lock (_lifetime)
        {
            Validate();
            token.ThrowIfCancellationRequested();
            commit();
        }
    }

    // The callback performs only the final synchronous state or transaction commit.
    internal bool TryApply(Action commit)
    {
        lock (_lifetime)
        {
            if (_lifetime.IsCancellationRequested)
                return false;
            commit();
            return true;
        }
    }

    internal void Retire()
    {
        lock (_lifetime)
            _ = _lifetime.CancelAsync();
    }
}
