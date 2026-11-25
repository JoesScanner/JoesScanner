using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace JoesScanner.Services;

internal sealed class DiagnosticLog(Func<IReadOnlyList<string>> directories, TimeProvider clock)
{
    internal const long MaximumBytes = 25_000_000;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    private const int MaxMessage = 4096;
    private const int MaxEntries = 500;
    private const long SegmentBytes = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Channel<Pending> _events = Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object _memoryGate = new();
    private readonly Queue<string> _memory = new(MaxEntries);
    private readonly Queue<(DateTimeOffset At, string Line)> _pendingDisk = new(MaxEntries);
    private readonly SemaphoreSlim _io = new(1, 1);
    private string? _active;
    private DateTimeOffset _nextPrune;
    private volatile bool _detailed;
    private int _generation;

    private sealed record Pending(DateTimeOffset At, string Message, int Generation);

    internal bool Detailed
    {
        get => _detailed;
        set => _detailed = value;
    }

    internal void Add(string message, bool detailed)
    {
        if ((detailed && !_detailed) || string.IsNullOrWhiteSpace(message))
            return;
        if (message.Length > MaxMessage)
            message = "An oversized diagnostic event was omitted.";
        _events.Writer.TryWrite(new(clock.GetUtcNow(), message, Volatile.Read(ref _generation)));
    }

    internal string[] Snapshot(int count)
    {
        lock (_memoryGate)
            return _memory.Reverse().Take(Math.Clamp(count, 0, MaxEntries)).ToArray();
    }

    internal async Task RunAsync()
    {
        using var cadence = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            await FlushAsync().ConfigureAwait(false);
        } while (await cadence.WaitForNextTickAsync().ConfigureAwait(false));
    }

    internal async Task FlushAsync(bool prune = false)
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref _generation);
            for (var i = 0; i < 512 && _events.Reader.TryRead(out var item); i++)
            {
                if (item.Generation != generation)
                    continue;
                var text = DiagnosticRedaction.Sanitize(item.Message).Replace('\r', ' ').Replace('\n', ' ');
                if (text.Length == 0)
                    continue;
                var line = string.Create(CultureInfo.InvariantCulture, $"{item.At:HH:mm:ss}  {text}");
                lock (_memoryGate)
                {
                    if (_memory.Count == MaxEntries)
                        _memory.Dequeue();
                    _memory.Enqueue(line);
                }
                if (_pendingDisk.Count == MaxEntries)
                    _pendingDisk.Dequeue();
                _pendingDisk.Enqueue((item.At, line));
            }

            var now = clock.GetUtcNow();
            if (!prune && _pendingDisk.Count == 0 && now < _nextPrune)
                return;
            while (_pendingDisk.TryPeek(out var expired) && expired.At <= now - MaximumAge)
                _pendingDisk.Dequeue();

            try
            {
                var roots = directories();
                if (_pendingDisk.Count == 0)
                    Prune(roots, now, 0);
                else if (roots.Count > 0)
                    await WriteAsync(roots, now).ConfigureAwait(false);
                _nextPrune = now.AddMinutes(15);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retain a bounded window for the next tick. A partial append
                // already contains redacted text and is never appended to again.
                _active = null;
                _nextPrune = now.AddSeconds(10);
            }
        }
        finally { _io.Release(); }
    }

    private async Task WriteAsync(IReadOnlyList<string> roots, DateTimeOffset now)
    {
        Directory.CreateDirectory(roots[0]);
        if ((File.GetAttributes(roots[0]) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The diagnostics directory is redirected.");
#if IOS || MACCATALYST
        if (_active == null)
            AppPaths.ExcludeFromPlatformBackup(roots[0]);
#endif
        while (_pendingDisk.TryPeek(out var first))
        {
            var day = first.At.UtcDateTime.Date;
            var batch = _pendingDisk.TakeWhile(item => item.At.UtcDateTime.Date == day).Take(128).ToArray();
            var bytes = Utf8.GetBytes(string.Join('\n', batch.Select(item => item.Line)) + "\n");
            Prune(roots, now, bytes.Length);
            if (_active == null || !File.Exists(_active) || EntryTime(Path.GetFileName(_active))?.UtcDateTime.Date != day ||
                new FileInfo(_active).Length + bytes.Length > SegmentBytes)
            {
                var stamp = first.At.UtcDateTime.ToString("yyyy-MM-dd_HHmmssfff", CultureInfo.InvariantCulture);
                _active = Path.Combine(roots[0], $"app_log_{stamp}_{Guid.NewGuid():N}.txt");
            }
            await using (var output = new FileStream(_active, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete,
                8192, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            for (var i = 0; i < batch.Length; i++)
                _pendingDisk.Dequeue();
        }
    }

    private void Prune(IReadOnlyList<string> roots, DateTimeOffset now, long incoming)
    {
        var files = Files(roots).OrderBy(file => file.Start).ThenBy(file => file.Info.LastWriteTimeUtc).ToArray();
        var total = files.Sum(file => file.Info.Length);
        foreach (var file in files)
        {
            if (file.Start > now - MaximumAge && total + incoming <= MaximumBytes)
                break;
            var length = file.Info.Length;
            File.Delete(file.Info.FullName);
            total -= length;
            if (_active == file.Info.FullName)
                _active = null;
        }
    }

    internal async Task ClearAsync()
    {
        Interlocked.Increment(ref _generation);
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_memoryGate)
                _memory.Clear();
            _pendingDisk.Clear();
            _active = null;
            foreach (var file in Files(directories()))
                File.Delete(file.Info.FullName);
        }
        finally { _io.Release(); }
    }

    internal static IEnumerable<(FileInfo Info, DateTimeOffset Start)> Files(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("app_log_*.txt"))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && EntryTime(file.Name) is { } start)
                    yield return (file, start);
            }
        }
    }

    private static DateTimeOffset? EntryTime(string name)
    {
        const string prefix = "app_log_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".txt", StringComparison.Ordinal))
            return null;
        var stem = name.AsSpan(prefix.Length, name.Length - prefix.Length - 4);
        if (stem.Length == 10 && DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day))
        {
            var local = day.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        if (stem.Length == 53 && stem[20] == '_' && Guid.TryParseExact(stem[21..], "N", out _) &&
            DateTimeOffset.TryParseExact(stem[..20], "yyyy-MM-dd_HHmmssfff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp))
            return stamp;
        return null;
    }
}
