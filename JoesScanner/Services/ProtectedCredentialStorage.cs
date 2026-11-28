using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JoesScanner.Services;

internal interface IProtectedCredentialStorage
{
    string? Read(string key);
    void Write(string key, string value);
    void Remove(string key);
}

internal sealed class CredentialStorageException() : InvalidOperationException(
    "Protected credential storage is unavailable. Unlock this device and try again. If credentials were lost after a restore, enter them again in Settings.");

internal sealed class ProtectedCredentials(IProtectedCredentialStorage storage)
{
    internal static string Key(string installation, string scope) =>
        "joesscanner.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation + "\n" + scope)));

    internal readonly struct Pair(string username, string password)
    {
        internal string Username { get; } = username;
        internal string Password { get; } = password;
    }

    internal static string Encode(Pair credentials) => JsonSerializer.Serialize(new[] { credentials.Username, credentials.Password });

    internal static Pair? Decode(string? value)
    {
        if (value == null)
            return null;
        try
        {
            if (JsonSerializer.Deserialize<string[]>(value) is { Length: 2 } parts && parts.All(part => part != null))
                return new Pair(parts[0], parts[1]);
        }
        catch (JsonException) { }
        throw new CredentialStorageException();
    }

    internal string? Read(string key)
    {
        try { return storage.Read(key); }
        catch (Exception) { throw new CredentialStorageException(); }
    }

    internal void Save(string key, string value)
    {
        try
        {
            storage.Write(key, value);
            if (storage.Read(key) == value)
                return;
        }
        catch (Exception)
        {
            // Platform error messages may contain credential values.
        }
        throw new CredentialStorageException();
    }

    internal void Remove(string key)
    {
        try
        {
            storage.Remove(key);
            if (storage.Read(key) == null)
                return;
        }
        catch (Exception) { }
        throw new CredentialStorageException();
    }

    // The SQLite writer transaction serializes first use, including across processes.
    // Losing protected storage only loses proof of rotation. The recorder can still
    // provide audio through ordinary lookup, and no substitute key is written to disk.
    internal Func<byte[]?> SignedReferenceKey()
    {
        const string name = "joesscanner.v1.signed-audio-reference";
        long retryAfter = 0;
        byte[]? cached = null;
        return () =>
        {
            if (cached != null)
                return cached;
            if (Environment.TickCount64 < retryAfter)
                return null;
            try
            {
                var stored = Read(name);
                if (stored == null)
                {
                    stored = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    Save(name, stored);
                }
                var bytes = Convert.FromBase64String(stored);
                if (bytes.Length == 32)
                    cached = bytes;
            }
            catch (Exception ex) when (ex is CredentialStorageException or FormatException) { }
            if (cached == null)
                retryAfter = Environment.TickCount64 + 30_000;
            return cached;
        };
    }
}
