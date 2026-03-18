using Microsoft.Data.Sqlite;
using JoesScanner.Helpers;

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
            var canonicalUrl = NormalizeCanonicalUrl(serverUrl);
            var serverKey = ServerKeyHelper.Normalize(serverUrl);
            if (string.IsNullOrWhiteSpace(serverKey) || string.IsNullOrWhiteSpace(canonicalUrl))
                return;

            await using var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await DbBootstrapper.EnsureInitializedAsync(connection, cancellationToken).ConfigureAwait(false);

            await using var cmd = connection.CreateCommand();
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

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string NormalizeCanonicalUrl(string serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                return string.Empty;

            if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri))
                return string.Empty;

            var builder = new UriBuilder(uri.Scheme, uri.Host)
            {
                Port = uri.IsDefaultPort ? -1 : uri.Port
            };

            return builder.Uri.ToString().TrimEnd('/');
        }
    }
}
