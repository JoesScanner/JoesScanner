using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace JoesScanner.Services
{
    public interface IHistoryLookupsRepository
    {
        Task<(HistoryLookupData? Data, DateTime? FetchedUtc)> GetAsync(string serverKey, CancellationToken cancellationToken = default);
        Task UpsertAsync(string serverKey, HistoryLookupData data, DateTime fetchedUtc, CancellationToken cancellationToken = default);
    }

    // Stores History dropdown lookup payloads per server.
    // This is intentionally lightweight: a single row per server.
    public sealed class HistoryLookupsRepository : IHistoryLookupsRepository
    {
        private readonly string _dbPath;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public HistoryLookupsRepository(IDatabasePathProvider dbPathProvider)
        {
            _dbPath = (dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider))).DbPath;
        }

        public async Task<(HistoryLookupData? Data, DateTime? FetchedUtc)> GetAsync(string serverKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serverKey))
                return (null, null);

            await EnsureSchemaAsync(cancellationToken);

            await using var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            await connection.OpenAsync(cancellationToken);

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT fetched_utc, receivers_json, sites_json, talkgroups_json, talkgroup_groups_json
FROM history_lookups
WHERE server_key = $server_key
LIMIT 1;";
            cmd.Parameters.AddWithValue("$server_key", serverKey);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return (null, null);

            var fetchedRaw = reader.IsDBNull(0) ? null : reader.GetString(0);
            DateTime? fetchedUtc = null;
            if (!string.IsNullOrWhiteSpace(fetchedRaw) && DateTime.TryParse(fetchedRaw, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                fetchedUtc = parsed;

            var receiversJson = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var sitesJson = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var talkgroupsJson = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var groupsJson = reader.IsDBNull(4) ? "" : reader.GetString(4);

            try
            {
                var receivers = JsonSerializer.Deserialize<List<HistoryLookupItem>>(receiversJson, JsonOptions) ?? new List<HistoryLookupItem>();
                var sites = JsonSerializer.Deserialize<List<HistoryLookupItem>>(sitesJson, JsonOptions) ?? new List<HistoryLookupItem>();
                var talkgroups = JsonSerializer.Deserialize<List<HistoryLookupItem>>(talkgroupsJson, JsonOptions) ?? new List<HistoryLookupItem>();
                var groups = JsonSerializer.Deserialize<Dictionary<string, List<HistoryLookupItem>>>(groupsJson, JsonOptions)
                    ?? new Dictionary<string, List<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);

                var data = new HistoryLookupData
                {
                    Receivers = receivers,
                    Sites = sites,
                    Talkgroups = talkgroups,
                    TalkgroupGroups = groups.ToDictionary(k => k.Key, v => (IReadOnlyList<HistoryLookupItem>)v.Value, StringComparer.OrdinalIgnoreCase)
                };

                return (data, fetchedUtc);
            }
            catch
            {
                return (null, fetchedUtc);
            }
        }

        public async Task UpsertAsync(string serverKey, HistoryLookupData data, DateTime fetchedUtc, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serverKey) || data == null)
                return;

            await EnsureSchemaAsync(cancellationToken);

            var receiversJson = JsonSerializer.Serialize(data.Receivers ?? Array.Empty<HistoryLookupItem>(), JsonOptions);
            var sitesJson = JsonSerializer.Serialize(data.Sites ?? Array.Empty<HistoryLookupItem>(), JsonOptions);
            var talkgroupsJson = JsonSerializer.Serialize(data.Talkgroups ?? Array.Empty<HistoryLookupItem>(), JsonOptions);

            // Convert IReadOnlyDictionary to concrete type for serialization.
            var groups = new Dictionary<string, List<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);
            if (data.TalkgroupGroups != null)
            {
                foreach (var kvp in data.TalkgroupGroups)
                    groups[kvp.Key] = kvp.Value?.ToList() ?? new List<HistoryLookupItem>();
            }
            var groupsJson = JsonSerializer.Serialize(groups, JsonOptions);

            await using var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            await connection.OpenAsync(cancellationToken);

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
INSERT INTO history_lookups (server_key, fetched_utc, receivers_json, sites_json, talkgroups_json, talkgroup_groups_json)
VALUES ($server_key, $fetched_utc, $receivers, $sites, $talkgroups, $groups)
ON CONFLICT(server_key)
DO UPDATE SET
    fetched_utc = excluded.fetched_utc,
    receivers_json = excluded.receivers_json,
    sites_json = excluded.sites_json,
    talkgroups_json = excluded.talkgroups_json,
    talkgroup_groups_json = excluded.talkgroup_groups_json;";

            cmd.Parameters.AddWithValue("$server_key", serverKey);
            cmd.Parameters.AddWithValue("$fetched_utc", fetchedUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$receivers", receiversJson);
            cmd.Parameters.AddWithValue("$sites", sitesJson);
            cmd.Parameters.AddWithValue("$talkgroups", talkgroupsJson);
            cmd.Parameters.AddWithValue("$groups", groupsJson);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? FileSystem.AppDataDirectory);

            await using var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            await connection.OpenAsync(cancellationToken);

            // Enable WAL for better concurrent read/write behavior.
            var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS history_lookups (
    server_key TEXT PRIMARY KEY,
    fetched_utc TEXT NOT NULL,
    receivers_json TEXT NOT NULL,
    sites_json TEXT NOT NULL,
    talkgroups_json TEXT NOT NULL,
    talkgroup_groups_json TEXT NOT NULL
);";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
