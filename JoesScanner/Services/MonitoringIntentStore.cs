using System.Text;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

// A restart requires both the committed SQLite snapshot and a matching file.
// Revoking that permit does not need SQLite's writer lock.
internal sealed class MonitoringIntentStore(string legacyStopKey, Action<string> report)
{
    internal const string SnapshotKey = "monitoring_intent_v1";
    internal const string PermitFileName = "monitoring-reconnect.v1";
    private const string LegacyScopeKey = "monitoring_stopped_live_scope";
    private readonly record struct Intent(Guid Id, bool Connected, string? Server, string? Account);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1);
    private long _revision;
    private long _committedRevision;
    private Intent? _lastStop;

    internal bool CanRestore(string? server, string? account)
    {
        try
        {
            var permit = ReadPermit();
            return permit is { Connected: true } ready && server != null &&
                ready.Server == server && ready.Account == account &&
                Parse(AppStateStore.GetString(SnapshotKey)) == permit;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            report($"Automatic reconnect state could not be read ({ex.GetType().Name}).");
            return false;
        }
    }

    internal long BeginStart()
    {
        lock (_gate)
        {
            // An interrupted startup must not inherit a permit from the previous
            // run. Keep an existing Stop scope for the durable journal.
            try
            {
                if (ReadPermit() is { Connected: true })
                    WritePermit("");
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                report($"The previous reconnect permit could not be cleared ({ex.GetType().Name}).");
            }
            return ++_revision;
        }
    }

    internal bool WasExplicitlyStopped(string server, string? account, bool protectedServer)
    {
        try
        {
            Intent? stopped;
            lock (_gate)
                stopped = _lastStop;
            var saved = stopped ?? ReadPermit() ?? Parse(AppStateStore.GetString(SnapshotKey));
            if (saved is { } intent)
                return !intent.Connected && intent.Server == server && intent.Account == account;

            // Retain old explicit Stop scopes for the journal only. The split
            // legacy settings cannot grant permission to reconnect.
            if (!AppStateStore.GetBool(legacyStopKey))
                return false;
            var legacy = AppStateStore.GetString(LegacyScopeKey);
            return legacy == server + "\n" + account || !protectedServer && legacy.Length == 0;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            report($"The previous Stop scope could not be read ({ex.GetType().Name}).");
            return false;
        }
    }

    internal Task SaveStartedAsync(long revision, string server, string? account, CancellationToken token) =>
        PersistAsync(revision, new(Guid.NewGuid(), true, server, account), token);

    internal Task SaveStoppedAsync(string? server, string? account)
    {
        Intent stop = new(Guid.NewGuid(), false, server, account);
        long revision;
        lock (_gate)
        {
            revision = ++_revision;
            _lastStop = stop;
            TryWritePermit(stop);
        }
        return PersistAsync(revision, stop, CancellationToken.None);
    }

    private Task PersistAsync(long revision, Intent snapshot, CancellationToken token) => Task.Run(async () =>
    {
        await _writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var encoded = Encode(snapshot);
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (revision < _committedRevision || snapshot.Connected && revision != _revision)
                        return;
                }

                try
                {
                    AppStateStore.InTransaction(transaction =>
                    {
                        AppStateStore.Write(transaction, SnapshotKey, encoded);
                        return true;
                    }, durable: true);
                    break;
                }
                catch (SqliteException ex) when (attempt < 2 && ex.SqliteErrorCode is 5 or 6)
                {
                    await Task.Delay(150 * (attempt + 1), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    report($"Monitoring intent could not be saved in SQLite ({ex.GetType().Name}).");
                    return;
                }
            }

            lock (_gate)
            {
                _committedRevision = revision;
                if (snapshot.Connected && revision == _revision && !token.IsCancellationRequested)
                {
                    _lastStop = null;
                    TryWritePermit(snapshot);
                }
            }
        }
        finally { _writer.Release(); }
    }, token);

    private void TryWritePermit(Intent intent)
    {
        try { WritePermit(Encode(intent)); }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            report($"The monitoring reconnect permit could not be written ({ex.GetType().Name}).");
        }
    }

    private static Intent? ReadPermit()
    {
        try
        {
            using var stream = new FileStream(AppPaths.GetDbPath(PermitFileName), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (FileNotFoundException) { return null; }
    }

    private static string Encode(Intent intent) =>
        string.Join('\n', intent.Id.ToString("N"), intent.Connected ? "1" : "0", intent.Server ?? "", intent.Account ?? "");

    private static Intent? Parse(string? encoded)
    {
        if (encoded?.Split('\n') is not [var rawId, var connected, var server, var account] ||
            !Guid.TryParseExact(rawId, "N", out var id) || id == Guid.Empty ||
            connected is not ("0" or "1") || connected == "1" && server.Length == 0)
            return null;
        return new Intent(id, connected == "1", server.Length == 0 ? null : server, account.Length == 0 ? null : account);
    }

    private static void WritePermit(string value)
    {
        // Truncate first so incomplete writes cannot retain the previous grant.
        using var stream = new FileStream(AppPaths.GetDbPath(PermitFileName), FileMode.Create,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.Flush(flushToDisk: true);
    }

    private static bool IsStorageFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException;
}
