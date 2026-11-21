using Foundation;
using JoesScanner.Services;
using Security;

const string key = "test-account-key";
const string value = "test-credential-value";
var storage = new ProtectedCredentials(new PlatformCredentialStorage());

Run("Update writes only data and verifies the existing item in place", () =>
{
    var saved = "previous-value";
    SecKeyChain.OnUpdate = (query, attributes) =>
    {
        CheckQuery(query);
        Check(attributes.Kind == null && attributes.Service == null && attributes.Account == null &&
              attributes.Accessible == null && attributes.ValueData?.Text == value,
              "The update dictionary must contain only the new data.");
        saved = attributes.ValueData!.Text;
        return SecStatusCode.Success;
    };
    SecKeyChain.OnQuery = query =>
    {
        CheckQuery(query);
        return (new SecRecord { ValueData = new NSData(saved) }, SecStatusCode.Success);
    };

    storage.Save(key, value);
    Check(saved == value && AppLog.Entries.Count == 0, "An in-place update must succeed without a failure log.");
});

Run("A missing item is added with the existing identifier and device-only protection", () =>
{
    string? saved = null;
    SecKeyChain.OnQuery = query =>
    {
        CheckQuery(query);
        return saved == null
            ? (null, SecStatusCode.ItemNotFound)
            : (new SecRecord { ValueData = new NSData(saved) }, SecStatusCode.Success);
    };
    SecKeyChain.OnUpdate = (query, _) =>
    {
        CheckQuery(query);
        return SecStatusCode.ItemNotFound;
    };
    SecKeyChain.OnAdd = item =>
    {
        CheckIdentity(item);
        Check(item.Accessible == SecAccessible.AfterFirstUnlockThisDeviceOnly, "New items must stay device-only.");
        saved = item.ValueData?.Text;
        return SecStatusCode.Success;
    };

    Check(storage.Read(key) == null, "A missing item is a normal read result.");
    storage.Save(key, value);
    Check(saved == value && AppLog.Entries.Count == 0, "A successful insert must not log a failure.");
});

Run("A Keychain query error logs the operation and status before the public warning", () =>
{
    SecKeyChain.OnQuery = _ => (null, SecStatusCode.InteractionNotAllowed);
    CheckFailure(() => storage.Read(key), "Keychain QueryAsRecord failed (status InteractionNotAllowed [-25308]).");
});

Run("A successful query without data is diagnosed separately", () =>
{
    SecKeyChain.OnQuery = _ => (new SecRecord(), SecStatusCode.Success);
    CheckFailure(() => storage.Read(key), "Keychain QueryAsRecord failed (status Success [0]; no usable UTF-8 data).");
});

Run("An update error is reported without adding or deleting an item", () =>
{
    SecKeyChain.OnUpdate = (_, _) => SecStatusCode.Param;
    CheckFailure(() => storage.Save(key, value), "Keychain Update failed (status Param [-50]).");
});

Run("An insert error reports Add after Update finds no item", () =>
{
    SecKeyChain.OnUpdate = (_, _) => SecStatusCode.ItemNotFound;
    SecKeyChain.OnAdd = _ => SecStatusCode.MissingEntitlement;
    CheckFailure(() => storage.Save(key, value), "Keychain Add failed (status MissingEntitlement [-34018]).");
});

Run("A delete error reports Remove", () =>
{
    SecKeyChain.OnRemove = _ => SecStatusCode.InteractionNotAllowed;
    CheckFailure(() => storage.Remove(key), "Keychain Remove failed (status InteractionNotAllowed [-25308]).");
});

static void Run(string name, Action test)
{
    SecKeyChain.Reset();
    AppLog.Entries.Clear();
    test();
    Console.WriteLine("PASS " + name);
}

void CheckIdentity(SecRecord record) =>
    Check(record.Kind == SecKind.GenericPassword && record.Service == "app.joesscanner.protected-credentials" &&
          record.Account == key, "The existing Keychain item identity must be preserved.");

void CheckQuery(SecRecord record)
{
    CheckIdentity(record);
    Check(record.Accessible == null && record.ValueData == null, "The query should only match the item identity.");
}

static void CheckFailure(Action action, string expectedLog)
{
    try
    {
        action();
        throw new InvalidOperationException("Expected the protected credential warning.");
    }
    catch (CredentialStorageException ex)
    {
        Check(ex.Message == new CredentialStorageException().Message && ex.InnerException == null,
              "The public exception must stay generic.");
        Check(AppLog.Entries.SequenceEqual([expectedLog]), "Expected one diagnostic containing only the operation and status.");
    }
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
