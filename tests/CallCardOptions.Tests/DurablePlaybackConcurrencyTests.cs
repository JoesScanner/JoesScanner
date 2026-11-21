using System.Diagnostics;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class DurablePlaybackConcurrencyTests
{
    private const string Server = "https://scanner.example/one";
    private const long CrashProbeOwner = 20;
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.QueueRaces-");
        try
        {
            await WakeAtIdleAsync(new Paths(Path.Combine(directory.FullName, "wake.sqlite")));
            await RetriedIngestionAsync(new Paths(Path.Combine(directory.FullName, "ingestion.sqlite")));
            await ConcurrentArrivalOrderAsync(new Paths(Path.Combine(directory.FullName, "arrival.sqlite")));
            await NativeCompletionAsync(new Paths(Path.Combine(directory.FullName, "completion.sqlite")));
            await TerminateAroundCommitAsync(new Paths(Path.Combine(directory.FullName, "crash.sqlite")));
            await BlockedOwnershipAsync(new Paths(Path.Combine(directory.FullName, "ownership.sqlite")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task ConcurrentArrivalOrderAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var firstServer = new DurableLiveQueue.Run(Server, 1, null);
        var secondServer = firstServer with { Server = "https://scanner.example/two" };
        await queue.OpenAsync(firstServer, Now, default);
        await queue.OpenAsync(secondServer, Now, default);
        using var locker = new SqliteConnection($"Data Source={paths.DbPath}");
        locker.Open();
        using var held = locker.BeginTransaction();
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        Task<DurableLiveQueue.Observation> Accept(DurableLiveQueue.Run run, string id, DateTimeOffset? time) =>
            queue.ObserveWithPositionAsync(run, id, time, new(new(), null, CallCardIdFallback.None, null), token,
                RecordingOrigin.Live, existingOnly: false, _ => retrying.TrySetResult());

        var first = Accept(firstServer, "same-id", Now.AddYears(100));
        var second = Accept(secondServer, "same-id", Now.AddYears(-100));
        var third = Accept(firstServer, "west", Now.ToOffset(TimeSpan.FromHours(-7)));
        var fourth = Accept(firstServer, "east", Now.ToOffset(TimeSpan.FromHours(9)));
        var fifth = Accept(firstServer, "missing-time", null);
        var jump = queue.JumpToLiveAsync(secondServer, 0, skipSelected: false, token);
        var arrivedAfterJump = Accept(secondServer, "after-jump", Now.AddYears(-90));
        await retrying.Task.WaitAsync(token);
        Check(!second.IsCompleted && !fifth.IsCompleted, "Later producers cannot overtake a write waiting to retry.");
        held.Commit();

        var observations = await Task.WhenAll(first, second, third, fourth, fifth).WaitAsync(token);
        Check((await jump)?.Target.Id == "same-id" && (await arrivedAfterJump).ArrivalSequence > observations[^1].ArrivalSequence,
            "Jump must follow already accepted observations and precede observations queued after it.");
        var sequences = observations.Select(result => result.ArrivalSequence).ToArray();
        Check(sequences.Zip(sequences.Skip(1)).All(pair => pair.First < pair.Second) && observations.All(result => result.Changed),
            "Concurrent producer calls must receive one strictly increasing sequence across recorders.");
        await queue.ObserveAsync(firstServer, "same-id", Now.AddYears(-10),
            new(new CallCardMetadata { Talkgroup = "Enriched" }, null, CallCardIdFallback.None, Server + "/late.mp3"), token,
            existingOnly: true);
        Check((await queue.NextAsync(firstServer, 0, token)) is
            { Id: "same-id", ArrivalSequence: var position, Snapshot.Metadata.Talkgroup: "Enriched" } && position == sequences[0] &&
            (await queue.NextAsync(secondServer, 0, token))?.ArrivalSequence == sequences[1],
            "Late metadata and URLs must keep the original sequence and recorder isolation.");
        foreach (var id in new[] { "same-id", "west", "east", "missing-time" })
        {
            Check((await queue.NextAsync(firstServer, 0, token))?.Id == id, "Timezone, timestamp, and missing audio cannot reorder FIFO.");
            await queue.MarkAsync(firstServer, id, PlaybackOutcome.Skipped, token);
        }
        Console.WriteLine("PASS concurrent SQLite retries preserve shared arrival order, scope and timestamp independence");
    }

    private static async Task WakeAtIdleAsync(Paths paths)
    {
        var wake = new LiveQueueWake();
        var queue = new DurableLiveQueue(paths);
        var lifecycle = new MonitoringLifecycle();
        var run = lifecycle.TryBeginStart()!;
        lifecycle.Activate(run);
        var durable = new DurableLiveQueue.Run(Server, run.Id, null);
        await queue.OpenAsync(durable, Now, default);
        var emptyResult = new TaskCompletionSource();
        async Task<bool> MustRecheckAsync(Func<Task> afterRead)
        {
            var before = wake.BeforeRead();
            Check(await queue.NextAsync(durable, 0, default) == null, "The SQLite query must have observed an empty queue.");
            await afterRead();
            return lifecycle.CanRecover(run) && wake.ChangedSince(before);
        }

        var readComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleRead = MustRecheckAsync(() =>
        {
            readComplete.SetResult();
            return emptyResult.Task;
        });
        await await Task.WhenAny(readComplete.Task, staleRead);
        var arrivals = await Task.WhenAll(new[] { "one", "two", "three" }.Select(id => AddAsync(queue, durable, id)));
        foreach (var saved in arrivals)
        {
            Check(saved, "All three recordings must have committed.");
            wake.Signal();
        }
        emptyResult.SetResult();
        Check(await staleRead, "Three arrivals during an empty query must wake the existing worker.");
        foreach (var id in new[] { "one", "two", "three" })
        {
            Check((await queue.NextAsync(durable, 0, default))?.Id == id, "A wake must preserve local arrival order.");
            await queue.CommitCompletionAsync(durable, id);
        }
        var newRead = wake.BeforeRead();
        Check(!wake.ChangedSince(newRead), "The recheck consumes the arrivals without continual polling.");

        emptyResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledRead = MustRecheckAsync(() =>
        {
            readComplete.SetResult();
            return emptyResult.Task;
        });
        await await Task.WhenAny(readComplete.Task, canceledRead);
        Check(await AddAsync(queue, durable, "before-stop"), "A recording should be committed before Stop.");
        wake.Signal();
        await lifecycle.StopAsync(_ => { }, _ => { }, _ => Task.CompletedTask);
        emptyResult.SetResult();
        Check(!await canceledRead, "Explicit Stop must prevent an already pending arrival from waking the retired worker.");
        Console.WriteLine("PASS Live wake coalesces simultaneous arrivals at idle and respects Stop");
    }

    private static async Task RetriedIngestionAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run(Server, 1, null);
        await queue.OpenAsync(run, Now, default);
        var writes = 0;
        var reports = 0;
        var saved = await DurableLiveQueue.PersistObservationAsync(() =>
        {
            if (++writes == 1)
                throw new SqliteException("Injected SQLite lock.", 5);
            return AddAsync(queue, run, "same");
        }, _ => reports++, CancellationToken.None);
        Check(saved && writes == 2 && reports == 1 && (await queue.NextAsync(run, 0, default))?.Id == "same",
            "The unchanged observation must still be saved after retry.");
        Check(!await AddAsync(queue, run, "same"), "A duplicate observation must not emit an extra durable wake.");
        Console.WriteLine("PASS Live ingestion retries the same observation after a SQLite write failure");
    }

    private static async Task NativeCompletionAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run(Server, 2, null);
        await queue.OpenAsync(run, Now, default);
        foreach (var id in new[] { "stop", "manual", "before-end", "write-failure" })
            await AddAsync(queue, run, id);

        var requests = new LivePlaybackRequests();
        var commits = new DurablePlaybackCommit(queue.CommitCompletionAsync);
        await CompleteThenCancelAsync("stop", requests.RetireCurrent);
        await CompleteThenCancelAsync("manual", () => { using var next = requests.Begin(manual: true); });

        using (var request = requests.Begin(manual: false))
        {
            var completion = new PlaybackCompletion();
            var token = request.Token;
            var outcome = await PlaybackAttempt.RunAsync(() =>
            {
                requests.RetireCurrent();
                Check(!completion.Confirm(token), "A late end event after cancellation cannot confirm playback.");
                return Task.FromCanceled(token);
            }, token, _ => throw new Exception("Expected cancellation."), completion);
            Check(outcome == PlaybackOutcome.Canceled && await queue.IsPendingAsync(run, "before-end", 0, default),
                "Cancellation before genuine completion must remain pending.");
        }

        var failWrite = true;
        var retained = new DurablePlaybackCommit((owner, id) => failWrite
            ? Task.FromException(new IOException("Storage temporarily unavailable."))
            : queue.CommitCompletionAsync(owner, id));
        try { await retained.ConfirmAsync(run, "write-failure"); }
        catch (IOException) { }
        Check(retained.Pending is { Id: "write-failure" }, "Confirmed completion must survive a failed write in memory.");
        var diskBeforeCommit = new DurableLiveQueue(paths);
        Check(await diskBeforeCommit.IsPendingAsync(run, "write-failure", 0, default),
            "An unexpected process exit before commit may leave the recording pending.");
        failWrite = false;
        await retained.FlushAsync();
        await queue.StopAsync(run);
        var restarted = run with { Owner = 3 };
        var reopened = new DurableLiveQueue(paths);
        await reopened.OpenAsync(restarted, Now.AddSeconds(1), default);
        Check((await reopened.NextAsync(restarted, 0, default))?.Id == "before-end" &&
              !await reopened.IsPendingAsync(restarted, "write-failure", 0, default) &&
              !await reopened.IsPendingAsync(restarted, "stop", 0, default) &&
              !await reopened.IsPendingAsync(restarted, "manual", 0, default),
            "Restart must preserve pending recordings and exclude durably completed recordings.");
        Console.WriteLine("PASS Confirmed native completion survives Stop and manual cancellation, with restart coverage around the commit");

        async Task CompleteThenCancelAsync(string id, Action cancel)
        {
            using var request = requests.Begin(manual: false);
            var token = request.Token;
            var completion = new PlaybackCompletion();
            var outcome = await PlaybackAttempt.RunAsync(async () =>
            {
                Check(completion.Confirm(token), "Native playback must have finished first.");
                cancel();
                Check(completion.Confirmed && !completion.Confirm(token), "Duplicate callbacks and later cancellation must preserve the first end.");
                await commits.ConfirmAsync(run, id);
                token.ThrowIfCancellationRequested();
            }, token, _ => throw new Exception("A completed recording should not report expected cancellation."), completion);
            Check(outcome == PlaybackOutcome.Completed && !await queue.IsPendingAsync(run, id, 0, default),
                "Confirmed completion must persist even when the selection has retired.");
        }
    }

    private static async Task TerminateAroundCommitAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run(Server, CrashProbeOwner, null);
        await queue.OpenAsync(run, Now, default);
        foreach (var stage in new[] { "before", "after", "arrivals" })
        {
            if (stage != "arrivals")
                await AddAsync(queue, run, stage);
            var launch = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
            launch.ArgumentList.Add(typeof(DurablePlaybackConcurrencyTests).Assembly.Location);
            launch.ArgumentList.Add("--completion-crash-probe");
            launch.ArgumentList.Add(paths.DbPath);
            launch.ArgumentList.Add(stage);
            using var child = Process.Start(launch) ?? throw new InvalidOperationException("Could not start the crash probe.");
            try
            {
                var boundary = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(boundary == stage, $"The child must reach the {stage}-commit crash boundary.");
            }
            finally
            {
                if (!child.HasExited)
                    child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        var restarted = run with { Owner = CrashProbeOwner + 1 };
        var reopened = new DurableLiveQueue(paths);
        await reopened.OpenAsync(restarted, Now.AddSeconds(1), default);
        Check((await reopened.NextAsync(restarted, 0, default))?.Id == "before" &&
            !await reopened.IsPendingAsync(restarted, "after", 0, default),
            "Termination before the commit leaves a replayable recording. A committed recording stays completed.");
        await reopened.CommitCompletionAsync(restarted, "before");
        Check((await reopened.NextAsync(restarted, 0, default))?.Id == "arrived-first", "The first committed arrival must survive a crash.");
        await reopened.CommitCompletionAsync(restarted, "arrived-first");
        Check((await reopened.NextAsync(restarted, 0, default))?.Id == "arrived-second", "Recovery after termination must retain FIFO.");
        Console.WriteLine("PASS abrupt process termination preserves committed arrival order and playback dispositions");
    }

    internal static async Task RunCrashProbeAsync(string path, string stage)
    {
        var completion = new PlaybackCompletion();
        Check(completion.Confirm(default), "The crash probe must simulate a confirmed native end.");
        var queue = new DurableLiveQueue(new Paths(path));
        var run = new DurableLiveQueue.Run(Server, CrashProbeOwner, null);
        if (stage == "after")
        {
            var commit = new DurablePlaybackCommit(queue.CommitCompletionAsync);
            await commit.ConfirmAsync(run, stage);
        }
        if (stage == "arrivals")
        {
            var snapshot = new LiveCallSnapshot(new(), null, CallCardIdFallback.None, null);
            await queue.ObserveAsync(run, "arrived-first", Now.AddDays(1), snapshot, default, RecordingOrigin.Live);
            await queue.ObserveAsync(run, "arrived-second", Now.AddDays(-1), snapshot, default, RecordingOrigin.History);
        }
        Console.WriteLine(stage);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task BlockedOwnershipAsync(Paths paths)
    {
        var queue = new DurableLiveQueue(paths);
        var old = new DurableLiveQueue.Run(Server, 10, null);
        var initial = old with { Owner = 9 };
        var other = new DurableLiveQueue.Run("https://scanner.example/two", 10, null);
        await queue.OpenAsync(initial, Now, default);
        await queue.OpenAsync(other, Now, default);
        await AddAsync(queue, initial, "same");
        await AddAsync(queue, other, "same");
        using var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DbPath }.ToString());
        lockConnection.Open();
        using var writer = lockConnection.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        var opening = queue.OpenAsync(old, Now, cancellation.Token);
        cancellation.Cancel();
        var stopping = queue.StopAsync(old);
        var replacement = old with { Owner = 11 };
        var starting = queue.OpenAsync(replacement, Now.AddSeconds(1), default);
        Check(!starting.IsCompleted, "A replacement cannot acquire ownership while SQLite has a writer.");
        writer.Commit();
        try { await opening; }
        catch (OperationCanceledException) { }
        await Task.WhenAll(stopping, starting);

        await queue.StopAsync(old);
        try
        {
            await queue.OpenAsync(old, Now.AddSeconds(2), default);
            throw new Exception("A delayed obsolete open must be rejected.");
        }
        catch (InvalidOperationException) { }
        Check((await queue.NextAsync(replacement, 0, default))?.Id == "same" &&
              (await queue.NextAsync(other, 0, default))?.Id == "same" &&
              await queue.NextAsync(old, 0, default) == null,
            "The replacement owns only its recorder, and the other server's matching ID remains pending.");
        Console.WriteLine("PASS Blocked SQLite writer, Stop/Start, obsolete ownership, and cross-server isolation");
    }

    private static Task<bool> AddAsync(DurableLiveQueue queue, DurableLiveQueue.Run run, string id) =>
        queue.ObserveAsync(run, id, Now, new LiveCallSnapshot(new(), null, CallCardIdFallback.None, run.Server + "/clip.mp3"),
            default, RecordingOrigin.Live);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Paths(string DbPath) : IDatabasePathProvider;
}
