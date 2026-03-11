using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace JoesScanner.Services
{
    // Simple in-memory, thread-safe application log with a fixed maximum number of entries.
    // Also writes to a daily rolling file under AppDataDirectory/Logs.
    public static class AppLog
    {
        private const string LogEnabledPreferenceKey = "app_log_enabled";
        private const string LogEnabledMarkerFileName = "app_log_enabled.txt";

        private static readonly object Sync = new();
        private static readonly LinkedList<string> Buffer = new();

        private const int MaxEntries = 500;

        // Keep this bounded so a burst cannot grow memory without limit.
        // DropOldest keeps the most recent events, which is usually what you want for debugging.
        private readonly record struct LogWriteItem(string Line, int Generation);

        private static readonly Channel<LogWriteItem> FileChannel = Channel.CreateBounded<LogWriteItem>(new BoundedChannelOptions(5000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        private static int _fileWriterStarted;
        private static int _writeGeneration;

        private static volatile bool _isEnabled;

        static AppLog()
        {
            // Fail closed until persisted state is resolved. This avoids early startup
            // writes slipping through when storage APIs are temporarily unavailable.
            _isEnabled = false;

            // Enforce persisted state immediately on first use so startup cannot keep
            // stale in-memory or queued log entries alive when logging is off.
            ReloadEnabledStateFromStorage(purgePersistedLogs: true);
        }

        private static bool ReadEnabledPreference()
        {
            // Logging must fail closed if we find any explicit disabled marker.
            // This protects against path changes between packaged/unpackaged or early/late startup resolution.
            if (TryReadEnabledMarker(out var markerEnabled))
                return markerEnabled;

            try
            {
                return Preferences.Get(LogEnabledPreferenceKey, false);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadEnabledMarker(out bool enabled)
        {
            enabled = true;
            var sawExplicitTrue = false;

            foreach (var dir in GetCandidateLogsDirectories())
            {
                try
                {
                    var path = Path.Combine(dir, LogEnabledMarkerFileName);
                    if (!File.Exists(path))
                        continue;

                    var text = (File.ReadAllText(path) ?? string.Empty).Trim();

                    if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
                    {
                        enabled = false;
                        return true;
                    }

                    if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, "on", StringComparison.OrdinalIgnoreCase))
                    {
                        sawExplicitTrue = true;
                    }
                }
                catch
                {
                }
            }

            enabled = sawExplicitTrue;
            return sawExplicitTrue;
        }

        private static void TryWriteEnabledMarker(bool enabled)
        {
            foreach (var dir in GetCandidateLogsDirectories())
            {
                try
                {
                    Directory.CreateDirectory(dir);

                    var path = Path.Combine(dir, LogEnabledMarkerFileName);
                    File.WriteAllText(path, enabled ? "1" : "0");
                }
                catch
                {
                }
            }
        }

        private static IReadOnlyList<string> GetCandidateLogsDirectories()
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                var full = path;
                try { full = Path.GetFullPath(path); } catch { }

                if (seen.Add(full))
                    results.Add(full);
            }

            try
            {
                Add(Path.Combine(AppPaths.GetAppDataDirectorySafe(), "Logs"));
            }
            catch
            {
            }

            try
            {
                var appDataDirectory = FileSystem.AppDataDirectory;
                if (!string.IsNullOrWhiteSpace(appDataDirectory))
                    Add(Path.Combine(appDataDirectory, "Logs"));
            }
            catch
            {
            }

            try
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(baseDir))
                    Add(Path.Combine(baseDir, "JoesScanner", "Logs"));
            }
            catch
            {
            }

            return results;
        }

        private static void ApplyResolvedEnabledState(bool enabled, bool purgePersistedLogs)
        {
            _isEnabled = enabled;
            Interlocked.Increment(ref _writeGeneration);

            if (enabled)
                return;

            Clear();
            DrainPendingWrites();

            if (purgePersistedLogs)
            {
                ClearPersistedLogsSafe();
            }
        }


        public static bool IsEnabled => _isEnabled;

        public static bool ReloadEnabledStateFromStorage(bool purgePersistedLogs = true)
        {
            var enabled = ReadEnabledPreference();
            TryWriteEnabledMarker(enabled);
            ApplyResolvedEnabledState(enabled, purgePersistedLogs: purgePersistedLogs && !enabled);
            return enabled;
        }

        public static void DebugWriteLine(Func<string> messageFactory)
        {
            if (!_isEnabled || messageFactory == null)
                return;

            string message;
            try
            {
                message = messageFactory();
            }
            catch
            {
                return;
            }

            DebugWriteLine(message);
        }

        public static void DebugWriteLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (!_isEnabled)
                return;

            // When logging is off, nothing should be emitted anywhere.
            // When logging is on, keep a single logging path through AppLog only.
            Add(message);
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                Preferences.Set(LogEnabledPreferenceKey, enabled);
            }
            catch
            {
                // Ignore; marker file still persists the preference.
            }

            TryWriteEnabledMarker(enabled);
            ApplyResolvedEnabledState(enabled, purgePersistedLogs: !enabled);
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Buffer.Clear();
            }
        }

        public static void ClearAll()
        {
            try
            {
                Clear();
                ClearPersistedLogsSafe();
            }
            catch
            {
            }
        }

        private static void ClearPersistedLogsSafe()
        {
            foreach (var dir in GetCandidateLogsDirectories())
            {
                try
                {
                    if (!Directory.Exists(dir))
                        continue;

                    foreach (var file in Directory.EnumerateFiles(dir, "app_log_*.txt", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static void StartFileWriterIfNeeded()
        {
            if (Interlocked.Exchange(ref _fileWriterStarted, 1) != 0)
                return;

            _ = Task.Run(FileWriterLoopAsync);
        }

        private static void DrainPendingWrites()
        {
            try
            {
                while (FileChannel.Reader.TryRead(out _))
                {
                }
            }
            catch
            {
            }
        }

        private static async Task FileWriterLoopAsync()
        {
            StreamWriter? writer = null;
            string? currentPath = null;

            try
            {
                while (await FileChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    var batch = new List<LogWriteItem>(128);

                    while (FileChannel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);

                        // Cap batch size so we do not hold huge batches if a burst arrives.
                        if (batch.Count >= 512)
                            break;
                    }

                    if (batch.Count == 0)
                        continue;

                    try
                    {
                        var path = GetLogFilePath();
                        if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            writer?.Dispose();
                            currentPath = path;

                            var dir = Path.GetDirectoryName(path);
                            if (!string.IsNullOrWhiteSpace(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                            {
                                AutoFlush = false
                            };
                        }

                        var currentGeneration = Volatile.Read(ref _writeGeneration);
                        var loggingEnabled = _isEnabled;

                        for (var i = 0; i < batch.Count; i++)
                        {
                            var item = batch[i];
                            if (!loggingEnabled || item.Generation != currentGeneration)
                                continue;

                            writer!.WriteLine(item.Line);
                        }

                        writer!.Flush();
                    }
                    catch
                    {
                        // Never let logging break the app.
                        try
                        {
                            writer?.Dispose();
                        }
                        catch
                        {
                        }

                        writer = null;
                        currentPath = null;
                    }
                }
            }
            catch
            {
                // Swallow.
            }
            finally
            {
                try
                {
                    writer?.Dispose();
                }
                catch
                {
                }
            }
        }

        private static string GetLogsDirectory()
        {
            return Path.Combine(AppPaths.GetAppDataDirectorySafe(), "Logs");
        }

        private static string GetLogFilePath()
        {
            // Daily rolling file based on LOCAL date so the file name matches the timestamps users see.
            var day = DateTime.Now.ToString("yyyy-MM-dd");
            return Path.Combine(GetLogsDirectory(), $"app_log_{day}.txt");
        }

        public static void Add(Func<string> messageFactory)
        {
            if (!_isEnabled || messageFactory == null)
                return;

            string message;
            try
            {
                message = messageFactory();
            }
            catch
            {
                return;
            }

            Add(message);
        }

        public static void Add(string message)
        {
            if (!_isEnabled || string.IsNullOrWhiteSpace(message))
                return;

            var line = $"{DateTime.Now:HH:mm:ss}  {message}";

            lock (Sync)
            {
                Buffer.AddLast(line);
                if (Buffer.Count > MaxEntries)
                    Buffer.RemoveFirst();
            }

            StartFileWriterIfNeeded();

            // If the channel is full, DropOldest mode makes this succeed while keeping recent logs.
            var generation = Volatile.Read(ref _writeGeneration);
            FileChannel.Writer.TryWrite(new LogWriteItem(line, generation));
        }

        // Returns a snapshot of up to maxLines log entries as an array.
        // The result is ordered from newest to oldest.
        public static string[] GetSnapshot(int maxLines)
        {
            if (!_isEnabled)
                return [];

            lock (Sync)
            {
                return Buffer.Reverse().Take(maxLines).ToArray();
            }
        }

        // Creates a zip containing the most recent log files plus a snapshot of the in-memory buffer.
        // Returns the full path to the zip on disk.
        public static async Task<string?> ExportLogsAsync(int maxLogFiles = 3, int snapshotMaxLines = 500)
        {
            try
            {
                if (!_isEnabled)
                    return null;

                var cacheDir = FileSystem.CacheDirectory;
                if (string.IsNullOrWhiteSpace(cacheDir))
                    return null;

                Directory.CreateDirectory(cacheDir);

                var zipPath = Path.Combine(cacheDir, $"joesscanner_logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                if (File.Exists(zipPath))
                {
                    try { File.Delete(zipPath); } catch { }
                }

                var logFiles = GetMostRecentLogFiles(maxLogFiles);

                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    // Snapshot.txt (oldest to newest for readability)
                    var snapshotLines = GetSnapshot(snapshotMaxLines).Reverse().ToArray();
                    var snapshotText = string.Join(Environment.NewLine, snapshotLines);

                    var snapshotEntry = zip.CreateEntry("snapshot.txt", CompressionLevel.Fastest);
                    await using (var entryStream = snapshotEntry.Open())
                    await using (var writer = new StreamWriter(entryStream))
                    {
                        await writer.WriteAsync(snapshotText).ConfigureAwait(false);
                    }

                    // Include file logs
                    for (var i = 0; i < logFiles.Count; i++)
                    {
                        var filePath = logFiles[i];
                        if (!File.Exists(filePath))
                            continue;

                        var name = Path.GetFileName(filePath);
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        zip.CreateEntryFromFile(filePath, $"files/{name}", CompressionLevel.Fastest);
                    }
                }

                return zipPath;
            }
            catch
            {
                return null;
            }
        }

        private static List<string> GetMostRecentLogFiles(int maxFiles)
        {
            try
            {
                if (maxFiles <= 0)
                    return [];

                var dir = GetLogsDirectory();
                if (!Directory.Exists(dir))
                    return [];

                var files = Directory.EnumerateFiles(dir, "app_log_*.txt", SearchOption.TopDirectoryOnly)
                    .Select(p =>
                    {
                        try { return new FileInfo(p); } catch { return null; }
                    })
                    .Where(f => f != null)
                    .OrderByDescending(f => f!.LastWriteTimeUtc)
                    .Take(maxFiles)
                    .Select(f => f!.FullName)
                    .ToList();

                return files;
            }
            catch
            {
                return [];
            }
        }
    }
}
