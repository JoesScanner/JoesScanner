namespace JoesScanner.Services;

// Accessed on the UI dispatcher, alongside the playback worker's running flag.
internal sealed class LiveQueueWake
{
    private long _generation;

    internal long BeforeRead() => _generation;
    internal void Signal() => ++_generation;
    internal bool ChangedSince(long read) => _generation != read;
}
