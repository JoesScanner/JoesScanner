namespace JoesScanner.Services;

// A traversal and the recent-file list can each hold the same downloaded clip.
// The file remains available until native playback and any cached reference end.
internal sealed class HistoryAudioFile(string url, string? ownedPath = null, long length = 0) : IDisposable
{
    private int _references = 1;

    internal string Url { get; } = url;
    internal bool CanReuse => length is > 0 and <= 8 * 1024 * 1024 && File.Exists(ownedPath);

    internal HistoryAudioFile Retain()
    {
        Interlocked.Increment(ref _references);
        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0 && ownedPath != null)
            Delete(ownedPath);
    }

    internal static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
