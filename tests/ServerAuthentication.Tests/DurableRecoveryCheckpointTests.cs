using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static partial class MobileRecoveryTests
{
    internal static async Task RecoverySurvivesTimedJumpAndRestart()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = timeout.Token;
        using var terminated = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var run = new DurableLiveQueue.Run(server, 1, null);
        await queue.OpenAsync(run, Now, token);
        var recovery = await LiveStreamRecovery.OpenAsync(queue, run, terminated.Token);
        var cutoff = Now.AddHours(-1);
        await recovery.RequestAsync(cutoff);
        var savedWindow = recovery.OutstandingWindow!;
        var first = Row("committed-page", Now.AddMinutes(15), "first.mp3");
        var missed = Row("stalled-page", Now.AddMinutes(-35), "late.mp3");
        var secondPage = NewArrivalSignal();
        long? firstPosition;
        using (var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
            if (offset == 0)
                return FullRecoveryPage(first, size);
            secondPage.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return Reply(missed);
        })))
        {
            var stream = IngestionStream(Settings(server), http, _ => null);
            await using var reader = stream.GetCallStreamAsync([], terminated.Token, recovery).GetAsyncEnumerator(terminated.Token);
            Check(await reader.MoveNextAsync() && reader.Current.BackendId == "committed-page", "Page one must arrive durably.");
            firstPosition = (await SaveIngestionAsync(queue, run, reader.Current, token)).ArrivalSequence;
            var reading = reader.MoveNextAsync().AsTask();
            await secondPage.Task.WaitAsync(token);
            try
            {
                await recovery.ReconciledForPlayback.WaitAsync(TimeSpan.FromSeconds(10), token);
                throw new InvalidOperationException("The stalled HTTP page unexpectedly reconciled.");
            }
            catch (TimeoutException) { }

            var jump = await queue.JumpToLiveAsync(run, 0, skipSelected: false, token,
                prepareBoundary: () => Task.FromResult(0L));
            Check(jump is { Target.Id: "committed-page", PlayTarget: true } &&
                  await queue.ReadRecoveryWindowAsync(run, token) == savedWindow &&
                  !recovery.ReconciledForPlayback.IsCompleted, "The playback timeout must leave the same durable HTTP window.");

            // End the worker immediately after Jump. No Stop transaction runs.
            terminated.Cancel();
            await ((Task)reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        var restartedQueue = storage.OpenQueue();
        var restarted = run with { Owner = 2 };
        await restartedQueue.OpenAsync(restarted, Now, token);
        var restored = await LiveStreamRecovery.OpenAsync(restartedQueue, restarted, token);
        Check(restored.OutstandingWindow == savedWindow, "A fresh service must load the earliest incomplete window.");
        var firstPages = 0;
        using var restartedHttp = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
            if (offset != 0)
                return Reply(missed, first);
            return ++firstPages == 1
                ? FullRecoveryPage(first, size)
                : Reply(first, missed, Row("fresh-poll", Now, "fresh.mp3"));
        }));
        var restartedStream = IngestionStream(Settings(server), restartedHttp, _ => null);
        await using var recovered = restartedStream.GetCallStreamAsync([], token, restored).GetAsyncEnumerator(token);
        Check(await recovered.MoveNextAsync() && recovered.Current.BackendId == "committed-page", "Retry should see the first page again.");
        var prior = recovered.Current;
        Check((await SaveIngestionAsync(restartedQueue, restarted, prior, token)).ArrivalSequence == firstPosition,
            "Retry must keep the committed position.");
        Check(await recovered.MoveNextAsync() && recovered.Current.BackendId == "stalled-page", "Retry must reach the stalled page.");
        var old = recovered.Current;
        Check((await SaveIngestionAsync(restartedQueue, restarted, old, token)) is { IsRecoveryBacklog: true, ArrivalSequence: not null },
            "The newly found old call must receive a skipped durable identity.");
        Check((await restartedQueue.GetAccessAsync(server, "stalled-page", null, token))?.Access.Origin == RecordingOrigin.History,
            "The old call must keep authorized History provenance.");
        Check(await recovered.MoveNextAsync() && recovered.Current.Talkgroup == "HEARTBEAT", "Fallback poll must remain responsive.");
        Check(await recovered.MoveNextAsync() && recovered.Current.BackendId == "fresh-poll", "History duplicates must not replay.");
        Check(!(await SaveIngestionAsync(restartedQueue, restarted, recovered.Current, token)).IsRecoveryBacklog,
            "Normal polling after reconciliation must continue to admit new calls.");
        Check(restored.ReconciledForPlayback.IsCompletedSuccessfully &&
              await restartedQueue.ReadRecoveryWindowAsync(restarted, token) == null,
            "Only a fully acknowledged retry may close the window.");
        await restartedQueue.MarkAsync(restarted, "committed-page", PlaybackOutcome.Completed, token);
        Check((await restartedQueue.NextAsync(restarted, 0, token))?.Id == "fresh-poll",
            "Autoplay must bypass the late recovered call while preserving the next Live observation.");
        Check(CountIdentity(storage, "stalled-page") == 1, "The recovered recording must be durably stored exactly once.");
        GC.KeepAlive(prior);
        GC.KeepAlive(old);
    }

    internal static async Task ManualJumpFencesOutstandingSocketRecovery()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        using var socket = new ScriptSocket();
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var run = new DurableLiveQueue.Run(server, 1, null);
        await queue.OpenAsync(run, Now, token);
        var recovery = await LiveStreamRecovery.OpenAsync(queue, run, token);
        await recovery.RequestAsync(Now.AddHours(-1));
        var window = recovery.OutstandingWindow!;
        var pageTwo = NewArrivalSignal();
        var release = NewArrivalSignal();
        var afterJump = new { DT_RowId = "after-jump", StartTimeUTC = Now.AddDays(-2).ToUnixTimeSeconds(),
            AudioFilename = "live.mp3", CallDuration = 5 };
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
            if (offset == 0)
                return FullRecoveryPage(Row("http-first", Now.AddMinutes(15), "http.mp3"), size);
            pageTwo.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return Reply(Row("from-gap", Now.AddMinutes(-10), "gap.mp3"),
                new { DT_RowId = "after-jump", StartTimeUTC = Now.AddMinutes(-9).ToUnixTimeSeconds(), CallText = "late transcript" });
        }));
        var stream = IngestionStream(Settings(server), http, _ => socket);
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Expected socket handshake.");
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "http-first", "Expected HTTP page one before the socket row.");
        var a = await SaveIngestionAsync(queue, run, reader.Current, token);
        var reading = reader.MoveNextAsync().AsTask();
        await pageTwo.Task.WaitAsync(token);
        socket.Send(Row("before-jump", Now.AddMinutes(-20), "before.mp3"));
        Check(await reading && reader.Current.BackendId == "before-jump", "Socket must continue during the stalled HTTP page.");
        var b = await SaveIngestionAsync(queue, run, reader.Current, token);

        Check((await queue.JumpToLiveAsync(run, 0, skipSelected: false, token)) is { Target.Id: "before-jump" } &&
              await queue.ReadRecoveryWindowAsync(run, token) == window, "Manual Jump cannot finish the HTTP window.");
        socket.Send(afterJump);
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "after-jump" && reader.Current.RecoveryWindowId == null,
            "An old clock stamp must not turn a new socket event into recovery work.");
        var liveCard = reader.Current;
        var c = await SaveIngestionAsync(queue, run, liveCard, token);
        release.TrySetResult();
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "from-gap", "The open window must finish its second page.");
        var d = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(d.IsRecoveryBacklog && a.ArrivalSequence < b.ArrivalSequence && b.ArrivalSequence < c.ArrivalSequence &&
              c.ArrivalSequence < d.ArrivalSequence, "Recovery and socket rows must retain their first local observation order.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "after-jump", IsLiveUpdate: true },
            "The HTTP duplicate should enrich the already observed socket call.");
        var duplicate = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(duplicate.ArrivalSequence == c.ArrivalSequence && !duplicate.IsRecoveryBacklog,
            "A recovery duplicate must leave the first Live observation eligible.");
        var nextRead = reader.MoveNextAsync().AsTask();
        await recovery.ReconciledForPlayback.WaitAsync(token);
        Check(await queue.ReadRecoveryWindowAsync(run, token) == null, "The acknowledged page may now close its window.");
        await queue.MarkAsync(run, "before-jump", PlaybackOutcome.Completed, token);
        Check((await queue.NextAsync(run, 0, token))?.Id == "after-jump" &&
              (await queue.JumpToLiveAsync(run, 0, skipSelected: false, token))?.Target.Id == "after-jump",
            "A skipped recovery row cannot become the Live edge or suppress newer Live playback.");
        socket.Send(Row("last-live", Now.AddMinutes(-10), "last.mp3"));
        Check(await nextRead && reader.Current.BackendId == "last-live", "New Live observations should continue.");
        var e = await SaveIngestionAsync(queue, run, reader.Current, token);
        await queue.MarkAsync(run, "after-jump", PlaybackOutcome.Completed, token);
        Check(e.ArrivalSequence > d.ArrivalSequence && (await queue.NextAsync(run, 0, token))?.Id == "last-live",
            "Autoplay must pass the skipped identity on the way to the next Live call.");
        GC.KeepAlive(liveCard);
    }

    internal static async Task RecoveryReceiptsAndOwnership()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var first = new DurableLiveQueue.Run(server, 1, null);
        await queue.OpenAsync(first, Now, token);
        var recovery = await LiveStreamRecovery.OpenAsync(queue, first, token);
        var cutoff = Now.AddMinutes(-40);
        await recovery.RequestAsync(cutoff);
        var initialWindow = recovery.OutstandingWindow!;
        await recovery.RequestAsync(cutoff.AddMinutes(10));
        await recovery.CompleteAsync(initialWindow, token);
        Check(recovery.OutstandingWindow is { Since: var since } && since == cutoff &&
              await queue.ReadRecoveryWindowAsync(first, token) == recovery.OutstandingWindow,
            "An obsolete revision must leave the newer request and its earlier cutoff intact.");

        using (var socket = new ScriptSocket())
        using (var http = new HttpClient(new AsyncStubHandler((_, _) =>
            Task.FromResult(Reply(Row("retry-receipt", Now.AddMinutes(-5), "retry.mp3"))))))
        {
            var stream = IngestionStream(Settings(server), http, _ => socket);
            await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
            Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Expected socket handshake.");
            Check(await reader.MoveNextAsync() && reader.Current.BackendId == "retry-receipt", "Expected recovery observation.");
            var refused = reader.Current;
            refused.ConfirmLiveAdmission(false);
            Check(await reader.MoveNextAsync() && reader.Current.BackendId == "retry-receipt" &&
                  await queue.ReadRecoveryWindowAsync(first, token) != null, "An uncommitted eligible item must keep the window retryable.");

            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = storage.DbPath }.ToString());
            db.Open();
            var failure = NewArrivalSignal();
            Task<DurableLiveQueue.Observation> saving;
            using (var writeLock = db.BeginTransaction())
            {
                saving = SaveIngestionAsync(queue, first, reader.Current, token, _ => failure.TrySetResult());
                await failure.Task.WaitAsync(token);
                Check(!saving.IsCompleted && !recovery.ReconciledForPlayback.IsCompleted,
                    "A SQLite failure cannot acknowledge the HTTP work.");
            }
            Check((await saving).ArrivalSequence != null && CountIdentity(storage, "retry-receipt") == 1,
                "SQLite retry should durably admit the observation once.");
            GC.KeepAlive(refused);
            // Dispose before the final batch acknowledgement to exercise cancellation.
        }
        Check(await queue.ReadRecoveryWindowAsync(first, token) != null, "Stream cancellation cannot finish the window.");

        var accountA = new DurableLiveQueue.Run("https://hosted.example", 2, 2, true, "A");
        var accountB = accountA with { Owner = 3, Account = "B" };
        await queue.OpenAsync(accountA, Now, token);
        var a = await LiveStreamRecovery.OpenAsync(queue, accountA, token);
        await a.RequestAsync(cutoff);
        var old = a.OutstandingWindow!;
        await queue.OpenAsync(accountB, Now, token);
        var b = await LiveStreamRecovery.OpenAsync(queue, accountB, token);
        Check(b.OutstandingWindow == null, "A new account must not inherit another account's window.");
        await b.RequestAsync(cutoff.AddMinutes(10));
        await queue.StopAsync(accountA);
        Check(await queue.ReadRecoveryWindowAsync(accountA, token) == null &&
              await queue.ReadRecoveryWindowAsync(accountB, token) == b.OutstandingWindow,
            "A stopped run cannot reconcile and the other account's work must remain available.");

        var restarted = accountA with { Owner = 4 };
        await queue.OpenAsync(restarted, Now, token, explicitlyStopped: true);
        var reopened = await LiveStreamRecovery.OpenAsync(queue, restarted, token);
        Check(reopened.OutstandingWindow == old, "Explicit Start must recover the stopped account's previous window.");
        var snapshot = new LiveCallSnapshot(new(), "History metadata", CallCardIdFallback.None);
        Check((await queue.ObserveWithPositionAsync(accountB, "same-id", cutoff, snapshot, token, RecordingOrigin.History,
            existingOnly: false, recoveryWindowId: old.Id)).ArrivalSequence == null, "A recovery ID cannot authorize another account.");
        Check((await queue.ObserveWithPositionAsync(restarted, "same-id", cutoff, snapshot, token, RecordingOrigin.History,
            existingOnly: false, recoveryWindowId: old.Id)).IsRecoveryBacklog, "Deliberate Stop must keep outstanding old work out of autoplay.");
        Check(await queue.NextAsync(restarted, 0, token) == null && await queue.GetArrivalSequenceAsync(accountB, "same-id", token) == null,
            "Skipped records must not leak across accounts or into autoplay.");
    }

    private static long CountIdentity(IngestionDatabase storage, string id)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = storage.DbPath }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM live_queue_recordings WHERE recording_id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()!;
    }
}
