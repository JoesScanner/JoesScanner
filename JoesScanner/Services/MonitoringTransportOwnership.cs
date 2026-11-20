namespace JoesScanner.Services;

// Remote commands can arrive off the dispatcher. Publish the run and clip
// together so a callback always captures a consistent pair.
internal sealed class MonitoringTransportOwnership
{
    internal sealed class Publication(long runId)
    {
        internal long RunId { get; } = runId;
    }

    private Publication? _publication;

    internal void Publish(long runId) =>
        Volatile.Write(ref _publication, new Publication(runId));

    internal void Clear() => Volatile.Write(ref _publication, null);
    internal Publication? Capture() => Volatile.Read(ref _publication);

    internal bool CanDispatch(Publication captured, long activeRunId, bool stopsMonitoring) =>
        captured.RunId != 0 && captured.RunId == activeRunId &&
        (stopsMonitoring || ReferenceEquals(captured, Capture()));
}
