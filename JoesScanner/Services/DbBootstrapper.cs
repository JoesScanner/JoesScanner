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
    private const int CurrentSchemaVersion = 5;
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

            await EnsureSchemaV1Async(connection, cancellationToken).ConfigureAwait(false);
            if (version < CurrentSchemaVersion)
                await MigrateLiveQueueAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureLiveRecoverySchemaAsync(connection, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(dataSource))
                InitializedDataSources[dataSource] = 0;
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    public static void EnsureInitialized(SqliteConnection connection) =>
        EnsureInitializedAsync(connection).GetAwaiter().GetResult();

    private static async Task EnsureLiveRecoverySchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = connection.BeginTransaction();
        await EnsureColumnExistsAsync(connection, "live_queue_recordings", "recovery_backlog",
            "INTEGER NOT NULL DEFAULT 0", token, transaction).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "live_queue_recordings", "audio_revision",
            "INTEGER NOT NULL DEFAULT 0", token, transaction).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "live_queue_recordings", "audio_recovered",
            "INTEGER NOT NULL DEFAULT 0", token, transaction).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "live_queue_servers", "playback_floor",
            "INTEGER NOT NULL DEFAULT 0", token, transaction).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS live_queue_recovery (
                server TEXT NOT NULL,
                account_scope TEXT NOT NULL,
                window_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                since_utc INTEGER NOT NULL,
                skip_unseen INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (server, account_scope),
                FOREIGN KEY (server, account_scope) REFERENCES live_queue_servers (server, account_scope)
            );
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
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

-- Saved listening connections are distinct from cached directory and legacy URL rows.
CREATE TABLE IF NOT EXISTS scanner_connections (
  server_id TEXT PRIMARY KEY,
  endpoint TEXT NOT NULL UNIQUE,
  display_name TEXT NOT NULL,
  directory_id INTEGER NULL UNIQUE,
  enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
  credentials_migrated INTEGER NOT NULL DEFAULT 1,
  legacy_filter_key TEXT NULL,
  sort_order INTEGER NULL,
  directory_source TEXT NULL,
  directory_hold INTEGER NOT NULL DEFAULT 0,
  directory_candidate TEXT NULL,
  directory_firewall INTEGER NULL,
  candidate_firewall INTEGER NULL
);

CREATE TABLE IF NOT EXISTS scanner_connection_origins (
  server_id TEXT NOT NULL REFERENCES scanner_connections(server_id) ON DELETE CASCADE,
  origin TEXT NOT NULL,
  PRIMARY KEY (server_id, origin)
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
  server_key TEXT NOT NULL DEFAULT '',
  created_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL,
  filters_json TEXT NOT NULL DEFAULT '{}',
  rules_json TEXT NOT NULL DEFAULT '[]',
  json_payload TEXT NOT NULL DEFAULT '{}'
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

        await EnsureColumnExistsAsync(connection, "scanner_connections", "sort_order", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "scanner_connections", "directory_source", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "scanner_connections", "directory_hold", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "scanner_connections", "directory_candidate", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "scanner_connections", "directory_firewall", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "scanner_connections", "candidate_firewall", "INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "display_name", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "sort_order", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "is_builtin", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "created_utc", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "updated_utc", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "last_used_utc", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "uses_api_firewall_credentials", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "servers", "directory_source", "TEXT NULL", cancellationToken).ConfigureAwait(false);

        // Auth state columns have grown over time; older installs may not have these.
        await EnsureColumnExistsAsync(connection, "server_auth_state", "subscription_amount_usd", "REAL NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "server_auth_state", "premium_min_amount_usd", "REAL NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "server_auth_state", "tier_level", "INTEGER NULL", cancellationToken).ConfigureAwait(false);

        await EnsureColumnExistsAsync(connection, "calls", "transcript_available", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureFilterProfileSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateLiveQueueAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var transaction = connection.BeginTransaction();
        // Another process can finish the migration while this connection is
        // waiting for the write lock.
        var version = await GetUserVersionAsync(connection, token, transaction).ConfigureAwait(false);
        if (version == CurrentSchemaVersion)
            return;
        if (version > CurrentSchemaVersion)
            throw new InvalidOperationException($"Database schema version {version} is newer than this app supports ({CurrentSchemaVersion}).");

        await using var queue = connection.CreateCommand();
        queue.Transaction = transaction;
        if (version < 4)
            await MigrateLiveQueueScopesAsync(connection, transaction, queue, token).ConfigureAwait(false);

        // Use the old insertion rowids for a deterministic order and keep the
        // maintenance cursor valid. The old schema did not record local acceptance order.
        queue.CommandText = """
            CREATE TABLE live_queue_recordings_v5 (
                arrival_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                server TEXT NOT NULL,
                account_scope TEXT NOT NULL DEFAULT '',
                recording_id TEXT NOT NULL,
                recorded_utc INTEGER,
                first_owner INTEGER NOT NULL,
                payload TEXT,
                audio_ready INTEGER NOT NULL,
                disposition INTEGER NOT NULL DEFAULT 0 CHECK(disposition IN (0, 1, 2)),
                origin INTEGER NOT NULL DEFAULT 0,
                protected INTEGER NOT NULL DEFAULT 0 CHECK(protected = (account_scope <> '')),
                UNIQUE(server, account_scope, recording_id),
                FOREIGN KEY(server, account_scope) REFERENCES live_queue_servers(server, account_scope)
            );
            INSERT INTO live_queue_recordings_v5
                (arrival_sequence, server, account_scope, recording_id, recorded_utc,
                 first_owner, payload, audio_ready, disposition, origin, protected)
            SELECT rowid, server, account_scope, recording_id, recorded_utc,
                   first_owner, payload, audio_ready, disposition, origin, protected
            FROM live_queue_recordings ORDER BY rowid;
            DROP TABLE live_queue_recordings;
            ALTER TABLE live_queue_recordings_v5 RENAME TO live_queue_recordings;
            ALTER TABLE live_queue_servers DROP COLUMN jump_utc;
            ALTER TABLE live_queue_servers DROP COLUMN jump_id;
            CREATE INDEX live_queue_pending_arrival
                ON live_queue_recordings(server, account_scope, arrival_sequence) WHERE disposition = 0;
            CREATE INDEX live_queue_transport_arrival
                ON live_queue_recordings(server, account_scope, arrival_sequence);
            CREATE TABLE IF NOT EXISTS schema_meta (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                schema_version INTEGER NOT NULL,
                updated_utc TEXT NOT NULL
            );
            INSERT INTO schema_meta (id, schema_version, updated_utc)
            VALUES (1, $v, $u)
            ON CONFLICT(id) DO UPDATE SET schema_version = excluded.schema_version, updated_utc = excluded.updated_utc;
            """ + $"\nPRAGMA user_version={CurrentSchemaVersion};";
        queue.Parameters.AddWithValue("$v", CurrentSchemaVersion);
        queue.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await queue.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async Task MigrateLiveQueueScopesAsync(SqliteConnection connection, SqliteTransaction transaction,
        SqliteCommand queue, CancellationToken token)
    {
        // Older installations can predate the Live journal entirely.
        queue.CommandText = """
                CREATE TABLE IF NOT EXISTS live_queue_servers (
                    server TEXT PRIMARY KEY,
                    owner INTEGER NOT NULL,
                    stopped INTEGER NOT NULL DEFAULT 0,
                    accept_after INTEGER NOT NULL DEFAULT 0,
                    resume_id TEXT,
                    jump_utc INTEGER,
                    jump_id TEXT
                );
                CREATE TABLE IF NOT EXISTS live_queue_recordings (
                    server TEXT NOT NULL REFERENCES live_queue_servers(server),
                    recording_id TEXT NOT NULL,
                    recorded_utc INTEGER NOT NULL,
                    first_owner INTEGER NOT NULL,
                    payload TEXT,
                    audio_ready INTEGER NOT NULL,
                    disposition INTEGER NOT NULL DEFAULT 0 CHECK(disposition IN (0, 1, 2)),
                    PRIMARY KEY(server, recording_id)
                );
            """;
        await queue.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "live_queue_recordings", "origin", "INTEGER NOT NULL DEFAULT 0", token, transaction).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "live_queue_recordings", "protected", "INTEGER NOT NULL DEFAULT 0", token, transaction).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "live_queue_recordings", "account_scope", "TEXT NULL", token, transaction).ConfigureAwait(false);

        connection.CreateFunction("live_migration_hosted", (string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.IdnHost.TrimEnd('.').Equals("app.joesscanner.com", StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        connection.CreateFunction("live_migration_origin", (string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null, isDeterministic: true);
        connection.CreateFunction("live_migration_recorder", (string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/') : null, isDeterministic: true);

        // Never use the currently signed-in account to infer ownership. Legacy
        // monitoring state for hosted servers was shared and stays archived.
        queue.CommandText = """
            INSERT OR IGNORE INTO live_queue_servers (server, owner)
            SELECT DISTINCT server, 0 FROM live_queue_recordings;
            CREATE TABLE live_queue_servers_v4 (
                server TEXT NOT NULL,
                account_scope TEXT NOT NULL DEFAULT '',
                owner INTEGER NOT NULL,
                stopped INTEGER NOT NULL DEFAULT 0,
                accept_after INTEGER NOT NULL DEFAULT 0,
                resume_id TEXT,
                jump_utc INTEGER,
                jump_id TEXT,
                PRIMARY KEY(server, account_scope)
            );
            CREATE TABLE live_queue_recordings_v4 (
                server TEXT NOT NULL,
                account_scope TEXT NOT NULL DEFAULT '',
                recording_id TEXT NOT NULL,
                recorded_utc INTEGER NOT NULL,
                first_owner INTEGER NOT NULL,
                payload TEXT,
                audio_ready INTEGER NOT NULL,
                disposition INTEGER NOT NULL DEFAULT 0 CHECK(disposition IN (0, 1, 2)),
                origin INTEGER NOT NULL DEFAULT 0,
                protected INTEGER NOT NULL DEFAULT 0 CHECK(protected = (account_scope <> '')),
                PRIMARY KEY(server, account_scope, recording_id),
                FOREIGN KEY(server, account_scope) REFERENCES live_queue_servers_v4(server, account_scope)
            );
            CREATE TEMP TABLE live_queue_legacy_scope AS
            SELECT s.server, CASE
                WHEN live_migration_hosted(s.server)
                  OR EXISTS (SELECT 1 FROM servers WHERE uses_api_firewall_credentials = 1
                      AND live_migration_origin(base_url) = live_migration_origin(s.server))
                  OR EXISTS (SELECT 1 FROM live_queue_recordings q WHERE q.server = s.server AND q.protected <> 0)
                    THEN ':legacy'
                WHEN EXISTS (SELECT 1 FROM servers WHERE uses_api_firewall_credentials = 0
                      AND live_migration_recorder(base_url) = live_migration_recorder(s.server))
                  OR EXISTS (SELECT 1 FROM live_queue_recordings q WHERE q.server = s.server AND q.origin IN (1, 2))
                    THEN ''
                ELSE ':legacy'
            END AS scope FROM live_queue_servers s;
            CREATE UNIQUE INDEX temp.live_queue_legacy_scope_server ON live_queue_legacy_scope(server);

            INSERT INTO live_queue_servers_v4
                (server, account_scope, owner, stopped, accept_after, resume_id, jump_utc, jump_id)
            SELECT s.server, l.scope, s.owner, s.stopped, s.accept_after, s.resume_id, s.jump_utc, s.jump_id
            FROM live_queue_servers s JOIN live_queue_legacy_scope l USING (server);

            INSERT INTO live_queue_servers_v4 (server, account_scope, owner)
            SELECT DISTINCT server, upper(account_scope), 0 FROM live_queue_recordings
            WHERE protected <> 0 AND length(account_scope) = 64 AND upper(account_scope) NOT GLOB '*[^0-9A-F]*';

            -- Retain rowids so the existing bounded maintenance cursor remains valid.
            INSERT INTO live_queue_recordings_v4
                (rowid, server, account_scope, recording_id, recorded_utc, first_owner, payload, audio_ready, disposition, origin, protected)
            SELECT rowid, server, scope, recording_id, recorded_utc, first_owner, payload, audio_ready, disposition, origin, scope <> ''
            FROM (
                SELECT q.rowid, q.server, q.recording_id, q.recorded_utc, q.first_owner, q.payload, q.audio_ready, q.disposition, q.origin,
                    CASE WHEN q.protected <> 0 AND length(q.account_scope) = 64
                              AND upper(q.account_scope) NOT GLOB '*[^0-9A-F]*'
                         THEN upper(q.account_scope) ELSE l.scope END AS scope
                FROM live_queue_recordings q JOIN live_queue_legacy_scope l USING (server)
            );
            DROP TABLE live_queue_legacy_scope;
            DROP TABLE live_queue_recordings;
            DROP TABLE live_queue_servers;
            ALTER TABLE live_queue_servers_v4 RENAME TO live_queue_servers;
            ALTER TABLE live_queue_recordings_v4 RENAME TO live_queue_recordings;
            """;
        await queue.ExecuteNonQueryAsync(token).ConfigureAwait(false);
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

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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

    private static async Task EnsureFilterProfileSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Older databases can have either the split JSON columns or only the bootstrap payload.
        // Keep the payload column so inserts also satisfy the older NOT NULL constraint.
        await using var transaction = connection.BeginTransaction();
        await EnsureColumnExistsAsync(connection, "filter_profiles", "json_payload", "TEXT NOT NULL DEFAULT '{}'", cancellationToken, transaction).ConfigureAwait(false);
        var addedServerKey = await EnsureColumnExistsAsync(connection, "filter_profiles", "server_key", "TEXT NOT NULL DEFAULT ''", cancellationToken, transaction).ConfigureAwait(false);
        var addedFilters = await EnsureColumnExistsAsync(connection, "filter_profiles", "filters_json", "TEXT NOT NULL DEFAULT '{}'", cancellationToken, transaction).ConfigureAwait(false);
        var addedRules = await EnsureColumnExistsAsync(connection, "filter_profiles", "rules_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken, transaction).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            WITH payloads AS MATERIALIZED (
                SELECT profile_id, json_payload AS payload
                FROM filter_profiles
                WHERE json_valid(json_payload) AND json_payload <> '{}'
            )
            UPDATE filter_profiles AS profile
            SET server_key = CASE WHEN $server AND typeof(legacy_server) = 'text' THEN legacy_server ELSE server_key END,
                filters_json = CASE WHEN $filters AND json_valid(legacy_filters) AND substr(legacy_filters, 1, 1) = '{'
                    THEN legacy_filters ELSE filters_json END,
                rules_json = CASE WHEN $rules AND json_valid(legacy_rules) AND substr(legacy_rules, 1, 1) = '['
                    THEN legacy_rules ELSE rules_json END
            FROM (
                SELECT profile_id,
                    coalesce(json_extract(payload, '$.serverKey'), json_extract(payload, '$.ServerKey')) AS legacy_server,
                    coalesce(json_extract(payload, '$.filters'), json_extract(payload, '$.Filters')) AS legacy_filters,
                    coalesce(json_extract(payload, '$.rules'), json_extract(payload, '$.Rules')) AS legacy_rules
                FROM payloads
            ) AS legacy
            WHERE profile.profile_id = legacy.profile_id AND ($server OR $filters OR $rules);
            CREATE INDEX IF NOT EXISTS idx_filter_profiles_server_key ON filter_profiles(server_key);
            """;
        cmd.Parameters.AddWithValue("$server", addedServerKey);
        cmd.Parameters.AddWithValue("$filters", addedFilters);
        cmd.Parameters.AddWithValue("$rules", addedRules);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> EnsureColumnExistsAsync(SqliteConnection connection, string tableName, string columnName,
        string columnDefinition, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
