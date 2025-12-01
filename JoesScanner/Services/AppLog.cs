using System;
using System.Collections.Generic;
using System.Linq;

namespace JoesScanner.Services
{
    public static class AppLog
    {
        private static readonly object Sync = new();
        private static readonly LinkedList<string> Buffer = new();
        private const int MaxEntries = 500;

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

        public static string[] GetSnapshot(int maxLines)
        {
            lock (Sync)
            {
                return Buffer
                    .Reverse()              // newest first
                    .Take(maxLines)
                    .Reverse()              // back to oldest first
                    .ToArray();
            }
        }
    }
}
