namespace JoesScanner.Services;

public sealed partial class DurableLiveQueue
{
    internal readonly record struct LagAdvance(TimeSpan? Lag, long? Floor = null, bool Superseded = false);

    internal Task<LagAdvance> TryAdvanceVerifiedLagAsync(Run run, long historicalFloor,
        LiveAutomaticAudioFilter? automatic, PlaybackLagEvidence evidence, PlaybackLagRecoveryPreference preference,
        Func<Action, bool> commitIfCurrent, CancellationToken token) =>
        InArrivalOrderAsync(() => WithConnectionAsync(token, db =>
        {
            using var transaction = db.BeginTransaction();
            var source = AutomaticAudioClause(db, run, automatic);
            using var read = EligibleCommand(db, run, historicalFloor, $"""
                SELECT q.recording_id, q.recorded_utc
                FROM live_queue_recordings q JOIN live_queue_servers s USING (server, account_scope)
                WHERE {EligibleRecording} AND {ReadyPending} AND q.first_owner = $owner {source}
                ORDER BY q.arrival_sequence LIMIT 200;
                """);
            AddAutomaticAudioParameters(read, automatic);
            read.Transaction = transaction;

            var ready = new List<(string Id, long? RecordedUtc)>();
            using (var rows = read.ExecuteReader())
                while (rows.Read())
                    ready.Add((rows.GetString(0), rows.IsDBNull(1) ? null : rows.GetInt64(1)));
            var lag = evidence.Measure(ready);
            if (lag is not { } verified || !preference.Exceeds(verified))
                return new LagAdvance(lag);

            using var edge = db.CreateCommand();
            edge.Transaction = transaction;
            edge.CommandText = """
                SELECT coalesce((SELECT seq FROM sqlite_sequence WHERE name = 'live_queue_recordings'), 0) + 1;
                """;
            var floor = (long)edge.ExecuteScalar()!;

            // Commit the recorder floor and terminal dispositions together.
            // A later explicit seek can lower the traversal floor, but cannot
            // turn skipped entries back into automatic work.
            using var skip = EligibleCommand(db, run, historicalFloor, $"""
                UPDATE live_queue_recordings AS q SET disposition = 2, payload = NULL, audio_ready = 0
                FROM live_queue_servers s
                WHERE q.server = s.server AND q.account_scope = s.account_scope AND {EligibleRecording}
                  AND q.disposition = 0 AND q.arrival_sequence >= s.playback_floor AND q.arrival_sequence < $edge {source}
                """, ("$edge", floor));
            AddAutomaticAudioParameters(skip, automatic);
            skip.Transaction = transaction;
            skip.ExecuteNonQuery();
            UpdatePlaybackFloor(db, transaction, run, floor);

            token.ThrowIfCancellationRequested();
            return commitIfCurrent(() =>
            {
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }) ? new LagAdvance(verified, floor) : new LagAdvance(verified, Superseded: true);
        }), token);
}
