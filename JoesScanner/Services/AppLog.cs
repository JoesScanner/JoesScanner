using System;
using System.Collections.Generic;
using System.IO;
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

        private static readonly object Sync = new();
        private static readonly LinkedList<string> Buffer = new();

        private const int MaxEntries = 500;

        private static readonly Channel<string> FileChannel = Channel.CreateUnbounded<string>();
        private static int _fileWriterStarted;

        private static volatile bool _isEnabled;

        static AppLog()
        {
            // Default ON so testers get logs unless they explicitly disable them.
            _isEnabled = Preferences.Get(LogEnabledPreferenceKey, true);
        }

        public static bool IsEnabled => _isEnabled;

        public static void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            Preferences.Set(LogEnabledPreferenceKey, enabled);

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

        private static void StartFileWriterIfNeeded()
        {
            if (Interlocked.Exchange(ref _fileWriterStarted, 1) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (await FileChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
                    {
                        while (FileChannel.Reader.TryRead(out var line))
                        {
                            try
                            {
                                var path = GetLogFilePath();
                                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                                await File.AppendAllTextAsync(path, line + Environment.NewLine).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Never let logging break the app.
                            }
                        }
                    }
                }
                catch
                {
                    // Swallow.
                }
            });
        }

        private static string GetLogFilePath()
        {
            // Daily rolling file.
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            return Path.Combine(FileSystem.AppDataDirectory, "Logs", $"app_log_{day}.txt");
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
    }
}
