using System.Text.Json;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;

internal static class DurableAccountIsolationTests
{
    private const string Hosted = "https://app.joesscanner.com";
    private const string DirectoryServer = "https://directory.test/scanner";
    private const string Custom = "https://custom.example/recorder";
    private const string Unknown = "https://unclassified.example/recorder";
    private static readonly string AccountA = new('A', 64);
    private static readonly string AccountB = new('B', 64);
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.QueueAccounts-");
        try
        {
            await IsolateScopesAsync(new Paths(Path.Combine(directory.FullName, "scopes.db")));
            await AccountSwitchDuringPlaybackAsync(Path.Combine(directory.FullName, "playback.db"));
            await AccountSwitchDuringRecoveryAsync(Path.Combine(directory.FullName, "recovery.db"));
            await MigrateAsync(new Paths(Path.Combine(directory.FullName, "legacy-v2.db")), 2);
            await MigrateAsync(new Paths(Path.Combine(directory.FullName, "legacy-v3.db")), 3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task IsolateScopesAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var a = new DurableLiveQueue.Run(Hosted, 1, 2, Protected: true, Account: AccountA);
        var b = a with { Owner = 2, Account = AccountB };
        await queue.OpenAsync(a, Now, default);
        await queue.OpenAsync(b, Now, default);

        foreach (var id in new[] { "completed", "skipped", "interrupted", "recent" })
        {
            var time = id == "recent" ? Now : Now.AddHours(-30);
            Check(await Observe(queue, a, id, time, "A") && await Observe(queue, b, id, time, "B"),
                "Equal recorder IDs must be insertable for both accounts.");
        }
        await queue.MarkPlayingAsync(a, "completed", default);
        await new DurablePlaybackCommit(queue.CommitCompletionAsync).ConfirmAsync(a, "completed");
        await queue.MarkAsync(a, "skipped", PlaybackOutcome.Skipped, default);
        await queue.MarkPlayingAsync(a, "interrupted", default);
        await queue.MarkAsync(a, "interrupted", PlaybackOutcome.Canceled, default);
        Check(await queue.IsPendingAsync(b, "completed", 0, default) && await queue.IsPendingAsync(b, "skipped", 0, default),
            "Completing or skipping a recording under A must leave B's copy pending.");
        Check((await queue.NextAsync(a, 0, default))?.Snapshot?.Metadata.Talkgroup == "A" &&
              (await queue.NextAsync(b, 0, default))?.Snapshot?.Metadata.Talkgroup == "B",
            "Reading the queue must never borrow the other account's metadata.");

        await queue.StopAsync(a);
        for (var index = 0; index < 510; index++)
            await Observe(queue, b, $"bulk-{index:D3}", Now.AddSeconds(index + 1), "B");
        var jump = await queue.JumpToLiveAsync(b, 0, skipSelected: true, default);
        Check(jump?.Target.Id == "bulk-509", "Jump to Live must find the last recording in its own backlog.");
        Check(await queue.NextAsync(b, 0, default) == null, "Jump must retire B's selected backlog.");
        var previous = await queue.FindTransportTargetAsync(b, 0, jump!.Target.Order, older: true, default);
        Check(previous?.Id == "bulk-508" &&
              (await queue.FindTransportTargetAsync(b, 0, previous.Order, older: false, default))?.Id == "bulk-509",
            "Previous and Next must navigate B's scoped journal.");
        await queue.CommitCompletionAsync(a, "interrupted");
        Check(!await Observe(queue, a, "delayed", Now, "retired"), "A retired observation must not commit.");

        var reopened = new DurableLiveQueue(paths);
        var aAgain = a with { Owner = 3 };
        var bAgain = b with { Owner = 4 };
        await reopened.OpenAsync(aAgain, Now, default);
        await reopened.OpenAsync(bAgain, Now, default);
        Check((await reopened.NextAsync(aAgain, 0, default))?.Id == "interrupted" &&
              await reopened.NextAsync(bAgain, 0, default) == null,
            "Interrupted A playback and terminal B dispositions must survive switching and reopening.");
        Check(!await reopened.IsPendingAsync(aAgain, "completed", 0, default) &&
              !await reopened.IsPendingAsync(aAgain, "skipped", 0, default),
            "A's original completion and skip must survive switching back.");
        Check(await reopened.GetAccessAsync(Hosted, "bulk-509", AccountA, default) == null &&
              await reopened.GetAccessAsync(Hosted, "interrupted", null, default) == null,
            "Manual and retry lookups must require the correct account scope.");
        var downgraded = DurableLiveQueue.HistoricalFloor(true, 2, true, 1, Now);
        var expired = DurableLiveQueue.HistoricalFloor(true, 2, false, 2, Now);
        Check((await reopened.NextAsync(aAgain, downgraded, default))?.Id == "recent" &&
              await reopened.NextAsync(aAgain, expired, default) == null &&
              await reopened.NextAsync(aAgain, DurableLiveQueue.HistoricalFloor(true, 2, true, 0, Now), default) == null,
            "Switching back must respect downgrade, expiry, and Level 0.");
        Check(await Observe(reopened, aAgain, "arrival-before-B-jump", Now.AddSeconds(1), "A"),
            "B's Jump watermark must not suppress A's recovery.");

        var custom = new DurableLiveQueue.Run(Custom, 5, null);
        var secondCustom = custom with { Server = "https://custom.example/other" };
        await reopened.OpenAsync(custom, Now, default);
        await reopened.OpenAsync(secondCustom, Now, default);
        await Observe(reopened, custom, "completed", Now, "custom");
        await Observe(reopened, secondCustom, "completed", Now, "other custom");
        await reopened.MarkAsync(custom, "completed", PlaybackOutcome.Completed, default);
        Check((await reopened.NextAsync(secondCustom, 0, default))?.Snapshot?.Metadata.Talkgroup == "other custom" &&
              !await Observe(reopened, custom with { Account = AccountB }, "completed", Now, "same recorder"),
            "Custom queue identity stays recorder-based and distinct across recorder paths.");
        Console.WriteLine("PASS Hosted account metadata, duplicate IDs, completion, Skip, Jump, Previous/Next, large backlog, restart and entitlement isolation");
    }

    private static async Task AccountSwitchDuringPlaybackAsync(string path)
    {
        var audio = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), audio, path);
        await live.StartForTestAsync(AccountA);
        await live.AddForTestAsync("same");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        audio.Playing = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var old = live.StartQueueForTestAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await live.StopForTestAsync();
        await old.WaitAsync(TimeSpan.FromSeconds(10));

        audio.Playing = null;
        await live.StartForTestAsync(AccountB);
        await live.AddForTestAsync("same");
        await live.StartQueueForTestAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(!await live.IsPendingForTestAsync("same") && audio.Completed.SequenceEqual(["same"]),
            "B can play and complete the same ID after A was interrupted.");
        await live.StopForTestAsync();
        await live.StartForTestAsync(AccountA);
        Check(await live.IsPendingForTestAsync("same"), "B's native completion must not complete A's interrupted recording.");
        Console.WriteLine("PASS Monitoring account switch during native playback preserves the interrupted recording");
    }

    private static async Task AccountSwitchDuringRecoveryAsync(string path)
    {
        var audio = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), audio, path);
        await live.StartForTestAsync(AccountA);
        await live.AddForTestAsync("same");
        var delay = live.HoldNextSelection();
        var stale = live.StartQueueForTestAsync();
        await delay.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await live.StopForTestAsync();
        await live.StartForTestAsync(AccountB);
        await live.AddForTestAsync("same");
        delay.Return.TrySetResult();
        await stale.WaitAsync(TimeSpan.FromSeconds(10));
        Check(audio.Started.Count == 0 && await live.IsPendingForTestAsync("same"),
            "A delayed selection from A cannot start sound or change B's disposition.");
        await live.StartQueueForTestAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(audio.Completed.SequenceEqual(["same"]), "Only B's selection should play after the switch.");
        Console.WriteLine("PASS Monitoring account switch during recovery rejects the retired SQLite selection");
    }

    private static async Task MigrateAsync(Paths paths, int version)
    {
        await using (var legacy = await OpenAsync(paths))
        {
            Execute(legacy, $"""
                PRAGMA foreign_keys=OFF;
                PRAGMA user_version={version};
                CREATE TABLE live_queue_servers (
                    server TEXT PRIMARY KEY, owner INTEGER NOT NULL, stopped INTEGER NOT NULL DEFAULT 0,
                    accept_after INTEGER NOT NULL DEFAULT 0, resume_id TEXT, jump_utc INTEGER, jump_id TEXT
                );
                CREATE TABLE live_queue_recordings (
                    server TEXT NOT NULL REFERENCES live_queue_servers(server), recording_id TEXT NOT NULL,
                    recorded_utc INTEGER NOT NULL, first_owner INTEGER NOT NULL, payload TEXT,
                    audio_ready INTEGER NOT NULL, disposition INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(server, recording_id)
                );
                CREATE TABLE servers (server_key TEXT PRIMARY KEY, base_url TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1, sort_order INTEGER NOT NULL DEFAULT 0,
                    uses_api_firewall_credentials INTEGER NOT NULL);
                INSERT INTO servers (server_key, base_url, uses_api_firewall_credentials)
                VALUES ('directory', 'https://directory.test/', 1), ('custom', $custom, 0);
                INSERT INTO live_queue_servers VALUES
                    ($hosted, 8, 0, 19, 'pending', $time, 'skipped'),
                    ($directory, 9, 0, 0, 'protected-unknown', NULL, NULL),
                    ($custom, 10, 0, 0, 'custom-pending', NULL, NULL);
                INSERT INTO live_queue_recordings (rowid, server, recording_id, recorded_utc, first_owner, payload, audio_ready, disposition)
                VALUES
                    (50, $hosted, 'completed', $time, 8, NULL, 0, 1),
                    (52, $hosted, 'skipped', $time, 8, NULL, 0, 2),
                    (54, $hosted, 'pending', $time + 1, 8, $payload, 1, 0),
                    (55, $directory, 'protected-unknown', $time, 9, $payload, 1, 0),
                    (56, $custom, 'custom-pending', $time, 10, $payload, 1, 0),
                    (57, $unknown, 'unknown-orphan', $time, 11, $payload, 1, 0);
                CREATE TABLE schema_meta (id INTEGER PRIMARY KEY, schema_version INTEGER NOT NULL, updated_utc TEXT NOT NULL);
                CREATE TRIGGER interrupt_migration BEFORE INSERT ON schema_meta
                BEGIN SELECT RAISE(ABORT, 'fixture interrupts migration after reconstruction'); END;
                """, ("$hosted", Hosted), ("$directory", DirectoryServer), ("$custom", Custom), ("$unknown", Unknown),
                ("$time", Now.UtcTicks), ("$payload", JsonSerializer.Serialize(Snapshot("legacy"))));
            if (version == 3)
                Execute(legacy, """
                    ALTER TABLE live_queue_recordings ADD COLUMN origin INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE live_queue_recordings ADD COLUMN protected INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE live_queue_recordings ADD COLUMN account_scope TEXT NULL;
                    UPDATE live_queue_recordings SET origin=2, protected=1, account_scope=$account WHERE server=$hosted;
                    UPDATE live_queue_recordings SET protected=1, account_scope='unreliable' WHERE server=$directory;
                    """, ("$account", AccountA.ToLowerInvariant()), ("$hosted", Hosted), ("$directory", DirectoryServer));

            try
            {
                await DbBootstrapper.EnsureInitializedAsync(legacy);
                throw new InvalidOperationException("Expected the late migration failure.");
            }
            catch (SqliteException ex) when (ex.Message.Contains("fixture interrupts migration", StringComparison.Ordinal)) { }
            Check(Scalar(legacy, "PRAGMA user_version") == version &&
                  Scalar(legacy, "SELECT count(*) FROM live_queue_recordings") == 6 &&
                  Scalar(legacy, "SELECT count(*) FROM pragma_table_info('live_queue_recordings') WHERE pk > 0") == 2 &&
                  Scalar(legacy, "SELECT count(*) FROM sqlite_schema WHERE name LIKE '%_v4'") == 0,
                "Failure after reconstruction must roll back rows, schema, and version together.");
            Execute(legacy, "DROP TRIGGER interrupt_migration;");
        }

        var queue = new DurableLiveQueue(paths);
        var a = new DurableLiveQueue.Run(Hosted, 101, 2, Protected: true, Account: AccountA);
        var b = a with { Owner = 102, Account = AccountB };
        var custom = new DurableLiveQueue.Run(Custom, 103, null);
        foreach (var run in new[] { a, b, custom })
            await queue.OpenAsync(run, Now, default);
        var migrated = await queue.NextAsync(a, 0, default);
        Check(version == 2 ? migrated == null : migrated is { Id: "pending", Owner: 8, RecordedUtc: var ticks,
                Access.Origin: RecordingOrigin.History } && ticks == Now.UtcTicks + 1 &&
                migrated.Snapshot?.Metadata.Talkgroup == "legacy",
            "Only reliable v3 ownership may restore hosted recordings and their provenance.");
        Check(await queue.NextAsync(b, 0, default) == null &&
              await queue.GetAccessAsync(DirectoryServer, "protected-unknown", AccountA, default) == null &&
              await queue.GetAccessAsync(Unknown, "unknown-orphan", null, default) == null,
            "Ambiguous historical records must never be available to an active account or custom queue.");
        Check((await queue.NextAsync(custom, 0, default))?.Id == "custom-pending", "Known custom recordings must remain usable.");
        Check(await Observe(queue, b, "completed", Now, "new"), "A legacy terminal row cannot suppress an unrelated account.");
        if (version == 3)
            Check(!await Observe(queue, a, "completed", Now, "duplicate") &&
                  !await Observe(queue, a, "skipped", Now, "duplicate"),
                "Known completed and skipped dispositions must remain terminal after migration.");

        await using var reopened = await OpenAsync(paths);
        await DbBootstrapper.EnsureInitializedAsync(reopened);
        Check(Scalar(reopened, "PRAGMA user_version") == 4 &&
              Scalar(reopened, "SELECT schema_version FROM schema_meta") == 4 &&
              Scalar(reopened, "SELECT count(*) FROM pragma_foreign_key_check") == 0 &&
              Scalar(reopened, "SELECT count(*) FROM live_queue_recordings WHERE rowid BETWEEN 50 AND 57") == 6 &&
              Scalar(reopened, "SELECT count(*) FROM live_queue_recordings WHERE account_scope=':legacy'") == (version == 2 ? 5 : 2) &&
              Scalar(reopened, "SELECT count(*) FROM live_queue_servers WHERE account_scope=':legacy' AND owner=8 AND resume_id='pending' AND jump_id='skipped'") == 1 &&
              Scalar(reopened, "SELECT count(*) FROM live_queue_servers WHERE account_scope='' AND resume_id='custom-pending'") == 1,
            "Migration must preserve rowids and archived run state with consistent keys and version metadata.");
        Console.WriteLine($"PASS Populated v{version} migration, late rollback, reopen, preserved dispositions and conservative legacy scope");
    }

    private static Task<bool> Observe(DurableLiveQueue queue, DurableLiveQueue.Run run, string id, DateTimeOffset time, string text) =>
        queue.ObserveAsync(run, id, time, Snapshot(text), default, RecordingOrigin.History);

    private static LiveCallSnapshot Snapshot(string label) =>
        new(new CallCardMetadata { Talkgroup = label }, null, CallCardIdFallback.None, "clip.mp3");

    private static async Task<SqliteConnection> OpenAsync(Paths path)
    {
        var connection = new SqliteConnection($"Data Source={path.DbPath}");
        await connection.OpenAsync();
        return connection;
    }

    private static int Scalar(SqliteConnection connection, string sql) =>
        Convert.ToInt32(Execute(connection, sql));

    private static object? Execute(SqliteConnection connection, string sql, params (string Key, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters)
            command.Parameters.AddWithValue(key, value);
        return command.ExecuteScalar();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Paths(string DbPath) : IDatabasePathProvider;
}
