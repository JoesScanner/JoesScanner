using System.Text.Json;
using JoesScanner.Models;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

public sealed partial class DurableLiveQueue
{
    private const string ReadyPending = """
        q.disposition = 0 AND q.audio_ready = 1 AND q.arrival_sequence >= s.playback_floor
        """;

    internal Task<Selection[]> ReadReadyWindowAsync(IReadOnlyList<Run> runs, Func<Run, long> recoveryFloor,
        int limit, CancellationToken token, long after = 0, IReadOnlyList<long>? visibleReplays = null,
        LiveAutomaticAudioFilter? automatic = null) =>
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
                var source = AutomaticAudioClause(db, run, automatic);
                using var query = EligibleCommand(db, run, recoveryFloor(run), $"""
                    SELECT q.recorded_utc, q.payload, q.origin, q.protected, q.account_scope, q.first_owner, q.arrival_sequence,
                           q.recording_id, q.disposition, q.audio_revision, q.audio_recovered, coalesce(q.recording_id = s.resume_id, 0)
                    FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                    WHERE {EligibleRecording} AND q.arrival_sequence > $after AND (({ReadyPending}) {replayClause}) {source}
                    ORDER BY q.arrival_sequence LIMIT $limit;
                    """, ("$after", after), ("$limit", limit));
                AddAutomaticAudioParameters(query, automatic);
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
        CancellationToken token, LiveAutomaticAudioFilter? automatic = null) =>
        WithConnectionAsync(token, db =>
        {
            using var transaction = db.BeginTransaction(deferred: true);
            long count = 0;
            foreach (var run in runs)
            {
                token.ThrowIfCancellationRequested();
                var source = AutomaticAudioClause(db, run, automatic);
                using var query = EligibleCommand(db, run, recoveryFloor(run), $"""
                    SELECT count(*) FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                    WHERE {EligibleRecording} AND {ReadyPending} AND q.arrival_sequence <> $active {source};
                    """, ("$active", active ?? 0));
                AddAutomaticAudioParameters(query, automatic);
                query.Transaction = transaction;
                count += (long)query.ExecuteScalar()!;
            }
            transaction.Commit();
            return (int)Math.Min(int.MaxValue, count);
        });

    internal Task<long> ReadAutomaticAudioEdgeAsync(CancellationToken token) =>
        InArrivalOrderAsync(() => WithConnectionAsync(token, db =>
        {
            using var edge = db.CreateCommand();
            edge.CommandText = "SELECT coalesce((SELECT seq FROM sqlite_sequence WHERE name = 'live_queue_recordings'), 0);";
            return (long)edge.ExecuteScalar()!;
        }), token);

    private static string AutomaticAudioClause(SqliteConnection db, Run run, LiveAutomaticAudioFilter? filter)
    {
        if (filter == null)
            return string.Empty;

        const string edge = """
            AND q.arrival_sequence > $audioEdge
            AND ($audioRecoveredSince = 0 OR q.origin = $live OR q.recorded_utc >= $audioRecoveredSince)
            """;
        if (filter.FavoriteEdges == null)
            return edge;

        // Match the scoped Favorites identity before applying the scheduler's
        // lookahead limit. An offscreen candidate may be the first favorite.
        db.CreateFunction<string?, long, bool>("js_live_favorite", (payload, arrival) =>
        {
            if (payload == null)
                return false;
            try
            {
                return filter.IncludesFavorite(run.ServerId, JsonSerializer.Deserialize<LiveCallSnapshot>(payload)?.Metadata, arrival);
            }
            catch (JsonException) { return false; }
        }, isDeterministic: true);
        return edge + " AND js_live_favorite(q.payload, q.arrival_sequence)";
    }

    private static void AddAutomaticAudioParameters(SqliteCommand query, LiveAutomaticAudioFilter? filter)
    {
        if (filter == null)
            return;
        query.Parameters.AddWithValue("$audioEdge", filter.AfterArrival);
        query.Parameters.AddWithValue("$audioRecoveredSince", filter.RecoveredSinceUtcTicks);
    }

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
