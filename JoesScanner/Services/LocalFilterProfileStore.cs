using System.Globalization;
using System.Text.Json;
using JoesScanner.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Maui.Storage;

namespace JoesScanner.Services
{
    // DB-backed shared profile store used by History, Archive, and Settings.
    // No legacy file migration is supported.
    public sealed class LocalFilterProfileStore : IFilterProfileStore
    {
        private const string DbFileName = "joesscanner.db";
        private const string TableName = "filter_profiles";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly SemaphoreSlim _gate = new(1, 1);

        private static string DbPath => Path.Combine(FileSystem.AppDataDirectory, DbFileName);

        private static SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={DbPath};Cache=Shared");
            conn.Open();
            DbBootstrapper.EnsureInitialized(conn);
            EnsureTable(conn);
            return conn;
        }

        private static void EnsureTable(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
CREATE TABLE IF NOT EXISTS {TableName} (
  profile_id   TEXT PRIMARY KEY,
  name         TEXT NOT NULL,
  server_key   TEXT NOT NULL DEFAULT '',
  created_utc  TEXT NOT NULL,
  updated_utc  TEXT NOT NULL,
  filters_json TEXT NOT NULL,
  rules_json   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_filter_profiles_name ON {TableName}(name);
CREATE INDEX IF NOT EXISTS idx_filter_profiles_server_key ON {TableName}(server_key);
";
            cmd.ExecuteNonQuery();

            // Back-compat migration: older installs may not have the server_key column.
            EnsureServerKeyColumn(conn);
        }

        private static void EnsureServerKeyColumn(SqliteConnection conn)
        {
            try
            {
                using var check = conn.CreateCommand();
                check.CommandText = $"PRAGMA table_info({TableName});";

                var has = false;
                using (var reader = check.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var col = reader.GetString(1);
                        if (string.Equals(col, "server_key", StringComparison.OrdinalIgnoreCase))
                        {
                            has = true;
                            break;
                        }
                    }
                }

                if (has)
                    return;

                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE {TableName} ADD COLUMN server_key TEXT NOT NULL DEFAULT '';";
                alter.ExecuteNonQuery();

                using var idx = conn.CreateCommand();
                idx.CommandText = $"CREATE INDEX IF NOT EXISTS idx_filter_profiles_server_key ON {TableName}(server_key);";
                idx.ExecuteNonQuery();
            }
            catch
            {
            }
        }

        public async Task<IReadOnlyList<FilterProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var conn = OpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
SELECT profile_id, name, server_key, created_utc, updated_utc, filters_json, rules_json
FROM {TableName}
ORDER BY name COLLATE NOCASE;";

                var list = new List<FilterProfile>();
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var p = ReadProfileRow(reader);
                    if (p != null)
                        list.Add(p);
                }

                return list;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<FilterProfile>> GetProfilesForServerAsync(string serverKey, CancellationToken cancellationToken = default)
        {
            var key = (serverKey ?? string.Empty).Trim();

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var conn = OpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
SELECT profile_id, name, server_key, created_utc, updated_utc, filters_json, rules_json
FROM {TableName}
WHERE server_key = $k OR server_key = ''
ORDER BY name COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("$k", key);

                var list = new List<FilterProfile>();
                var needsMigration = new List<string>();

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var p = ReadProfileRow(reader);
                    if (p == null)
                        continue;

                    // Auto-attach legacy profiles (no server key) to the current server.
                    if (string.IsNullOrWhiteSpace(p.ServerKey) && !string.IsNullOrWhiteSpace(key))
                    {
                        needsMigration.Add(p.Id);
                        p = new FilterProfile
                        {
                            Id = p.Id,
                            Name = p.Name,
                            ServerKey = key,
                            Filters = p.Filters ?? new FilterProfileFilters(),
                            Rules = p.Rules ?? new List<FilterRuleStateRecord>(),
                            CreatedUtc = p.CreatedUtc,
                            UpdatedUtc = p.UpdatedUtc
                        };
                    }

                    // Only return the server-scoped list.
                    if (string.IsNullOrWhiteSpace(key) || string.Equals((p.ServerKey ?? string.Empty).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        list.Add(p);
                }

                if (needsMigration.Count > 0 && !string.IsNullOrWhiteSpace(key))
                {
                    await MigrateServerKeyAsync(conn, needsMigration, key, cancellationToken);
                }

                return list;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<FilterProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return null;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var conn = OpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
SELECT profile_id, name, server_key, created_utc, updated_utc, filters_json, rules_json
FROM {TableName}
WHERE profile_id = $id
LIMIT 1;";
                cmd.Parameters.AddWithValue("$id", profileId);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return null;

                return ReadProfileRow(reader);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SaveOrUpdateAsync(FilterProfile profile, CancellationToken cancellationToken = default)
        {
            if (profile == null)
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var id = (profile.Id ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    id = Guid.NewGuid().ToString("N");

                var name = (profile.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = "Profile";

                var now = DateTime.UtcNow;

                var serverKey = (profile.ServerKey ?? string.Empty).Trim();

                await using var conn = OpenConnection();

                // Preserve created_utc if existing.
                var createdUtc = await GetCreatedUtcAsync(conn, id, cancellationToken) ?? (profile.CreatedUtc == default ? now : profile.CreatedUtc);

                var filters = profile.Filters ?? new FilterProfileFilters();
                var rules = profile.Rules ?? new List<FilterRuleStateRecord>();

                var filtersJson = JsonSerializer.Serialize(filters, JsonOptions);
                var rulesJson = JsonSerializer.Serialize(rules, JsonOptions);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
INSERT INTO {TableName} (profile_id, name, server_key, created_utc, updated_utc, filters_json, rules_json)
VALUES ($id, $name, $sk, $c, $u, $fj, $rj)
ON CONFLICT(profile_id) DO UPDATE SET
  name = excluded.name,
  server_key = excluded.server_key,
  updated_utc = excluded.updated_utc,
  filters_json = excluded.filters_json,
  rules_json = excluded.rules_json;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$sk", serverKey);
                cmd.Parameters.AddWithValue("$c", createdUtc.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$u", now.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$fj", filtersJson);
                cmd.Parameters.AddWithValue("$rj", rulesJson);

                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var conn = OpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {TableName} WHERE profile_id = $id;";
                cmd.Parameters.AddWithValue("$id", profileId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RenameAsync(string profileId, string newName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return;

            var name = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var conn = OpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
UPDATE {TableName}
SET name = $name,
    updated_utc = $u
WHERE profile_id = $id;";
                cmd.Parameters.AddWithValue("$id", profileId);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<FilterProfileStoreEnvelope> ExportAsync(CancellationToken cancellationToken = default)
        {
            var profiles = await GetProfilesAsync(cancellationToken);
            // Produce a stable payload.
            return new FilterProfileStoreEnvelope
            {
                SchemaVersion = 2,
                Profiles = profiles
                    .Where(p => p != null)
                    .Select(p => new FilterProfile
                    {
                        Id = p.Id,
                        Name = p.Name,
                        ServerKey = p.ServerKey,
                        Filters = p.Filters ?? new FilterProfileFilters(),
                        Rules = p.Rules ?? new List<FilterRuleStateRecord>(),
                        CreatedUtc = p.CreatedUtc,
                        UpdatedUtc = p.UpdatedUtc
                    })
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        public async Task ImportAsync(FilterProfileStoreEnvelope envelope, bool merge, CancellationToken cancellationToken = default)
        {
            if (envelope == null)
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var conn = OpenConnection();

                if (!merge)
                {
                    await using var clear = conn.CreateCommand();
                    clear.CommandText = $"DELETE FROM {TableName};";
                    await clear.ExecuteNonQueryAsync(cancellationToken);
                }

                var incoming = envelope.Profiles ?? new List<FilterProfile>();
                foreach (var p in incoming)
                {
                    if (p == null)
                        continue;

                    var id = (p.Id ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(id))
                        id = Guid.NewGuid().ToString("N");

                    var name = (p.Name ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        name = "Profile";

                    var now = DateTime.UtcNow;
                    var created = p.CreatedUtc == default ? now : p.CreatedUtc;

                    var serverKey = (p.ServerKey ?? string.Empty).Trim();

                    var filtersJson = JsonSerializer.Serialize(p.Filters ?? new FilterProfileFilters(), JsonOptions);
                    var rulesJson = JsonSerializer.Serialize(p.Rules ?? new List<FilterRuleStateRecord>(), JsonOptions);

                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $@"
INSERT INTO {TableName} (profile_id, name, server_key, created_utc, updated_utc, filters_json, rules_json)
VALUES ($id, $name, $sk, $c, $u, $fj, $rj)
ON CONFLICT(profile_id) DO UPDATE SET
  name = excluded.name,
  server_key = excluded.server_key,
  created_utc = excluded.created_utc,
  updated_utc = excluded.updated_utc,
  filters_json = excluded.filters_json,
  rules_json = excluded.rules_json;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$sk", serverKey);
                    cmd.Parameters.AddWithValue("$c", created.ToString("o", CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("$u", now.ToString("o", CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("$fj", filtersJson);
                    cmd.Parameters.AddWithValue("$rj", rulesJson);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task MigrateServerKeyAsync(SqliteConnection conn, List<string> profileIds, string serverKey, CancellationToken ct)
        {
            try
            {
	                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE {TableName} SET server_key = $sk WHERE profile_id = $id AND server_key = '';";
                var pSk = cmd.CreateParameter();
                pSk.ParameterName = "$sk";
                pSk.Value = serverKey;
                cmd.Parameters.Add(pSk);

                var pId = cmd.CreateParameter();
                pId.ParameterName = "$id";
                cmd.Parameters.Add(pId);

                foreach (var id in profileIds)
                {
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    pId.Value = id;
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
            }
        }

        private static async Task<DateTime?> GetCreatedUtcAsync(SqliteConnection conn, string id, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT created_utc FROM {TableName} WHERE profile_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            var obj = await cmd.ExecuteScalarAsync(ct);
            if (obj == null || obj == DBNull.Value)
                return null;

            var raw = Convert.ToString(obj, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
        }

        private static FilterProfile? ReadProfileRow(SqliteDataReader reader)
        {
            try
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var serverKey = reader.GetString(2);
                var createdRaw = reader.GetString(3);
                var updatedRaw = reader.GetString(4);
                var filtersJson = reader.GetString(5);
                var rulesJson = reader.GetString(6);

                var created = DateTime.TryParse(createdRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.UtcNow;
                var updated = DateTime.TryParse(updatedRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var u) ? u : created;

                var filters = string.IsNullOrWhiteSpace(filtersJson)
                    ? new FilterProfileFilters()
                    : (JsonSerializer.Deserialize<FilterProfileFilters>(filtersJson, JsonOptions) ?? new FilterProfileFilters());

                var rules = string.IsNullOrWhiteSpace(rulesJson)
                    ? new List<FilterRuleStateRecord>()
                    : (JsonSerializer.Deserialize<List<FilterRuleStateRecord>>(rulesJson, JsonOptions) ?? new List<FilterRuleStateRecord>());

                return new FilterProfile
                {
                    Id = id,
                    Name = name,
                    ServerKey = serverKey,
                    Filters = filters,
                    Rules = rules,
                    CreatedUtc = created,
                    UpdatedUtc = updated
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
