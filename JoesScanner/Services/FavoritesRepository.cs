using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

// The outbox is immutable once selected for transmission. A newer local edit stays
// in the draft until the server acknowledges or conflicts with its predecessor.
public sealed class FavoritesRepository
{
    internal const string Device = "device";
    private readonly string _connectionString;
    private readonly object _gate = new();
    private bool _initialized;

    public FavoritesRepository(IDatabasePathProvider paths)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = GetPath(paths.DbPath) }.ToString();
    }

    private static string GetPath(string mainPath) => Path.Combine(Path.GetDirectoryName(mainPath)!, "favorites.db");

    internal static bool PrepareStorage(string mainPath) => DatabaseRecovery.Prepare(GetPath(mainPath),
        EnsureSchema, new[] { "favorite_records", "favorite_server_links" });

    private sealed record Row(FavoriteRecord? Remote = null, FavoriteChange? Outbox = null,
        FavoriteEdit? Draft = null, FavoriteEdit? Conflict = null)
    {
        internal long Revision => Remote?.Revision ?? 0;
        internal FavoriteEdit? Desired => Draft ?? (Outbox is { } sent
            ? new(sent.Value, sent.Deleted, sent.ExpectedRevision) : null);
        internal FavoriteValue? Effective => Desired is { } desired
            ? (desired.Deleted ? null : desired.Value)
            : Remote is { Deleted: false } remote ? remote.Value : null;
    }

    internal FavoritesCollection Read(string owner) => InTransaction(tx =>
    {
        var rows = ReadAll(tx, owner);
        return new FavoritesCollection(rows.Select(pair => pair.Row.Effective).OfType<FavoriteValue>().ToArray(),
            rows.Select(pair => pair.Row.Conflict).OfType<FavoriteEdit>().ToArray(),
            owner != Device && rows.Any(pair => pair.Row.Desired != null));
    });

    internal void Set(string owner, FavoriteValue value, bool deleted = false) => InTransaction(tx =>
    {
        var row = Get(tx, owner, value.Id);
        Write(tx, owner, value.Id, SetDraft(row, value, deleted));
    });

    internal void Resolve(string owner, string id, bool useLocal) => InTransaction(tx =>
    {
        var row = Get(tx, owner, id);
        if (row.Conflict is not { } edit)
            return;
        Write(tx, owner, id, useLocal ? SetDraft(row, edit.Value, edit.Deleted) : row with { Conflict = null });
    });

    internal FavoriteChange[] Prepare(string owner, int limit) => InTransaction(tx =>
    {
        var changes = new List<FavoriteChange>();
        foreach (var (id, row) in ReadAll(tx, owner, "AND (outbox IS NOT NULL OR draft IS NOT NULL)", limit))
        {
            if (row.Outbox is { } submitted)
            {
                changes.Add(submitted);
            }
            else if (row.Draft is { } edit)
            {
                var change = new FavoriteChange(Guid.NewGuid().ToString("N"), edit.BaseRevision, edit.Value, edit.Deleted);
                Write(tx, owner, id, row with { Outbox = change, Draft = null });
                changes.Add(change);
            }
        }
        return changes.ToArray();
    });

    internal void Apply(string owner, FavoriteSyncResponse response) => InTransaction(tx =>
    {
        foreach (var result in response.Results)
        {
            var row = Get(tx, owner, result.Id);
            if (row.Outbox?.MutationId != result.MutationId)
                continue;

            var conflict = row.Desired;
            var draft = row.Draft;
            var remote = Newest(row.Remote, result.Record);
            row = row with { Remote = remote, Outbox = null, Draft = null };

            if (result.Accepted && remote?.MutationId == result.MutationId)
            {
                row = row with
                {
                    Draft = draft != null && !Matches(remote, draft) ? draft with { BaseRevision = remote.Revision } : null
                };
            }
            else if (conflict != null && !Matches(remote, conflict))
            {
                row = row with { Conflict = conflict };
            }
            Write(tx, owner, result.Id, row);
        }

        foreach (var remote in response.Records)
        {
            var row = Get(tx, owner, remote.Value.Id);
            if (row.Revision >= remote.Revision)
                continue;
            row = row with { Remote = remote };
            if (row.Outbox == null && row.Draft is { } draft && draft.BaseRevision < remote.Revision)
                row = row with { Draft = null, Conflict = Matches(remote, draft) ? null : draft };
            Write(tx, owner, remote.Value.Id, row);
        }
    });

    internal IReadOnlyDictionary<Guid, string> ReadLinks(string owner) => InTransaction(tx => ReadLinks(tx, owner));

    internal string EnsureLink(string owner, Guid server) => InTransaction(tx =>
    {
        var links = ReadLinks(tx, owner);
        if (links.TryGetValue(server, out var linked))
            return linked;

        // The device's association is reusable when the user explicitly imports
        // their signed-out Favorites. Cloud data is still partitioned by account.
        var key = owner != Device && ReadLinks(tx, Device).TryGetValue(server, out var local)
            && !links.ContainsValue(local) ? local : "c:" + Guid.NewGuid().ToString("N");
        SaveLink(tx, owner, server, key);
        return key;
    });

    internal bool Link(string owner, Guid server, string key) => InTransaction(tx =>
    {
        if (!key.StartsWith("c:", StringComparison.Ordinal) || !FavoriteValue.IsHex(key.AsSpan(2), 32))
            return false;

        var links = ReadLinks(tx, owner);
        if (links.Any(pair => pair.Key != server && pair.Value == key))
            return false;
        if (links.TryGetValue(server, out var previous) && previous != key)
        {
            // Linking is explicit. Copy any local Favorites to the new association
            // without erasing the old association's records on other devices.
            foreach (var value in ReadAll(tx, owner).Select(pair => pair.Row.Effective).OfType<FavoriteValue>())
                if (value.ServerKey == previous)
                    Import(tx, owner, FavoriteValue.Talkgroup(key, value.Identity!, value.Label, value.GroupId));
        }
        SaveLink(tx, owner, server, key);
        return true;
    });

    internal void ImportDevice(string owner) => InTransaction(tx =>
    {
        if (owner == Device)
            return;

        var localLinks = ReadLinks(tx, Device);
        var accountLinks = ReadLinks(tx, owner);
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (server, key) in localLinks)
        {
            if (accountLinks.TryGetValue(server, out var existing))
            {
                remap[key] = existing;
            }
            else if (!accountLinks.ContainsValue(key))
            {
                SaveLink(tx, owner, server, key);
                accountLinks[server] = key;
            }
        }

        foreach (var value in ReadAll(tx, Device).Select(pair => pair.Row.Effective).OfType<FavoriteValue>())
        {
            if (value.ServerKey is { } source && remap.TryGetValue(source, out var target))
                Import(tx, owner, FavoriteValue.Talkgroup(target, value.Identity!, value.Label, value.GroupId));
            else
                Import(tx, owner, value);
        }
    });

    internal void DeleteServer(string owner, string key) => InTransaction(tx =>
    {
        foreach (var (id, row) in ReadAll(tx, owner))
        {
            var value = row.Effective ?? row.Conflict?.Value;
            if (value?.ServerKey == key)
                Write(tx, owner, id, SetDraft(row, value, deleted: true));
        }
    });

    private static void Import(SqliteTransaction tx, string owner, FavoriteValue value)
    {
        // A known tombstone or conflict is a record too. Import never overwrites it.
        var row = Get(tx, owner, value.Id);
        if (row == new Row())
            Write(tx, owner, value.Id, SetDraft(row, value, deleted: false));
    }

    private static Row SetDraft(Row row, FavoriteValue value, bool deleted)
    {
        if (row.Outbox == null && Matches(row.Remote, new(value, deleted, row.Revision)))
            return row with { Draft = null, Conflict = null };
        return row with { Draft = new(value, deleted, row.Revision), Conflict = null };
    }

    private static bool Matches(FavoriteRecord? remote, FavoriteEdit edit) => edit.Deleted
        ? remote == null || remote.Deleted
        : remote is { Deleted: false } && remote.Value == edit.Value;

    private static FavoriteRecord? Newest(FavoriteRecord? cached, FavoriteRecord? incoming) =>
        incoming != null && (cached == null || incoming.Revision >= cached.Revision) ? incoming : cached;

    private static Dictionary<Guid, string> ReadLinks(SqliteTransaction tx, string owner)
    {
        using var command = Command(tx, owner,
            "SELECT server_id, server_key FROM favorite_server_links WHERE owner = $owner;");
        using var read = command.ExecuteReader();
        var links = new Dictionary<Guid, string>();
        while (read.Read())
            links.Add(Guid.ParseExact(read.GetString(0), "N"), read.GetString(1));
        return links;
    }

    private static void SaveLink(SqliteTransaction tx, string owner, Guid server, string key)
    {
        using var command = Command(tx, owner, """
            INSERT INTO favorite_server_links (owner, server_id, server_key) VALUES ($owner, $server, $key)
            ON CONFLICT (owner, server_id) DO UPDATE SET server_key = excluded.server_key;
            """);
        command.Parameters.AddWithValue("$server", server.ToString("N"));
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static List<(string Id, Row Row)> ReadAll(SqliteTransaction tx, string owner, string filter = "", int limit = -1)
    {
        using var command = Command(tx, owner,
            $"SELECT id, remote, outbox, draft, conflict FROM favorite_records WHERE owner = $owner {filter} ORDER BY id LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", limit);
        using var read = command.ExecuteReader();
        var rows = new List<(string, Row)>();
        while (read.Read())
            rows.Add((read.GetString(0), ReadRow(read)));
        return rows;
    }

    private static Row Get(SqliteTransaction tx, string owner, string id)
    {
        using var command = Command(tx, owner, """
            SELECT id, remote, outbox, draft, conflict FROM favorite_records WHERE owner = $owner AND id = $id;
            """);
        command.Parameters.AddWithValue("$id", id);
        using var read = command.ExecuteReader();
        return read.Read() ? ReadRow(read) : new();
    }

    private static Row ReadRow(IDataRecord read) =>
        new(Read<FavoriteRecord>(read, 1), Read<FavoriteChange>(read, 2), Read<FavoriteEdit>(read, 3), Read<FavoriteEdit>(read, 4));

    private static T? Read<T>(IDataRecord read, int ordinal) where T : class
    {
        if (read.GetValue(ordinal) is not string json)
            return null;
        try { return JsonSerializer.Deserialize<T>(json, FavoritesJson.Options); }
        catch (JsonException) { return null; }
    }

    private static void Write(SqliteTransaction tx, string owner, string id, Row row)
    {
        using var command = Command(tx, owner, """
            INSERT INTO favorite_records (owner, id, remote, outbox, draft, conflict)
            VALUES ($owner, $id, $remote, $outbox, $draft, $conflict)
            ON CONFLICT (owner, id) DO UPDATE SET remote = excluded.remote, outbox = excluded.outbox,
                draft = excluded.draft, conflict = excluded.conflict;
            """);
        command.Parameters.AddWithValue("$id", id);
        AddJson(command, "$remote", row.Remote);
        AddJson(command, "$outbox", row.Outbox);
        AddJson(command, "$draft", row.Draft);
        AddJson(command, "$conflict", row.Conflict);
        command.ExecuteNonQuery();
    }

    private static void AddJson<T>(SqliteCommand command, string key, T? value) where T : class =>
        command.Parameters.AddWithValue(key, value == null ? DBNull.Value : JsonSerializer.Serialize(value, FavoritesJson.Options));

    private static SqliteCommand Command(SqliteTransaction tx, string owner, string sql)
    {
        var command = tx.Connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$owner", owner);
        return command;
    }

    private void InTransaction(Action<SqliteTransaction> action) => InTransaction(tx =>
    {
        action(tx);
        return true;
    });

    private T InTransaction<T>(Func<SqliteTransaction, T> action)
    {
        lock (_gate)
        {
            using var db = new SqliteConnection(_connectionString);
            db.Open();
            if (!_initialized)
            {
                EnsureSchema(db);
                _initialized = true;
            }
            using var tx = db.BeginTransaction();
            var result = action(tx);
            tx.Commit();
            return result;
        }
    }

    private static void EnsureSchema(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        using var schema = db.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS favorite_records (
                owner TEXT NOT NULL, id TEXT NOT NULL, remote TEXT, outbox TEXT, draft TEXT, conflict TEXT,
                PRIMARY KEY (owner, id)
            );
            CREATE TABLE IF NOT EXISTS favorite_server_links (
                owner TEXT NOT NULL, server_id TEXT NOT NULL, server_key TEXT NOT NULL,
                PRIMARY KEY (owner, server_id), UNIQUE (owner, server_key)
            );
            """;
        schema.ExecuteNonQuery();
        schema.CommandText = """
            SELECT owner, id, remote, outbox, draft, conflict FROM favorite_records LIMIT 0;
            SELECT owner, server_id, server_key FROM favorite_server_links LIMIT 0;
            """;
        schema.Prepare();
        transaction.Commit();
    }
}
