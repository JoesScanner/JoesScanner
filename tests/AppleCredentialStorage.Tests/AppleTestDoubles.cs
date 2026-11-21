namespace Foundation
{
    internal enum NSStringEncoding { UTF8 }

    internal sealed class NSData(string text) : IDisposable
    {
        internal string Text { get; } = text;
        public void Dispose() { }
        internal static NSData FromString(string value, NSStringEncoding encoding) => new(value);
    }

    internal static class NSString
    {
        internal static string FromData(NSData data, NSStringEncoding encoding) => data.Text;
    }
}

namespace Security
{
    using Foundation;

    internal enum SecKind { GenericPassword }
    internal enum SecAccessible { AfterFirstUnlockThisDeviceOnly }
    internal enum SecStatusCode { Success = 0, Param = -50, ItemNotFound = -25300, InteractionNotAllowed = -25308, MissingEntitlement = -34018 }

    internal sealed class SecRecord() : IDisposable
    {
        internal SecRecord(SecKind kind) : this() => Kind = kind;
        internal SecKind? Kind { get; }
        internal string? Service { get; set; }
        internal string? Account { get; set; }
        internal SecAccessible? Accessible { get; set; }
        internal NSData? ValueData { get; set; }
        public void Dispose() { }
    }

    internal static class SecKeyChain
    {
        internal static Func<SecRecord, (SecRecord?, SecStatusCode)>? OnQuery { get; set; }
        internal static Func<SecRecord, SecRecord, SecStatusCode>? OnUpdate { get; set; }
        internal static Func<SecRecord, SecStatusCode>? OnAdd { get; set; }
        internal static Func<SecRecord, SecStatusCode>? OnRemove { get; set; }

        internal static SecRecord? QueryAsRecord(SecRecord query, out SecStatusCode status)
        {
            var (record, result) = OnQuery!(query);
            status = result;
            return record;
        }

        internal static SecStatusCode Update(SecRecord query, SecRecord attributes) => OnUpdate!(query, attributes);
        internal static SecStatusCode Add(SecRecord item) => OnAdd!(item);
        internal static SecStatusCode Remove(SecRecord query) => OnRemove!(query);

        internal static void Reset()
        {
            OnQuery = null;
            OnUpdate = null;
            OnAdd = null;
            OnRemove = null;
        }
    }
}

namespace JoesScanner.Services
{
    public interface IDatabasePathProvider { }

    public sealed partial class SettingsService
    {
        internal SettingsService(IDatabasePathProvider dbPathProvider, IProtectedCredentialStorage storage) { }
    }

    internal static class AppLog
    {
        internal static List<string> Entries { get; } = [];
        internal static void Add(string message) => Entries.Add(message);
    }
}
