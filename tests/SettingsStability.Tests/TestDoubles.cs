using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

public abstract class BindableObject
{
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) { }
}

namespace JoesScanner.Models
{
    internal sealed record CallItem(string ReceiverName, string SystemName, string Talkgroup)
    {
        internal Guid? ServerId { get; init; }
    }
}

namespace JoesScanner.Services
{
    internal sealed class HistoryLookupData { }

    internal static class AppPaths
    {
        public static string DatabaseDirectory { get; set; } = string.Empty;
        public static string GetDbPath(string fileName) =>
            Path.Combine(DatabaseDirectory, fileName);
    }

    internal static class AppLog
    {
        public static void Basic(Func<string> message) { }
        public static void Basic(string message) { }
        public static void Add(Func<string> message) { }
    }

    internal static class AppStateStore
    {
        private static readonly ConcurrentDictionary<string, string> Values = new();
        public static Action<string>? BeforeWrite { get; set; }

        public static string GetString(string key, string fallback) => Values.GetValueOrDefault(key, fallback);

        public static void SetString(string key, string value)
        {
            BeforeWrite?.Invoke(key);
            Values[key] = value;
        }
    }
}
