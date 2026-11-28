using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class DurableLiveQueueTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private const string Server = "https://scanner.example/one";

    public static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.DurableQueue-");
        var database = new Paths(Path.Combine(directory.FullName, "calls.db"));
        try
        {
            await MigrationAndOverflowAsync(database);
            await RestartAndFailureAsync(database);
            await StopJumpAndEntitlementAsync(database);
            await ProtectedProvenanceAsync(database);
            await FreshListeningBoundaryAsync(new Paths(Path.Combine(directory.FullName, "session.db")));
            await MigrateV4Async(new Paths(Path.Combine(directory.FullName, "v4.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task FreshListeningBoundaryAsync(Paths database)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var queue = new DurableLiveQueue(database);
        var first = new DurableLiveQueue.Run(Server, 1, 2, Protected: true, Account: "A");
        var otherAccount = first with { Account = "B" };
        var otherServer = first with { Server = "https://scanner.example/two" };
        foreach (var scope in new[] { first, otherAccount, otherServer })
        {
            await queue.StartListeningAsync(scope, Start, token);
            await Add(queue, scope, "existing", Start, origin: RecordingOrigin.Live);
        }

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jump = queue.JumpToLiveAsync(first, 0, skipSelected: false, token, prepareBoundary: async () =>
        {
            held.SetResult();
            await release.Task.WaitAsync(token);
            return 0;
        });
        await held.Task.WaitAsync(token);
        var second = first with { Owner = 2 };
        var starting = queue.StartListeningAsync(second, Start.AddMinutes(1), token);
        var newCall = queue.ObserveAsync(second, "new", Start.AddDays(-1), new(new(), "New", 0), token, RecordingOrigin.Live);
        var staleCall = queue.ObserveAsync(first, "late-old", Start, new(new(), "Old", 0), token, RecordingOrigin.Live);
        Check(!newCall.IsCompleted && !starting.IsCompleted, "The new observation must wait behind the reserved start boundary.");
        release.TrySetResult();
        await jump;
        await starting;
        Check(await newCall && !await staleCall, "A new-owner arrival survives the boundary and late old-owner work is rejected.");
        await queue.StartListeningAsync(second, Start.AddMinutes(2), token);
        Check((await queue.NextAsync(second, 0, token))?.Id == "new" && !await queue.IsPendingAsync(second, "existing", 0, token),
            "Repeating the same start must keep current arrivals eligible.");
        Check((await queue.NextAsync(otherAccount, 0, token))?.Id == "existing" &&
              (await queue.NextAsync(otherServer, 0, token))?.Id == "existing", "The boundary must affect only the selected server and account.");
        Console.WriteLine("PASS The fresh-session transaction fences concurrent observations and preserves other account and server queues");
    }

    private static async Task MigrateV4Async(Paths paths)
    {
        using var db = new SqliteConnection($"Data Source={paths.DbPath}");
        db.Open();
        using var sql = db.CreateCommand();
        sql.CommandText = """
            PRAGMA user_version=4;
            CREATE TABLE live_queue_servers (
                server TEXT NOT NULL, account_scope TEXT NOT NULL, owner INTEGER NOT NULL,
                stopped INTEGER NOT NULL DEFAULT 0, accept_after INTEGER NOT NULL DEFAULT 0,
                resume_id TEXT, jump_utc INTEGER, jump_id TEXT, PRIMARY KEY (server, account_scope));
            CREATE TABLE live_queue_recordings (
                server TEXT NOT NULL, account_scope TEXT NOT NULL, recording_id TEXT NOT NULL,
                recorded_utc INTEGER NOT NULL, first_owner INTEGER NOT NULL, payload TEXT,
                audio_ready INTEGER NOT NULL, disposition INTEGER NOT NULL, origin INTEGER NOT NULL,
                protected INTEGER NOT NULL, PRIMARY KEY (server, account_scope, recording_id),
                FOREIGN KEY (server, account_scope) REFERENCES live_queue_servers(server, account_scope));
            CREATE INDEX live_queue_transport_order
                ON live_queue_recordings(server, account_scope, recorded_utc, recording_id);
            INSERT INTO live_queue_servers VALUES ($server, $account, 6, 0, 0, 'older-stamp', 10000, 'skipped');
            INSERT INTO live_queue_servers VALUES ($hosted, 'account-one', 7, 1, 0, NULL, NULL, NULL);
            INSERT INTO live_queue_recordings(rowid, server, account_scope, recording_id, recorded_utc, first_owner,
                payload, audio_ready, disposition, origin, protected) VALUES
                (10, $server, $account, 'newer-stamp', 10000, 6, NULL, 0, 0, 1, 1),
                (20, $server, $account, 'completed', 6000, 6, NULL, 0, 1, 1, 1),
                (30, $server, $account, 'skipped', 4000, 6, NULL, 0, 2, 2, 1),
                (40, $server, $account, 'older-stamp', 3000, 6, NULL, 0, 0, 1, 1),
                (50, $hosted, 'account-one', 'older-stamp', 2000, 7, NULL, 0, 0, 2, 1);
            WITH RECURSIVE ids(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM ids WHERE n<100001)
            INSERT INTO live_queue_recordings(server, account_scope, recording_id, recorded_utc, first_owner,
                payload, audio_ready, disposition, origin, protected)
            SELECT $server, $account, 'prior-' || n, n, 6, NULL, 0, 1, 1, 1 FROM ids;
            CREATE TABLE live_queue_recordings_v6 (conflict INTEGER);
            """;
        const string hosted = "https://app.joesscanner.com";
        var provenAccount = new string('C', 64);
        sql.Parameters.AddWithValue("$account", provenAccount);
        sql.Parameters.AddWithValue("$server", Server);
        sql.Parameters.AddWithValue("$hosted", hosted);
        sql.ExecuteNonQuery();

        try
        {
            await DbBootstrapper.EnsureInitializedAsync(db);
            throw new InvalidOperationException("The injected migration error should have aborted the upgrade.");
        }
        catch (SqliteException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal)) { }
        sql.CommandText = "PRAGMA user_version";
        Check((long)sql.ExecuteScalar()! == 4, "An interrupted migration must keep the v4 version.");
        sql.CommandText = "DROP TABLE live_queue_recordings_v6";
        sql.ExecuteNonQuery();
        await DbBootstrapper.EnsureInitializedAsync(db);

        sql.CommandText = "PRAGMA user_version";
        Check((long)sql.ExecuteScalar()! == DbBootstrapper.CurrentSchemaVersion, "The retry must install the current schema.");
        sql.CommandText = "SELECT count(*) FROM live_queue_recordings";
        Check((long)sql.ExecuteScalar()! == 100006, "Migration must preserve all identities.");
        sql.CommandText = "SELECT resume_id FROM live_queue_servers WHERE server = $server";
        Check((string?)sql.ExecuteScalar() == "older-stamp", "Migration must retain the existing resume record.");
        sql.CommandText = "PRAGMA foreign_key_check";
        Check(sql.ExecuteScalar() == null, "Migration must retain valid scope references.");

        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run(Server, 8, 2, Protected: true, Account: provenAccount);
        await queue.OpenAsync(run, Start, default);
        Check((await queue.NextAsync(run, 0, default)) is { Id: "newer-stamp", ArrivalSequence: 10 } &&
              (await queue.FindTransportTargetAsync(run, 0, 10, older: false, default)) is { Id: "completed", ArrivalSequence: 20 },
            "SQLite's legacy insertion rowids must determine migration order, including terminal rows.");
        using (var plan = DurableLiveQueue.CreateTransportTargetCommand(db, run, 0, 50000, older: false))
        {
            plan.CommandText = "EXPLAIN QUERY PLAN " + plan.CommandText;
            using var rows = plan.ExecuteReader();
            var indexSeek = false;
            while (rows.Read())
            {
                var step = rows.GetString(3);
                indexSeek |= step.Contains("live_queue_transport_arrival", StringComparison.Ordinal) &&
                    step.Contains("arrival_sequence", StringComparison.Ordinal);
                Check(!step.Contains("TEMP B-TREE", StringComparison.Ordinal), "Large queues must not be re-sorted for navigation.");
            }
            Check(indexSeek, "The transport must seek within the scoped arrival index with 100,000+ identities.");
        }
        await queue.CommitCompletionAsync(run, "newer-stamp");
        Check((await queue.NextAsync(run, 0, default)) is { Id: "older-stamp", ArrivalSequence: 40 } &&
            !await queue.IsPendingAsync(run, "skipped", 0, default) && !await queue.IsPendingAsync(run, "completed", 0, default),
            "V4 pending, completed and skipped dispositions must all survive migration.");
        await Add(queue, run, "post-upgrade", Start.AddYears(-20), origin: RecordingOrigin.History);
        Check(await queue.GetArrivalSequenceAsync(run, "post-upgrade", default) > 100051,
            "Newly accepted recordings must follow all migrated identities even with older timestamps.");
        var account = new DurableLiveQueue.Run(hosted, 9, null, Protected: true, Account: "account-one");
        await queue.OpenAsync(account, Start, default);
        Check((await queue.NextAsync(account, 0, default)) is { Id: "older-stamp", ArrivalSequence: 50 } &&
            await queue.GetArrivalSequenceAsync(account with { Account = "other" }, "older-stamp", default) == null,
            "Migration must keep identical IDs isolated by server and account.");
        Console.WriteLine("PASS transactional v4 migration, legacy rowids and dispositions, scoped indexing at 100,000+ identities");
    }

    private static async Task MigrationAndOverflowAsync(Paths database)
    {
        using (var legacy = new SqliteConnection($"Data Source={database.DbPath}"))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                PRAGMA user_version=1;
                CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO app_settings VALUES ('durability_test', 'keep');
                """;
            create.ExecuteNonQuery();
        }
        var queue = new DurableLiveQueue(database);
        var run = new DurableLiveQueue.Run(Server, 1, null);
        var otherServer = DurableLiveQueue.ServerKey("https://scanner.example/two/")!;
        var other = new DurableLiveQueue.Run(otherServer, 2, null);
        await queue.OpenAsync(run, Start, default);
        await queue.OpenAsync(other, Start, default);
        Check(DurableLiveQueue.ServerKey("https://SCANNER.example:443/one/") == Server, "Canonical URI must be stable.");
        Check(other.Server != run.Server, "Same-origin recorder paths must be independent.");

        using (var upgraded = new SqliteConnection($"Data Source={database.DbPath}"))
        {
            upgraded.Open();
            using var check = upgraded.CreateCommand();
            check.CommandText = "SELECT value FROM app_settings WHERE key = 'durability_test'";
            Check((string?)check.ExecuteScalar() == "keep", "Migration must preserve existing settings.");
            check.CommandText = "PRAGMA user_version";
            Check(Convert.ToInt32(check.ExecuteScalar()) == DbBootstrapper.CurrentSchemaVersion, "Version 1 should migrate to the current schema.");
        }

        // Newest first, over the old 100-row recovery and 500-row cache limits;
        // each pair has the same timestamp and must retain its arrival order.
        for (var i = 509; i >= 0; i--)
            await Add(queue, run, $"{i:D4}", Start.AddSeconds(i / 2));
        await Add(queue, other, "0000", Start);
        for (var i = 509; i >= 0; i--)
        {
            var id = $"{i:D4}";
            Check((await queue.NextAsync(run, 0, default))?.Id == id, "Playback order changed or a recording was lost.");
            await queue.MarkAsync(run, id, PlaybackOutcome.Completed, default);
        }
        Check(await queue.NextAsync(run, 0, default) == null &&
            (await queue.NextAsync(other, 0, default))?.Id == "0000", "A completion must stay on its originating server.");
        Console.WriteLine("PASS Durable queue migration, 510-row overflow, same-timestamp ordering, and same-origin server isolation");
    }

    private static async Task RestartAndFailureAsync(Paths database)
    {
        var queue = new DurableLiveQueue(database);
        var run = new DurableLiveQueue.Run(Server, 3, null);
        await queue.OpenAsync(run, Start, default);
        await Add(queue, run, "pending-audio", Start.AddMinutes(30), playable: false);
        await Add(queue, run, "later", Start.AddMinutes(31));
        Check((await queue.NextAsync(run, 0, default)) is { Id: "pending-audio", Snapshot.AudioUrl: null },
            "An older recording missing audio must hold its position.");
        await queue.MarkAsync(run, "later", PlaybackOutcome.Failed, default);
        await queue.MarkAsync(run, "later", PlaybackOutcome.Canceled, default);

        // Reopen as a new process with the persisted SQLite state and no old CallItem.
        queue = new DurableLiveQueue(database);
        var restarted = run with { Owner = 4 };
        await queue.OpenAsync(restarted, Start.AddHours(1), default);
        Check((await queue.NextAsync(restarted, 0, default))?.Id == "pending-audio", "The unresolved head must survive restart.");
        await queue.MarkAsync(run, "later", PlaybackOutcome.Completed, default);
        Check(await queue.IsPendingAsync(restarted, "later", 0, default), "Old owners cannot complete a recording.");
        await queue.ObserveAsync(restarted, "pending-audio", null,
            new LiveCallSnapshot(new CallCardMetadata { Radio = "Unit 4" }, null, CallCardIdFallback.None,
                $"{Server}/ready.mp3"), default);
        var ready = await queue.NextAsync(restarted, 0, default);
        Check(ready is { Id: "pending-audio", Snapshot.Metadata.Radio: "Unit 4", Snapshot.Metadata.Talkgroup: "Dispatch" },
            "An update without a timestamp should make the persisted row playable without dropping existing metadata.");
        await queue.MarkAsync(restarted, "pending-audio", PlaybackOutcome.Completed, default);
        await Add(queue, restarted, "pending-audio", Start.AddMinutes(30));
        await Add(queue, restarted, "0000", Start);
        Check((await queue.NextAsync(restarted, 0, default))?.Id == "later", "Duplicate events cannot resurrect completed recordings.");
        await queue.MarkPlayingAsync(restarted, "later", default);
        await Add(queue, restarted, "arrived-out-of-order", Start.AddMinutes(29));
        var secondRestart = restarted with { Owner = 7 };
        await queue.OpenAsync(secondRestart, Start.AddHours(1), default);
        Check((await queue.NextAsync(secondRestart, 0, default))?.Id == "later",
            "The pending recording that arrived first must resume after restart.");
        await queue.MarkAsync(secondRestart, "arrived-out-of-order", PlaybackOutcome.Skipped, default);
        await queue.ObserveAsync(secondRestart, "signed", Start.AddHours(1),
            new LiveCallSnapshot(new(), null, CallCardIdFallback.None, $"{Server}/clip.mp3?signature=test"), default);
        Check((await queue.NextAsync(secondRestart, 0, default))?.Id == "later" &&
              await queue.IsPendingAsync(secondRestart, "signed", 0, default), "Signed audio can remain pending without persisting its URL.");
        await queue.MarkAsync(secondRestart, "signed", PlaybackOutcome.Skipped, default);
        Check(!await queue.IsPendingAsync(secondRestart, "signed", 0, default), "Completed transient audio must not replay from the visible list.");
        Console.WriteLine("PASS Durable queue restart, failed and interrupted attempts, late audio, and terminal deduplication");
    }

    private static async Task ProtectedProvenanceAsync(Paths database)
    {
        var queue = new DurableLiveQueue(database);
        var run = new DurableLiveQueue.Run("https://app.joesscanner.com", 81, 2, Protected: true, Account: "account-A");
        await queue.OpenAsync(run, Start, default);
        var imported = Start.AddHours(-30);
        await Add(queue, run, "historical", imported, origin: RecordingOrigin.History);
        await Add(queue, run, "historical", imported, origin: RecordingOrigin.Live);
        await Add(queue, run, "future-history", Start.AddYears(2), origin: RecordingOrigin.History);
        await Add(queue, run, "live", Start, playable: false);
        var standard = Start.AddHours(-24).UtcTicks;
        Check((await queue.NextAsync(run, standard, default))?.Id == "live",
            "Historical imports outside the Standard window must not inherit the Live exemption, even after duplicate delivery.");
        Check((await queue.NextAsync(run, long.MaxValue, default))?.Id == "live", "An unverified history floor still allows verified Live provenance.");
        var next = run with { Owner = 82 };
        await queue.OpenAsync(next, Start.AddMinutes(1), default);
        Check(await queue.NextAsync(next, long.MaxValue, default) == null, "Restart cannot exempt former Live from historical authorization.");
        var access = await queue.GetAccessAsync(run.Server, "historical", run.Account, default);
        Check(access is { Access: { Origin: RecordingOrigin.History, Protected: true, Account: "account-A" } },
            "Persisted manual and retry paths must see the original provenance after restart.");
        Check(await queue.NextAsync(next with { Account = "account-B" }, 0, default) == null,
            "Changing accounts cannot select recordings bound to the original account.");
        Console.WriteLine("PASS Durable provenance, same-run downgrade, restart, and account isolation");
    }

    private static async Task StopJumpAndEntitlementAsync(Paths database)
    {
        var queue = new DurableLiveQueue(database);
        var run = new DurableLiveQueue.Run(Server, 5, 2);
        await queue.OpenAsync(run, Start, default);
        await Add(queue, run, "before-stop", Start.AddHours(-30));
        await queue.StopAsync(run);
        await Add(queue, run, "after-stop", Start.AddHours(1));
        Check(await queue.NextAsync(run, 0, default) == null, "Stop must immediately fence the old owner.");
        var next = run with { Owner = 6, VerifiedTier = 1 };
        await queue.OpenAsync(next, Start.AddHours(2), default);
        await Add(queue, next, "missed-while-stopped", Start.AddHours(1), origin: RecordingOrigin.History);
        Check(!await queue.IsPendingAsync(next, "missed-while-stopped", 0, default),
            "HTTP backfill from the stopped interval must remain excluded.");
        await Add(queue, next, "jump-target", Start.AddHours(2));
        var now = Start.AddHours(2);
        var standard = DurableLiveQueue.HistoricalFloor(true, 2, true, 1, now);
        Check(standard == Start.AddHours(-22).UtcTicks &&
              (await queue.NextAsync(next, standard, default))?.Id == "later",
            "An authorization floor must withhold older pending items on downgrade.");
        var expired = DurableLiveQueue.HistoricalFloor(true, 2, false, 2, now);
        Check(expired == long.MaxValue && (await queue.NextAsync(next, expired, default))?.Id == "jump-target" &&
              DurableLiveQueue.HistoricalFloor(false, null, false, 0, now) == 0,
            "Failed historical verification may still allow this run's observed Live calls.");
        Check((await queue.JumpToLiveAsync(next, 0, skipSelected: false, default)) is { Target.Id: "jump-target", PlayTarget: true },
            "Jump should resolve its own newest row.");
        await Add(queue, next, "earlier-at-jump", Start.AddHours(2), origin: RecordingOrigin.History);
        Check((await queue.NextAsync(next, 0, default))?.Id == "jump-target",
            "New recovery rows must append after the selected live edge.");
        await queue.MarkAsync(next, "jump-target", PlaybackOutcome.Completed, default);
        Check((await queue.NextAsync(next, 0, default))?.Id == "earlier-at-jump",
            "An unseen recovery row arriving after Jump must still play.");
        Console.WriteLine("PASS Durable queue explicit Stop, stopped-gap exclusion, entitlement floor, manual completion, and Jump");
    }

    private static Task Add(DurableLiveQueue queue, DurableLiveQueue.Run run, string id, DateTimeOffset time, bool playable = true,
        RecordingOrigin origin = RecordingOrigin.Live) =>
        queue.ObserveAsync(run, id, time, new LiveCallSnapshot(new CallCardMetadata { Talkgroup = "Dispatch" },
            null, CallCardIdFallback.None, playable ? $"{run.Server}/{id}.mp3" : null), default, origin);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Paths(string DbPath) : IDatabasePathProvider;
}
