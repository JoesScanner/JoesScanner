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
    private readonly object _observationOrder = new();
    private Task _lastObservation = Task.CompletedTask;
    internal sealed record Run(string Server, long Owner, int? VerifiedTier, bool Protected = false, string? Account = null)
    {
        internal string AccountScope => !Protected ? string.Empty : !string.IsNullOrWhiteSpace(Account) && Account != ":legacy"
            ? Account : throw new InvalidOperationException("A protected Live queue requires a known account.");

        internal bool Owns(RecordingAccessContext? access) => Protected
            ? access is { Protected: true, Account: not null } && access.Account == AccountScope
            : access?.Protected != true;
    }
    internal readonly record struct Observation(long? ArrivalSequence, bool Changed, bool IsRecoveryBacklog = false);
    internal sealed record JumpResult(Recording Target, bool PlayTarget);

    internal sealed record Recording(string Id, long ArrivalSequence, long? RecordedUtc, LiveCallSnapshot? Snapshot,
        RecordingAccessContext Access, long Owner)
    {
        internal CallItem ToCallItem(string server)
        {
            var state = new LiveCallState(server, Id);
            if (Snapshot is { } observed)
                state.Observe(observed, LiveCallState.NextVersion());
            DateTimeOffset? timestamp = RecordedUtc is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
            var metadata = state.Read().Metadata with { Timestamp = timestamp?.LocalDateTime };
            return new CallItem
            {
                BackendId = Id, LiveState = state, RecordingTimeUtc = timestamp,
                RecordingServer = server, RecordingAccess = Access, LivePlaybackOwner = Owner, LiveArrivalSequence = ArrivalSequence,
                Timestamp = timestamp?.LocalDateTime ?? default, CardMetadata = metadata,
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
                return ExecuteScopeTransitionAsync(run, """
            INSERT INTO live_queue_servers(server, account_scope, owner, accept_after)
            VALUES ($server, $account, $owner, CASE WHEN $stopped = 1 THEN $now ELSE 0 END)
            ON CONFLICT(server, account_scope) DO UPDATE SET owner = excluded.owner,
                accept_after = CASE WHEN stopped = 1 OR $stopped = 1 THEN $now ELSE accept_after END,
                resume_id = CASE WHEN stopped = 1 OR $stopped = 1 THEN NULL ELSE resume_id END, stopped = 0;
            UPDATE live_queue_recovery SET skip_unseen = 1
            WHERE server = $server AND account_scope = $account AND $stopped = 1;
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
            await RetryBusyWriteAsync(() => ExecuteScopeTransitionAsync(run,
                """
                UPDATE live_queue_servers SET stopped = 1, resume_id = NULL
                WHERE server = $server AND account_scope = $account AND owner = $owner;
                UPDATE live_queue_recovery SET skip_unseen = 1
                WHERE server = $server AND account_scope = $account
                  AND EXISTS (SELECT 1 FROM live_queue_servers
                      WHERE server = $server AND account_scope = $account AND owner = $owner AND stopped = 1);
                """,
                CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _ownershipWrites.Release(); }
    }

    private const string ActiveRecoveryScope = """
        EXISTS (SELECT 1 FROM live_queue_servers s
            WHERE s.server = $server AND s.account_scope = $account AND s.owner = $owner AND s.stopped = 0)
        """;

    internal Task<LiveStreamRecovery.Window?> ReadRecoveryWindowAsync(Run run, CancellationToken token) =>
        WithConnectionAsync<LiveStreamRecovery.Window?>(token, db =>
        {
            using var command = Command(db, run, $"""
                SELECT since_utc, window_id, revision FROM live_queue_recovery
                WHERE server = $server AND account_scope = $account AND {ActiveRecoveryScope}
                """);
            return ReadRecoveryWindow(command);
        });

    internal Task<LiveStreamRecovery.Window?> RequestRecoveryAsync(Run run, DateTimeOffset since,
        Action<Exception> report, CancellationToken token) => InArrivalOrderAsync(
        () => PersistObservationAsync(() => WithConnectionAsync(token, db =>
        {
            using var command = Command(db, run, $"""
                INSERT INTO live_queue_recovery(server, account_scope, window_id, revision, since_utc)
                SELECT $server, $account, $window, 1, $since WHERE {ActiveRecoveryScope}
                ON CONFLICT(server, account_scope) DO UPDATE SET
                    since_utc = min(since_utc, excluded.since_utc), revision = revision + 1
                RETURNING since_utc, window_id, revision
                """, ("$window", Guid.NewGuid().ToString("N")), ("$since", since.ToUnixTimeSeconds()));
            return ReadRecoveryWindow(command);
        }), report, token), token);

    internal Task<bool> CompleteRecoveryAsync(Run run, LiveStreamRecovery.Window window,
        Action<Exception> report, CancellationToken token) => InArrivalOrderAsync(
        () => PersistObservationAsync(() => WithConnectionAsync(token, db =>
        {
            using var command = Command(db, run, $"""
                DELETE FROM live_queue_recovery WHERE server = $server AND account_scope = $account
                  AND window_id = $window AND revision = $revision AND {ActiveRecoveryScope}
                """, ("$window", window.Id), ("$revision", window.Revision));
            token.ThrowIfCancellationRequested();
            return command.ExecuteNonQuery() != 0;
        }), report, token), token);

    private static LiveStreamRecovery.Window? ReadRecoveryWindow(SqliteCommand command)
    {
        using var rows = command.ExecuteReader();
        return rows.Read()
            ? new(DateTimeOffset.FromUnixTimeSeconds(rows.GetInt64(0)), rows.GetString(1), rows.GetInt64(2))
            : null;
    }

    internal static async Task<T> PersistObservationAsync<T>(Func<Task<T>> write, Action<Exception> report, CancellationToken token,
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
        RecordingOrigin origin = RecordingOrigin.Unknown, bool existingOnly = false) =>
        (await ObserveWithPositionAsync(run, id, time, incoming, token, origin, existingOnly).ConfigureAwait(false)).Changed;

    internal Task<Observation> ObserveWithPositionAsync(Run run, string id, DateTimeOffset? time, LiveCallSnapshot incoming,
        CancellationToken token, RecordingOrigin origin, bool existingOnly, Action<Exception>? report = null,
        TimeProvider? clock = null, string? recoveryWindowId = null) => InArrivalOrderAsync(() =>
        {
            Task<Observation> Write() => WriteObservationAsync(run, id, time, incoming, token, origin, existingOnly, recoveryWindowId);
            return report == null ? Write() : PersistObservationAsync(Write, report, token, clock);
        }, token);

    private async Task<T> InArrivalOrderAsync<T>(Func<Task<T>> work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Reserve the writer's place before any asynchronous work. SQLite assigns
        // the sequence at insert, and later observations cannot overtake a retry.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_observationOrder)
        {
            previous = _lastObservation;
            _lastObservation = done.Task;
        }

        try
        {
            await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await work().ConfigureAwait(false);
        }
        finally { done.SetResult(); }
    }

    private Task<Observation> WriteObservationAsync(Run run, string id, DateTimeOffset? time, LiveCallSnapshot incoming,
        CancellationToken token, RecordingOrigin origin, bool existingOnly, string? recoveryWindowId)
    {
        // Signed URLs may contain credentials. Keep those on the in-memory card.
        var url = DurableAudioUrl(incoming.AudioUrl);
        return WithConnectionAsync<Observation>(token, db =>
        {
            using var transaction = db.BeginTransaction();
            using var lookup = Command(db, run, """
                SELECT q.recorded_utc, q.payload, q.origin, q.protected, q.account_scope, q.first_owner,
                       q.arrival_sequence, q.disposition, q.recovery_backlog
                FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                WHERE q.server = $server AND q.account_scope = $account AND q.recording_id = $id
                  AND s.owner = $owner AND s.stopped = 0
                """, ("$id", id));
            lookup.Transaction = transaction;
            Recording? previous;
            using (var reader = lookup.ExecuteReader())
            {
                if (!reader.Read())
                    previous = null;
                else if (reader.GetInt32(7) != 0)
                    return new(reader.GetInt64(6), false, reader.GetBoolean(8));
                else
                    previous = Read(reader, id);
            }

            if (existingOnly && previous == null)
                return default;
            var stamp = previous?.RecordedUtc ?? time?.UtcTicks;
            var isRecoveryBacklog = false;
            if (previous == null && recoveryWindowId != null)
            {
                using var recovery = Command(db, run, """
                    SELECT r.skip_unseen OR (s.accept_after <> 0 AND coalesce($time, 0) < s.accept_after)
                    FROM live_queue_recovery r JOIN live_queue_servers s USING (server, account_scope)
                    WHERE r.server = $server AND r.account_scope = $account AND r.window_id = $window
                      AND s.owner = $owner AND s.stopped = 0
                    """, ("$window", recoveryWindowId), ("$time", (object?)stamp ?? DBNull.Value));
                recovery.Transaction = transaction;
                if (recovery.ExecuteScalar() is not long skip)
                    return default;
                isRecoveryBacklog = skip != 0;
            }

            var merged = new LiveCallState(run.Server, id);
            if (previous?.Snapshot is { } savedSnapshot)
                merged.Observe(savedSnapshot, 1);
            merged.Observe(incoming with { AudioUrl = url }, 2);
            var snapshot = merged.Read();
            if (url == null && !string.IsNullOrWhiteSpace(incoming.AudioUrl))
                snapshot = snapshot with { AudioUrl = null };
            if (previous != null && previous.Snapshot == snapshot && previous.RecordedUtc == stamp)
                return new(previous.ArrivalSequence, false);

            using var save = Command(db, run, previous == null ? """
                INSERT INTO live_queue_recordings(server, recording_id, recorded_utc, first_owner, payload, audio_ready,
                    origin, protected, account_scope, disposition, recovery_backlog)
                SELECT $server, $id, $time, $owner, $payload, $ready, $origin, $protected, $account,
                    CASE WHEN $backlog = 1 THEN 2 ELSE 0 END, $backlog FROM live_queue_servers
                WHERE server = $server AND account_scope = $account AND owner = $owner AND stopped = 0
                  AND ($backlog = 1 OR $origin = $live OR accept_after = 0 OR $time >= accept_after)
                RETURNING arrival_sequence
                """ : """
                UPDATE live_queue_recordings SET payload = $payload, audio_ready = $ready, recorded_utc = $time
                WHERE server = $server AND account_scope = $account AND recording_id = $id
                RETURNING arrival_sequence
                """, ("$id", id), ("$time", (object?)stamp ?? DBNull.Value), ("$payload", JsonSerializer.Serialize(snapshot)),
                ("$ready", isRecoveryBacklog || snapshot.AudioUrl == null ? 0 : 1), ("$backlog", isRecoveryBacklog ? 1 : 0),
                ("$live", (int)RecordingOrigin.Live),
                ("$origin", (int)origin), ("$protected", run.Protected ? 1 : 0));
            save.Transaction = transaction;
            token.ThrowIfCancellationRequested();
            var sequence = save.ExecuteScalar() as long?;
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(sequence, sequence != null, isRecoveryBacklog && sequence != null);
        });
    }

    private const string EligibleRecording = """
        q.server = $server AND q.account_scope = $account AND s.owner = $owner AND s.stopped = 0
          AND ((q.first_owner = $owner AND q.origin = $live) OR $floor = 0 OR q.recorded_utc BETWEEN $floor AND $ceiling)
        """;

    internal Task<Recording?> NextAsync(Run run, long historicalFloor, CancellationToken token) =>
        WithConnectionAsync(token, db =>
        {
            using var command = EligibleCommand(db, run, historicalFloor, $"""
                SELECT q.recorded_utc, q.payload, q.origin, q.protected, q.account_scope, q.first_owner, q.arrival_sequence, q.recording_id
                FROM live_queue_recordings q
                JOIN live_queue_servers s USING (server, account_scope)
                WHERE {EligibleRecording} AND q.disposition = 0
                ORDER BY q.arrival_sequence LIMIT 1
                """);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader, reader.GetString(7)) : null;
        });

    // Transport can replay completed or skipped recordings. Autoplay only reads pending rows.
    internal Task<Recording?> FindTransportTargetAsync(Run run, long historicalFloor, long? anchor,
        bool older, CancellationToken token) =>
        WithConnectionAsync(token, db => FindTransportTarget(db, run, historicalFloor, anchor, older));

    internal Task<bool> HasNewerPendingAsync(Run run, long historicalFloor, long anchor, CancellationToken token) =>
        WithConnectionAsync(token, db =>
        {
            using var command = EligibleCommand(db, run, historicalFloor, $"""
                SELECT 1 FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                WHERE {EligibleRecording} AND q.disposition = 0 AND q.arrival_sequence > $anchor
                LIMIT 1
                """, ("$anchor", anchor));
            return command.ExecuteScalar() != null;
        });

    private static Recording? FindTransportTarget(SqliteConnection db, Run run, long historicalFloor, long? anchor,
        bool older, SqliteTransaction? transaction = null, bool liveEdge = false)
    {
        using var command = CreateTransportTargetCommand(db, run, historicalFloor, anchor, older, liveEdge);
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader, reader.GetString(7)) : null;
    }

    internal static SqliteCommand CreateTransportTargetCommand(SqliteConnection db, Run run, long historicalFloor,
        long? anchor, bool older, bool liveEdge = false)
    {
        var comparison = older ? "<" : ">";
        var sort = older ? "DESC" : "ASC";
        // Keep the range predicate visible to SQLite's index planner.
        var range = anchor is null ? "" : $"AND q.arrival_sequence {comparison} $anchor";
        var excludesRecoveryBacklog = liveEdge ? "AND q.recovery_backlog = 0" : "";
        var sql = $"""
            SELECT q.recorded_utc, q.payload, q.origin, q.protected, q.account_scope, q.first_owner, q.arrival_sequence, q.recording_id
            FROM live_queue_recordings q
            JOIN live_queue_servers s USING (server, account_scope)
            WHERE {EligibleRecording} {range} {excludesRecoveryBacklog}
            ORDER BY q.arrival_sequence {sort} LIMIT 1
            """;
        return anchor is { } position
            ? EligibleCommand(db, run, historicalFloor, sql, ("$anchor", position))
            : EligibleCommand(db, run, historicalFloor, sql);
    }

    internal Task<(RecordingAccessContext Access, long Owner, DateTimeOffset? RecordedAt)?> GetAccessAsync(
        string server, string id, string? account, CancellationToken token) =>
        WithConnectionAsync<(RecordingAccessContext, long, DateTimeOffset?)?>(token, db =>
        {
            using var command = Command(db, new Run(server, 0, null, Protected: account != null, Account: account), """
                SELECT origin, protected, account_scope, first_owner, recorded_utc FROM live_queue_recordings
                WHERE server = $server AND account_scope = $account AND recording_id = $id
                """, ("$id", id));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (ReadAccess(reader, 0), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero)) : null;
        });

    internal Task<long?> GetArrivalSequenceAsync(Run run, string id, CancellationToken token) =>
        WithConnectionAsync(token, db =>
        {
            using var command = Command(db, run, """
                SELECT arrival_sequence FROM live_queue_recordings
                WHERE server = $server AND account_scope = $account AND recording_id = $id
                """, ("$id", id));
            return command.ExecuteScalar() as long?;
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
        string? failedId = null, Func<Task<long>>? prepareBoundary = null,
        Action<SqliteTransaction>? commitPlayback = null) => InArrivalOrderAsync(async () =>
    {
        // Reserve the FIFO boundary before waiting for native completion to commit.
        var floor = prepareBoundary != null ? await prepareBoundary().ConfigureAwait(false) : historicalFloor;
        return await WithConnectionAsync<JumpResult?>(token, db =>
        {
            // Select and skip within one writer transaction. Observations queued
            // after this jump receive positions beyond its edge.
            using var transaction = db.BeginTransaction();
            token.ThrowIfCancellationRequested();
            var newest = FindTransportTarget(db, run, floor, anchor: null, older: true, transaction, liveEdge: true);
            // This affects the disposition of later recovery observations. The
            // window, cutoff, and revision still belong to the HTTP reconciler.
            using var markBacklog = Command(db, run, $"""
                UPDATE live_queue_recovery SET skip_unseen = 1
                WHERE server = $server AND account_scope = $account AND {ActiveRecoveryScope}
                """);
            markBacklog.Transaction = transaction;
            markBacklog.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            if (newest == null)
            {
                commitPlayback?.Invoke(transaction);
                token.ThrowIfCancellationRequested();
                transaction.Commit();
                return null;
            }
            var playTarget = !skipSelected && newest.Id != failedId;
            using var resetResume = Command(db, run, """
                UPDATE live_queue_servers SET resume_id = NULL
                WHERE server = $server AND account_scope = $account AND owner = $owner AND stopped = 0
                """);
            resetResume.Transaction = transaction;
            token.ThrowIfCancellationRequested();
            resetResume.ExecuteNonQuery();
            using var skip = EligibleCommand(db, run, floor, $"""
                UPDATE live_queue_recordings AS q SET disposition = 2, payload = NULL, audio_ready = 0
                FROM live_queue_servers s
                WHERE q.server = s.server AND q.account_scope = s.account_scope AND {EligibleRecording} AND q.disposition = 0
                  AND q.arrival_sequence <= $lastSkipped
                """, ("$lastSkipped", playTarget ? newest.ArrivalSequence - 1 : newest.ArrivalSequence));
            skip.Transaction = transaction;
            skip.ExecuteNonQuery();
            commitPlayback?.Invoke(transaction);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return new JumpResult(newest, playTarget);
        }).ConfigureAwait(false);
    }, token);

    private static Recording Read(SqliteDataReader reader, string id)
    {
        var snapshot = reader.IsDBNull(1) ? null :
                JsonSerializer.Deserialize<LiveCallSnapshot>(reader.GetString(1))
                ?? throw new InvalidDataException("Live recording snapshot is missing.");
        if (snapshot?.AudioUrl is { } audio)
            snapshot = snapshot with { AudioUrl = DurableAudioUrl(audio) };
        return new(id, reader.GetInt64(6), reader.IsDBNull(0) ? null : reader.GetInt64(0), snapshot,
            ReadAccess(reader, 2), reader.GetInt64(5));
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

    private Task ExecuteScopeTransitionAsync(Run run, string sql, CancellationToken token, params (string, object)[] parameters) =>
        WithConnectionAsync(token, db =>
        {
            using var transaction = db.BeginTransaction();
            using var command = Command(db, run, sql, parameters);
            command.Transaction = transaction;
            token.ThrowIfCancellationRequested();
            var changes = command.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return changes;
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
        params (string Name, object Value)[] parameters)
    {
        // A nonzero authorized floor is Standard's rolling 24-hour window.
        var ceiling = historicalFloor < long.MaxValue - TimeSpan.TicksPerDay
            ? historicalFloor + TimeSpan.TicksPerDay : long.MaxValue;
        return Command(database, run, sql, [("$floor", historicalFloor), ("$ceiling", ceiling),
            ("$live", (int)RecordingOrigin.Live), .. parameters]);
    }

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
