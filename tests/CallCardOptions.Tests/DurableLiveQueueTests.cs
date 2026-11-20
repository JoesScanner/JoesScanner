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
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
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
            Check(Convert.ToInt32(check.ExecuteScalar()) == 4, "Version 1 should migrate to version 4.");
        }

        // Newest first, over the old 100-row recovery and 500-row cache limits;
        // each pair has the same timestamp and must sort deterministically by ID.
        for (var i = 509; i >= 0; i--)
            await Add(queue, run, $"{i:D4}", Start.AddSeconds(i / 2));
        await Add(queue, other, "0000", Start);
        for (var i = 0; i < 510; i++)
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
        Check((await queue.NextAsync(secondRestart, 0, default))?.Id == "arrived-out-of-order",
            "Automatic recovery must still select the oldest eligible recording.");
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
        await Add(queue, run, "live", Start, playable: false);
        var standard = Start.AddHours(-24).UtcTicks;
        Check((await queue.NextAsync(run, standard, default))?.Id == "live",
            "A current-owner historical import must not inherit the Live exemption, even after duplicate delivery.");
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
        await Add(queue, next, "missed-while-stopped", Start.AddHours(1));
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
            "Jump must fence earlier inserts and preserve its selected recording.");
        await queue.MarkAsync(next, "jump-target", PlaybackOutcome.Completed, default);
        Check(await queue.NextAsync(next, 0, default) == null, "Manual completion and Jump must not reorder or replay recordings.");
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
