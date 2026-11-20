#if ANDROID
using Android.Content;
using AndroidX.Security.Crypto;
#elif IOS || MACCATALYST
using Foundation;
using Security;
#else
using Microsoft.Maui.Storage;
#endif

namespace JoesScanner.Services;

public sealed partial class SettingsService
{
    public SettingsService(IDatabasePathProvider dbPathProvider) : this(dbPathProvider, new PlatformCredentialStorage()) { }
}

internal sealed class PlatformCredentialStorage : IProtectedCredentialStorage
{
#if ANDROID
    // Commit synchronously so migration can verify durable persistence before deleting legacy rows.
    private const string StoreName = "joesscanner.protected-credentials";

    private static ISharedPreferences Open()
    {
        var context = global::Android.App.Application.Context;
#pragma warning disable CS0618 // Same encrypted-preferences provider used by MAUI Essentials.
        using var builder = new MasterKey.Builder(context, StoreName);
        builder.SetKeyScheme(MasterKey.KeyScheme.Aes256Gcm);
        using var key = builder.Build() ?? throw new CredentialStorageException();
        return EncryptedSharedPreferences.Create(context, StoreName, key,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.Aes256Siv,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.Aes256Gcm)
            ?? throw new CredentialStorageException();
#pragma warning restore CS0618
    }

    public string? Read(string key)
    {
        using var preferences = Open();
        return preferences.GetString(key, null!);
    }

    public void Write(string key, string value)
    {
        using var preferences = Open();
        using var editor = preferences.Edit();
        if (editor?.PutString(key, value)?.Commit() != true)
            throw new CredentialStorageException();
    }

    public void Remove(string key)
    {
        using var preferences = Open();
        using var editor = preferences.Edit();
        if (editor?.Remove(key)?.Commit() != true)
            throw new CredentialStorageException();
    }
#elif IOS || MACCATALYST
    private static SecRecord Record(string key) => new(SecKind.GenericPassword)
    {
        Service = "app.joesscanner.protected-credentials",
        Account = key
    };

    public string? Read(string key)
    {
        using var query = Record(key);
        using var result = SecKeyChain.QueryAsRecord(query, out var status);
        return status switch
        {
            SecStatusCode.Success when result?.ValueData is { } value =>
                NSString.FromData(value, NSStringEncoding.UTF8) ?? throw new CredentialStorageException(),
            SecStatusCode.ItemNotFound => null,
            _ => throw new CredentialStorageException()
        };
    }

    public void Write(string key, string value)
    {
        using var query = Record(key);
        using var data = NSData.FromString(value, NSStringEncoding.UTF8) ?? throw new CredentialStorageException();
        using var update = new SecRecord(SecKind.GenericPassword) { ValueData = data };
        var status = SecKeyChain.Update(query, update);
        if (status == SecStatusCode.ItemNotFound)
        {
            query.ValueData = data;
            query.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
            status = SecKeyChain.Add(query);
        }
        if (status != SecStatusCode.Success)
            throw new CredentialStorageException();
    }

    public void Remove(string key)
    {
        using var query = Record(key);
        var status = SecKeyChain.Remove(query);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            throw new CredentialStorageException();
    }
#else
    // The existing settings interface is synchronous. Start the Windows asynchronous
    // protection operation away from the UI context so its continuation cannot block on it.
    public string? Read(string key) => Task.Run(() => SecureStorage.Default.GetAsync(key)).GetAwaiter().GetResult();
    public void Write(string key, string value) =>
        Task.Run(() => SecureStorage.Default.SetAsync(key, value)).GetAwaiter().GetResult();
    public void Remove(string key) => SecureStorage.Default.Remove(key);
#endif
}
