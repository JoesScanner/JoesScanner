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
        private static readonly Channel<string> FileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(5000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        private static int _fileWriterStarted;

        private static volatile bool _isEnabled;

        static AppLog()
        {
            // Default ON so testers get logs unless they explicitly disable them.
            _isEnabled = Preferences.Get(LogEnabledPreferenceKey, true);
        }

        private static bool ReadEnabledPreference()
        {
            // Preference is the primary source of truth. The marker file is a robust fallback/override
            // in case Preferences storage is unavailable or fails silently on a given platform/runtime.
            try
            {
                var pref = Preferences.Get(LogEnabledPreferenceKey, true);

                if (TryReadEnabledMarker(out var markerEnabled))
                    return markerEnabled;

                return pref;
            }
            catch
            {
                if (TryReadEnabledMarker(out var markerEnabled))
                    return markerEnabled;

                return _isEnabled;
            }
        }

        private static bool TryReadEnabledMarker(out bool enabled)
        {
            enabled = true;

            try
            {
                var dir = GetLogsDirectory();
                var path = Path.Combine(dir, LogEnabledMarkerFileName);

                if (!File.Exists(path))
                    return false;

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
                    enabled = true;
                    return true;
                }

                // Unrecognized content: ignore and treat as no marker.
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void TryWriteEnabledMarker(bool enabled)
        {
            try
            {
                var dir = GetLogsDirectory();
                Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, LogEnabledMarkerFileName);
                File.WriteAllText(path, enabled ? "1" : "0");
            }
            catch
            {
            }
        }


        public static bool IsEnabled
        {
            get
            {
                var enabled = ReadEnabledPreference();
                _isEnabled = enabled;
                return enabled;
            }
        }

        public static void DebugWriteLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (!IsEnabled)
                return;

            // Route through AppLog so the Settings log view matches what is emitted.
            Add(message);

            try
            {
                Debug.WriteLine(message);
            }
            catch
            {
            }

            try
            {
                Console.WriteLine(message);
            }
            catch
            {
            }
        }

        public static void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;

            try
            {
                Preferences.Set(LogEnabledPreferenceKey, enabled);
            }
            catch
            {
                // Ignore; marker file still persists the preference.
            }

            TryWriteEnabledMarker(enabled);

            if (!enabled)
            {
                Clear();
            }
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

                var dir = GetLogsDirectory();
                if (!Directory.Exists(dir))
                    return;

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

        private static void StartFileWriterIfNeeded()
        {
            if (Interlocked.Exchange(ref _fileWriterStarted, 1) != 0)
                return;

            _ = Task.Run(FileWriterLoopAsync);
        }

        private static async Task FileWriterLoopAsync()
        {
            StreamWriter? writer = null;
            string? currentPath = null;

            try
            {
                while (await FileChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    var batch = new List<string>(128);

                    while (FileChannel.Reader.TryRead(out var line))
                    {
                        batch.Add(line);

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

                        for (var i = 0; i < batch.Count; i++)
                        {
                            writer!.WriteLine(batch[i]);
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
            return Path.Combine(FileSystem.AppDataDirectory, "Logs");
        }

        private static string GetLogFilePath()
        {
            // Daily rolling file based on LOCAL date so the file name matches the timestamps users see.
            var day = DateTime.Now.ToString("yyyy-MM-dd");
            return Path.Combine(GetLogsDirectory(), $"app_log_{day}.txt");
        }

        public static void Add(Func<string> messageFactory)
        {
            if (!IsEnabled || messageFactory == null)
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
            if (!IsEnabled || string.IsNullOrWhiteSpace(message))
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
            FileChannel.Writer.TryWrite(line);
        }

        // Returns a snapshot of up to maxLines log entries as an array.
        // The result is ordered from newest to oldest.
        public static string[] GetSnapshot(int maxLines)
        {
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
