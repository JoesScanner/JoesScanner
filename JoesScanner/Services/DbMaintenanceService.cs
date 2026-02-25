using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

// Best-effort DB cleanup/retention service.
// Runs off the UI thread.
public sealed class DbMaintenanceService : IDbMaintenanceService, IDisposable
{
    // Retention targets
    private static readonly TimeSpan CallsMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan LookupCacheMaxAge = TimeSpan.FromDays(14);
    private static readonly TimeSpan TelemetryQueueMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan UnregisteredQueueMaxAge = TimeSpan.FromDays(7);

    // Maintenance cadence
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    // VACUUM is expensive; only do it after large deletes.
    private const int VacuumDeleteThresholdRows = 2000;

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
        // Initial small delay so startup IO is prioritized.
        try { await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false); } catch { return; }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best effort.
            }

            try
            {
                await Task.Delay(Period, _cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await DbBootstrapper.EnsureInitializedAsync(conn, ct).ConfigureAwait(false);

        var deleted = 0;
        deleted += await CleanupLookupCacheAsync(conn, ct).ConfigureAwait(false);
        deleted += await EnforceCallsRetentionAsync(conn, ct).ConfigureAwait(false);
        deleted += await EnforceTelemetryQueueRetentionAsync(conn, ct).ConfigureAwait(false);
        deleted += await EnforceUnregisteredQueueRetentionAsync(conn, ct).ConfigureAwait(false);

        if (deleted >= VacuumDeleteThresholdRows)
            await VacuumIfNeededAsync(conn, ct).ConfigureAwait(false);
    }

    private static async Task<int> EnforceTelemetryQueueRetentionAsync(SqliteConnection conn, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - TelemetryQueueMaxAge;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DELETE FROM telemetry_queue
WHERE created_utc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> EnforceUnregisteredQueueRetentionAsync(SqliteConnection conn, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - UnregisteredQueueMaxAge;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DELETE FROM unregistered_session_queue
WHERE created_utc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> CleanupLookupCacheAsync(SqliteConnection conn, CancellationToken ct)
    {
        var cutoffTicks = DateTimeOffset.UtcNow.Subtract(LookupCacheMaxAge).UtcTicks;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM lookup_cache WHERE fetched_at_utc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoffTicks);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> EnforceCallsRetentionAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Time-based retention: keep calls from the last 24 hours only.
        // received_at_utc is stored as DateTimeOffset.ToString("O"), which is lexicographically sortable
        // for UTC timestamps.
        var cutoff = DateTimeOffset.UtcNow.Subtract(CallsMaxAge).ToString("O", CultureInfo.InvariantCulture);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM calls WHERE received_at_utc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task VacuumIfNeededAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Ensure WAL contents are checkpointed before vacuum.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
