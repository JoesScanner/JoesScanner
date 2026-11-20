using System.Text.Json;
using JoesScanner.Models;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

// This journal contains recordings observed by Live. Its high-water observation
// can identify a boundary, but is not proof that a server-side gap was reconciled.
public sealed class DurableLiveQueue(IDatabasePathProvider paths)
{
    private readonly SemaphoreSlim _ownershipWrites = new(1);
    private readonly Dictionary<(string Server, string Account), (long Owner, bool Retired)> _ownershipRequests = [];
    internal sealed record Run(string Server, long Owner, int? VerifiedTier, bool Protected = false, string? Account = null)
    {
        internal string AccountScope => !Protected ? string.Empty : !string.IsNullOrWhiteSpace(Account) && Account != ":legacy"
            ? Account : throw new InvalidOperationException("A protected Live queue requires a known account.");

        internal bool Owns(RecordingAccessContext? access) => Protected
            ? access is { Protected: true, Account: not null } && access.Account == AccountScope
            : access?.Protected != true;
    }
    internal readonly record struct Position(string Id, long RecordedUtc);
    internal sealed record JumpResult(Recording Target, bool PlayTarget);

    internal sealed record Recording(string Id, long RecordedUtc, LiveCallSnapshot? Snapshot, RecordingAccessContext Access, long Owner)
    {
        internal Position Order => new(Id, RecordedUtc);

        internal CallItem ToCallItem(string server)
        {
            var state = new LiveCallState(server, Id);
            if (Snapshot is { } observed)
                state.Observe(observed, LiveCallState.NextVersion());
            var timestamp = new DateTimeOffset(RecordedUtc, TimeSpan.Zero);
            var metadata = state.Read().Metadata with { Timestamp = timestamp.LocalDateTime };
            return new CallItem
            {
                BackendId = Id, LiveState = state, RecordingTimeUtc = timestamp,
                RecordingServer = server, RecordingAccess = Access, LivePlaybackOwner = Owner,
                Timestamp = timestamp.LocalDateTime, CardMetadata = metadata,
                Talkgroup = metadata.Talkgroup ?? string.Empty, Source = metadata.Radio ?? string.Empty,
                Site = metadata.Site ?? string.Empty, VoiceReceiver = metadata.Receiver ?? string.Empty,
                CallDurationSeconds = metadata.DurationSeconds ?? 0,
                Transcription = Snapshot?.Transcription ?? string.Empty, AudioUrl = Snapshot?.AudioUrl ?? string.Empty
            };
        }
    }

    // Keep the base path: installations on the same origin can serve different
    // recorders. Query strings and embedded credentials are never stored as keys.
    internal static string? ServerKey(string url) => RecordingStorageReference.ServerKey(url);

    internal async Task OpenAsync(Run run, DateTimeOffset now, CancellationToken token, bool explicitlyStopped = false)
    {
        token.ThrowIfCancellationRequested();
        var scope = (run.Server, run.AccountScope);
        lock (_ownershipRequests)
        {
            if (_ownershipRequests.TryGetValue(scope, out var latest) &&
                (latest.Owner > run.Owner || latest.Owner == run.Owner && latest.Retired))
                throw new InvalidOperationException("This Live queue run has already been replaced or retired.");
            _ownershipRequests[scope] = (run.Owner, false);
        }

        await _ownershipWrites.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RetryBusyWriteAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                lock (_ownershipRequests)
                {
                    if (_ownershipRequests[scope] != (run.Owner, false))
                        throw new InvalidOperationException("This Live queue run has already been replaced or retired.");
                }
                return ExecuteAsync(run, """
            INSERT INTO live_queue_servers(server, account_scope, owner, accept_after)
            VALUES ($server, $account, $owner, CASE WHEN $stopped = 1 THEN $now ELSE 0 END)
            ON CONFLICT(server, account_scope) DO UPDATE SET owner = excluded.owner,
                accept_after = CASE WHEN stopped = 1 OR $stopped = 1 THEN $now ELSE accept_after END,
                resume_id = CASE WHEN stopped = 1 OR $stopped = 1 THEN NULL ELSE resume_id END, stopped = 0
            """, token, ("$now", now.UtcTicks), ("$stopped", explicitlyStopped ? 1 : 0));
            }, token).ConfigureAwait(false);
        }
        finally { _ownershipWrites.Release(); }
    }

    internal async Task StopAsync(Run run)
    {
        var scope = (run.Server, run.AccountScope);
        lock (_ownershipRequests)
        {
            if (!_ownershipRequests.TryGetValue(scope, out var latest) || latest.Owner <= run.Owner)
                _ownershipRequests[scope] = (run.Owner, true);
        }
        await _ownershipWrites.WaitAsync().ConfigureAwait(false);
        try
        {
            await RetryBusyWriteAsync(() => ExecuteAsync(run,
                "UPDATE live_queue_servers SET stopped = 1, resume_id = NULL WHERE server = $server AND account_scope = $account AND owner = $owner",
                CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _ownershipWrites.Release(); }
    }

    internal static async Task<bool> PersistObservationAsync(Func<Task<bool>> write, Action<Exception> report, CancellationToken token,
        TimeProvider? clock = null)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        var reported = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await write().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                token.ThrowIfCancellationRequested();
                if (!reported)
                {
                    report(ex);
                    reported = true;
                }
                await Task.Delay(delay, clock ?? TimeProvider.System, token).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 8000));
            }
        }
    }

    private static async Task RetryBusyWriteAsync(Func<Task> write, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await write().ConfigureAwait(false);
                return;
            }
            catch (SqliteException ex) when (attempt < 2 && ex.SqliteErrorCode is 5 or 6)
            {
                await Task.Delay(150 * (attempt + 1), token).ConfigureAwait(false);
            }
        }
    }

    internal async Task<bool> ObserveAsync(Run run, string id, DateTimeOffset? time, LiveCallSnapshot incoming, CancellationToken token,
        RecordingOrigin origin = RecordingOrigin.Unknown, bool existingOnly = false)
    {
        // Signed URLs may contain credentials. Keep those on the in-memory card.
        var url = DurableAudioUrl(incoming.AudioUrl);
        return await WithConnectionAsync(token, db =>
        {
            using var transaction = db.BeginTransaction();
            using var lookup = Command(db, run, """
                SELECT recorded_utc, payload, origin, protected, account_scope, first_owner FROM live_queue_recordings
                WHERE server = $server AND account_scope = $account AND recording_id = $id AND disposition = 0
                """, ("$id", id));
            lookup.Transaction = transaction;
            Recording? previous;
            using (var reader = lookup.ExecuteReader())
                previous = reader.Read() ? Read(reader, id) : null;

            if (existingOnly && previous == null)
                return false;
            var stamp = previous?.RecordedUtc ?? time?.UtcTicks;
            if (stamp == null)
                return false;

            var merged = new LiveCallState(run.Server, id);
            if (previous?.Snapshot is { } savedSnapshot)
                merged.Observe(savedSnapshot, 1);
            merged.Observe(incoming with { AudioUrl = url }, 2);
            var snapshot = merged.Read();
            if (url == null && !string.IsNullOrWhiteSpace(incoming.AudioUrl))
                snapshot = snapshot with { AudioUrl = null };
            if (previous?.Snapshot == snapshot)
                return false;

            using var save = Command(db, run, """
                INSERT INTO live_queue_recordings(server, recording_id, recorded_utc, first_owner, payload, audio_ready,
                    origin, protected, account_scope)
                SELECT $server, $id, $time, $owner, $payload, $ready, $origin, $protected, $account FROM live_queue_servers
                WHERE server = $server AND account_scope = $account AND owner = $owner AND stopped = 0
                  AND ($existing = 1 OR $time >= accept_after)
                  AND ($existing = 1 OR $origin = $live OR jump_utc IS NULL OR ($time, $id) >= (jump_utc, jump_id))
                ON CONFLICT(server, account_scope, recording_id) DO UPDATE SET
                  payload = excluded.payload, audio_ready = excluded.audio_ready
                WHERE disposition = 0
                """, ("$id", id), ("$time", stamp.Value), ("$payload", JsonSerializer.Serialize(snapshot)),
                ("$ready", snapshot.AudioUrl == null ? 0 : 1), ("$existing", previous == null ? 0 : 1),
                ("$live", (int)RecordingOrigin.Live),
                ("$origin", (int)origin), ("$protected", run.Protected ? 1 : 0));
            save.Transaction = transaction;
            token.ThrowIfCancellationRequested();
            var saved = save.ExecuteNonQuery() != 0;
            transaction.Commit();
            return saved;
        }).ConfigureAwait(false);
    }

    private const string EligibleRecording = """
        q.server = $server AND q.account_scope = $account AND s.owner = $owner AND s.stopped = 0
          AND ((q.first_owner = $owner AND q.origin = $live) OR q.recorded_utc >= $floor)
        """;

    internal Task<Recording?> NextAsync(Run run, long historicalFloor, CancellationToken token) =>
        WithConnectionAsync(token, db =>
        {
            using var command = EligibleCommand(db, run, historicalFloor, $"""
                SELECT q.recorded_utc, q.payload, q.origin, q.protected, q.account_scope, q.first_owner, q.recording_id
                FROM live_queue_recordings q
                JOIN live_queue_servers s USING (server, account_scope)
                WHERE {EligibleRecording} AND q.disposition = 0
                ORDER BY q.recorded_utc, q.recording_id LIMIT 1
                """);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader, reader.GetString(6)) : null;
        });

    // Transport can replay completed or skipped recordings. Autoplay only reads pending rows.
    internal Task<Recording?> FindTransportTargetAsync(Run run, long historicalFloor, Position? anchor,
        bool older, CancellationToken token) =>
        WithConnectionAsync(token, db => FindTransportTarget(db, run, historicalFloor, anchor, older));

    internal Task<bool> HasNewerPendingAsync(Run run, long historicalFloor, Position anchor, CancellationToken token) =>
        WithConnectionAsync(token, db =>
        {
            using var command = EligibleCommand(db, run, historicalFloor, $"""
                SELECT 1 FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                WHERE {EligibleRecording} AND q.disposition = 0 AND (q.recorded_utc, q.recording_id) > ($time, $id)
                LIMIT 1
                """, ("$time", anchor.RecordedUtc), ("$id", anchor.Id));
            return command.ExecuteScalar() != null;
        });

    private static Recording? FindTransportTarget(SqliteConnection db, Run run, long historicalFloor, Position? anchor,
        bool older, SqliteTransaction? transaction = null)
    {
        using var command = CreateTransportTargetCommand(db, run, historicalFloor, anchor, older);
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader, reader.GetString(6)) : null;
    }

    internal static SqliteCommand CreateTransportTargetCommand(SqliteConnection db, Run run, long historicalFloor,
        Position? anchor, bool older)
    {
        var comparison = older ? "<" : ">";
        var sort = older ? "DESC" : "ASC";
        // A nullable-parameter OR hides the timestamp/ID range from SQLite's index planner.
        var range = anchor is null ? "" : $"""
            AND (q.recorded_utc, q.recording_id) {comparison}
                (COALESCE((SELECT recorded_utc FROM live_queue_recordings
                    WHERE server = $server AND account_scope = $account AND recording_id = $id), $time), $id)
            """;
        var sql = $"""
            SELECT q.recorded_utc, q.payload, q.origin, q.protected, q.account_scope, q.first_owner, q.recording_id
            FROM live_queue_recordings q
            JOIN live_queue_servers s USING (server, account_scope)
            WHERE {EligibleRecording} {range}
            ORDER BY q.recorded_utc {sort}, q.recording_id {sort} LIMIT 1
            """;
        return anchor is { } position
            ? EligibleCommand(db, run, historicalFloor, sql, ("$id", position.Id), ("$time", position.RecordedUtc))
            : EligibleCommand(db, run, historicalFloor, sql);
    }

    internal Task<(RecordingAccessContext Access, long Owner, DateTimeOffset RecordedAt)?> GetAccessAsync(
        string server, string id, string? account, CancellationToken token) =>
        WithConnectionAsync<(RecordingAccessContext, long, DateTimeOffset)?>(token, db =>
        {
            using var command = Command(db, new Run(server, 0, null, Protected: account != null, Account: account), """
                SELECT origin, protected, account_scope, first_owner, recorded_utc FROM live_queue_recordings
                WHERE server = $server AND account_scope = $account AND recording_id = $id
                """, ("$id", id));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (ReadAccess(reader, 0), reader.GetInt64(3), new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero)) : null;
        });

    internal Task MarkPlayingAsync(Run run, string id, CancellationToken token) =>
        ExecuteAsync(run, """
            UPDATE live_queue_servers SET resume_id = $id
            WHERE server = $server AND account_scope = $account AND owner = $owner AND stopped = 0
              AND EXISTS (SELECT 1 FROM live_queue_recordings
                  WHERE server = $server AND account_scope = $account AND recording_id = $id AND disposition = 0)
            """, token, ("$id", id));

    internal Task<bool> IsPendingAsync(Run run, string id, long historicalFloor, CancellationToken token) =>
        WithConnectionAsync(token, db =>
        {
            using var command = EligibleCommand(db, run, historicalFloor, $"""
                SELECT 1 FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                WHERE {EligibleRecording} AND q.recording_id = $id AND q.disposition = 0
                """, ("$id", id));
            return command.ExecuteScalar() != null;
        });

    internal static long HistoricalFloor(bool protectedServer, int? verifiedTier, bool authorizedNow, int currentTier,
        DateTimeOffset now) =>
        !protectedServer ? 0 :
        !authorizedNow || verifiedTier is not (1 or 2) ? long.MaxValue :
        Math.Min(verifiedTier.Value, currentTier) switch
        {
            1 => now.AddHours(-24).UtcTicks,
            2 => 0,
            _ => long.MaxValue
        };

    internal Task MarkAsync(Run run, string id, PlaybackOutcome outcome, CancellationToken token) =>
        !outcome.AdvancesQueue() ? Task.CompletedTask :
            ExecuteAsync(run, """
                UPDATE live_queue_recordings SET disposition = $status, payload = NULL, audio_ready = 0
                WHERE server = $server AND account_scope = $account AND recording_id = $id AND disposition = 0
                  AND EXISTS (SELECT 1 FROM live_queue_servers
                      WHERE server = $server AND account_scope = $account AND owner = $owner AND stopped = 0)
                """, token, ("$id", id), ("$status", outcome == PlaybackOutcome.Completed ? 1 : 2));

    internal Task CommitCompletionAsync(Run run, string id) =>
        RetryBusyWriteAsync(() => MarkAsync(run, id, PlaybackOutcome.Completed, CancellationToken.None), CancellationToken.None);

    internal Task<JumpResult?> JumpToLiveAsync(Run run, long historicalFloor, bool skipSelected, CancellationToken token,
        string? failedId = null) =>
        WithConnectionAsync<JumpResult?>(token, db =>
        {
            // Select and skip within one writer transaction. New arrivals are admitted
            // after it commits, even when the recorder gives them a tied timestamp.
            using var transaction = db.BeginTransaction();
            token.ThrowIfCancellationRequested();
            var newest = FindTransportTarget(db, run, historicalFloor, anchor: null, older: true, transaction);
            token.ThrowIfCancellationRequested();
            if (newest == null)
                return null;
            var playTarget = !skipSelected && newest.Id != failedId;
            using var barrier = Command(db, run, """
                UPDATE live_queue_servers SET
                    jump_utc = CASE WHEN jump_utc IS NULL OR (jump_utc, jump_id) < ($time, $id) THEN $time ELSE jump_utc END,
                    jump_id = CASE WHEN jump_utc IS NULL OR (jump_utc, jump_id) < ($time, $id) THEN $id ELSE jump_id END,
                    resume_id = NULL
                WHERE server = $server AND account_scope = $account AND owner = $owner AND stopped = 0
                """, ("$time", newest.RecordedUtc), ("$id", newest.Id));
            barrier.Transaction = transaction;
            token.ThrowIfCancellationRequested();
            barrier.ExecuteNonQuery();
            using var skip = EligibleCommand(db, run, historicalFloor, $"""
                UPDATE live_queue_recordings AS q SET disposition = 2, payload = NULL, audio_ready = 0
                FROM live_queue_servers s
                WHERE q.server = s.server AND q.account_scope = s.account_scope AND {EligibleRecording} AND q.disposition = 0
                  AND ((q.recorded_utc, q.recording_id) < ($time, $id)
                    OR ($skip = 1 AND (q.recorded_utc, q.recording_id) = ($time, $id)))
                """, ("$time", newest.RecordedUtc), ("$id", newest.Id), ("$skip", playTarget ? 0 : 1));
            skip.Transaction = transaction;
            skip.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new JumpResult(newest, playTarget);
        });

    private static Recording Read(SqliteDataReader reader, string id)
    {
        var snapshot = reader.IsDBNull(1) ? null :
                JsonSerializer.Deserialize<LiveCallSnapshot>(reader.GetString(1))
                ?? throw new InvalidDataException("Live recording snapshot is missing.");
        if (snapshot?.AudioUrl is { } audio)
            snapshot = snapshot with { AudioUrl = DurableAudioUrl(audio) };
        return new(id, reader.GetInt64(0), snapshot, ReadAccess(reader, 2), reader.GetInt64(5));
    }

    private static RecordingAccessContext ReadAccess(SqliteDataReader reader, int offset) =>
        new((RecordingOrigin)reader.GetInt32(offset), reader.GetBoolean(offset + 1),
            reader.GetBoolean(offset + 1) ? reader.GetString(offset + 2) : null);

    internal static string? DurableAudioUrl(string? url) => RecordingStorageReference.ForJournal(url);

    private Task ExecuteAsync(Run run, string sql, CancellationToken token, params (string, object)[] parameters) =>
        WithConnectionAsync(token, db =>
        {
            using var command = Command(db, run, sql, parameters);
            token.ThrowIfCancellationRequested();
            return command.ExecuteNonQuery();
        });

    private static SqliteCommand Command(SqliteConnection database, Run run, string sql, params (string Name, object Value)[] parameters)
    {
        var command = database.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$server", run.Server);
        command.Parameters.AddWithValue("$owner", run.Owner);
        command.Parameters.AddWithValue("$account", run.AccountScope);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static SqliteCommand EligibleCommand(SqliteConnection database, Run run, long historicalFloor, string sql,
        params (string Name, object Value)[] parameters) =>
        Command(database, run, sql, [("$floor", historicalFloor), ("$live", (int)RecordingOrigin.Live), .. parameters]);

    private Task<T> WithConnectionAsync<T>(CancellationToken token, Func<SqliteConnection, T> work) =>
        Task.Run(async () =>
        {
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = paths.DbPath, DefaultTimeout = 2 }.ToString());
            await database.OpenAsync(token).ConfigureAwait(false);
            await DbBootstrapper.EnsureInitializedAsync(database, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return work(database);
        }, token);
}
