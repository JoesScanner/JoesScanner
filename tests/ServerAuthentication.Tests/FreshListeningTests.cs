using JoesScanner.Models;
using JoesScanner.Services;

internal static partial class MobileRecoveryTests
{
    private const string ListeningServer = "https://custom.example";

    internal static async Task FreshSocketListening()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var storage = new IngestionDatabase();
        var beforeExit = storage.OpenQueue();
        var old = new DurableLiveQueue.Run(ListeningServer, 1, null);
        await beforeExit.StartListeningAsync(old, Now.AddHours(-1), token);
        for (var i = 0; i < 20; i++)
            await beforeExit.ObserveAsync(old, $"old-{i}", Now.AddMinutes(-20), new(new(), $"Old {i}", 0), token);
        await beforeExit.MarkPlayingAsync(old, "old-0", token);
        var firstSequence = await beforeExit.GetArrivalSequenceAsync(old, "old-0", token);

        // Simulate process exit with the first clip in progress. No Stop write.
        var queue = storage.OpenQueue();
        var run = old with { Owner = 2 };
        var recovery = await LiveStreamRecovery.StartListeningAsync(queue, run, Now, token);
        Check(await queue.NextAsync(run, 0, token) == null, "The interrupted clip and all 19 other pending calls must be excluded.");
        Check((await queue.FindTransportTargetAsync(run, 0, null, older: true, token))?.Snapshot?.Transcription == "Old 19" &&
            await queue.GetArrivalSequenceAsync(run, "old-0", token) == firstSequence,
            "Prior recordings must retain playback history metadata and their original positions.");

        using var ws = new ScriptSocket();
        using var http = new HttpClient(new AsyncStubHandler((_, _) => Task.FromResult(Reply())));
        var stream = IngestionStream(Settings(ListeningServer), http, _ => ws);
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Expected the WebSocket to connect at Live.");
        ws.Send(Row("old-0", Now, "updated.mp3"));
        Check(await reader.MoveNextAsync() && (await SaveIngestionAsync(queue, run, reader.Current, token)).IsRecoveryBacklog,
            "A repeated full socket update cannot re-queue a prior-session identity.");
        ws.Send(Row("first-live", Now.AddMinutes(5), "first.mp3"));
        ws.Send(Row("second-live", Now.AddDays(-2), "second.mp3"));
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "first-live", IsCurrentTransmission: true },
            "Current socket recordings must remain eligible for local alerts.");
        var a = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "second-live", IsCurrentTransmission: false },
            "A replay timestamped before the socket opened must remain playable without sending an alert.");
        var b = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(a.ArrivalSequence < b.ArrivalSequence && (await queue.NextAsync(run, 0, token))?.Id == "first-live",
            "New WebSocket observations must be FIFO even when their recording clocks run backward.");

        await recovery.RequestAsync(Now.AddMinutes(1));
        var activeWindow = recovery.OutstandingWindow!;
        var resumed = await queue.ObserveWithPositionAsync(run, "same-session-recovery", Now.AddMinutes(2),
            new(new(), "Temporary interruption", 0), token, RecordingOrigin.History, existingOnly: false,
            recoveryWindowId: activeWindow.Id);
        Check(!recovery.IsInherited(activeWindow) && resumed.ArrivalSequence > b.ArrivalSequence &&
              !resumed.IsRecoveryBacklog, "A recovery opened during the current session must keep eligible calls pending.");
    }

    internal static async Task FreshHttpListening()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var token = timeout.Token;
        using var storage = new IngestionDatabase();
        var oldQueue = storage.OpenQueue();
        var old = new DurableLiveQueue.Run(ListeningServer, 1, null);
        var cutoff = Now.AddHours(-1);
        var previous = await LiveStreamRecovery.StartListeningAsync(oldQueue, old, cutoff, token);
        await previous.RequestAsync(cutoff);
        var window = previous.OutstandingWindow!;
        await oldQueue.StopAsync(old);

        var queue = storage.OpenQueue();
        var run = old with { Owner = 2 };
        var recovery = await LiveStreamRecovery.StartListeningAsync(queue, run, Now, token);
        Check(recovery.OutstandingWindow == window, "Stop and Start must preserve the unfinished HTTP checkpoint.");
        var baseline = Row("snapshot", Now.AddHours(2), "snapshot.mp3");
        var recovered = Row("older-window", Now.AddMinutes(-25), "older.mp3");
        var fresh = Row("new-poll", Now.AddMinutes(-45), "fresh.mp3");
        var polls = 0;
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
            if (offset > 0)
                throw new HttpRequestException("Test-only partial page.");
            return ++polls switch
            {
                1 => Reply(baseline),
                2 => FullRecoveryPage(recovered, size),
                _ => Reply(baseline, fresh)
            };
        }));
        var stream = IngestionStream(Settings(ListeningServer), http, _ => null);
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Expected HTTP monitoring to connect.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "snapshot", IsListeningBaseline: true },
            "The first HTTP snapshot must establish the baseline before paginating inherited recovery.");
        var snapshot = reader.Current;
        Check((await SaveIngestionAsync(queue, run, snapshot, token)).IsRecoveryBacklog &&
              await queue.NextAsync(run, 0, token) == null, "A future recorder timestamp must not turn a baseline row into Live.");
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "older-window" &&
              reader.Current.RecoveryWindowId == window.Id, "Expected the inherited window after establishing the baseline.");
        Check((await SaveIngestionAsync(queue, run, reader.Current, token)).IsRecoveryBacklog &&
              await queue.NextAsync(run, 0, token) == null, "Late inherited recovery is retained for History.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "new-poll", IsListeningBaseline: false, RecoveryWindowId: null, IsCurrentTransmission: false },
            "A regular post-baseline poll can admit new calls even while inherited recovery is retrying.");
        var saved = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(!saved.IsRecoveryBacklog && (await queue.NextAsync(run, 0, token))?.Id == "new-poll" &&
              await queue.ReadRecoveryWindowAsync(run, token) == window,
            "New polls must remain playable while a failed old recovery window stays open.");
        GC.KeepAlive(snapshot);
    }

    internal static async Task EmptyHttpListeningBaseline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var run = new DurableLiveQueue.Run(ListeningServer, 1, null);
        var recovery = await LiveStreamRecovery.StartListeningAsync(queue, run, Now, token);
        var polls = 0;
        using var http = new HttpClient(new AsyncStubHandler((_, _) => Task.FromResult(++polls == 1
            ? Reply() : Reply(Row("first-new", Now, "first.mp3")))));
        var stream = IngestionStream(Settings(ListeningServer), http, _ => null);
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT" &&
            await queue.NextAsync(run, 0, token) == null, "A fresh install should wait for Live traffic.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "first-new", IsListeningBaseline: false, IsCurrentTransmission: true },
            "An empty baseline must not suppress the first call that arrives later.");
        Check(!(await SaveIngestionAsync(queue, run, reader.Current, token)).IsRecoveryBacklog &&
            (await queue.NextAsync(run, 0, token))?.Id == "first-new", "First new call should become the FIFO head.");
    }
}
