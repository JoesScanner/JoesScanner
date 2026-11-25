using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JoesScanner.Services;

internal readonly record struct RecordingCacheKey(string Value)
{
    internal static RecordingCacheKey? Create(Guid? recorder, string endpoint, string? recordingId,
        string? account, string? accountAuthority)
    {
        var archive = RecordingStorageReference.ServerKey(endpoint);
        if (recorder == null || archive == null || string.IsNullOrWhiteSpace(recordingId))
            return null;

        // Only the digest reaches disk. The archive also distinguishes an edited address
        // that points at a different collection using the same recording IDs.
        var identity = JsonSerializer.Serialize(new[] { recorder.Value.ToString("N"), archive, recordingId,
            account, accountAuthority });
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }
}

public sealed class PlaybackAudioCache
{
    internal const int DefaultLimitMiB = 100;
    internal const long MiB = 1024L * 1024;
    internal const long LowStorageBytes = 64 * MiB;
    internal static IReadOnlyList<int> LimitsMiB { get; } = Array.AsReadOnly(new[] { 25, 50, 100, 250, 500, 1024 });
    private const long MaximumTransferBytes = 1024 * MiB;
    private const int MaximumSidecarBytes = (int)MiB;

    private readonly object _gate = new();
    private readonly Dictionary<RecordingCacheKey, Entry> _entries = new();
    private readonly Dictionary<RecordingCacheKey, DownloadGate> _downloads = new();
    private readonly SemaphoreSlim _foregroundTransfers = new(2);
    private readonly string _directory;
    private readonly string _incoming;
    private readonly string _fallback;
    private readonly Func<string, long?> _availableBytes;
    private readonly Lazy<Task> _recovery;
    private long _limit;
    private long _usage;
    private long _lastAccess;
    private long _clearRevision;
    private bool _underPressure;

    private sealed class Entry(RecordingCacheKey key, string path, string marker, long length, long written, long access, long analysisBytes)
    {
        internal RecordingCacheKey Key { get; } = key;
        internal string Path { get; } = path;
        internal string Marker { get; } = marker;
        internal string Analysis => Path + ".analysis.json";
        internal long Length { get; } = length;
        internal long Written { get; } = written;
        internal long Access { get; set; } = access;
        internal long AnalysisBytes { get; set; } = analysisBytes;
        internal long Size => Length + AnalysisBytes + MarkerBytes;
        internal long MarkerBytes { get; set; }
        internal int Leases { get; set; }
        internal bool Retired { get; set; }
    }

    private sealed class DownloadGate
    {
        internal readonly SemaphoreSlim Lock = new(1);
        internal int Users;
    }

    internal PlaybackAudioCache(string directory, string fallback, int limitMiB,
        Func<string, long?>? availableBytes = null, string? legacyDownloads = null, string? legacyFilterCache = null)
    {
        _directory = Path.GetFullPath(directory);
        _incoming = Path.Combine(_directory, "incoming");
        _fallback = Path.GetFullPath(fallback);
        _limit = LimitBytes(limitMiB);
        _availableBytes = availableBytes ?? DriveFreeBytes;
        _recovery = new(() => Task.Run(() => Recover(legacyDownloads, legacyFilterCache)));
    }

    internal long UsageBytes => Interlocked.Read(ref _usage);
    internal event Action? UsageChanged;
    internal Task InitializeAsync() => _recovery.Value;

    internal async Task SetLimitAsync(int limitMiB)
    {
        Interlocked.Exchange(ref _limit, LimitBytes(limitMiB));
        await _recovery.Value.ConfigureAwait(false);
        await Task.Run(() =>
        {
            lock (_gate)
            {
                TrimTo(Interlocked.Read(ref _limit));
                PublishUsage();
            }
        }).ConfigureAwait(false);
    }

    internal async Task ClearAsync()
    {
        await _recovery.Value.ConfigureAwait(false);
        await Task.Run(() =>
        {
            lock (_gate)
            {
                ++_clearRevision;
                foreach (var entry in _entries.Values.ToArray())
                    Retire(entry);
                PublishUsage();
            }
        }).ConfigureAwait(false);
    }

    internal async Task<bool> CanPreloadAsync(CancellationToken token)
    {
        await _recovery.Value.WaitAsync(token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            lock (_gate)
                return CheckStorage() && _entries.Values.Where(entry => entry.Leases != 0).Sum(entry => entry.Size) < Interlocked.Read(ref _limit);
        }, token).ConfigureAwait(false);
    }

    // A caller supplies its normal miss authorization and its local-media authorization.
    // No path leaves this method until that caller's current access has been checked.
    internal async Task<PlaybackAudioLease?> AcquireAsync(RecordingCacheKey? key,
        Func<bool, CancellationToken, Task> authorize, Func<Transfer, CancellationToken, Task<string>> fetch,
        CancellationToken token, bool speculative = false)
    {
        await _recovery.Value.WaitAsync(token);
        if (speculative && (key == null || !await CanPreloadAsync(token)))
            return null;

        DownloadGate? download = null;
        if (key is { } identity)
        {
            lock (_gate)
            {
                if (!_downloads.TryGetValue(identity, out download))
                    _downloads.Add(identity, download = new());
                ++download.Users;
            }
        }

        try
        {
            if (download != null)
                await download.Lock.WaitAsync(token);
            try
            {
                var (hit, revision, canStore) = await Task.Run(() =>
                {
                    lock (_gate)
                    {
                        token.ThrowIfCancellationRequested();
                        var cached = key is { } cacheKey ? TryLease(cacheKey) : null;
                        return (cached, _clearRevision, CheckStorage());
                    }
                }, token);
                if (hit != null)
                {
                    try
                    {
                        await authorize(true, token);
                        token.ThrowIfCancellationRequested();
                        return hit;
                    }
                    catch
                    {
                        hit.Dispose();
                        throw;
                    }
                }
                if (speculative && !canStore)
                    return null;

                await authorize(false, token);
                if (!speculative)
                    await _foregroundTransfers.WaitAsync(token);
                try
                {
                    var limit = speculative ? Interlocked.Read(ref _limit) : MaximumTransferBytes;
                    using var transfer = new Transfer(this, limit, speculative);
                    var path = await fetch(transfer, token);
                    token.ThrowIfCancellationRequested();
                    if (key is { } cacheKey && canStore)
                    {
                        var stored = await Task.Run(() =>
                        {
                            lock (_gate)
                            {
                                token.ThrowIfCancellationRequested();
                                return revision == _clearRevision && CheckStorage() ? TryPublish(cacheKey, path) : null;
                            }
                        }, token);
                        if (stored != null)
                            return stored;
                    }
                    if (speculative)
                        return null;
                    transfer.Relinquish(path);
                    return new(new Uri(path).AbsoluteUri, cacheHit: false, release: () => Delete(path));
                }
                finally
                {
                    if (!speculative)
                        _foregroundTransfers.Release();
                }
            }
            finally { download?.Lock.Release(); }
        }
        finally
        {
            if (download != null)
            {
                lock (_gate)
                {
                    if (--download.Users == 0)
                    {
                        _downloads.Remove(key!.Value);
                        download.Lock.Dispose();
                    }
                }
            }
        }
    }

    private PlaybackAudioLease? TryLease(RecordingCacheKey key)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry.Retired)
            return null;
        if (!IsIntact(entry))
        {
            Retire(entry);
            PublishUsage();
            return null;
        }
        entry.Access = NextAccess();
        TryFileAction(() => File.SetLastWriteTimeUtc(entry.Marker, new DateTime(entry.Access, DateTimeKind.Utc)));
        return Lease(entry, cacheHit: true);
    }

    private PlaybackAudioLease? TryPublish(RecordingCacheKey key, string stagingFile)
    {
        // A prior lease marked for deletion still owns its filename.
        if (_entries.ContainsKey(key))
            return null;
        var length = Length(stagingFile);
        var budget = Interlocked.Read(ref _limit);
        if (length is <= 0 || length + 64 > budget)
            return null;
        TrimTo(budget - length - 64);
        if (_entries.Values.Sum(entry => entry.Size) + length + 64 > budget)
        {
            PublishUsage();
            return null;
        }

        var path = Path.Combine(_directory, key.Value + Path.GetExtension(stagingFile));
        var marker = Path.Combine(_directory, key.Value + ".entry");
        try
        {
            Directory.CreateDirectory(_directory);
            if (IsReparsePoint(_directory))
                return null;
            // The marker is published last. Recovery treats media without a marker as incomplete.
            File.Move(stagingFile, path);
            var access = NextAccess();
            long written;
            try
            {
                written = File.GetLastWriteTimeUtc(path).Ticks;
                File.WriteAllText(marker, FormattableString.Invariant($"{length} {written}"));
                File.SetLastWriteTimeUtc(marker, new DateTime(access, DateTimeKind.Utc));
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                // Keep a successfully fetched file playable if bookkeeping cannot be written.
                Delete(marker);
                return new(new Uri(path).AbsoluteUri, cacheHit: false, release: () => Delete(path));
            }
            var stored = new Entry(key, path, marker, length, written, access, 0) { MarkerBytes = Length(marker) };
            _entries.Add(key, stored);
            return Lease(stored, cacheHit: false);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return null;
        }
        finally { PublishUsage(); }
    }

    private PlaybackAudioLease Lease(Entry entry, bool cacheHit)
    {
        ++entry.Leases;
        return new(new Uri(entry.Path).AbsoluteUri, cacheHit, release: () => Release(entry), invalidate: () =>
        {
            lock (_gate) Retire(entry);
        });
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Leases == 0)
            {
                if (entry.Retired)
                    Retire(entry);
                TrimTo(Interlocked.Read(ref _limit));
                PublishUsage();
            }
        }
    }

    internal string? ReadAnalysis(string path)
    {
        lock (_gate)
        {
            var entry = ActiveEntry(path);
            if (entry == null || Length(entry.Analysis) is <= 0 or > MaximumSidecarBytes)
                return null;
            try { return File.ReadAllText(entry.Analysis); }
            catch (Exception ex) when (IsStorageError(ex)) { return null; }
        }
    }

    internal bool HasAnalysisLease(string path)
    {
        lock (_gate) return ActiveEntry(path) != null;
    }

    internal bool StoreAnalysis(string path, string json)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaximumSidecarBytes)
            return false;
        lock (_gate)
        {
            var entry = ActiveEntry(path);
            if (entry == null || !CheckStorage())
                return false;
            var budget = Interlocked.Read(ref _limit) - bytes + entry.AnalysisBytes;
            TrimTo(budget);
            if (_entries.Values.Sum(candidate => candidate.Size) > budget)
            {
                PublishUsage();
                return false;
            }
            var temporary = entry.Analysis + ".part";
            try
            {
                File.WriteAllText(temporary, json);
                File.Move(temporary, entry.Analysis, overwrite: true);
                entry.AnalysisBytes = bytes;
                return true;
            }
            catch (Exception ex) when (IsStorageError(ex)) { return false; }
            finally
            {
                Delete(temporary);
                PublishUsage();
            }
        }
    }

    private Entry? ActiveEntry(string path) =>
        TryKey(Path.GetFileNameWithoutExtension(path), out var key) && _entries.TryGetValue(key, out var entry) &&
        entry.Leases != 0 && !entry.Retired && entry.Path == path ? entry : null;

    private bool CheckStorage()
    {
        var available = _availableBytes(_directory);
        var low = available is < LowStorageBytes;
        if (low != _underPressure)
        {
            _underPressure = low;
            AppLog.Basic(() => low ? "Audio cache: low storage, preloading suspended." : "Audio cache: storage recovered.");
        }
        if (low)
        {
            foreach (var entry in EvictionOrder())
            {
                if (available >= LowStorageBytes)
                    break;
                Retire(entry);
                available = _availableBytes(_directory);
            }
            PublishUsage();
        }
        return !low;
    }

    private Entry[] EvictionOrder() => _entries.Values.Where(entry => entry.Leases == 0).OrderBy(entry => entry.Access).ToArray();

    private void TrimTo(long target)
    {
        var bytes = _entries.Values.Sum(entry => entry.Size);
        if (bytes <= target)
            return;
        foreach (var entry in EvictionOrder())
        {
            var size = entry.Size;
            Retire(entry);
            if (!_entries.ContainsKey(entry.Key))
                bytes -= size;
            if (bytes <= target)
                break;
        }
    }

    private void Retire(Entry entry)
    {
        entry.Retired = true;
        if (entry.Leases != 0)
            return;
        Delete(entry.Marker);
        Delete(entry.Analysis);
        Delete(entry.Path);
        if (!File.Exists(entry.Marker) && !File.Exists(entry.Analysis) && !File.Exists(entry.Path))
            _entries.Remove(entry.Key);
    }

    private void PublishUsage()
    {
        var bytes = _entries.Values.Sum(entry => entry.Size);
        if (Interlocked.Exchange(ref _usage, bytes) != bytes)
            UsageChanged?.Invoke();
    }

    private void Recover(string? legacyDownloads, string? legacyFilterCache)
    {
        lock (_gate)
        {
            if (!IsReparsePoint(_directory))
                DeleteTransfers(_incoming);
            DeleteTransfers(_fallback);
            if (legacyDownloads != null)
                foreach (var path in Files(legacyDownloads))
                {
                    var components = Path.GetFileName(path).Split(['_', '.']);
                    if (components.Length is 3 or 4 && components[0] is "jaas" or "hist" &&
                        Guid.TryParseExact(components[1], "N", out _) && RecordingAudioTransfer.Extensions.Contains("." + components[2]) &&
                        (components.Length == 3 || components[3] == "tmp"))
                        Delete(path);
                }

            var paths = Files(_directory);
            foreach (var group in paths.Where(path => IsManagedEntryFile(path) && TryKey(Path.GetFileName(path).Split('.')[0], out _))
                         .GroupBy(path => Path.GetFileName(path).Split('.')[0]))
            {
                var key = new RecordingCacheKey(group.Key);
                var marker = Path.Combine(_directory, key.Value + ".entry");
                var media = group.Where(path => RecordingAudioTransfer.Extensions.Contains(Path.GetExtension(path)) &&
                    Path.GetFileNameWithoutExtension(path) == key.Value).Take(2).ToArray();
                var entry = media.Length == 1 ? RestoreEntry(key, media[0], marker) : null;
                if (entry != null)
                {
                    _entries.Add(key, entry);
                    _lastAccess = Math.Max(_lastAccess, entry.Access);
                }
                foreach (var path in group)
                    if (path != entry?.Path && path != entry?.Marker && path != entry?.Analysis)
                        Delete(path);
            }
            // The former filter cache used URL/settings hashes, without recorder or account provenance.
            if (legacyFilterCache != null)
                foreach (var path in Files(legacyFilterCache))
                {
                    var name = Path.GetFileName(path).Split('.')[0].Split('_');
                    if (name.Length == 2 && TryKey(name[0], out _) && TryKey(name[1], out _) && IsManagedEntryFile(path))
                        Delete(path);
                }
            TrimTo(Interlocked.Read(ref _limit));
            PublishUsage();
        }
    }

    private static Entry? RestoreEntry(RecordingCacheKey key, string media, string marker)
    {
        try
        {
            var markerSize = Length(marker);
            if (markerSize is <= 0 or > 64)
                return null;
            var data = File.ReadAllText(marker).Split(' ');
            if (data.Length != 2 || !long.TryParse(data[0], CultureInfo.InvariantCulture, out var length) ||
                !long.TryParse(data[1], CultureInfo.InvariantCulture, out var written) || length <= 0)
                return null;
            var entry = new Entry(key, media, marker, length, written, File.GetLastWriteTimeUtc(marker).Ticks,
                Length(media + ".analysis.json")) { MarkerBytes = markerSize };
            return IsIntact(entry) ? entry : null;
        }
        catch (Exception ex) when (IsStorageError(ex)) { return null; }
    }

    private static bool IsIntact(Entry entry)
    {
        try { return Length(entry.Path) == entry.Length && File.GetLastWriteTimeUtc(entry.Path).Ticks == entry.Written; }
        catch (Exception ex) when (IsStorageError(ex)) { return false; }
    }

    private long NextAccess() => _lastAccess = Math.Max(DateTime.UtcNow.Ticks, _lastAccess + 1);

    private static bool TryKey(string name, out RecordingCacheKey key)
    {
        key = new(name);
        return name.Length == 64 && name.All(char.IsAsciiHexDigit);
    }

    private static bool IsManagedEntryFile(string path)
    {
        var name = Path.GetFileName(path);
        var parts = name.Split('.');
        if (parts.Length == 2)
            return parts[1] == "entry" || RecordingAudioTransfer.Extensions.Contains("." + parts[1]);
        return parts.Length >= 3 && RecordingAudioTransfer.Extensions.Contains("." + parts[1]) &&
            (parts is [_, _, "tmp"] or [_, _, "analysis", "json"] or [_, _, "analysis", "json", "part"]);
    }

    private static long LimitBytes(int limit) => (LimitsMiB.Contains(limit) ? limit : DefaultLimitMiB) * MiB;

    private static long? DriveFreeBytes(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;

            // On Unix the local cache may be on a different filesystem from "/".
            string? directory = path;
            while (directory != null && !Directory.Exists(directory))
                directory = Path.GetDirectoryName(directory);
            return directory == null ? null : new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is ArgumentException or NotSupportedException) { return null; }
    }

    private static string[] Files(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || IsReparsePoint(directory))
                return [];
            return Directory.GetFiles(directory);
        }
        catch (Exception ex) when (IsStorageError(ex)) { return []; }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (IsStorageError(ex)) { return false; }
    }

    private static void DeleteTransfers(string directory)
    {
        foreach (var path in Files(directory))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) &&
                (Path.GetExtension(path) == ".part" || RecordingAudioTransfer.Extensions.Contains(Path.GetExtension(path))))
                Delete(path);
    }

    private static long Length(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint) ? file.Length : 0;
        }
        catch (Exception ex) when (IsStorageError(ex)) { return 0; }
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException;
    private static void TryFileAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (IsStorageError(ex)) { }
    }

    private static void Delete(string path) => TryFileAction(() => File.Delete(path));

    internal sealed class Transfer(PlaybackAudioCache cache, long maximumBytes, bool speculative) : IDisposable
    {
        private string? _path;

        internal async Task<string> StoreAsync(HttpResponseMessage response, string extension, CancellationToken token)
        {
            var length = response.Content.Headers.ContentLength ?? 0;
            FileStream? file = null;
            var path = !IsReparsePoint(cache._directory) && !(cache._availableBytes(cache._incoming) < length)
                ? OpenIncoming(cache._incoming, out file) : null;
            path ??= OpenIncoming(cache._fallback, out file);
            if (path == null)
                throw new RecordingAudioUnavailableException(detail: "Local space for playback audio is unavailable.");
            _path = path;
            string mediaExtension;
            await using (file!)
                mediaExtension = await RecordingAudioTransfer.CopyAsync(response, file!, extension, maximumBytes,
                    () => cache._availableBytes(path), speculative, token).ConfigureAwait(false);
            var complete = Path.ChangeExtension(path, mediaExtension);
            File.Move(path, complete);
            return _path = complete;
        }

        private static string? OpenIncoming(string directory, out FileStream? file)
        {
            file = null;
            try
            {
                Directory.CreateDirectory(directory);
                if (IsReparsePoint(directory))
                    return null;
                var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
                file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                return path;
            }
            catch (Exception ex) when (IsStorageError(ex)) { return null; }
        }

        internal void Relinquish(string path)
        {
            if (_path == path)
                _path = null;
        }

        public void Dispose()
        {
            if (_path != null)
                Delete(_path);
        }
    }
}

internal sealed class PlaybackAudioLease(string url, bool cacheHit = false, Action? release = null, Action? invalidate = null) : IDisposable
{
    private Action? _release = release;
    internal string Url { get; } = url;
    internal bool CacheHit { get; } = cacheHit;
    internal void Invalidate() => invalidate?.Invoke();
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
