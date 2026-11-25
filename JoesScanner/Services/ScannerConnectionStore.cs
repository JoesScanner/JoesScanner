using Microsoft.Data.Sqlite;
using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal enum DirectoryHold { None, Suspended, EndpointChanged }

internal sealed record SavedScannerConnection(Guid Id, string Endpoint, string Name, int? DirectoryId, bool Enabled,
    bool CredentialsMigrated = true, string? LegacyFilterKey = null, string? DirectorySource = null,
    DirectoryHold Hold = DirectoryHold.None, string? DirectoryCandidate = null,
    bool? DirectoryFirewall = null, bool? CandidateFirewall = null)
{
    internal string FilterKey => LegacyFilterKey ?? Id.ToString("N");
    internal bool Managed => DirectoryId.HasValue || HostedServerRules.IsProvidedDefaultServerUrl(Endpoint);
    internal bool DirectoryBlocked => Hold != DirectoryHold.None;
}

public sealed class ScannerConnectionStore(IDatabasePathProvider paths)
{
    internal IReadOnlyList<SavedScannerConnection> Load(string? legacyEndpoint, int? legacyDirectoryId = null, string? legacyName = null)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        ImportLegacySelection(db, transaction, legacyEndpoint, legacyDirectoryId, legacyName);
        var connections = ReadConnections(db, transaction);
        transaction.Commit();
        return connections;
    }

    internal SavedScannerConnection Save(string endpoint, string? name, int? directoryId, Guid? editId,
        Action<SavedScannerConnection>? saveCredentials = null, string? directorySource = null, bool? directoryFirewall = null)
    {
        var safe = Normalize(endpoint) ?? throw new ArgumentException("A valid recorder URL is required.", nameof(endpoint));
        using var db = Open();
        var connections = ReadConnections(db, null);
        var byEndpoint = connections.FirstOrDefault(connection => connection.Endpoint == safe);
        var byDirectory = directoryId.HasValue
            ? connections.FirstOrDefault(connection => connection.DirectoryId == directoryId)
            : null;
        var edited = editId.HasValue ? connections.FirstOrDefault(connection => connection.Id == editId) : null;

        if (editId.HasValue && edited == null)
            throw new InvalidOperationException("This recorder was removed while it was being edited.");
        var existing = edited ?? byDirectory ?? (directoryId == null && byEndpoint?.DirectoryId == null ? byEndpoint : null);
        if (existing?.Managed == true && (existing.Endpoint != safe ||
            directoryId.HasValue && existing.DirectoryId != directoryId))
            throw new InvalidOperationException("Review the approved directory update before changing this recorder's address.");
        if (byEndpoint != null && byEndpoint.Id != existing?.Id)
            throw new InvalidOperationException("This endpoint is already configured as another recorder.");

        var displayName = string.IsNullOrWhiteSpace(name) ? existing?.Name ?? new Uri(safe).Authority : name.Trim();
        var saved = existing == null
            ? new SavedScannerConnection(Guid.NewGuid(), safe, displayName, directoryId, true,
                DirectorySource: directorySource, DirectoryFirewall: directoryFirewall)
            : existing with { Endpoint = safe, Name = displayName, CredentialsMigrated = true };
        saveCredentials?.Invoke(saved);
        using var transaction = db.BeginTransaction();
        if (existing == null)
            WriteConnection(db, transaction, saved);
        else
        {
            using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE scanner_connections
                SET endpoint = $endpoint, display_name = $name, credentials_migrated = 1
                WHERE server_id = $id AND endpoint = $previous AND directory_id IS $directory;
                """;
            update.Parameters.AddWithValue("$endpoint", saved.Endpoint);
            update.Parameters.AddWithValue("$name", saved.Name);
            update.Parameters.AddWithValue("$id", saved.Id.ToString("N"));
            update.Parameters.AddWithValue("$previous", existing.Endpoint);
            update.Parameters.AddWithValue("$directory", (object?)existing.DirectoryId ?? DBNull.Value);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("This recorder changed while it was being edited.");
            RememberOrigin(db, transaction, existing);
            RememberOrigin(db, transaction, saved);
        }
        transaction.Commit();
        return saved;
    }

    internal static void ApplyDirectory(SqliteConnection db, SqliteTransaction transaction, DirectorySnapshot snapshot)
    {
        var profiles = ReadConnections(db, transaction);
        var entries = snapshot.Entries.ToDictionary(entry => entry.DirectoryId);
        foreach (var saved in profiles.Where(saved => saved.Managed))
        {
            var profile = saved;
            if (profile.DirectorySource != null && profile.DirectorySource != snapshot.Source)
                continue;
            // The fixed hosted archive predates directory IDs. Bind its published official entry
            // without treating URL matches for independent recorders as directory membership.
            if (profile.DirectoryId == null)
            {
                var archive = snapshot.Entries.FirstOrDefault(entry => entry.IsOfficial &&
                    Normalize(entry.Url) == profile.Endpoint && profiles.All(other => other.DirectoryId != entry.DirectoryId));
                if (archive == null)
                    continue;
                profile = profile with { DirectoryId = archive.DirectoryId };
            }
            var id = profile.DirectoryId.Value;
            var entry = entries.GetValueOrDefault(id);
            if (snapshot.Suspended?.Contains(id) == true)
            {
                profile = profile with { Hold = DirectoryHold.Suspended, DirectoryCandidate = null, CandidateFirewall = null,
                    DirectorySource = snapshot.Source };
            }
            else if (entry != null && Normalize(entry.Url) is { } endpoint)
            {
                profile = profile with { DirectorySource = snapshot.Source, Name = string.IsNullOrWhiteSpace(entry.Name) ? profile.Name : entry.Name };
                if (profile.Hold == DirectoryHold.Suspended && snapshot.Suspended == null)
                    profile = profile with { DirectoryCandidate = null, CandidateFirewall = null };
                else if (profile.DirectoryBlocked || endpoint != profile.Endpoint)
                {
                    profile = profile with { DirectoryCandidate = endpoint, CandidateFirewall = entry.UsesApiFirewallCredentials,
                        Hold = profile.Hold == DirectoryHold.Suspended ? DirectoryHold.Suspended : DirectoryHold.EndpointChanged };
                }
                else
                    profile = profile with { DirectoryFirewall = entry.UsesApiFirewallCredentials };
            }
            else if (profile.DirectoryCandidate != null)
                profile = profile with { DirectoryCandidate = null, CandidateFirewall = null };

            if (profile != saved)
                WriteConnection(db, transaction, profile);
        }
    }

    internal SavedScannerConnection? ConfirmDirectory(Guid id, string endpoint, string source)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var profiles = ReadConnections(db, transaction);
        var profile = profiles.FirstOrDefault(profile => profile.Id == id);
        if (profile == null || !profile.DirectoryBlocked || profile.DirectoryCandidate != endpoint || profile.DirectorySource != source)
            return null;
        if (profiles.Any(other => other.Id != id && other.Endpoint == endpoint))
            throw new InvalidOperationException("That address is already configured as another recorder.");
        var confirmed = profile with { Endpoint = endpoint, Hold = DirectoryHold.None, DirectoryCandidate = null,
            DirectoryFirewall = profile.CandidateFirewall, CandidateFirewall = null };
        WriteConnection(db, transaction, confirmed);
        transaction.Commit();
        return confirmed;
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

    internal void Move(Guid id, int direction)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction));

        using var db = Open();
        using var transaction = db.BeginTransaction();
        var connections = ReadConnections(db, transaction);
        var previous = connections.FindIndex(profile => profile.Id == id);
        var next = previous + direction;
        if (previous < 0 || next < 0 || next >= connections.Count)
            return;

        (connections[previous], connections[next]) = (connections[next], connections[previous]);
        using var update = db.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE scanner_connections SET sort_order = $order WHERE server_id = $id;";
        var order = update.Parameters.Add("$order", SqliteType.Integer);
        var key = update.Parameters.Add("$id", SqliteType.Text);
        for (var i = 0; i < connections.Count; i++)
        {
            order.Value = i;
            key.Value = connections[i].Id.ToString("N");
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal IReadOnlyList<string> CredentialOrigins(SavedScannerConnection profile)
    {
        using var db = Open();
        using var select = db.CreateCommand();
        select.CommandText = "SELECT origin FROM scanner_connection_origins WHERE server_id = $id;";
        select.Parameters.AddWithValue("$id", profile.Id.ToString("N"));
        using var reader = select.ExecuteReader();
        var origins = new HashSet<string>(StringComparer.Ordinal);
        if (SavedConnectionUrl.Scope(profile.Endpoint) is { } current)
            origins.Add(current);
        while (reader.Read())
            origins.Add(reader.GetString(0));
        return origins.ToArray();
    }

    internal void Remove(Guid id)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        MarkLegacySelectionImported(db, transaction);
        using var delete = db.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM scanner_connections WHERE server_id = $id;";
        delete.Parameters.AddWithValue("$id", id.ToString("N"));
        delete.ExecuteNonQuery();
        transaction.Commit();
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

    private static void ImportLegacySelection(SqliteConnection db, SqliteTransaction transaction,
        string? legacyEndpoint, int? directoryId, string? name)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM app_settings WHERE setting_key = 'scanner_catalog_initialized'),
                   EXISTS (SELECT 1 FROM scanner_connections);
            """;
        bool hasConnections;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetBoolean(0))
                return;
            hasConnections = reader.GetBoolean(1);
        }

        if (Normalize(legacyEndpoint) is { } endpoint)
        {
            if (!hasConnections)
            {
                WriteConnection(db, transaction, new(Guid.NewGuid(), endpoint, name ?? new Uri(endpoint).Authority, directoryId, true,
                    CredentialsMigrated: false, LegacyFilterKey: ServerKeyHelper.Normalize(endpoint)));
            }
            else if (directoryId.HasValue)
            {
                // A legacy selection imported before the Settings UI may only have its URL.
                command.CommandText = """
                    UPDATE scanner_connections SET directory_id = $directory,
                        display_name = CASE WHEN display_name = $authority THEN $name ELSE display_name END
                    WHERE endpoint = $endpoint AND legacy_filter_key IS NOT NULL AND directory_id IS NULL
                        AND NOT EXISTS (SELECT 1 FROM scanner_connections WHERE directory_id = $directory);
                    """;
                var authority = new Uri(endpoint).Authority;
                command.Parameters.AddWithValue("$endpoint", endpoint);
                command.Parameters.AddWithValue("$directory", directoryId.Value);
                command.Parameters.AddWithValue("$name", name ?? authority);
                command.Parameters.AddWithValue("$authority", authority);
                command.ExecuteNonQuery();
            }
        }
        MarkLegacySelectionImported(db, transaction);
    }

    private static void MarkLegacySelectionImported(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO app_settings(setting_key, setting_value, updated_utc)
            VALUES ('scanner_catalog_initialized', 'true', '');
            """;
        command.ExecuteNonQuery();
    }

    private static List<SavedScannerConnection> ReadConnections(SqliteConnection db, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT server_id, endpoint, display_name, directory_id, enabled, credentials_migrated, legacy_filter_key,
                directory_source, directory_hold, directory_candidate, directory_firewall, candidate_firewall
            FROM scanner_connections ORDER BY coalesce(sort_order, rowid), rowid;
            """;
        using var reader = command.ExecuteReader();
        var connections = new List<SavedScannerConnection>();
        while (reader.Read())
        {
            if (Guid.TryParseExact(reader.GetString(0), "N", out var id) && Normalize(reader.GetString(1)) is { } endpoint)
                connections.Add(new(id, endpoint, reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    (DirectoryHold)reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetBoolean(10), reader.IsDBNull(11) ? null : reader.GetBoolean(11)));
        }
        return connections;
    }

    private static void WriteConnection(SqliteConnection db, SqliteTransaction transaction, SavedScannerConnection saved)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scanner_connections(server_id, endpoint, display_name, directory_id, enabled, credentials_migrated, legacy_filter_key, sort_order,
                directory_source, directory_hold, directory_candidate, directory_firewall, candidate_firewall)
            VALUES ($id, $endpoint, $name, $directory, $enabled, $migrated, $filterKey,
                (SELECT coalesce(max(coalesce(sort_order, rowid)), 0) + 1 FROM scanner_connections),
                $directorySource, $directoryHold, $directoryCandidate, $directoryFirewall, $candidateFirewall)
            ON CONFLICT(server_id) DO UPDATE SET
                endpoint = excluded.endpoint, display_name = excluded.display_name, directory_id = excluded.directory_id,
                credentials_migrated = excluded.credentials_migrated, directory_source = excluded.directory_source,
                directory_hold = excluded.directory_hold, directory_candidate = excluded.directory_candidate,
                directory_firewall = excluded.directory_firewall, candidate_firewall = excluded.candidate_firewall;
            """;
        command.Parameters.AddWithValue("$id", saved.Id.ToString("N"));
        command.Parameters.AddWithValue("$endpoint", saved.Endpoint);
        command.Parameters.AddWithValue("$name", saved.Name);
        command.Parameters.AddWithValue("$directory", (object?)saved.DirectoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", saved.Enabled);
        command.Parameters.AddWithValue("$migrated", saved.CredentialsMigrated);
        command.Parameters.AddWithValue("$filterKey", (object?)saved.LegacyFilterKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$directorySource", (object?)saved.DirectorySource ?? DBNull.Value);
        command.Parameters.AddWithValue("$directoryHold", (int)saved.Hold);
        command.Parameters.AddWithValue("$directoryCandidate", (object?)saved.DirectoryCandidate ?? DBNull.Value);
        command.Parameters.AddWithValue("$directoryFirewall", (object?)saved.DirectoryFirewall ?? DBNull.Value);
        command.Parameters.AddWithValue("$candidateFirewall", (object?)saved.CandidateFirewall ?? DBNull.Value);
        command.ExecuteNonQuery();
        RememberOrigin(db, transaction, saved);
    }

    private static void RememberOrigin(SqliteConnection db, SqliteTransaction transaction, SavedScannerConnection profile)
    {
        if (SavedConnectionUrl.Scope(profile.Endpoint) is not { } origin)
            return;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO scanner_connection_origins(server_id, origin) VALUES ($id, $origin);
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("N"));
        command.Parameters.AddWithValue("$origin", origin);
        command.ExecuteNonQuery();
    }
}
