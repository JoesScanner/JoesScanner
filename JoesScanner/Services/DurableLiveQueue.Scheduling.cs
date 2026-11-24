using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

public sealed partial class DurableLiveQueue
{
    private const string ReadyPending = """
        q.disposition = 0 AND q.audio_ready = 1 AND q.arrival_sequence >= s.playback_floor
        """;

    internal Task<Selection[]> ReadReadyWindowAsync(IReadOnlyList<Run> runs, Func<Run, long> recoveryFloor,
        int limit, CancellationToken token, long after = 0, IReadOnlyList<long>? visibleReplays = null) =>
        WithConnectionAsync(token, db =>
        {
            using var transaction = db.BeginTransaction(deferred: true);
            var choices = new List<Selection>();
            var replayClause = visibleReplays is { Count: > 0 }
                ? $"OR (q.disposition = 1 AND q.recovery_backlog = 0 AND q.arrival_sequence IN ({string.Join(", ", Enumerable.Range(0, visibleReplays.Count).Select(i => "$replay" + i))}))"
                : string.Empty;
            foreach (var run in runs)
            {
                token.ThrowIfCancellationRequested();
                using var query = EligibleCommand(db, run, recoveryFloor(run), $"""
                    SELECT q.recorded_utc, q.payload, q.origin, q.protected, q.account_scope, q.first_owner, q.arrival_sequence,
                           q.recording_id, q.disposition, q.audio_revision, q.audio_recovered, coalesce(q.recording_id = s.resume_id, 0)
                    FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                    WHERE {EligibleRecording} AND q.arrival_sequence > $after AND (({ReadyPending}) {replayClause})
                    ORDER BY q.arrival_sequence LIMIT $limit;
                    """, ("$after", after), ("$limit", limit));
                query.Transaction = transaction;
                if (visibleReplays != null)
                    for (var i = 0; i < visibleReplays.Count; i++)
                        query.Parameters.AddWithValue("$replay" + i, visibleReplays[i]);
                using var reader = query.ExecuteReader();
                while (reader.Read())
                    choices.Add(new(run, Read(reader, reader.GetString(7)) with
                    {
                        IsCompletedReplay = reader.GetInt32(8) == 1,
                        AudioRevision = reader.GetInt64(9),
                        AudioRecovered = reader.GetBoolean(10),
                        Resuming = reader.GetBoolean(11)
                    }));
            }
            transaction.Commit();
            return choices.OrderBy(row => row.Row.ArrivalSequence).Take(limit).ToArray();
        });

    internal Task<int> CountReadyAsync(IReadOnlyList<Run> runs, Func<Run, long> recoveryFloor, long? active,
        CancellationToken token) =>
        WithConnectionAsync(token, db =>
        {
            using var transaction = db.BeginTransaction(deferred: true);
            long count = 0;
            foreach (var run in runs)
            {
                token.ThrowIfCancellationRequested();
                using var query = EligibleCommand(db, run, recoveryFloor(run), $"""
                    SELECT count(*) FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                    WHERE {EligibleRecording} AND {ReadyPending} AND q.arrival_sequence <> $active;
                    """, ("$active", active ?? 0));
                query.Transaction = transaction;
                count += (long)query.ExecuteScalar()!;
            }
            transaction.Commit();
            return (int)Math.Min(int.MaxValue, count);
        });

    // A failed attempt cannot overwrite newer audio evidence observed while it unwound.
    // Keep the arrival position for the next genuine audio update.
    internal Task DeferAudioAsync(Run run, string id, long audioRevision, CancellationToken token) =>
        ExecuteAsync(run, $"""
            UPDATE live_queue_recordings SET audio_ready = 0
            WHERE server = $server AND account_scope = $account AND recording_id = $id
              AND disposition = 0 AND audio_revision = $revision AND {ActiveRecoveryScope};
            UPDATE live_queue_servers SET resume_id = NULL
            WHERE server = $server AND account_scope = $account AND owner = $owner AND resume_id = $id;
            """, token, ("$id", id), ("$revision", audioRevision));

    // Persist processing when an automatic-only filter changes, even for
    // offscreen or not-yet-ready calls. Removing that rule won't replay them.
    internal Task ProcessFilteredAsync(IReadOnlyList<Run> runs, Func<Run, long> recoveryFloor,
        Func<Selection, bool> filtered, CancellationToken token) =>
        InArrivalOrderAsync(() => WithConnectionAsync(token, db =>
        {
            using var transaction = db.BeginTransaction();
            foreach (var run in runs)
            {
                using var query = EligibleCommand(db, run, recoveryFloor(run), $"""
                    SELECT q.recorded_utc, q.payload, q.origin, q.protected, q.account_scope,
                           q.first_owner, q.arrival_sequence, q.recording_id
                    FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                    WHERE {EligibleRecording} AND q.disposition = 0 AND q.arrival_sequence >= s.playback_floor;
                    """);
                query.Transaction = transaction;
                var skipped = new List<long>();
                using (var reader = query.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        var candidate = Read(reader, reader.GetString(7));
                        if (filtered(new(run, candidate)))
                            skipped.Add(candidate.ArrivalSequence);
                    }
                }
                using var update = Command(db, run, """
                    UPDATE live_queue_recordings SET disposition = 2, payload = NULL, audio_ready = 0
                    WHERE arrival_sequence = $sequence;
                    """);
                update.Transaction = transaction;
                var sequence = update.Parameters.Add("$sequence", SqliteType.Integer);
                foreach (var item in skipped)
                {
                    token.ThrowIfCancellationRequested();
                    sequence.Value = item;
                    update.ExecuteNonQuery();
                }
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }), token);
}
