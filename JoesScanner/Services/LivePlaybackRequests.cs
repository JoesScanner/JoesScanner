namespace JoesScanner.Services;

// The Live view model uses this on its UI dispatcher. Each operation owns its token until it finishes.
internal sealed class LivePlaybackRequests
{
    private Request? _current;

    public bool HasManualSelection => _current?.IsManual == true;

    public Request Begin(bool manual)
    {
        var prior = _current;
        var request = new Request(this, manual);
        _current = request;
        prior?.Cancel();
        return request;
    }

    public void CancelCurrent() => _current?.Cancel();

    public static bool IsExpectedCancellation(OperationCanceledException exception, CancellationToken token) =>
        token.IsCancellationRequested && !IsTimeout(exception) &&
        (!exception.CancellationToken.CanBeCanceled || exception.CancellationToken.IsCancellationRequested);

    public static bool IsTimeout(OperationCanceledException exception) =>
        exception.InnerException is TimeoutException;

    internal sealed class Request(LivePlaybackRequests owner, bool manual) : IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private bool _disposed;

        public CancellationToken Token => _source.Token;
        public bool IsManual { get; } = manual;
        public bool IsCurrent => ReferenceEquals(owner._current, this);
        public bool CanContinue => IsCurrent && !_source.IsCancellationRequested;

        internal void Cancel() => _source.Cancel();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (IsCurrent)
                owner._current = null;
            _source.Dispose();
        }
    }
}
