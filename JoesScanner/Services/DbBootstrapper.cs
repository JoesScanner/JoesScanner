using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

// Centralized SQLite bootstrap and schema guardrails.
//
// Goals:
// - One place to apply DB pragmas.
// - One place to create/upgrade schema.
// - A schema version (PRAGMA user_version) to prevent silent drift.
//
// IMPORTANT:
// Keep this bootstrapper compatible with existing installs.
// CREATE TABLE IF NOT EXISTS will not alter existing tables, so any additions
// must be handled via EnsureColumnExists.
public static class DbBootstrapper
{
    private const int CurrentSchemaVersion = 1;
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, byte> InitializedDataSources = new(StringComparer.OrdinalIgnoreCase);

    public static async Task EnsureInitializedAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection == null)
            throw new ArgumentNullException(nameof(connection));

        await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);

        var dataSource = GetInitializationKey(connection);
        if (!string.IsNullOrWhiteSpace(dataSource) && InitializedDataSources.ContainsKey(dataSource))
            return;

        await InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(dataSource) && InitializedDataSources.ContainsKey(dataSource))
                return;

            var version = await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
                throw new InvalidOperationException($"Database schema version {version} is newer than this app supports ({CurrentSchemaVersion}).");

            if (version < 0)
                version = 0;

            if (version < 1)
            {
                await EnsureSchemaV1Async(connection, cancellationToken).ConfigureAwait(false);
                await SetUserVersionAsync(connection, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Even when the version looks current, still ensure missing objects.
                await EnsureSchemaV1Async(connection, cancellationToken).ConfigureAwait(false);

                if (version != CurrentSchemaVersion)
                    await SetUserVersionAsync(connection, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(dataSource))
                InitializedDataSources[dataSource] = 0;
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    public static void EnsureInitialized(SqliteConnection connection)
    {
        if (connection == null)
            throw new ArgumentNullException(nameof(connection));

        ApplyPragmasAsync(connection, CancellationToken.None).GetAwaiter().GetResult();

        var dataSource = GetInitializationKey(connection);
        if (!string.IsNullOrWhiteSpace(dataSource) && InitializedDataSources.ContainsKey(dataSource))
            return;

        InitializationGate.Wait();
        try
        {
            if (!string.IsNullOrWhiteSpace(dataSource) && InitializedDataSources.ContainsKey(dataSource))
                return;

            var version = GetUserVersionAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
            if (version > CurrentSchemaVersion)
                throw new InvalidOperationException($"Database schema version {version} is newer than this app supports ({CurrentSchemaVersion}).");

            if (version < 0)
                version = 0;

            if (version < 1)
            {
                EnsureSchemaV1Async(connection, CancellationToken.None).GetAwaiter().GetResult();
                SetUserVersionAsync(connection, CurrentSchemaVersion, CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                EnsureSchemaV1Async(connection, CancellationToken.None).GetAwaiter().GetResult();

                if (version != CurrentSchemaVersion)
                    SetUserVersionAsync(connection, CurrentSchemaVersion, CancellationToken.None).GetAwaiter().GetResult();
            }

            if (!string.IsNullOrWhiteSpace(dataSource))
                InitializedDataSources[dataSource] = 0;
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    public static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA temp_store=MEMORY;
PRAGMA foreign_keys=ON;
PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaV1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS app_settings (
  setting_key TEXT PRIMARY KEY,
  setting_value TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS servers (
  server_key TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  base_url TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  sort_order INTEGER NOT NULL DEFAULT 0,
  is_builtin INTEGER NOT NULL DEFAULT 0,
  created_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL,
  last_used_utc TEXT NULL,
  uses_api_firewall_credentials INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS server_runtime_state (
  server_key TEXT PRIMARY KEY,
  last_history_loaded_utc TEXT NULL,
  last_live_seen_utc TEXT NULL,
  last_error_utc TEXT NULL,
  last_error TEXT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS server_auth_state (
  server_key TEXT PRIMARY KEY,
  is_validated INTEGER NOT NULL DEFAULT 0,
  validated_utc TEXT NULL,
  expires_utc TEXT NULL,
  last_attempt_utc TEXT NULL,
  last_status_code INTEGER NULL,
  last_error TEXT NULL,
  last_check_utc TEXT NULL,
  last_level TEXT NULL,
  last_message TEXT NULL,
  price_id TEXT NULL,
  subscription_amount_usd REAL NULL,
  premium_min_amount_usd REAL NULL,
  tier_level INTEGER NULL,
  renewal_utc TEXT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS calls (
  server_key TEXT NOT NULL,
  backend_id TEXT NOT NULL,

  start_time_utc TEXT NULL,
  time_text TEXT NULL,
  date_text TEXT NULL,

  target_id TEXT NULL,
  target_label TEXT NULL,
  target_tag TEXT NULL,

  source_id TEXT NULL,
  source_label TEXT NULL,
  source_tag TEXT NULL,

  lcn INTEGER NULL,
  frequency REAL NULL,
  call_audio_type TEXT NULL,
  call_type TEXT NULL,

  system_id TEXT NULL,
  system_label TEXT NULL,
  system_type TEXT NULL,

  site_id TEXT NULL,
  site_label TEXT NULL,
  voice_receiver TEXT NULL,

  audio_filename TEXT NULL,
  audio_start_pos REAL NULL,
  call_duration_seconds REAL NULL,

  call_text TEXT NULL,
  transcription TEXT NULL,
  transcript_available INTEGER NOT NULL DEFAULT 0,

  received_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  transcription_update_notified_at_utc TEXT NULL,

  PRIMARY KEY (server_key, backend_id)
);

CREATE TABLE IF NOT EXISTS lookup_cache (
  server_key TEXT NOT NULL,
  cache_key TEXT NOT NULL,
  fetched_at_utc INTEGER NOT NULL,
  json_payload TEXT NOT NULL,
  PRIMARY KEY(server_key, cache_key)
);

CREATE TABLE IF NOT EXISTS history_lookups (
  server_key TEXT PRIMARY KEY,
  fetched_utc TEXT NOT NULL,
  receivers_json TEXT NOT NULL,
  sites_json TEXT NOT NULL,
  talkgroups_json TEXT NOT NULL,
  talkgroup_groups_json TEXT NOT NULL
);



CREATE TABLE IF NOT EXISTS filter_profiles (
  profile_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  created_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL,
  json_payload TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS telemetry_queue (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  created_utc TEXT NOT NULL,
  event_type TEXT NOT NULL,
  json_payload TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS unregistered_session_queue (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  created_utc TEXT NOT NULL,
  event_type TEXT NOT NULL,
  json_payload TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_servers_enabled_sort ON servers(enabled, sort_order);
CREATE INDEX IF NOT EXISTS idx_calls_server_start ON calls(server_key, start_time_utc);
CREATE INDEX IF NOT EXISTS idx_calls_server_received ON calls(server_key, received_at_utc);

CREATE INDEX IF NOT EXISTS idx_filter_profiles_name ON filter_profiles(name);
CREATE INDEX IF NOT EXISTS idx_telemetry_queue_created ON telemetry_queue(created_utc);
CREATE INDEX IF NOT EXISTS idx_unregistered_queue_created ON unregistered_session_queue(created_utc);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await EnsureColumnExistsAsync(connection, "servers", "display_name", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "sort_order", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "is_builtin", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "created_utc", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "updated_utc", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "last_used_utc", "TEXT NULL", cancellationToken).ConfigureAwait(false);

        // Auth state columns have grown over time; older installs may not have these.
        await EnsureColumnExistsAsync(connection, "server_auth_state", "subscription_amount_usd", "REAL NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "server_auth_state", "premium_min_amount_usd", "REAL NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "server_auth_state", "tier_level", "INTEGER NULL", cancellationToken).ConfigureAwait(false);

        await EnsureColumnExistsAsync(connection, "calls", "transcript_available", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
    
        // Meta table to make schema state explicit and queryable (in addition to PRAGMA user_version).
        await using (var meta = connection.CreateCommand())
        {
            meta.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_meta (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    schema_version INTEGER NOT NULL,
    updated_utc TEXT NOT NULL
);";
            await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText = @"
INSERT INTO schema_meta (id, schema_version, updated_utc)
VALUES (1, $v, $u)
ON CONFLICT(id) DO UPDATE SET schema_version = excluded.schema_version, updated_utc = excluded.updated_utc;";
            upsert.Parameters.AddWithValue("$v", CurrentSchemaVersion);
            upsert.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

}


    private static string GetInitializationKey(SqliteConnection connection)
    {
        try
        {
            return connection.DataSource ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result == null)
            return 0;

        try
        {
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static async Task SetUserVersionAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version={version};";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnExistsAsync(SqliteConnection connection, string tableName, string columnName, string columnDefinition, CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
