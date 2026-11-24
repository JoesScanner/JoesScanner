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
        try { prior?.Cancel(); }
        catch
        {
            request.Dispose();
            throw;
        }
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

    internal sealed class Request : IDisposable
    {
        private readonly LivePlaybackRequests _owner;
        private readonly CancellationTokenSource _source;
        private bool _canceling;
        private bool _disposed;

        internal Request(LivePlaybackRequests owner, bool manual, CancellationToken ownership)
        {
            _owner = owner;
            IsManual = manual;
            _source = ownership.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ownership) : new();
            Token = _source.Token;
        }

        public CancellationToken Token { get; }
        public bool IsManual { get; }
        public bool IsCurrent => ReferenceEquals(_owner._current, this);
        public bool CanContinue => IsCurrent && !Token.IsCancellationRequested;

        internal void Cancel()
        {
            if (_disposed || _canceling)
                return;
            _canceling = true;
            try { _source.Cancel(); }
            finally
            {
                _canceling = false;
                if (_disposed)
                    _source.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (IsCurrent)
                _owner._current = null;
            // A cancellation callback can finish the old request inline.
            if (!_canceling)
                _source.Dispose();
        }
    }
}
