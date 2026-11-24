using Microsoft.Data.Sqlite;
using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal sealed record SavedScannerConnection(Guid Id, string Endpoint, string Name, int? DirectoryId, bool Enabled,
    bool CredentialsMigrated = true, string? LegacyFilterKey = null)
{
    internal string FilterKey => LegacyFilterKey ?? Id.ToString("N");
    internal bool Managed => DirectoryId.HasValue || HostedServerRules.IsProvidedDefaultServerUrl(Endpoint);
}

public sealed class ScannerConnectionStore(IDatabasePathProvider paths)
{
    internal IReadOnlyList<SavedScannerConnection> Load(string? legacyEndpoint)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        ImportLegacySelection(db, transaction, legacyEndpoint);
        var connections = ReadConnections(db, transaction);
        transaction.Commit();
        return connections;
    }

    internal SavedScannerConnection Save(string endpoint, string? name, int? directoryId, Guid? editId,
        Action<SavedScannerConnection>? saveCredentials = null)
    {
        var safe = Normalize(endpoint) ?? throw new ArgumentException("A valid recorder URL is required.", nameof(endpoint));
        using var db = Open();
        var connections = ReadConnections(db, null);
        var byEndpoint = connections.FirstOrDefault(connection => connection.Endpoint == safe);
        var byDirectory = directoryId.HasValue
            ? connections.FirstOrDefault(connection => connection.DirectoryId == directoryId)
            : null;
        var edited = editId.HasValue ? connections.FirstOrDefault(connection => connection.Id == editId) : null;

        // A directory selection associates the local connection with the directory ID.
        // A manual address edit applies only to a manual connection.
        var editable = directoryId == null && edited?.Managed == false ? edited : null;
        if (editable != null && byEndpoint != null && editable.Id != byEndpoint.Id)
            throw new InvalidOperationException("This endpoint is already configured as another recorder.");

        var existing = byDirectory ?? byEndpoint ?? editable;
        var displayName = string.IsNullOrWhiteSpace(name) ? new Uri(safe).Authority : name.Trim();
        var saved = new SavedScannerConnection(existing?.Id ?? Guid.NewGuid(), safe, displayName,
            directoryId ?? existing?.DirectoryId, existing?.Enabled ?? true, LegacyFilterKey: existing?.LegacyFilterKey);
        saveCredentials?.Invoke(saved);
        using var transaction = db.BeginTransaction();
        WriteConnection(db, transaction, saved);
        transaction.Commit();
        return saved;
    }

    internal void MarkCredentialsMigrated(Guid id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE scanner_connections SET credentials_migrated = 1 WHERE server_id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();
    }

    internal void SetEnabled(Guid id, bool enabled)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE scanner_connections SET enabled = $enabled WHERE server_id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.Parameters.AddWithValue("$enabled", enabled);
        command.ExecuteNonQuery();
    }

    internal static string? Normalize(string? endpoint) =>
        RecordingStorageReference.ServerKey(SavedConnectionUrl.SafeDisplay(endpoint));

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DbPath }.ToString());
        try
        {
            db.Open();
            DbBootstrapper.EnsureInitialized(db);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static void ImportLegacySelection(SqliteConnection db, SqliteTransaction transaction, string? legacyEndpoint)
    {
        if (Normalize(legacyEndpoint) is not { } endpoint)
            return;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM scanner_connections);";
        if (command.ExecuteScalar() is 0L)
            WriteConnection(db, transaction, new(Guid.NewGuid(), endpoint, new Uri(endpoint).Authority, null, true,
                CredentialsMigrated: false, LegacyFilterKey: ServerKeyHelper.Normalize(endpoint)));
    }

    private static List<SavedScannerConnection> ReadConnections(SqliteConnection db, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT server_id, endpoint, display_name, directory_id, enabled, credentials_migrated, legacy_filter_key
            FROM scanner_connections ORDER BY rowid;
            """;
        using var reader = command.ExecuteReader();
        var connections = new List<SavedScannerConnection>();
        while (reader.Read())
        {
            if (Guid.TryParseExact(reader.GetString(0), "N", out var id) && Normalize(reader.GetString(1)) is { } endpoint)
                connections.Add(new(id, endpoint, reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return connections;
    }

    private static void WriteConnection(SqliteConnection db, SqliteTransaction transaction, SavedScannerConnection saved)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scanner_connections(server_id, endpoint, display_name, directory_id, enabled, credentials_migrated, legacy_filter_key)
            VALUES ($id, $endpoint, $name, $directory, $enabled, $migrated, $filterKey)
            ON CONFLICT(server_id) DO UPDATE SET
                endpoint = excluded.endpoint, display_name = excluded.display_name, directory_id = excluded.directory_id,
                credentials_migrated = excluded.credentials_migrated;
            """;
        command.Parameters.AddWithValue("$id", saved.Id.ToString("N"));
        command.Parameters.AddWithValue("$endpoint", saved.Endpoint);
        command.Parameters.AddWithValue("$name", saved.Name);
        command.Parameters.AddWithValue("$directory", (object?)saved.DirectoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", saved.Enabled);
        command.Parameters.AddWithValue("$migrated", saved.CredentialsMigrated);
        command.Parameters.AddWithValue("$filterKey", (object?)saved.LegacyFilterKey ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
