using Microsoft.Data.Sqlite;

namespace JoesScanner.Services
{
    public interface IServerRepository
    {
        Task TouchLastUsedUtcAsync(string serverUrl, DateTime utcNow, CancellationToken cancellationToken = default);
    }

    public sealed class ServerRepository : IServerRepository
    {
        private readonly string _dbPath;

        public ServerRepository(IDatabasePathProvider dbPathProvider)
        {
            _dbPath = (dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider))).DbPath;
        }

        public async Task TouchLastUsedUtcAsync(string serverUrl, DateTime utcNow, CancellationToken cancellationToken = default)
        {
            var (serverKey, canonicalUrl) = NormalizeServerUrl(serverUrl);
            if (string.IsNullOrWhiteSpace(serverKey))
                return;

            await EnsureSchemaAsync(cancellationToken);

            await using var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            await connection.OpenAsync(cancellationToken);

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
INSERT INTO servers (
    server_key,
    display_name,
    base_url,
    enabled,
    sort_order,
    is_builtin,
    created_utc,
    updated_utc,
    last_used_utc
)
VALUES (
    $server_key,
    COALESCE(NULLIF($display_name, ''), $server_key),
    $base_url,
    1,
    0,
    0,
    $last_used_utc,
    $last_used_utc,
    $last_used_utc
)
ON CONFLICT(server_key)
DO UPDATE SET
    base_url = excluded.base_url,
    last_used_utc = excluded.last_used_utc,
    updated_utc = excluded.updated_utc;";
            cmd.Parameters.AddWithValue("$server_key", serverKey);
            cmd.Parameters.AddWithValue("$display_name", string.Empty);
            cmd.Parameters.AddWithValue("$base_url", canonicalUrl);
            cmd.Parameters.AddWithValue("$last_used_utc", utcNow.ToUniversalTime().ToString("O"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            await connection.OpenAsync(cancellationToken);

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
PRAGMA foreign_keys=ON;
PRAGMA busy_timeout=5000;

CREATE TABLE IF NOT EXISTS servers (
    server_key TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    base_url TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    sort_order INTEGER NOT NULL DEFAULT 0,
    is_builtin INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    last_used_utc TEXT NULL
);";

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            // Upgrade older installs that had a minimal servers table.
            await EnsureColumnExistsAsync(connection, "servers", "display_name", "TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnExistsAsync(connection, "servers", "enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnExistsAsync(connection, "servers", "sort_order", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnExistsAsync(connection, "servers", "is_builtin", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnExistsAsync(connection, "servers", "created_utc", "TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnExistsAsync(connection, "servers", "updated_utc", "TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnExistsAsync(connection, "servers", "last_used_utc", "TEXT NULL", cancellationToken);
        }

        private static async Task EnsureColumnExistsAsync(SqliteConnection conn, string tableName, string columnName, string columnDefinition, CancellationToken ct)
        {
            await using var pragma = conn.CreateCommand();
            pragma.CommandText = $"PRAGMA table_info({tableName});";

            await using var reader = await pragma.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(1);
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
            await alter.ExecuteNonQueryAsync(ct);
        }

        private static (string ServerKey, string CanonicalUrl) NormalizeServerUrl(string serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                return (string.Empty, string.Empty);

            if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri))
                return (string.Empty, string.Empty);

            // Canonical form: scheme://host[:port]
            var builder = new UriBuilder(uri.Scheme, uri.Host)
            {
                Port = uri.IsDefaultPort ? -1 : uri.Port
            };

            var canonical = builder.Uri.ToString().TrimEnd('/');

            // Include the default port explicitly in the key so we do not collide keys.
            // This matches the behavior used by the HistoryLookupsCacheService.
            var port = uri.IsDefaultPort ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : uri.Port;
            var key = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{port}";

            return (key, canonical);
        }
    }
}
