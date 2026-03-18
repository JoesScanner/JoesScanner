using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using JoesScanner.Data;
using Microsoft.Data.Sqlite;
using JoesScanner.Helpers;

namespace JoesScanner.Services;

public sealed class LocalCallsRepository : ILocalCallsRepository, IDisposable
{
    private const int MaxCallsPerServer = 500;

    private readonly string _dbPath;
    private readonly Channel<DbWorkItem> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly ConcurrentDictionary<string, bool> _initByDbPath = new();

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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Ensure init runs at least once per db path.
        if (_initByDbPath.TryAdd(_dbPath, true))
        {
            await EnqueueAsync(async (conn, ct) =>
            {
                await InitializeSchemaAsync(conn, ct);
                return 0;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task UpsertCallsAsync(string serverKey, IReadOnlyList<DbCallRecord> calls, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(serverKey))
            return Task.CompletedTask;
        if (calls == null || calls.Count == 0)
            return Task.CompletedTask;

        var normalizedServerKey = ServerKeyHelper.Normalize(serverKey);

        return EnqueueAsync(async (conn, ct) =>
        {
            await InitializeSchemaAsync(conn, ct);
            await UpsertCallsInternalAsync(conn, normalizedServerKey, calls, ct);
            await EnforceRetentionAsync(conn, normalizedServerKey, MaxCallsPerServer, ct);
            return 0;
        }, cancellationToken);
    }

    public Task MarkTranscriptionUpdateNotifiedAsync(string serverKey, string backendId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(serverKey) || string.IsNullOrWhiteSpace(backendId))
            return Task.CompletedTask;

        var normalizedServerKey = ServerKeyHelper.Normalize(serverKey);
        var id = backendId.Trim();

        return EnqueueAsync(async (conn, ct) =>
        {
            await InitializeSchemaAsync(conn, ct);

            var now = DateTimeOffset.UtcNow;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE calls
SET transcription_update_notified_at_utc = $notified,
    updated_at_utc = $updated
WHERE server_key = $server_key AND backend_id = $backend_id;";

            cmd.Parameters.AddWithValue("$server_key", normalizedServerKey);
            cmd.Parameters.AddWithValue("$backend_id", id);
            cmd.Parameters.AddWithValue("$notified", now.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return 0;
        }, cancellationToken);
    }

    private async Task WorkerLoopAsync()
    {
        SqliteConnection? conn = null;

        try
        {
            conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync(_cts.Token).ConfigureAwait(false);

            await ApplyPragmasAsync(conn, _cts.Token).ConfigureAwait(false);

            while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    try
                    {
                        await item.Work(conn, _cts.Token).ConfigureAwait(false);
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
        catch
        {
            // If the worker dies, unblock queued tasks.
            while (_queue.Reader.TryRead(out var item))
                item.Tcs.TrySetCanceled();
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
        var item = new DbWorkItem(work, tcs);

        if (!_queue.Writer.TryWrite(item))
            tcs.TrySetCanceled();

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        }

        return tcs.Task;
    }

    private static async Task ApplyPragmasAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Safe pragmas for mobile + desktop. WAL reduces lock contention.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA temp_store=MEMORY;
PRAGMA foreign_keys=ON;
PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Add new columns on existing installs (CREATE TABLE IF NOT EXISTS will not modify an existing schema).
        await EnsureColumnExistsAsync(conn, "calls", "transcript_available", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
    }

    private static async Task InitializeSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS calls (
  server_key TEXT NOT NULL,
  backend_id TEXT NOT NULL,

  start_time_utc TEXT NULL,
  time_text TEXT NULL,
  date_text TEXT NULL,

  target_id TEXT NULL,
  target_label TEXT NULL,
  target_tag TEXT NULL,

  source_id TEXT NULL,
  source_label TEXT NULL,
  source_tag TEXT NULL,

  lcn INTEGER NULL,
  frequency REAL NULL,
  call_audio_type TEXT NULL,
  call_type TEXT NULL,

  system_id TEXT NULL,
  system_label TEXT NULL,
  system_type TEXT NULL,

  site_id TEXT NULL,
  site_label TEXT NULL,
  voice_receiver TEXT NULL,

  audio_filename TEXT NULL,
  audio_start_pos REAL NULL,
  call_duration_seconds REAL NULL,

  call_text TEXT NULL,
  transcription TEXT NULL,
  transcript_available INTEGER NOT NULL DEFAULT 0,

  received_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL,
  transcription_update_notified_at_utc TEXT NULL,

  PRIMARY KEY (server_key, backend_id)
);



            CREATE TABLE IF NOT EXISTS lookup_cache (
                server_key TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                fetched_at_utc INTEGER NOT NULL,
                json_payload TEXT NOT NULL,
                PRIMARY KEY(server_key, cache_key)
            );
CREATE INDEX IF NOT EXISTS idx_calls_server_start ON calls(server_key, start_time_utc);
CREATE INDEX IF NOT EXISTS idx_calls_server_received ON calls(server_key, received_at_utc);
";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    
    private static async Task EnsureColumnExistsAsync(SqliteConnection conn, string tableName, string columnName, string columnDefinition, CancellationToken ct)
    {
        // PRAGMA table_info returns: cid, name, type, notnull, dflt_value, pk
        using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await pragma.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

private static async Task UpsertCallsInternalAsync(SqliteConnection conn, string serverKey, IReadOnlyList<DbCallRecord> calls, CancellationToken ct)
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
  audio_filename = COALESCE(excluded.audio_filename, calls.audio_filename),
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
  updated_at_utc = excluded.updated_at_utc;
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

            cmd.Parameters["$server_key"].Value = serverKey;
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

            cmd.Parameters["$audio_filename"].Value = ToDbNull(call.AudioFilename);
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

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnforceRetentionAsync(SqliteConnection conn, string serverKey, int limit, CancellationToken ct)
    {
        if (limit <= 0)
            return;

        using var cmd = conn.CreateCommand();
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

        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(contextKey))
            return null;

        await InitializeAsync(ct).ConfigureAwait(false);

        var resultTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverKey = ServerKeyHelper.Normalize(contextKey);

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

        if (string.IsNullOrWhiteSpace(serverKey) || string.IsNullOrWhiteSpace(cacheKey))
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
            cmd.Parameters.AddWithValue("$server_key", serverKey);
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

    private sealed record DbWorkItem(Func<SqliteConnection, CancellationToken, Task<int>> Work, TaskCompletionSource<bool> Tcs);
}