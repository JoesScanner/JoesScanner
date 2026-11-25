namespace JoesScanner.Services;

internal sealed class LiveAudioPreloader
{
    internal const int MaxUpcoming = 2;
    internal sealed record Candidate(long Arrival, Func<CancellationToken, Task> Load);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots = new(MaxUpcoming);
    private Batch? _active;
    private long _revision;

    internal long Revision { get { lock (_gate) return _revision; } }

    internal Task Start(Func<CancellationToken, Task<IReadOnlyList<Candidate>>> forecast, CancellationToken playback,
        long? revision = null)
    {
        Batch batch;
        Batch? prior;
        lock (_gate)
        {
            if (playback.IsCancellationRequested || revision is { } expected && expected != _revision)
                return Task.CompletedTask;
            prior = _active;
            _active = batch = new(playback);
        }
        prior?.Cancel();
        return RunAsync(batch, forecast);
    }

    internal void Wake() => Volatile.Read(ref _active)?.Wake();
    internal void Retire()
    {
        Batch? prior;
        lock (_gate)
        {
            ++_revision;
            prior = _active;
            _active = null;
        }
        prior?.Cancel();
    }

    private async Task RunAsync(Batch batch, Func<CancellationToken, Task<IReadOnlyList<Candidate>>> forecast)
    {
        var token = batch.Token;
        try
        {
            var attempted = new HashSet<long>();
            while (attempted.Count < MaxUpcoming)
            {
                token.ThrowIfCancellationRequested();
                var candidates = await forecast(token);
                token.ThrowIfCancellationRequested();
                var next = candidates.Where(candidate => !attempted.Contains(candidate.Arrival))
                    .DistinctBy(candidate => candidate.Arrival).Take(MaxUpcoming - attempted.Count).ToArray();
                foreach (var candidate in next)
                    attempted.Add(candidate.Arrival);
                await Task.WhenAll(next.Select(candidate => LoadAsync(candidate, token)));
                if (attempted.Count < MaxUpcoming)
                    await batch.Signal.WaitAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.DebugWriteLine(() => $"Audio preload prediction failed ({ex.GetType().Name}).");
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_active, batch))
                    _active = null;
            batch.Dispose();
        }
    }

    private async Task LoadAsync(Candidate candidate, CancellationToken token)
    {
        await _slots.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            await candidate.Load(deadline.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.DebugWriteLine(() => $"Audio preload unavailable ({ex.GetType().Name}).");
        }
        finally { _slots.Release(); }
    }

    private sealed class Batch(CancellationToken playback) : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(playback);
        private bool _disposed;

        internal CancellationToken Token => _stop.Token;
        internal SemaphoreSlim Signal { get; } = new(0, 1);

        internal void Wake()
        {
            lock (_gate)
                if (!_disposed && Signal.CurrentCount == 0)
                    Signal.Release();
        }

        internal void Cancel()
        {
            lock (_gate)
            {
                if (!_disposed)
                    try { _stop.Cancel(); }
                    catch (AggregateException ex) { AppLog.DebugWriteLine(() => $"Audio preload cancellation failed ({ex.GetType().Name})."); }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _stop.Dispose();
                Signal.Dispose();
            }
        }
    }
}
