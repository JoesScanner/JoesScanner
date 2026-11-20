using System.Net.Http.Headers;
using System.Text;
using JoesScanner.Helpers;
using JoesScanner.Services;
using JoesScanner.Models;
using Microsoft.Data.Sqlite;

internal static class SavedCredentialTests
{
    private const string First = "https://first.example/radio";
    private const string Second = "https://second.example";
    private const string Password = "  p'a:ss/+@%#?🔒  ";

    internal static Task LegacyAndRestart()
    {
        using var fixture = new Fixture();
        const string embeddedUser = "url-user";
        var withCredentials = $"https://{embeddedUser}:{Uri.EscapeDataString(Password)}@first.example/radio?access_token=fake#fragment";
        fixture.Seed("server_url", withCredentials);
        fixture.Seed("server_cred_user::" + Second, "second-user");
        fixture.Seed("server_cred_pass::" + Second, "second-password");
        fixture.Seed("what3words_api_key", "coordinate-secret");
        fixture.Seed("theme_mode", "Dark");
        fixture.Execute("UPDATE servers SET base_url = $value WHERE server_key = 'custom';", ("$value", withCredentials));
        fixture.Execute("""
            INSERT INTO servers(server_key, display_name, base_url, info_url, source, directory_id, created_utc, updated_utc)
            VALUES ('dir::https://directory.example', 'Directory', 'https://directory.example', $value, 'directory', 12, '', '');
            """, ("$value", "https://info.example/map?view=local&token=info-secret"));
        var migrated = fixture.Load();

        Check(migrated.ServerUrl == First && migrated.ThemeMode == "Dark" &&
            migrated.GetCachedDirectoryServers().Single().InfoUrl == "https://info.example/map?view=local",
            "Migration must retain the recorder path, non-secret directory link and ordinary setting.");
        Check(migrated.What3WordsApiKey == "coordinate-secret", "The geocoding key must migrate.");
        Check(!fixture.HasPlaintextCredentialRows() && !fixture.DatabaseUrlsContain("access_token") &&
            !fixture.DatabaseUrlsContain("info-secret"), "Credentials must leave all persisted connection URLs.");
        var restarted = fixture.Load();
        AssertPair(restarted, First, embeddedUser, Password);
        AssertPair(restarted, Second, "second-user", "second-password");
        Check(restarted.What3WordsApiKey == "coordinate-secret", "The secure key must reload after restart.");
        return Task.CompletedTask;
    }

    internal static Task VaultFailures()
    {
        using var fixture = new Fixture();
        fixture.Seed("server_url", First);
        fixture.SeedScoped(First, "legacy-user", Password);
        fixture.Vault.DiscardWrites = true;
        var failed = fixture.Load();
        Check(failed.ServerUrl == First, "Ordinary settings must remain available.");
        ExpectStorageFailure(() => failed.TryGetServerCredentials(First, out _, out _));
        Check(failed.SavedCredentialWarning != null && fixture.HasPlaintextCredentialRows(),
            "An unwritten or unreadable protected value must leave legacy data available for migration retry.");
        fixture.Vault.DiscardWrites = false;
        var restored = fixture.Load();
        AssertPair(restored, First, "legacy-user", Password);
        fixture.Vault.FailRead = true;
        var locked = fixture.Load();
        ExpectStorageFailure(() => locked.TryGetServerCredentials(First, out _, out _));
        Check(!fixture.HasPlaintextCredentialRows(), "A read error must never write a fallback into SQLite.");
        fixture.Vault.FailRead = false;
        AssertPair(fixture.Load(), First, "legacy-user", Password);
        return Task.CompletedTask;
    }

    internal static Task InterruptedCleanup()
    {
        using var fixture = new Fixture();
        fixture.Seed("server_url", First);
        fixture.SeedScoped(First, "legacy-user", Password);
        fixture.Execute("""
            CREATE TRIGGER interrupt_migration BEFORE DELETE ON app_settings
            WHEN old.setting_key = 'server_cred_pass::https://first.example'
            BEGIN SELECT RAISE(ABORT, 'test interruption'); END;
            """);
        AssertPair(fixture.Load(), First, "legacy-user", Password);
        Check(fixture.CountPlaintextCredentialRows() == 2, "SQLite cleanup must roll back as a single transaction.");
        fixture.Vault.Values[fixture.Vault.Values.Keys.Single()] =
            ProtectedCredentials.Encode(new("new-user", "new-password"));
        fixture.Execute("DROP TRIGGER interrupt_migration;");
        AssertPair(fixture.Load(), First, "new-user", "new-password");
        Check(!fixture.HasPlaintextCredentialRows(), "Restart must finish cleanup without overwriting protected changes.");
        return Task.CompletedTask;
    }

    internal static Task UnscopedInterruptedSwitch()
    {
        foreach (var includeExplicitFirst in new[] { false, true })
        {
            using var fixture = new Fixture();
            if (includeExplicitFirst)
                fixture.SeedScoped(First, "first-user", Password);
            fixture.Seed("basic_auth_user", "first-user");
            fixture.Seed("basic_auth_pass", Password);
            // The old client had written the new selection when it stopped.
            fixture.Seed("server_url", Second + "/recorder");

            var migrated = fixture.Load();
            Check(migrated.ServerUrl == Second + "/recorder", "Migration must preserve the selected recorder path.");
            Check(!migrated.TryGetServerCredentials(migrated.ServerUrl, out _, out _) &&
                migrated.SavedCredentialWarning?.Contains("Enter the username and password for this server again") == true,
                "An unscoped pair must not authenticate the new selection.");
            using var unused = new HttpClient();
            var authentication = new ServerAuthentication(migrated, unused);
            Check(authentication.GetCustomAuthorization(new Uri(migrated.ServerUrl)) == null,
                "Recorder requests must never receive the unscoped pair.");
            Check(!fixture.HasPlaintextCredentialRows() &&
                fixture.Vault.Values.Values.Contains(ProtectedCredentials.Encode(new("first-user", Password))),
                "The ambiguous pair must be preserved in protected storage before removing the plaintext.");

            var restarted = fixture.Load();
            Check(!restarted.TryGetServerCredentials(Second, out _, out _) && restarted.SavedCredentialWarning != null,
                "The re-entry notice and the lack of a login must survive a restart.");
            if (includeExplicitFirst)
                AssertPair(restarted, First, "first-user", Password);
            else
                Check(!restarted.TryGetServerCredentials(First, out _, out _), "A global-only pair has no recoverable recorder scope.");

            restarted.SetServerCredentials(Second + "/recorder", "second-user", "replacement");
            AssertPair(fixture.Load(), Second, "second-user", "replacement");
            Check(restarted.SavedCredentialWarning == null && !fixture.HasPlaintextCredentialRows(),
                "Explicit re-entry should enable the correct connection without restoring legacy plaintext.");
        }
        return Task.CompletedTask;
    }

    internal static Task UnscopedPreservationFailure()
    {
        using var fixture = new Fixture();
        fixture.Seed("server_url", Second);
        fixture.SeedScoped(First, "first-user", Password);
        fixture.Seed("basic_auth_user", "ambiguous");
        fixture.Seed("basic_auth_pass", "ambiguous-secret");
        var unscoped = ProtectedCredentials.Encode(new("ambiguous", "ambiguous-secret"));
        fixture.Vault.DiscardValue = unscoped;
        var failed = fixture.Load();
        AssertPair(failed, First, "first-user", Password);
        Check(fixture.CountPlaintextCredentialRows() == 2, "A failed protected write must retain both unscoped legacy rows.");
        Check(!failed.TryGetServerCredentials(Second, out _, out _) && failed.SavedCredentialWarning != null,
            "The retained legacy pair must stay inactive and keep the re-entry notice.");
        failed.ServerUrl = "https://anonymous.example/recorder";
        Check(!failed.TryGetServerCredentials(failed.ServerUrl, out _, out _) && failed.SavedCredentialWarning == null,
            "A failed quarantine write must not block an unrelated anonymous recorder.");
        failed.SetServerCredentials("https://other.example", "saved-user", "saved-secret");
        failed.SetServerCredentials(Second, "reentered-user", Password);

        fixture.Vault.DiscardValue = null;
        var restarted = fixture.Load();
        AssertPair(restarted, Second, "reentered-user", Password);
        AssertPair(restarted, "https://other.example", "saved-user", "saved-secret");
        Check(!fixture.HasPlaintextCredentialRows() && fixture.Vault.Values.Values.Contains(unscoped),
            "Restart should retry quarantine while preserving any credentials the user entered.");
        return Task.CompletedTask;
    }

    internal static Task UnscopedCleanupRollback()
    {
        using var fixture = new Fixture();
        fixture.Seed("server_url", Second);
        fixture.Seed("basic_auth_user", "ambiguous");
        fixture.Seed("basic_auth_pass", Password);
        fixture.SeedScoped(HostedServerRules.DefaultServerUrl, "wordpress-user", "wordpress-secret");
        fixture.Execute("""
            CREATE TRIGGER interrupt_unscoped_cleanup BEFORE DELETE ON app_settings
            WHEN old.setting_key = 'basic_auth_pass'
            BEGIN SELECT RAISE(ABORT, 'test interruption'); END;
            """);
        var interrupted = fixture.Load();
        AssertPair(interrupted, HostedServerRules.DefaultServerUrl, "wordpress-user", "wordpress-secret");
        Check(fixture.CountPlaintextCredentialRows() == 2 && !interrupted.TryGetServerCredentials(Second, out _, out _),
            "A failed unscoped cleanup must retain both rows and leave the pair inert.");
        var vaultCount = fixture.Vault.Values.Count;
        fixture.Execute("DROP TRIGGER interrupt_unscoped_cleanup;");
        var restarted = fixture.Load();
        AssertPair(restarted, HostedServerRules.DefaultServerUrl, "wordpress-user", "wordpress-secret");
        Check(!fixture.HasPlaintextCredentialRows() && fixture.Vault.Values.Count == vaultCount &&
            restarted.SavedCredentialWarning != null, "Reopening should verify and finish the cleanup without creating duplicates.");
        return Task.CompletedTask;
    }

    internal static Task ScopesAndRemoval()
    {
        using var fixture = new Fixture();
        var settings = fixture.Load();
        settings.ServerUrl = First;
        settings.SetServerCredentials(First, "first-user", Password);
        settings.SetServerCredentials(Second, "second-user", "second-password");
        settings.SetServerCredentials(HostedServerRules.DefaultServerUrl, "account-a", "secret-a");
        settings.AuthSessionToken = "session-a";
        var retired = settings.ReadAccountSession();
        settings.SetServerCredentials(HostedServerRules.DefaultServerUrl, "account-b", "secret-b");
        Check(settings.AuthSessionToken.Length == 0 && !settings.TryReplaceAccountSession(retired.Version, "late-session-a"),
            "Switching accounts must retire the previous in-memory session and reject a late reply.");
        AssertPair(settings, HostedServerRules.DefaultServerUrl, "account-b", "secret-b");
        settings.AuthServerBaseUrl = "https://other-wordpress.example";
        Check(!settings.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out _, out _),
            "A separate WordPress authority must not receive the saved account.");
        settings.AuthServerBaseUrl = "";
        settings.ServerUrl = Second;
        using var unused = new HttpClient();
        var authentication = new ServerAuthentication(settings, unused);
        Check(authentication.GetCustomAuthorization(new Uri(First)) == null &&
            authentication.GetCustomAuthorization(new Uri("https://second.example:9443")) == null,
            "The selected Basic pair must never be emitted for another origin.");
        Check(authentication.GetCustomAuthorization(new Uri(Second))?.Equals(
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("second-user:second-password")))) == true,
            "The saved pair must reach normal request authentication.");
        settings.UpsertCachedDirectoryServers([new ServerDirectoryEntry { Url = "https://protected.example", DirectoryId = 50,
            Name = "Protected", UsesApiFirewallCredentials = true }]);
        settings.ServerUrl = "https://protected.example";
        settings.SetServerCredentials(settings.ServerUrl, "never-send", "to-firewall");
        Check(authentication.RequiresFirewall(new Uri(settings.ServerUrl)) &&
            authentication.GetCustomAuthorization(new Uri(settings.ServerUrl)) == null,
            "Protected directory settings must survive SQLite and reject custom authorization.");
        settings.ClearServerCredentials(First);
        settings.ClearServerCredentials(HostedServerRules.DefaultServerUrl);
        var reopened = fixture.Load();
        Check(!reopened.TryGetServerCredentials(First, out _, out _) &&
            !reopened.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out _, out _),
            "Explicit removal must survive restart.");
        AssertPair(reopened, Second, "second-user", "second-password");
        Check(!fixture.HasPlaintextCredentialRows(), "New credential writes must bypass SQLite.");
        return Task.CompletedTask;
    }

    internal static Task Redaction()
    {
        var message = DiagnosticRedaction.SanitizeAuthenticationMessage(
            "HTTP https://log-user:url-secret@redacted.example/path?token=query-secret; " +
            "Authorization: Bearer bearer-secret; what3words_api_key=\"coords-secret\"; account response echoed raw-secret",
            "raw-secret");
        Check(message.Contains("redacted.example") &&
            !new[] { "log-user", "url-secret", "query-secret", "bearer-secret", "coords-secret", "raw-secret" }.Any(message.Contains),
            "Diagnostic export must redact URI credentials, bearer tokens, API keys and echoed secrets.");
        return Task.CompletedTask;
    }

    private static void AssertPair(SettingsService settings, string url, string username, string password) =>
        Check(settings.TryGetServerCredentials(url, out var actualUser, out var actualPass) &&
            actualUser == username && actualPass == password, "Expected the original verified credential pair.");

    private static void ExpectStorageFailure(Action action)
    {
        try { action(); }
        catch (CredentialStorageException error)
        {
            Check(error.InnerException == null && !error.Message.Contains(Password),
                "A storage failure must not expose platform error details.");
            return;
        }
        throw new InvalidOperationException("Expected explicit protected storage failure.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal sealed class VaultStub : IProtectedCredentialStorage
    {
        internal Dictionary<string, string> Values { get; } = new();
        internal bool DiscardWrites;
        internal string? DiscardValue;
        internal bool FailRead;
        public string? Read(string key) => FailRead ? throw new InvalidOperationException(Password) : Values.GetValueOrDefault(key);
        public void Write(string key, string value)
        {
            if (!DiscardWrites && value != DiscardValue)
                Values[key] = value;
        }
        public void Remove(string key) => Values.Remove(key);
    }

    internal sealed class Fixture : IDisposable, IDatabasePathProvider
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("JoesScanner.Credentials-");
        public string DbPath => Path.Combine(_directory.FullName, "settings.sqlite");
        internal VaultStub Vault { get; } = new();

        internal Fixture() => _ = Load().ServerUrl;
        internal SettingsService Load() => new(this, Vault);

        internal void Seed(string key, string value) => Execute("""
            INSERT INTO app_settings(setting_key, setting_value, updated_utc) VALUES ($key, $value, '')
            ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;
            """, ("$key", key), ("$value", value));

        internal void SeedScoped(string server, string user, string password)
        {
            var scope = ServerKeyHelper.Normalize(server);
            Seed("server_cred_user::" + scope, user);
            Seed("server_cred_pass::" + scope, password);
        }
        internal bool HasPlaintextCredentialRows() => CountPlaintextCredentialRows() != 0;
        internal long CountPlaintextCredentialRows() => (long)Execute("""
            SELECT count(*) FROM app_settings WHERE setting_key IN ('basic_auth_user', 'basic_auth_pass', 'what3words_api_key')
            OR setting_key LIKE 'server_cred_%';
            """)!;

        internal bool DatabaseUrlsContain(string secret) => (long)Execute("""
            SELECT (SELECT count(*) FROM servers WHERE instr(base_url, $value) OR instr(info_url, $value) OR instr(server_key, $value))
            + (SELECT count(*) FROM app_settings WHERE instr(setting_value, $value));
            """, ("$value", secret))! != 0;

        internal object? Execute(string sql, params (string, string)[] parameters)
        {
            using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            return command.ExecuteScalar();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _directory.Delete(recursive: true);
        }
    }
}

namespace JoesScanner.Services
{
    internal static class MobileAudioPreferencesStore
    {
        internal const string LegacyMixKey = "mobile_mix_audio_with_other_apps";
        internal static MobileAudioPreferences Read(bool isIos) => throw new NotSupportedException();
        internal static void Write(MobileAudioPreferences preferences) => throw new NotSupportedException();
    }
}
