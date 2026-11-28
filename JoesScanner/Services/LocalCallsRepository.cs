using System.Globalization;
using System.Threading.Channels;
using JoesScanner.Data;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

public sealed class LocalCallsRepository : ILocalCallsRepository, IDisposable
{
    private const int MaxCallsPerServer = 500;

    private readonly string _dbPath;
    private readonly Channel<DbWorkItem> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private volatile bool _disposed;

    public LocalCallsRepository(IDatabasePathProvider dbPathProvider)
    {
            _dbPath = (dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider))).DbPath;
        _queue = Channel.CreateUnbounded<DbWorkItem>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });

        _worker = Task.Run(WorkerLoopAsync);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync((_, _) => Task.FromResult(0), cancellationToken);

    public Task UpsertCallsAsync(RecordingMetadataScope scope, IReadOnlyList<DbCallRecord> calls, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        cancellationToken.ThrowIfCancellationRequested();
        if (calls == null || calls.Count == 0)
            return Task.CompletedTask;

        return EnqueueAsync(async (conn, ct) =>
        {
            await UpsertCallsInternalAsync(conn, scope, calls, ct);
            return 0;
        }, cancellationToken);
    }

    public Task MarkTranscriptionUpdateNotifiedAsync(RecordingMetadataScope scope, string backendId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(backendId))
            return Task.CompletedTask;

        var id = backendId.Trim();

        return EnqueueAsync(async (conn, ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
UPDATE calls
SET transcription_update_notified_at_utc = $notified,
    updated_at_utc = $updated
WHERE server_key = $server_key AND backend_id = $backend_id;";

            cmd.Parameters.AddWithValue("$server_key", scope.StorageKey);
            cmd.Parameters.AddWithValue("$backend_id", id);
            cmd.Parameters.AddWithValue("$notified", now.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            scope.Commit(transaction.Commit, ct);
            return 0;
        }, cancellationToken);
    }

    private async Task WorkerLoopAsync()
    {
        SqliteConnection? conn = null;

        try
        {
            conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
            await conn.OpenAsync(_cts.Token).ConfigureAwait(false);

            conn.CreateFunction<string?, string?>("storable_recording_path", RecordingStorageReference.ForCache, isDeterministic: true);
            var initialized = false;

            while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    try
                    {
                        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(item.Token, _cts.Token);
                        lifetime.Token.ThrowIfCancellationRequested();
                        if (!initialized)
                        {
                            await DbBootstrapper.EnsureInitializedAsync(conn, _cts.Token).ConfigureAwait(false);
                            initialized = true;
                        }
                        await item.Work(conn, lifetime.Token).ConfigureAwait(false);
                        item.Tcs.TrySetResult(true);
                    }
                    catch (OperationCanceledException oce)
                    {
                        item.Tcs.TrySetCanceled(oce.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        item.Tcs.TrySetException(ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // If the worker dies, unblock queued tasks.
            _queue.Writer.TryComplete(ex);
            while (_queue.Reader.TryRead(out var item))
                item.Tcs.TrySetException(ex);
        }
        finally
        {
            try { conn?.Dispose(); } catch { }
        }
    }

    private Task EnqueueAsync(Func<SqliteConnection, CancellationToken, Task<int>> work, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new DbWorkItem(work, tcs, cancellationToken);

        if (!_queue.Writer.TryWrite(item))
            tcs.TrySetCanceled();

        return tcs.Task;
    }

    private static async Task UpsertCallsInternalAsync(SqliteConnection conn, RecordingMetadataScope scope, IReadOnlyList<DbCallRecord> calls, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO calls (
  server_key, backend_id,
  start_time_utc, time_text, date_text,
  target_id, target_label, target_tag,
  source_id, source_label, source_tag,
  lcn, frequency, call_audio_type, call_type,
  system_id, system_label, system_type,
  site_id, site_label, voice_receiver,
  audio_filename, audio_start_pos, call_duration_seconds,
  call_text, transcription, transcript_available,
  received_at_utc, updated_at_utc
)
VALUES (
  $server_key, $backend_id,
  $start_time_utc, $time_text, $date_text,
  $target_id, $target_label, $target_tag,
  $source_id, $source_label, $source_tag,
  $lcn, $frequency, $call_audio_type, $call_type,
  $system_id, $system_label, $system_type,
  $site_id, $site_label, $voice_receiver,
  $audio_filename, $audio_start_pos, $call_duration_seconds,
  $call_text, $transcription, $transcript_available,
  $received_at_utc, $updated_at_utc
)
ON CONFLICT(server_key, backend_id) DO UPDATE SET
  start_time_utc = COALESCE(excluded.start_time_utc, calls.start_time_utc),
  time_text = COALESCE(excluded.time_text, calls.time_text),
  date_text = COALESCE(excluded.date_text, calls.date_text),
  target_id = COALESCE(excluded.target_id, calls.target_id),
  target_label = COALESCE(excluded.target_label, calls.target_label),
  target_tag = COALESCE(excluded.target_tag, calls.target_tag),
  source_id = COALESCE(excluded.source_id, calls.source_id),
  source_label = COALESCE(excluded.source_label, calls.source_label),
  source_tag = COALESCE(excluded.source_tag, calls.source_tag),
  lcn = COALESCE(excluded.lcn, calls.lcn),
  frequency = COALESCE(excluded.frequency, calls.frequency),
  call_audio_type = COALESCE(excluded.call_audio_type, calls.call_audio_type),
  call_type = COALESCE(excluded.call_type, calls.call_type),
  system_id = COALESCE(excluded.system_id, calls.system_id),
  system_label = COALESCE(excluded.system_label, calls.system_label),
  system_type = COALESCE(excluded.system_type, calls.system_type),
  site_id = COALESCE(excluded.site_id, calls.site_id),
  site_label = COALESCE(excluded.site_label, calls.site_label),
  voice_receiver = COALESCE(excluded.voice_receiver, calls.voice_receiver),
  audio_filename = COALESCE(excluded.audio_filename, storable_recording_path(calls.audio_filename)),
  audio_start_pos = COALESCE(excluded.audio_start_pos, calls.audio_start_pos),
  call_duration_seconds = COALESCE(excluded.call_duration_seconds, calls.call_duration_seconds),
  call_text = COALESCE(excluded.call_text, calls.call_text),
  transcription = CASE
      WHEN excluded.transcription IS NOT NULL AND length(excluded.transcription) > 0 THEN excluded.transcription
      ELSE calls.transcription
    END,
  transcript_available = CASE
      WHEN excluded.transcription IS NOT NULL AND length(trim(excluded.transcription)) > 0 THEN 1
      ELSE calls.transcript_available
    END,
  updated_at_utc = excluded.updated_at_utc
WHERE calls.start_time_utc IS NULL OR excluded.start_time_utc IS NULL
   OR calls.start_time_utc = excluded.start_time_utc;
";

        // Parameters created once and reassigned for each row.
        cmd.Parameters.Add("$server_key", SqliteType.Text);
        cmd.Parameters.Add("$backend_id", SqliteType.Text);

        cmd.Parameters.Add("$start_time_utc", SqliteType.Text);
        cmd.Parameters.Add("$time_text", SqliteType.Text);
        cmd.Parameters.Add("$date_text", SqliteType.Text);

        cmd.Parameters.Add("$target_id", SqliteType.Text);
        cmd.Parameters.Add("$target_label", SqliteType.Text);
        cmd.Parameters.Add("$target_tag", SqliteType.Text);

        cmd.Parameters.Add("$source_id", SqliteType.Text);
        cmd.Parameters.Add("$source_label", SqliteType.Text);
        cmd.Parameters.Add("$source_tag", SqliteType.Text);

        cmd.Parameters.Add("$lcn", SqliteType.Integer);
        cmd.Parameters.Add("$frequency", SqliteType.Real);
        cmd.Parameters.Add("$call_audio_type", SqliteType.Text);
        cmd.Parameters.Add("$call_type", SqliteType.Text);

        cmd.Parameters.Add("$system_id", SqliteType.Text);
        cmd.Parameters.Add("$system_label", SqliteType.Text);
        cmd.Parameters.Add("$system_type", SqliteType.Text);

        cmd.Parameters.Add("$site_id", SqliteType.Text);
        cmd.Parameters.Add("$site_label", SqliteType.Text);
        cmd.Parameters.Add("$voice_receiver", SqliteType.Text);

        cmd.Parameters.Add("$audio_filename", SqliteType.Text);
        cmd.Parameters.Add("$audio_start_pos", SqliteType.Real);
        cmd.Parameters.Add("$call_duration_seconds", SqliteType.Real);

        cmd.Parameters.Add("$call_text", SqliteType.Text);
        cmd.Parameters.Add("$transcription", SqliteType.Text);
        cmd.Parameters.Add("$transcript_available", SqliteType.Integer);

        cmd.Parameters.Add("$received_at_utc", SqliteType.Text);
        cmd.Parameters.Add("$updated_at_utc", SqliteType.Text);

        foreach (var call in calls)
        {
            if (call == null)
                continue;

            ct.ThrowIfCancellationRequested();
            cmd.Parameters["$server_key"].Value = scope.StorageKey;
            cmd.Parameters["$backend_id"].Value = call.BackendId;

            cmd.Parameters["$start_time_utc"].Value = ToDbNull(call.StartTimeUtc);
            cmd.Parameters["$time_text"].Value = ToDbNull(call.TimeText);
            cmd.Parameters["$date_text"].Value = ToDbNull(call.DateText);

            cmd.Parameters["$target_id"].Value = ToDbNull(call.TargetId);
            cmd.Parameters["$target_label"].Value = ToDbNull(call.TargetLabel);
            cmd.Parameters["$target_tag"].Value = ToDbNull(call.TargetTag);

            cmd.Parameters["$source_id"].Value = ToDbNull(call.SourceId);
            cmd.Parameters["$source_label"].Value = ToDbNull(call.SourceLabel);
            cmd.Parameters["$source_tag"].Value = ToDbNull(call.SourceTag);

            cmd.Parameters["$lcn"].Value = call.Lcn.HasValue ? call.Lcn.Value : DBNull.Value;
            cmd.Parameters["$frequency"].Value = call.Frequency.HasValue ? call.Frequency.Value : DBNull.Value;
            cmd.Parameters["$call_audio_type"].Value = ToDbNull(call.CallAudioType);
            cmd.Parameters["$call_type"].Value = ToDbNull(call.CallType);

            cmd.Parameters["$system_id"].Value = ToDbNull(call.SystemId);
            cmd.Parameters["$system_label"].Value = ToDbNull(call.SystemLabel);
            cmd.Parameters["$system_type"].Value = ToDbNull(call.SystemType);

            cmd.Parameters["$site_id"].Value = ToDbNull(call.SiteId);
            cmd.Parameters["$site_label"].Value = ToDbNull(call.SiteLabel);
            cmd.Parameters["$voice_receiver"].Value = ToDbNull(call.VoiceReceiver);

            cmd.Parameters["$audio_filename"].Value = ToDbNull(RecordingStorageReference.ForCache(call.AudioFilename));
            cmd.Parameters["$audio_start_pos"].Value = call.AudioStartPos.HasValue ? call.AudioStartPos.Value : DBNull.Value;
            cmd.Parameters["$call_duration_seconds"].Value = call.CallDurationSeconds.HasValue ? call.CallDurationSeconds.Value : DBNull.Value;

            cmd.Parameters["$call_text"].Value = ToDbNull(call.CallText);
            cmd.Parameters["$transcription"].Value = ToDbNull(call.Transcription);
            cmd.Parameters["$transcript_available"].Value = HasMeaningfulText(call.Transcription) ? 1 : 0;

            // On insert, we prefer the record's ReceivedAtUtc. On update, we only modify UpdatedAt.
            // We still pass ReceivedAt to keep the statement simple; PK conflict prevents overwriting if row exists.
            cmd.Parameters["$received_at_utc"].Value = call.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            cmd.Parameters["$updated_at_utc"].Value = now;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await EnforceRetentionAsync(conn, tx, scope.StorageKey, MaxCallsPerServer, ct).ConfigureAwait(false);
        scope.Commit(tx.Commit, ct);
    }

    private static async Task EnforceRetentionAsync(SqliteConnection conn, SqliteTransaction transaction, string serverKey, int limit, CancellationToken ct)
    {
        if (limit <= 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
DELETE FROM calls
WHERE server_key = $server_key
  AND (server_key, backend_id) NOT IN (
    SELECT server_key, backend_id
    FROM calls
    WHERE server_key = $server_key
    ORDER BY
      CASE WHEN start_time_utc IS NULL OR length(start_time_utc) = 0 THEN 1 ELSE 0 END,
      start_time_utc DESC,
      received_at_utc DESC
    LIMIT $limit
  );";

        cmd.Parameters.AddWithValue("$server_key", serverKey);
        cmd.Parameters.AddWithValue("$limit", limit);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static object ToDbNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DBNull.Value;
        return value;
    }

    
    private static bool HasMeaningfulText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Treat very short whitespace-only strings as empty.
        return value.Trim().Length > 0;
    }


    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LocalCallsRepository));
    }

    

    public async Task<string?> GetLookupCacheAsync(string cacheKey, string contextKey, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(cacheKey) || RecordingStorageReference.ServerKey(contextKey) is not { } serverKey)
            return null;

        await InitializeAsync(ct).ConfigureAwait(false);

        var resultTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueAsync(async (db, token) =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                @"SELECT json_payload
                  FROM lookup_cache
                  WHERE server_key = @serverKey AND cache_key = @cacheKey
                  LIMIT 1;";
            cmd.Parameters.AddWithValue("@serverKey", serverKey);
            cmd.Parameters.AddWithValue("@cacheKey", cacheKey);

            var obj = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            var jsonPayload = obj == null || obj is DBNull ? null : obj.ToString();

            resultTcs.TrySetResult(jsonPayload);
            return 0;
        }, ct).ConfigureAwait(false);

        return await resultTcs.Task.ConfigureAwait(false);
    }

    public async Task UpsertLookupCacheAsync(string serverKey, string cacheKey, DateTimeOffset fetchedAt, string jsonPayload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (RecordingStorageReference.ServerKey(serverKey) is not { } normalizedServerKey || string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        jsonPayload ??= string.Empty;

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await EnqueueAsync(async (db, ct) =>
        {
            var cmd = db.CreateCommand();
            cmd.CommandText = @"
INSERT INTO lookup_cache (server_key, cache_key, fetched_at_utc, json_payload)
VALUES ($server_key, $cache_key, $fetched_at_utc, $json_payload)
ON CONFLICT(server_key, cache_key) DO UPDATE SET
    fetched_at_utc = excluded.fetched_at_utc,
    json_payload = excluded.json_payload;";
            cmd.Parameters.AddWithValue("$server_key", normalizedServerKey);
            cmd.Parameters.AddWithValue("$cache_key", cacheKey);
            cmd.Parameters.AddWithValue("$fetched_at_utc", fetchedAt.UtcTicks);
            cmd.Parameters.AddWithValue("$json_payload", jsonPayload);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _queue.Writer.TryComplete(); } catch { }
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private sealed record DbWorkItem(Func<SqliteConnection, CancellationToken, Task<int>> Work, TaskCompletionSource<bool> Tcs, CancellationToken Token);
}
