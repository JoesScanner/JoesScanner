namespace JoesScanner.Services;

// Each view model uses one instance on its UI dispatcher. Requests can also
// be retired by the page's playback ownership token.
internal sealed class LivePlaybackRequests
{
    private Request? _current;

    public bool HasManualSelection => _current?.IsManual == true;

    public Request Begin(bool manual, CancellationToken ownership = default)
    {
        var prior = _current;
        var request = new Request(this, manual, ownership);
        _current = request;
        prior?.Cancel();
        return request;
    }

    public void CancelCurrent() => _current?.Cancel();

    public void RetireCurrent()
    {
        var prior = _current;
        _current = null;
        prior?.Cancel();
    }

    public static bool IsExpectedCancellation(OperationCanceledException exception, CancellationToken token) =>
        token.IsCancellationRequested && !IsTimeout(exception) &&
        (!exception.CancellationToken.CanBeCanceled || exception.CancellationToken.IsCancellationRequested);

    public static bool IsTimeout(OperationCanceledException exception) =>
        exception.InnerException is TimeoutException;

    internal sealed class Request(LivePlaybackRequests owner, bool manual, CancellationToken ownership) : IDisposable
    {
        private readonly CancellationTokenSource _source = ownership.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ownership)
            : new();
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
