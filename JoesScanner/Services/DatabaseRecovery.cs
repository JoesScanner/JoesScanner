using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

internal sealed class NewerDatabaseException : InvalidOperationException { }
internal sealed class DamagedDatabaseException : IOException { }

internal static class DatabaseRecovery
{
    private const string DirectoryName = "DatabaseRecovery";
    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal", ""];
    private static readonly string[] MainTables =
    [
        "app_settings", "servers", "scanner_connections", "scanner_connection_origins",
        "server_runtime_state", "server_auth_state", "filter_profiles", "calls", "lookup_cache",
        "history_lookups", "live_queue_servers", "live_queue_recordings", "live_queue_recovery"
    ];

    internal static FileStream AcquireLease(string mainPath) =>
        new(Path.Combine(RecoveryDirectory(mainPath), "storage.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

    internal static bool PrepareMain(string path) => Prepare(path, connection =>
    {
        DbBootstrapper.EnsureInitialized(connection);
        using var check = connection.CreateCommand();
        check.CommandText = """
            SELECT setting_key, setting_value, updated_utc FROM app_settings LIMIT 0;
            SELECT server_id, endpoint, display_name, directory_id, enabled, credentials_migrated, legacy_filter_key
                FROM scanner_connections LIMIT 0;
            SELECT server_key, base_url FROM servers LIMIT 0;
            SELECT server_key, is_validated, expires_utc FROM server_auth_state LIMIT 0;
            SELECT profile_id, server_key, filters_json, rules_json FROM filter_profiles LIMIT 0;
            SELECT arrival_sequence, server, account_scope, recording_id FROM live_queue_recordings LIMIT 0;
            EXPLAIN INSERT INTO app_settings(setting_key, setting_value, updated_utc) VALUES ('', '', '')
                ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;
            """;
        check.Prepare();
    }, MainTables, maximumVersion: DbBootstrapper.CurrentSchemaVersion);

    internal static void BackupBeforeMigration(SqliteConnection source, string path, int version)
    {
        using var tables = source.CreateCommand();
        tables.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' LIMIT 1;";
        if (tables.ExecuteScalar() == null)
            return;

        var backup = Path.Combine(RecoveryDirectory(path),
            $"{Path.GetFileName(path)}.before-v{version}-v{DbBootstrapper.CurrentSchemaVersion}.db");
        if (File.Exists(backup))
            return;
        var temporary = backup + ".new";
        File.Delete(temporary);
        using (var target = Open(temporary))
        {
            source.BackupDatabase(target);
            MakeStandalone(target);
            Check(target);
        }
        File.Move(temporary, backup);
        AppLog.Basic("Saved a local SQLite snapshot before schema migration.");
    }

    internal static bool Prepare(string path, Action<SqliteConnection> initialize, IReadOnlyList<string> tables,
        int? maximumVersion = null)
    {
        var marker = Path.Combine(RecoveryDirectory(path), Path.GetFileName(path) + ".recovery");
        if (File.Exists(marker))
        {
            FinishRecovery(path, marker);
            return true;
        }

        try
        {
            using var current = Open(path);
            if (maximumVersion is { } maximum)
            {
                using var version = current.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                if (version.ExecuteScalar() is long saved && saved > maximum)
                    throw new NewerDatabaseException();
            }
            Check(current);
            initialize(current);
            return false;
        }
        catch (Exception error) when (IsDamage(error))
        {
            AppLog.Basic(() => $"Local database recovery started ({Describe(error)}).");
        }

        // Prepare a healthy replacement before moving the original. An interruption
        // before publication leaves the original in place. After publication, the
        // marker is sufficient to finish every move without touching the vault.
        var id = Guid.NewGuid().ToString("N");
        var archive = ArchiveDirectory(path, marker, id);
        Directory.CreateDirectory(archive);
        var replacement = Path.Combine(archive, "replacement.db");
        try
        {
            using var target = Open(replacement);
            initialize(target);
            MakeStandalone(target);
            Salvage(path, target, tables);
            Check(target);
        }
        finally
        {
            DbBootstrapper.Forget(replacement);
        }

        var pending = marker + ".new";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(System.Text.Encoding.ASCII.GetBytes(id));
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, marker);
        FinishRecovery(path, marker);
        return true;
    }

    private static void FinishRecovery(string path, string marker)
    {
        var archive = ArchiveDirectory(path, marker, File.ReadAllText(marker));
        var replacement = Path.Combine(archive, "replacement.db");
        if (File.Exists(replacement))
        {
            foreach (var suffix in Sidecars)
            {
                var old = path + suffix;
                if (File.Exists(old))
                    File.Move(old, Path.Combine(archive, Path.GetFileName(old)));
            }
            File.Move(replacement, path);
        }
        else if (!File.Exists(path) || !File.Exists(Path.Combine(archive, Path.GetFileName(path))))
        {
            throw new IOException("The local database recovery is incomplete.");
        }
        DbBootstrapper.Forget(path);
        File.Delete(marker);
        AppLog.Basic("Local database recovery completed. The original SQLite files were kept.");
    }

    private static string ArchiveDirectory(string path, string marker, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new IOException("The local database recovery marker is invalid.");
        return Path.Combine(Path.GetDirectoryName(marker)!, $"{Path.GetFileName(path)}-{id}");
    }

    private static void Salvage(string path, SqliteConnection target, IReadOnlyList<string> tables)
    {
        using var transaction = target.BeginTransaction();
        try
        {
            using var source = Open(path, readOnly: true);
            foreach (var table in tables)
            {
                try { CopyRows(source, transaction, table); }
                catch (Exception error) when (IsDamage(error))
                {
                    // Rows already copied and other readable tables remain available.
                    AppLog.Basic(() => $"Some rows in {table} could not be recovered ({Describe(error)}).");
                }
            }
        }
        catch (Exception error) when (IsDamage(error))
        {
            AppLog.Basic(() => $"The database could not be read for salvage ({Describe(error)}).");
        }
        if (tables.Contains("app_settings"))
            PreserveLocalOnly(transaction);
        transaction.Commit();
    }

    private static void CopyRows(SqliteConnection source, SqliteTransaction transaction, string table)
    {
        using var select = source.CreateCommand();
        select.CommandText = $"PRAGMA table_info({Identifier(table)});";
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var schema = select.ExecuteReader())
        {
            while (schema.Read())
                available.Add(schema.GetString(1));
        }

        using var insert = transaction.Connection!.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"PRAGMA table_info({Identifier(table)});";
        var columns = new List<string>();
        using (var schema = insert.ExecuteReader())
        {
            while (schema.Read())
            {
                var name = schema.GetString(1);
                if (available.Contains(name))
                    columns.Add(name);
                else if (schema.GetInt64(3) != 0 && schema.IsDBNull(4))
                    return; // No value or default for a required destination column.
            }
        }
        if (columns.Count == 0)
            return;

        var fields = string.Join(", ", columns.Select(Identifier));
        var placeholders = columns.Select((_, index) => $"$v{index}").ToArray();
        var usableRows = table == "app_settings" && available.Contains("setting_key")
            ? " WHERE typeof(setting_key) = 'text'"
            : "";
        // Scan table pages rather than depending on possibly damaged indexes.
        select.CommandText = $"SELECT {fields} FROM {Identifier(table)} NOT INDEXED{usableRows};";
        insert.CommandText = $"INSERT OR IGNORE INTO {Identifier(table)} ({fields}) VALUES ({string.Join(", ", placeholders)});";
        foreach (var placeholder in placeholders)
            insert.Parameters.AddWithValue(placeholder, DBNull.Value);
        using var rows = select.ExecuteReader();
        while (rows.Read())
        {
            for (var i = 0; i < columns.Count; i++)
                insert.Parameters[i].Value = rows.GetValue(i);
            try { insert.ExecuteNonQuery(); }
            catch (SqliteException error) when (error.SqliteErrorCode is 19 or 20)
            {
                // For example, the foreign key points to a damaged parent row.
            }
        }
    }

    private static void PreserveLocalOnly(SqliteTransaction transaction)
    {
        using var state = transaction.Connection!.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            SELECT setting_value FROM app_settings
            WHERE setting_key IN ('local_only_mode', 'telemetry_enabled')
            ORDER BY setting_key;
            """;
        // The saved Local Only choice takes precedence. If neither preference is
        // readable, keep the recovered installation offline until the user decides.
        if (state.ExecuteScalar() is string choice && bool.TryParse(choice, out _))
            return;

        state.CommandText = """
            INSERT INTO app_settings(setting_key, setting_value, updated_utc)
            VALUES ('local_only_mode', 'true', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value, updated_utc = excluded.updated_utc;
            """;
        state.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string RecoveryDirectory(string path)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, DirectoryName);
        Directory.CreateDirectory(directory);
#if IOS || MACCATALYST
        AppPaths.ExcludeFromPlatformBackup(directory);
#endif
        return directory;
    }

    private static string Identifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static void Check(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check(1);";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new DamagedDatabaseException();
    }

    private static void MakeStandalone(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        if (!string.Equals(command.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Could not finalize the local database snapshot.");
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
    }

    private static bool IsDamage(Exception error) => error is DamagedDatabaseException ||
        (error is SqliteException sqlite && (sqlite.SqliteErrorCode is 11 or 19 or 20 or 26 ||
            (sqlite.SqliteErrorCode == 1 && (sqlite.Message.Contains("no such ", StringComparison.OrdinalIgnoreCase) ||
             sqlite.Message.Contains("has no column", StringComparison.OrdinalIgnoreCase) ||
             sqlite.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
             sqlite.Message.Contains("foreign key mismatch", StringComparison.OrdinalIgnoreCase) ||
             sqlite.Message.Contains("ON CONFLICT clause does not match", StringComparison.OrdinalIgnoreCase)))));

    internal static string Describe(Exception error) => error is SqliteException sqlite
        ? $"SQLite {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}"
        : error.GetType().Name;

    internal static string? StartupNotice(bool mainRecovered, bool favoritesRecovered, bool unreadableSettings,
        string? credentialWarning)
    {
        if (mainRecovered)
            return "Saved data was recovered and a backup was kept. Check your server settings and reenter any missing credentials.";
        if (unreadableSettings)
            return "Some saved settings could not be read. Check your server settings and reenter any missing credentials.";
        if (favoritesRecovered)
            return "Favorites data was recovered and a backup was kept. Some Favorites may need to be added again.";
        return credentialWarning;
    }
}
