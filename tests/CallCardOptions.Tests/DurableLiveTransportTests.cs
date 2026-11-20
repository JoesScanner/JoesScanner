using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class DurableLiveTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
    private const string Server = "https://scanner.example/one";

    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.LiveTransport-");
        try
        {
            await BacklogTransportAsync(new Paths(Path.Combine(directory.FullName, "backlog.sqlite")));
            await HostedTransportAsync(new Paths(Path.Combine(directory.FullName, "hosted.sqlite")));
            await RapidTransportAndStopAsync(new Paths(Path.Combine(directory.FullName, "ownership.sqlite")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task BacklogTransportAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run(Server, 1, null);
        var other = run with { Server = "https://scanner.example/two" };
        await queue.OpenAsync(run, Now, default);
        await queue.OpenAsync(other, Now, default);
        for (var i = 499; i >= 0; i--)
            await Add(queue, run, $"{i:D4}", Now.AddSeconds(i / 2), audio: i != 0 && i != 100);
        await Add(queue, other, "0099", Now.AddSeconds(49));
        await Add(queue, other, "0100", Now.AddSeconds(49));
        await Add(queue, other, "0499", Now.AddSeconds(249));

        // This anchor is hundreds of recordings behind the live edge. No
        // card collections or ViewModel fixtures participate in the resolution.
        var anchor = new DurableLiveQueue.Position("0101", Now.AddSeconds(50).UtcTicks);
        var previous = await queue.FindTransportTargetAsync(run, 0, anchor, older: true, default);
        var next = await queue.FindTransportTargetAsync(run, 0, anchor, older: false, default);
        Check(previous is { Id: "0100", Snapshot.AudioUrl: null } && next?.Id == "0102",
            "Previous and Next must follow the durable timestamp/ID order and keep a missing-audio target.");
        Check(await queue.FindTransportTargetAsync(run, 0, new("0000", Now.UtcTicks), older: true, default) == null &&
            await queue.FindTransportTargetAsync(run, 0, new("0499", Now.AddSeconds(249).UtcTicks), older: false, default) == null,
            "Transport must stop at the ends of the available recordings.");
        Check(await queue.HasNewerPendingAsync(run, 0, anchor, default),
            "Offscreen recovery must still see newer pending recordings for catch-up speed.");
        Console.WriteLine("PASS Offscreen Previous and Next, missing audio, edges, and deterministic same-time ordering");

        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DbPath }.ToString()))
        {
            db.Open();
            foreach (var older in new[] { true, false })
            {
                using var command = DurableLiveQueue.CreateTransportTargetCommand(db, run, 0, anchor, older);
                command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
                using var reader = command.ExecuteReader();
                var plan = new List<string>();
                while (reader.Read())
                    plan.Add(reader.GetString(3));
                Check(plan.Any(step => step.Contains("live_queue_transport_order", StringComparison.Ordinal) &&
                        step.Contains("recorded_utc,recording_id", StringComparison.Ordinal)) &&
                    !plan.Any(step => step.Contains("TEMP B-TREE", StringComparison.Ordinal)),
                    "Previous and Next must seek within the scoped timestamp/ID index without temporary sorting.");
            }
        }
        Check((await queue.FindTransportTargetAsync(run, 0, anchor with { RecordedUtc = Now.UtcTicks },
                  older: true, default))?.Id == "0100" &&
            (await queue.FindTransportTargetAsync(run, 0, anchor with { Id = "0100-missing" },
                  older: false, default))?.Id == "0101",
            "Use the persisted anchor time when present, and the supplied time and ID when the anchor is missing.");
        Console.WriteLine("PASS Transport seeks both directions without sorting and preserves anchor timestamp fallback");

        var selected = await queue.FindTransportTargetAsync(run, 0, new("0400", Now.AddSeconds(200).UtcTicks), older: false, default);
        Check(selected?.Id == "0401", "Manual selection must identify the correct row before playback.");
        var completion = new PlaybackCompletion();
        var commit = new DurablePlaybackCommit(queue.CommitCompletionAsync);
        Check(completion.Confirm(default), "Simulated native end should be confirmed.");
        await commit.ConfirmAsync(run, selected!.Id);
        Check((await queue.NextAsync(run, 0, default))?.Id == "0000" &&
            await queue.IsPendingAsync(run, "0400", 0, default) &&
            await queue.IsPendingAsync(run, "0402", 0, default),
            "A manual completion must leave the old head and its unrelated neighbors pending.");
        var replay = await queue.FindTransportTargetAsync(run, 0, selected.Order with { Id = "0402" }, older: true, default);
        Check(replay is { Id: "0401", Snapshot: null } && replay.ToCallItem(run.Server) is { BackendId: "0401", RecordingTimeUtc: not null },
            "A completed offscreen row must remain available for deliberate, identity-based replay.");
        Console.WriteLine("PASS Manual completion during recovery preserves backlog and completed rows remain replayable");

        await queue.MarkPlayingAsync(run, "0000", default);
        await queue.MarkAsync(run, "0000", PlaybackOutcome.Failed, default);
        await queue.MarkAsync(run, "0000", PlaybackOutcome.Canceled, default);
        var jump = await queue.JumpToLiveAsync(run, 0, skipSelected: false, default);
        Check(jump is { Target.Id: "0499", PlayTarget: true } && (await queue.NextAsync(run, 0, default))?.Id == "0499" &&
            !await queue.IsPendingAsync(run, "0000", 0, default) &&
            (await queue.NextAsync(other, 0, default))?.Id == "0099",
            "Jump must leave the live edge pending, skip the failed or interrupted backlog, and leave the other server alone.");
        await queue.CommitCompletionAsync(run, "0499");
        Check(await Add(queue, run, "0498-a", Now.AddSeconds(249)),
            "A Live arrival after Jump must survive even when its ID sorts before the jumped-to ID.");
        Check(!await Add(queue, run, "recovered-early", Now.AddSeconds(100), origin: RecordingOrigin.History) &&
            !await Add(queue, run, "0000", Now),
            "Historical backfill before the boundary and duplicate skipped rows must remain excluded.");
        var restarted = run with { Owner = 2 };
        var reopened = new DurableLiveQueue(paths);
        await reopened.OpenAsync(restarted, Now.AddMinutes(5), default);
        Check((await reopened.NextAsync(restarted, 0, default))?.Id == "0498-a" &&
            !await reopened.IsPendingAsync(restarted, "0499", 0, default),
            "Restart must play the new Live arrival without replaying completed or skipped recordings.");
        Check((await reopened.JumpToLiveAsync(restarted, 0, skipSelected: true, default)) is { Target.Id: "0499", PlayTarget: false } &&
            await reopened.NextAsync(restarted, 0, default) == null,
            "Jump with audio off drops accumulated pending work without starting playback.");
        await Add(reopened, restarted, "failed-live-edge", Now.AddMinutes(6), audio: false);
        Check((await reopened.JumpToLiveAsync(restarted, 0, skipSelected: false, default, failedId: "failed-live-edge"))
                is { Target.Id: "failed-live-edge", PlayTarget: false } &&
            await Add(reopened, restarted, "new-live-edge", Now.AddMinutes(6).AddSeconds(1)) &&
            (await reopened.NextAsync(restarted, 0, default))?.Id == "new-live-edge",
            "An explicit Jump away from the same failed live edge must let the next arrival play.");
        Console.WriteLine("PASS Jump without visible cards over 500 recordings, failed head, restart, and tied Live arrivals");
    }

    private static async Task HostedTransportAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run("https://app.joesscanner.com", 3, 2, Protected: true, Account: "one");
        var otherAccount = run with { Owner = 4, Account = "two" };
        await queue.OpenAsync(run, Now, default);
        await queue.OpenAsync(otherAccount, Now, default);
        await Add(queue, run, "restricted-history", Now.AddHours(-25), origin: RecordingOrigin.History);
        await Add(queue, run, "eligible-history", Now.AddHours(-23), origin: RecordingOrigin.History);
        await Add(queue, run, "live", Now);
        await Add(queue, otherAccount, "other-account", Now.AddSeconds(1));
        var standard = DurableLiveQueue.HistoricalFloor(true, 2, true, 1, Now);
        var latest = new DurableLiveQueue.Position("live", Now.UtcTicks);
        var previous = await queue.FindTransportTargetAsync(run, standard, latest, older: true, default);
        Check(previous?.Id == "eligible-history" &&
            await queue.FindTransportTargetAsync(run, standard, previous.Order, older: true, default) == null &&
            await queue.FindTransportTargetAsync(run, long.MaxValue, latest, older: true, default) == null &&
            await queue.FindTransportTargetAsync(run, standard, latest, older: false, default) == null,
            "Navigation must honor the authorized tier, fail closed for protected history and preserve account isolation.");
        Check((await queue.JumpToLiveAsync(run, standard, skipSelected: false, default)) is { Target.Id: "live", PlayTarget: true } &&
            await queue.IsPendingAsync(run, "restricted-history", 0, default) &&
            await queue.IsPendingAsync(otherAccount, "other-account", 0, default),
            "Jump must not mutate withheld history or another account's pending recordings.");
        Console.WriteLine("PASS Transport honors hosted history floor, offline verification, and account isolation");
    }

    private static async Task RapidTransportAndStopAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var lifecycle = new MonitoringLifecycle();
        var current = lifecycle.TryBeginStart()!;
        lifecycle.Activate(current);
        var run = new DurableLiveQueue.Run(Server, current.Id, null);
        var requests = new LivePlaybackRequests();
        await queue.OpenAsync(run, Now, default);
        await Add(queue, run, "first", Now);
        await Add(queue, run, "last", Now.AddSeconds(1));
        using var query = requests.Begin(manual: true);
        var adjacent = await queue.FindTransportTargetAsync(run, 0, new("first", Now.UtcTicks), older: false, query.Token);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DbPath }.ToString());
        connection.Open();
        using var writer = connection.BeginTransaction();
        using var obsolete = requests.Begin(manual: true);
        var oldJump = queue.JumpToLiveAsync(run, 0, skipSelected: true, obsolete.Token);
        using var latest = requests.Begin(manual: true);
        var newJump = queue.JumpToLiveAsync(run, 0, skipSelected: false, latest.Token);
        Check(adjacent?.Id == "last" && !query.CanContinue && !obsolete.CanContinue && latest.CanContinue,
            "A newer transport action must invalidate a resolved lookup and an outstanding jump.");

        var stop = lifecycle.StopAsync(_ => requests.RetireCurrent(), _ => { }, _ => queue.StopAsync(run));
        Check(!latest.CanContinue && !lifecycle.CanRecover(current), "Stop must retire the newest command before SQLite is unblocked.");
        writer.Commit();
        await ExpectCancellationAsync(oldJump);
        await ExpectCancellationAsync(newJump);
        await stop;

        var restarted = run with { Owner = run.Owner + 1 };
        await queue.OpenAsync(restarted, Now.AddSeconds(2), default);
        Check((await queue.NextAsync(restarted, 0, default))?.Id == "first",
            "A pending Jump canceled by Stop must not commit its skip or start playback.");
        Console.WriteLine("PASS Rapid transport commands and Stop invalidate stale work before the jump commits");
    }

    private static async Task ExpectCancellationAsync(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("The obsolete transport operation was not canceled.");
    }

    private static Task<bool> Add(DurableLiveQueue queue, DurableLiveQueue.Run run, string id, DateTimeOffset time,
        bool audio = true, RecordingOrigin origin = RecordingOrigin.Live) =>
        queue.ObserveAsync(run, id, time,
            new LiveCallSnapshot(new CallCardMetadata { Talkgroup = "Dispatch" }, null, CallCardIdFallback.None,
                audio ? $"{run.Server}/clip.mp3" : null), default, origin);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Paths(string DbPath) : IDatabasePathProvider;
}
