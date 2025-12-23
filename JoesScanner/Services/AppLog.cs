namespace JoesScanner.Services
{
    // Simple in-memory, thread-safe application log with a fixed maximum number of entries.
    public static class AppLog
    {
        // Synchronization lock for guarding access to the buffer.
        private static readonly object Sync = new();

        // In-memory log buffer that stores formatted log lines.
        private static readonly LinkedList<string> Buffer = new();

        // Maximum number of log entries to keep in memory.
        private const int MaxEntries = 500;

        // Adds a new log entry with a timestamp to the in-memory buffer.
        // Empty or whitespace-only messages are ignored.
        public static void Add(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var line = $"{DateTime.Now:HH:mm:ss}  {message}";

            lock (Sync)
            {
                Buffer.AddLast(line);

                if (Buffer.Count > MaxEntries)
                {
                    Buffer.RemoveFirst();
                }
            }
        }

        // Returns a snapshot of up to maxLines log entries as an array.
        // The result is ordered from newest to oldest.
        public static string[] GetSnapshot(int maxLines)
        {
            lock (Sync)
            {
                return Buffer
                    .Reverse()              // newest first
                    .Take(maxLines)
                    .ToArray();
            }
        }
    }
}
