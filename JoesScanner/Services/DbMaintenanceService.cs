using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

// Cache retention and resumable cleanup run off the UI thread. Durable queue
// identities have no TTL: a late recorder response can still contain any ID.
public sealed class DbMaintenanceService : IDbMaintenanceService, IDisposable
{
    // Retention targets
    private static readonly TimeSpan CallsMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan LookupCacheMaxAge = TimeSpan.FromDays(14);
    private static readonly TimeSpan TelemetryQueueMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan UnregisteredQueueMaxAge = TimeSpan.FromDays(7);

    private static readonly TimeSpan Period = TimeSpan.FromHours(6);
    private static readonly TimeSpan CatchUpPeriod = TimeSpan.FromSeconds(30);
    internal const int BatchSize = 128;

    private readonly string _dbPath;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _started;

    public DbMaintenanceService(IDatabasePathProvider dbPathProvider)
    {
            _dbPath = (dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider))).DbPath;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _loop = Task.Run(LoopAsync);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private async Task LoopAsync()
    {
        var token = _cts.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                var delay = Period;
                try
                {
                    if (await RunOnceAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false))
                        delay = CatchUpPeriod;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    delay = CatchUpPeriod;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Basic(() => $"Database maintenance deferred ({ex.GetType().Name}).");
                }

                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal async Task<bool> RunOnceAsync(DateTimeOffset now, CancellationToken token)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, DefaultTimeout = 1 }.ToString());
        await db.OpenAsync(token).ConfigureAwait(false);
        await DbBootstrapper.EnsureInitializedAsync(db, token).ConfigureAwait(false);

        db.CreateFunction<string?, string?>("storable_recording_path", RecordingStorageReference.ForCache, isDeterministic: true);
        db.CreateFunction<string?, string?>("storable_recording_url", RecordingStorageReference.ForJournal, isDeterministic: true);
        db.CreateFunction<string?, string?>("recording_metadata_key", RecordingMetadataScope.NormalizeStoredKey, isDeterministic: true);
        db.CreateFunction<string?, string?>("recording_server_key", RecordingStorageReference.ServerKey, isDeterministic: true);
        db.CreateFunction<string?, string?>("history_server_key", key => HistoryRecorder.IsLookupCacheKey(key)
            ? key : RecordingStorageReference.ServerKey(key), isDeterministic: true);
        db.CreateFunction<string?, string?>("redact_diagnostic_text", text => text == null ? null : DiagnosticRedaction.Sanitize(text), isDeterministic: true);
        using (var setup = db.CreateCommand())
        {
            setup.CommandText = "PRAGMA secure_delete=ON; PRAGMA busy_timeout=1000;";
            await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        var needsMore = await ScrubLegacyBatchAsync(db, "calls", """
            UPDATE calls SET audio_filename = storable_recording_path(audio_filename)
            WHERE rowid > $after AND rowid <= $through
              AND audio_filename IS NOT storable_recording_path(audio_filename);
            """ + NormalizeCacheKeys("calls", "recording_metadata_key"), token).ConfigureAwait(false);
        needsMore |= await ScrubLegacyBatchAsync(db, "live_queue_recordings", """
            UPDATE live_queue_recordings SET payload = NULL, audio_ready = 0
            WHERE rowid > $after AND rowid <= $through AND disposition IN (1, 2)
              AND (payload IS NOT NULL OR audio_ready <> 0);
            WITH pending_audio AS MATERIALIZED (
                SELECT rowid AS id,
                    CASE WHEN json_valid(payload) THEN json_extract(payload, '$.AudioUrl') END AS audio,
                    CASE WHEN json_valid(payload) THEN json_extract(payload, '$.audioUrl') END AS legacy_audio
                FROM live_queue_recordings
                WHERE rowid > $after AND rowid <= $through AND disposition = 0
            )
            UPDATE live_queue_recordings AS q
            SET payload = json_set(json_remove(q.payload, '$.audioUrl'), '$.AudioUrl',
                    storable_recording_url(coalesce(p.audio, p.legacy_audio))),
                audio_ready = CASE WHEN storable_recording_url(coalesce(p.audio, p.legacy_audio)) IS NULL THEN 0 ELSE 1 END
            FROM pending_audio p WHERE q.rowid = p.id
              AND (p.audio IS NOT storable_recording_url(p.audio) OR p.legacy_audio IS NOT NULL);
            """, token).ConfigureAwait(false);
        needsMore |= await ScrubLegacyBatchAsync(db, "lookup_cache", NormalizeCacheKeys("lookup_cache"), token).ConfigureAwait(false);
        needsMore |= await ScrubLegacyBatchAsync(db, "history_lookups", NormalizeCacheKeys("history_lookups", "history_server_key"), token).ConfigureAwait(false);
        needsMore |= await ScrubLegacyBatchAsync(db, "telemetry_queue", """
            WITH fields AS MATERIALIZED (
                SELECT id,
                    CASE WHEN json_valid(json_payload) THEN json_extract(json_payload, '$.Data.stream_server_url') END AS server,
                    CASE WHEN json_valid(json_payload) THEN json_extract(json_payload, '$.Data.detail') END AS detail
                FROM telemetry_queue WHERE id > $after AND id <= $through
            )
            UPDATE telemetry_queue AS q
            SET json_payload = json_set(q.json_payload, '$.Data.stream_server_url', recording_server_key(f.server),
                '$.Data.detail', redact_diagnostic_text(f.detail))
            FROM fields f WHERE q.id = f.id
              AND (f.server IS NOT recording_server_key(f.server) OR f.detail IS NOT redact_diagnostic_text(f.detail));
            """, token).ConfigureAwait(false);

        // Process-scoped tokens are no longer written by the app. Clear older copies.
        using (var sessions = db.CreateCommand())
        {
            sessions.CommandText = """
                DELETE FROM app_settings WHERE setting_key IN ('auth_session_token', 'telemetry_last_session_token');
                """;
            await sessions.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        needsMore |= await DeleteExpiredBatchAsync(db, "calls", "received_at_utc",
            now.Subtract(CallsMaxAge).ToString("O", CultureInfo.InvariantCulture), token).ConfigureAwait(false);
        needsMore |= await DeleteExpiredBatchAsync(db, "lookup_cache", "fetched_at_utc",
            now.Subtract(LookupCacheMaxAge).UtcTicks, token).ConfigureAwait(false);
        needsMore |= await DeleteExpiredBatchAsync(db, "telemetry_queue", "created_utc",
            now.UtcDateTime.Subtract(TelemetryQueueMaxAge).ToString("O", CultureInfo.InvariantCulture), token).ConfigureAwait(false);
        needsMore |= await DeleteExpiredBatchAsync(db, "unregistered_session_queue", "created_utc",
            now.UtcDateTime.Subtract(UnregisteredQueueMaxAge).ToString("O", CultureInfo.InvariantCulture), token).ConfigureAwait(false);

        // Allow SQLite to recycle pages and WAL frames without a database-wide
        // VACUUM or waiting for readers to leave the active playback database.
        using var checkpoint = db.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await checkpoint.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return needsMore;
    }

    private static string NormalizeCacheKeys(string table, string normalize = "recording_server_key") => $"""
        UPDATE OR IGNORE {table} SET server_key = {normalize}(server_key)
        WHERE rowid > $after AND rowid <= $through AND server_key IS NOT {normalize}(server_key);
        DELETE FROM {table} WHERE rowid > $after AND rowid <= $through
          AND server_key IS NOT {normalize}(server_key);
        """;

    // Scan a fixed number of rows even when every row is already clean. Persist the
    // cursor in the same transaction so cancellation or process exit can retry it.
    private static async Task<bool> ScrubLegacyBatchAsync(SqliteConnection db, string table, string scrubSql,
        CancellationToken token)
    {
        var key = "db_recording_cleanup_v1_" + table;
        using var cursor = db.CreateCommand();
        cursor.CommandText = """
            SELECT json_extract(setting_value, '$[0]'), json_extract(setting_value, '$[1]')
            FROM app_settings WHERE setting_key = $key AND json_valid(setting_value);
            """;
        cursor.Parameters.AddWithValue("$key", key);
        var last = 0L;
        long? limit = null;
        using (var saved = await cursor.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (await saved.ReadAsync(token).ConfigureAwait(false) && !saved.IsDBNull(0) && !saved.IsDBNull(1))
            {
                last = saved.GetInt64(0);
                limit = saved.GetInt64(1);
            }
        }
        if (last == -1)
            return false;

        using var transaction = db.BeginTransaction();
        token.ThrowIfCancellationRequested();
        if (limit == null)
        {
            // The new writers sanitize their own data. A fixed high-water rowid
            // lets the upgrade finish even on a continuously busy recorder.
            using var maximum = db.CreateCommand();
            maximum.Transaction = transaction;
            maximum.CommandText = $"SELECT coalesce(max(rowid), 0) FROM {table};";
            limit = (long)(await maximum.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }
        using var boundary = db.CreateCommand();
        boundary.Transaction = transaction;
        boundary.CommandText = $"""
            SELECT coalesce(max(rowid), $after), count(*) FROM (
              SELECT rowid FROM {table} WHERE rowid > $after AND rowid <= $ceiling ORDER BY rowid LIMIT $batch
            );
            """;
        boundary.Parameters.AddWithValue("$after", last);
        boundary.Parameters.AddWithValue("$ceiling", limit.Value);
        boundary.Parameters.AddWithValue("$batch", BatchSize);
        long through;
        bool more;
        using (var rows = await boundary.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            await rows.ReadAsync(token).ConfigureAwait(false);
            through = rows.GetInt64(0);
            more = rows.GetInt32(1) == BatchSize && through < limit.Value;
        }
        if (through > last)
        {
            using var scrub = db.CreateCommand();
            scrub.Transaction = transaction;
            scrub.CommandText = scrubSql;
            scrub.Parameters.AddWithValue("$after", last);
            scrub.Parameters.AddWithValue("$through", through);
            await scrub.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        using var save = db.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO app_settings (setting_key, setting_value, updated_utc) VALUES ($key, json_array($cursor, $ceiling), $now)
            ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value, updated_utc = excluded.updated_utc;
            """;
        save.Parameters.AddWithValue("$key", key);
        save.Parameters.AddWithValue("$cursor", more ? through : -1);
        save.Parameters.AddWithValue("$ceiling", limit.Value);
        save.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await save.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return more;
    }

    private static async Task<bool> DeleteExpiredBatchAsync(SqliteConnection db, string table, string ageColumn,
        object cutoff, CancellationToken token)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"""
            DELETE FROM {table} WHERE rowid IN (
                SELECT rowid FROM {table} WHERE {ageColumn} < $cutoff LIMIT $limit
            );
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$limit", BatchSize);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == BatchSize;
    }
}
