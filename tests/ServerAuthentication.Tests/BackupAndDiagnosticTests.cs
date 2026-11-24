using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class BackupAndDiagnosticTests
{
    internal static async Task SanitizedZip()
    {
        var folder = Directory.CreateTempSubdirectory("JoesScanner.Diagnostics-");
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(folder.FullName, "Logs")).FullName;
            var cache = Path.Combine(folder.FullName, "Cache");
            var historical = Path.Combine(logs, "app_log_2026-11-19.txt");
            const string source = """
                08:01:02  Validate: url=legacy-user:legacy password@recorder.test/scanner, isDefault=False, hasBasicAuth=True
                08:01:03  Validate: response (with auth) HTTP 403 echoed-reason-secret
                08:01:04  Request failed HTTP 502 for https://net-user:net-pass@api.test/path-secret/audio?session_token=net-token
                08:01:05  endpoint=username:p%3Ass%40word@scanner.local/recorder/file?signature=uri-secret
                08:01:06  cache=localrecorder:8010/media/file?sig=local-signature relative=/audio?token=relative-token
                08:01:07  Authorization: Bearer short+=
                08:01:08  headers {"x-api-key":"old-api-key","password":"quoted-secret","id_token":"jwt-secret"}
                08:01:09  AuthObserved: report POST failed. status=401 body='{"message":"unlabelled-echo"}
                09:45:00  forged timestamp in raw body echo-two
                continuation echo-three'
                08:01:10  History: reconnect queued, attempt=2
                """;
            await File.WriteAllTextAsync(historical, source);
            foreach (var filename in new[] { "joesscanner.db", "joesscanner.db-wal", "securestorage.dat",
                "joesscanner.protected-credentials.xml", "app_log_protected-credentials.txt" })
                await File.WriteAllTextAsync(Path.Combine(logs, filename), "excluded-artifact-secret");

            var export = await DiagnosticZip.CreateAsync(cache, logs,
                ["""08:02:00  HttpRequestException: "https:\/\/api.test\/escaped-path?session_token=buffer-token" (HTTP 503)""",
                 "08:02:01  password=p'a,ss;+@ complicated-secret"], 3);
            using var zip = ZipFile.OpenRead(export);
            Check(zip.Entries.Select(entry => entry.FullName).SequenceEqual(["snapshot.txt", "files/app_log_2026-11-19.txt"]),
                "Only the sanitized snapshot and matching dated log may be archived.");
            var content = new StringBuilder();
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                content.AppendLine(await reader.ReadToEndAsync());
            }
            var sanitized = content.ToString();
            Check(!new[] { "legacy-user", "legacy password", "echoed-reason-secret", "net-user", "net-pass", "path-secret",
                "net-token", "p%3Ass%40word", "uri-secret", "local-signature", "relative-token", "short+=", "old-api-key",
                "quoted-secret", "jwt-secret", "unlabelled-echo", "echo-two", "echo-three", "excluded-artifact-secret",
                "buffer-token", "escaped-path", "complicated-secret" }.Any(secret => sanitized.Contains(secret, StringComparison.Ordinal)),
                "Archive entries must be redacted even when secrets are unlabeled or spanned multiple log lines.");
            Check(sanitized.Contains("08:01:03  Validate: response (with auth) HTTP 403") &&
                sanitized.Contains("Request failed HTTP 502") && sanitized.Contains("status=401") &&
                sanitized.Contains("scanner.local") && sanitized.Contains("reconnect queued, attempt=2"),
                "Redaction must retain event times, origins, status codes and useful error information.");
            Check(await File.ReadAllTextAsync(historical) == source, "Export must never rewrite the original log.");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    internal static Task AndroidBackupRules()
    {
        XNamespace android = "http://schemas.android.com/apk/res/android";
        var application = Resource("AndroidManifest.xml").Root!.Element("application")!;
        Check((string?)application.Attribute(android + "allowBackup") == "true" &&
            (string?)application.Attribute(android + "fullBackupContent") == "@xml/credential_backup_rules" &&
            (string?)application.Attribute(android + "dataExtractionRules") == "@xml/credential_extraction_rules",
            "Android backup must stay enabled with both exclusion policies wired up.");

        var modern = Resource("credential_extraction_rules.xml").Root!;
        foreach (var rules in new[] { Resource("credential_backup_rules.xml").Root!,
            modern.Element("cloud-backup")!, modern.Element("device-transfer")! })
        {
            Check(!rules.Elements("include").Any(), "Unrelated backup domains must remain eligible.");
            var excluded = rules.Elements("exclude").Select(rule =>
                ((string?)rule.Attribute("domain"), (string?)rule.Attribute("path"))).ToHashSet();
            foreach (var artifact in new[] { "joesscanner.db", "joesscanner.db-wal", "joesscanner.db-shm",
                "joesscanner.db-journal", "favorites.db", "favorites.db-wal", "favorites.db-shm", "favorites.db-journal",
                "Logs", "telemetry-events.json", "monitoring-reconnect.v1" })
                Check(excluded.Contains(("file", artifact)), "The app files directory must exclude every sensitive artifact.");
            Check(excluded.Contains(("sharedpref", "joesscanner.protected-credentials.xml")),
                "Keystore-protected ciphertext must never be transferred independently of its device.");
        }
        return Task.CompletedTask;
    }

    internal static Task WalMigration()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        using var reader = new SqliteConnection($"Data Source={fixture.DbPath};Pooling=False");
        reader.Open();
        using var query = reader.CreateCommand();
        query.CommandText = "PRAGMA journal_mode=WAL;";
        Check(Equals(query.ExecuteScalar(), "wal"), "SQLite must enter WAL mode.");
        query.CommandText = "PRAGMA wal_autocheckpoint=0;";
        query.ExecuteNonQuery();

        const string url = "https://backup-review.test/scanner";
        const string password = "synthetic-WAL-p'a:ss/%@";
        // Keep the connection that wrote the frames open. Closing the last
        // SQLite connection with a WAL lock can checkpoint and remove the WAL.
        query.CommandText = """
            INSERT OR REPLACE INTO app_settings(setting_key, setting_value, updated_utc) VALUES
            ('server_url', $url, ''),
            ('server_cred_user::https://backup-review.test', 'backup-user', ''),
            ('server_cred_pass::https://backup-review.test', $password, '');
            """;
        query.Parameters.AddWithValue("$url", url);
        query.Parameters.AddWithValue("$password", password);
        query.ExecuteNonQuery();

        using var wal = new MemoryStream();
        using (var sidecar = new FileStream(fixture.DbPath + "-wal", FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
            sidecar.CopyTo(wal);
        Check(wal.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)) >= 0,
            "The test must contain legacy plaintext in a genuine WAL sidecar.");
        Check(File.Exists(fixture.DbPath + "-shm"), "The active SQLite family must include its shared-memory sidecar.");
        using var snapshot = reader.BeginTransaction(deferred: true);
        query.Transaction = snapshot;
        query.Parameters.Clear();
        query.CommandText = "SELECT setting_value FROM app_settings WHERE setting_key='server_cred_pass::https://backup-review.test';";
        Check(Equals(query.ExecuteScalar(), password), "The reader must pin a pre-migration database snapshot.");

        fixture.Vault.DiscardWrites = true;
        var failed = fixture.Load();
        _ = failed.ServerUrl;
        Check(failed.SavedCredentialWarning != null && fixture.HasPlaintextCredentialRows(),
            "A failed protected write must leave existing SQLite settings available for retry.");
        fixture.Vault.DiscardWrites = false;

        var restarted = fixture.Load();
        Check(restarted.ServerUrl == url &&
            restarted.TryGetServerCredentials(url, out var name, out var saved) && name == "backup-user" && saved == password &&
            !fixture.HasPlaintextCredentialRows(), "A restart must preserve the path and login and remove the logical plaintext rows.");
        Check(Equals(query.ExecuteScalar(), password),
            "Migration must preserve the pinned reader. The WAL cannot be treated as a safe artifact to back up.");
        using var client = new HttpClient();
        Check(new ServerAuthentication(restarted, client).GetCustomAuthorization(new Uri(url)) != null &&
            Equals(fixture.Execute("PRAGMA integrity_check;"), "ok"), "The migrated connection and SQLite database must remain usable.");
        return Task.CompletedTask;
    }

    private static XDocument Resource(string name)
    {
        using var input = typeof(BackupAndDiagnosticTests).Assembly.GetManifestResourceStream(name)!;
        return XDocument.Load(input);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
