using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class CustomRecorderJournalSchemaTests
{
    private sealed record Paths(string DbPath) : IDatabasePathProvider;

    internal static async Task FreshAndUpgrade()
    {
        using var files = new SavedCredentialTests.Fixture();
        foreach (var upgrade in new[] { false, true })
        {
            var paths = new Paths(files.DbPath + (upgrade ? ".v5" : ".fresh"));
            using var db = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
            await db.OpenAsync();
            if (upgrade)
                SeedV5(db);
            await DbBootstrapper.EnsureInitializedAsync(db);
            var queue = new DurableLiveQueue(paths);
            var sameAccount = new string('A', 64);
            var otherAccount = new string('B', 64);
            var publicRun = new DurableLiveQueue.Run("https://recorder.test", 10, null);
            var alpha = publicRun with { Account = sameAccount, Owner = 11 };
            var bravo = publicRun with { Account = otherAccount, Owner = 12 };
            var hosted = publicRun with { Account = sameAccount, Owner = 13, Protected = true, VerifiedTier = 3 };
            var now = DateTimeOffset.UtcNow;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var run in new[] { publicRun, alpha, bravo, hosted })
            {
                await queue.OpenAsync(run, now, timeout.Token);
                var snapshot = new LiveCallSnapshot(new(), $"run-{run.Owner}", CallCardIdFallback.None, "audio.wav");
                if (!await queue.ObserveAsync(run, "shared", now, snapshot, timeout.Token, RecordingOrigin.Live))
                    throw new InvalidOperationException("The first observation must be durably admitted for each owner.");
            }

            var reopened = new DurableLiveQueue(paths);
            foreach (var run in new[] { publicRun, alpha, bravo, hosted })
            {
                var next = await reopened.NextAsync(run, 0, timeout.Token);
                var stored = await reopened.GetAccessAsync(run.Server, "shared", run.Account, timeout.Token, run.Protected);
                if (next?.Snapshot?.Transcription != $"run-{run.Owner}" || next.Owner != run.Owner ||
                    stored?.Owner != run.Owner || stored.Value.Access.Account != run.Account ||
                    stored.Value.Access.Protected != run.Protected || !run.Owns(next.Access) ||
                    (upgrade && next.ArrivalSequence <= 99))
                    throw new InvalidOperationException("Reconstruction, playback access or arrival order crossed an account boundary.");
            }
            if (Scalar(db, "SELECT count(DISTINCT account_scope) FROM live_queue_recordings WHERE recording_id='shared'") != 4)
                throw new InvalidOperationException("Public, custom and hosted identities require disjoint durable scopes.");
            Reject(db, "UPDATE live_queue_recordings SET protected=1 WHERE account_scope='' OR account_scope GLOB 'custom:*'");
            Reject(db, "UPDATE live_queue_recordings SET protected=0 WHERE protected=1");
            if (upgrade && (Scalar(db, """
                    SELECT count(*) FROM live_queue_recordings WHERE recording_id='old' AND arrival_sequence=19
                        AND disposition=2 AND recorded_utc IS NULL AND recovery_backlog=1 AND audio_revision=4
                        AND audio_recovered=1 AND payload='{}' AND first_owner=1
                    """) != 1 || Scalar(db, "SELECT playback_floor FROM live_queue_servers WHERE server='old'") != 88 ||
                    Scalar(db, "SELECT revision FROM live_queue_recovery WHERE server='old'") != 7))
                throw new InvalidOperationException("Migration changed a prior disposition, watermark, recovery window or playback floor.");
            if (Scalar(db, "SELECT count(*) FROM pragma_foreign_key_check") != 0)
                throw new InvalidOperationException("Account migration broke a queue foreign key.");
        }
        await EndpointChangeAsync(new Paths(files.DbPath + ".edited"));
        foreach (var version in new[] { 5, 6 })
            await AmbiguousCheckpointAsync(new Paths(files.DbPath + $".checkpoint-{version}"), version);
    }

    private static async Task EndpointChangeAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var now = DateTimeOffset.UtcNow;
        var first = new DurableLiveQueue.Run("https://recorder.test:8443/north", 1, null,
            Account: new string('A', 64), ServerId: Guid.NewGuid(), Operation: new RecorderOperation());
        var second = first with { Server = "https://recorder.test:8443/south", Owner = 2, Operation = new RecorderOperation() };
        if ((first with { Server = "https://RECORDER.TEST:8443/north/" }).StorageKey != first.StorageKey ||
            (first with { Server = "http://recorder.test:8443/north" }).StorageKey == first.StorageKey ||
            (first with { Server = "https://recorder.test/north" }).StorageKey == first.StorageKey ||
            (first with { Server = "https://recorder.test:8443/North" }).StorageKey == first.StorageKey ||
            first.StorageKey == second.StorageKey)
            throw new InvalidOperationException("The archive key must retain scheme, nondefault port and case-sensitive path.");
        var snapshot = new LiveCallSnapshot(new(), "first archive", CallCardIdFallback.None, "https://media.test/one.wav");
        await queue.StartListeningAsync(first, now, token);
        await queue.ObserveAsync(first, "overlap", now, snapshot, token, RecordingOrigin.Live);
        await queue.MarkAsync(first, "overlap", PlaybackOutcome.Completed, token);
        await queue.AdvancePlaybackFloorAsync([first], 47, token);
        var window = await queue.RequestRecoveryAsync(first, now, ex => throw ex, token);
        first.Operation!.Retire();
        await queue.StopAsync(first);
        await queue.StartListeningAsync(second, now, token);
        if (await queue.ReadRecoveryWindowAsync(second, token) != null ||
            !await queue.ObserveAsync(second, "overlap", now, snapshot with { Transcription = "second archive" }, token, RecordingOrigin.Live))
            throw new InvalidOperationException("Another archive inherited a recovery window or terminal disposition.");
        var reopened = new DurableLiveQueue(paths);
        var next = await reopened.NextAsync(second, 0, token);
        if (next?.Snapshot?.Transcription != "second archive" || next.Owner != second.Owner ||
            (await reopened.GetAccessAsync(second.Server, "overlap", second.Account, token, false, second.ServerId))?.Owner != second.Owner ||
            (await reopened.GetAccessAsync(first.Server, "overlap", first.Account, token, false, first.ServerId))?.Owner != first.Owner)
            throw new InvalidOperationException("Restart must retain each archive's row, metadata, disposition and floor.");
        // Retire after the transaction has read the old row and is preparing the
        // reference tag. An uncanceled caller still cannot publish its observation.
        var retiring = new DurableLiveQueue(paths, () => { second.Operation!.Retire(); return null; });
        try
        {
            await retiring.ObserveAsync(second, "late", now, snapshot with { AudioUrl = "https://media.test/clip?lease=test" },
                token, RecordingOrigin.Live);
            throw new InvalidOperationException("A retired observation committed to the journal.");
        }
        catch (RecordingAccessException) { }
        try
        {
            await reopened.MarkAsync(second, "overlap", PlaybackOutcome.Completed, token);
            throw new InvalidOperationException("A retired operation advanced the durable disposition.");
        }
        catch (RecordingAccessException) { }
        if (await reopened.GetArrivalSequenceAsync(second, "late", token) != null ||
            !await reopened.IsPendingAsync(second, "overlap", 0, token))
            throw new InvalidOperationException("Ownership loss must roll back the complete write.");
        var back = first with { Owner = 3, Operation = new RecorderOperation() };
        await reopened.OpenAsync(back, now, token);
        if (window == null || (await reopened.ReadRecoveryWindowAsync(back, token))?.Id != window.Id ||
            await reopened.ObserveAsync(back, "overlap", now, snapshot, token, RecordingOrigin.Live) ||
            await reopened.ReadRecoveryWindowAsync(second, token) != null)
            throw new InvalidOperationException("Returning to the original archive must retain its window and completed recording.");
    }

    private static async Task AmbiguousCheckpointAsync(Paths paths, int version)
    {
        var saved = Guid.NewGuid();
        var account = new string('A', 64);
        const string endpoint = "https://scanner.test/archive";
        using var db = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
        await db.OpenAsync();
        SeedV5(db, version);
        using var seed = db.CreateCommand();
        seed.CommandText = """
            CREATE TABLE app_settings (setting_key TEXT PRIMARY KEY, setting_value TEXT NOT NULL, updated_utc TEXT NOT NULL);
            INSERT INTO app_settings VALUES('theme_mode','Dark','original');
            INSERT INTO live_queue_servers(server,account_scope,owner,playback_floor)
            VALUES($id,$account,51,36),($url,'',52,37),($url,$account,53,38);
            INSERT INTO live_queue_recovery VALUES($id,$account,'custom-v5-window',7,1234,0),($url,'','unknown-login',8,2345,1),
                ($url,$account,'proven-hosted',9,3456,0);
            INSERT INTO live_queue_recordings(server,account_scope,recording_id,first_owner,audio_ready,protected)
            VALUES($url,'','shared',52,1,0),($url,$account,'shared',53,0,1);
            CREATE TABLE schema_meta(id INTEGER PRIMARY KEY,schema_version INTEGER NOT NULL,updated_utc TEXT NOT NULL);
            CREATE TRIGGER abort_at_end BEFORE INSERT ON schema_meta BEGIN SELECT RAISE(ABORT,'late interruption'); END;
            """;
        seed.Parameters.AddWithValue("$id", saved.ToString("N"));
        seed.Parameters.AddWithValue("$account", account);
        seed.Parameters.AddWithValue("$url", endpoint);
        await seed.ExecuteNonQueryAsync();
        if (version == 6)
        {
            seed.CommandText = """
                INSERT INTO live_queue_servers(server,account_scope,owner) VALUES($url,'custom:'||$account,54);
                INSERT INTO live_queue_recordings(server,account_scope,recording_id,first_owner,audio_ready,protected)
                VALUES($url,'custom:'||$account,'shared',54,0,0);
                """;
            await seed.ExecuteNonQueryAsync();
        }
        try
        {
            await DbBootstrapper.EnsureInitializedAsync(db);
            throw new InvalidOperationException("The late migration interruption did not run.");
        }
        catch (SqliteException ex) when (ex.Message.Contains("late interruption", StringComparison.Ordinal)) { }
        if (Scalar(db, "PRAGMA user_version") != version || Scalar(db, "SELECT count(*) FROM live_queue_servers WHERE server LIKE 'archived:%'") != 0)
            throw new InvalidOperationException("A failed migration left a partial archive remap.");
        seed.CommandText = "DROP TRIGGER abort_at_end";
        await seed.ExecuteNonQueryAsync();
        if (DatabaseRecovery.PrepareMain(paths.DbPath))
            throw new InvalidOperationException("A supported migration used corruption salvage.");
        var queue = new DurableLiveQueue(paths);
        var custom = new DurableLiveQueue.Run(endpoint, 61, null, Account: account, ServerId: saved);
        var hosted = custom with { Owner = 62, Protected = true, VerifiedTier = 2 };
        var anonymous = custom with { Owner = 63, ServerId = null, Account = null };
        foreach (var run in new[] { custom, hosted, anonymous })
        {
            await queue.OpenAsync(run, DateTimeOffset.UtcNow, default);
            if (await queue.ReadRecoveryWindowAsync(run, default) != null ||
                await queue.GetAccessAsync(run.Server, "shared", run.Account, default, run.Protected, run.ServerId) != null)
                throw new InvalidOperationException("A new run inherited an ambiguous checkpoint or recording.");
        }
        DbBootstrapper.Forget(paths.DbPath);
        if (DatabaseRecovery.PrepareMain(paths.DbPath) ||
            Scalar(db, "SELECT count(*) FROM live_queue_recovery WHERE window_id IN ('custom-v5-window','unknown-login','proven-hosted')") != 3 ||
            Scalar(db, "SELECT count(*) FROM live_queue_servers WHERE owner IN (51,52) AND playback_floor IN (36,37)") != 2 ||
            (await queue.GetAccessAsync(endpoint, "shared", account, default))?.Owner != 53 ||
            (version == 6 && (await queue.GetAccessAsync(endpoint, "shared", account, default, false))?.Owner != 54) ||
            Scalar(db, "SELECT count(*) FROM app_settings WHERE setting_key='theme_mode' AND setting_value='Dark'") != 1 ||
            Scalar(db, "SELECT count(*) FROM pragma_foreign_key_check") != 0 ||
            Scalar(db, "SELECT count(*) FROM pragma_integrity_check WHERE integrity_check<>'ok'") != 0)
            throw new InvalidOperationException("Restart lost physical rows, proven account history, unrelated data or integrity.");
    }

    private static void SeedV5(SqliteConnection connection, int version = 5)
    {
        using var cmd = connection.CreateCommand();
        var protection = version == 5 ? "protected=(account_scope<>'')" :
            "(protected=1 AND account_scope<>'' AND account_scope NOT GLOB 'custom:*') OR " +
            "(protected=0 AND (account_scope='' OR account_scope GLOB 'custom:?*'))";
        cmd.CommandText = $$"""
            PRAGMA user_version={{version}};
            CREATE TABLE live_queue_servers(server TEXT NOT NULL, account_scope TEXT NOT NULL DEFAULT '',
                owner INTEGER NOT NULL, stopped INTEGER NOT NULL DEFAULT 0, accept_after INTEGER NOT NULL DEFAULT 0,
                resume_id TEXT, playback_floor INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(server, account_scope));
            CREATE TABLE live_queue_recordings(arrival_sequence INTEGER PRIMARY KEY AUTOINCREMENT, server TEXT NOT NULL,
                account_scope TEXT NOT NULL DEFAULT '', recording_id TEXT NOT NULL, recorded_utc INTEGER, first_owner INTEGER NOT NULL,
                payload TEXT, audio_ready INTEGER NOT NULL, disposition INTEGER NOT NULL DEFAULT 0 CHECK(disposition IN (0,1,2)),
                origin INTEGER NOT NULL DEFAULT 0, protected INTEGER NOT NULL DEFAULT 0 CHECK({{protection}}),
                recovery_backlog INTEGER NOT NULL DEFAULT 0, audio_revision INTEGER NOT NULL DEFAULT 0, audio_recovered INTEGER NOT NULL DEFAULT 0,
                UNIQUE(server, account_scope, recording_id), FOREIGN KEY(server, account_scope) REFERENCES live_queue_servers(server, account_scope));
            CREATE TABLE live_queue_recovery(server TEXT NOT NULL,account_scope TEXT NOT NULL,window_id TEXT NOT NULL,
                revision INTEGER NOT NULL,since_utc INTEGER NOT NULL,skip_unseen INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(server, account_scope), FOREIGN KEY(server,account_scope) REFERENCES live_queue_servers(server,account_scope));
            INSERT INTO live_queue_servers(server, owner, playback_floor) VALUES('old',1,88);
            INSERT INTO live_queue_recovery VALUES('old','','window',7,100,1);
            INSERT INTO live_queue_recordings(arrival_sequence,server,recording_id,first_owner,payload,audio_ready,disposition,recovery_backlog,audio_revision,audio_recovered)
                VALUES(19,'old','old',1,'{}',0,2,1,4,1),(99,'old','pruned',1,NULL,0,1,0,0,0);
            DELETE FROM live_queue_recordings WHERE arrival_sequence=99;
            """;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Reject(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 275) { return; }
        throw new InvalidOperationException("The schema accepted a recording under the wrong protection mode.");
    }
}
