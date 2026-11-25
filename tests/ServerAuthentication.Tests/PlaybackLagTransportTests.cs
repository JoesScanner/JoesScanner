using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using JoesScanner.Models;
using JoesScanner.Services;

internal static partial class MobileRecoveryTests
{
    internal static async Task VerifyRecorderLagAcrossSimultaneousReconnections()
    {
        using var storage = new IngestionDatabase();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var queue = storage.OpenQueue();
        var clock = new RecorderLagClock(Now);
        var audio = new PlaybackLagGeneration();
        var a = new DurableLiveQueue.Run("https://recorder-a.example", 1, null, ServerId: Guid.NewGuid());
        var b = new DurableLiveQueue.Run("https://recorder-b.example", 2, null, ServerId: Guid.NewGuid());
        var probeA = NewArrivalSignal();
        var probeB = NewArrivalSignal();
        var release = NewArrivalSignal();
        using var socketA = new ScriptSocket();
        using var socketB = new ScriptSocket();
        var testA = await OpenRecorder(a, socketA, probeA, current: false);
        var testB = await OpenRecorder(b, socketB, probeB, current: true);
        using var streamA = testA.Stream;
        using var streamB = testB.Stream;
        await using var readerA = testA.Reader;
        await using var readerB = testB.Reader;

        Check(await readerA.MoveNextAsync() && readerA.Current.Talkgroup == "HEARTBEAT" &&
            await readerB.MoveNextAsync() && readerB.Current.Talkgroup == "HEARTBEAT", "Both production socket streams must connect.");
        clock.UtcNow = Now.AddMinutes(10);
        socketA.SendClose();
        socketB.SendClose();
        var consumeA = ConsumeAsync(readerA, a, testA.Result);
        var consumeB = ConsumeAsync(readerB, b, testB.Result);
        await Task.WhenAll(probeA.Task, probeB.Task).WaitAsync(token);

        // The two network gaps are durable while both responses are stalled.
        Check((await queue.ReadRecoveryWindowAsync(a, token))?.Since == Now &&
            (await queue.ReadRecoveryWindowAsync(b, token))?.Since == Now, "Each recorder must own a different open checkpoint.");
        release.SetResult();
        var resultA = await testA.Result.WaitAsync(token);
        var resultB = await testB.Result.WaitAsync(token);
        Check(resultA.Evidence != null && resultB.Evidence != null, "Both authorized snapshots must reconcile before evaluating their ready work.");

        var firstB = await queue.ReadReadyWindowAsync([b], _ => 0, 1, token);
        await queue.MarkPlayingAsync(b, firstB[0].Row.Id, token);
        var advanceA = await queue.TryAdvanceVerifiedLagAsync(a, 0, null, resultA.Evidence!, new(),
            action => testA.Recovery.CommitLagIfCurrent(resultA.Gap, action), token);
        var advanceB = await queue.TryAdvanceVerifiedLagAsync(b, 0, null, resultB.Evidence!, new(),
            action => testB.Recovery.CommitLagIfCurrent(resultB.Gap, action), token);
        Check(advanceA is { Floor: > 0, Lag.TotalMinutes: > 5 } && advanceB.Floor == null,
            "Elapsed outage is identical on A and B, but only the recorder with eligible stale work may advance.");
        var retained = (await queue.ReadReadyWindowAsync([b], _ => 0, 1, token)).Single().Row;
        Check(retained.Resuming && retained.Id == firstB[0].Row.Id, "A floor change cannot alter B's position or reserved recording.");
        Check(await queue.CountReadyAsync([a], _ => 0, null, token) == 0 &&
            await queue.GetArrivalSequenceAsync(a, "old-1", token) is > 0 &&
            !await queue.IsPendingAsync(a, "old-1", 0, token), "Skip must preserve the identity and make it terminal for autoplay.");

        await queue.ObserveAsync(a, "new", clock.UtcNow.AddSeconds(1), TestAudio(a, "new"), token, RecordingOrigin.Live);
        var nextA = await queue.ReadReadyWindowAsync([a], _ => 0, 1, token);
        Check(nextA.Single().Row.Id == "new", "A fresh arrival must still play after the scoped floor.");
        await queue.CommitCompletionAsync(b, retained.Id);
        Check((await queue.ReadReadyWindowAsync([b], _ => 0, 1, token)).Single().Row.Id != retained.Id,
            "B must advance once after natural completion.");
        timeout.Cancel();
        await Task.WhenAll(consumeA, consumeB);

        async Task<(CallStreamService Stream, LiveStreamRecovery Recovery, IAsyncEnumerator<CallItem> Reader,
            Task<LiveStreamRecovery.LagObservation> Result)> OpenRecorder(
            DurableLiveQueue.Run run, ScriptSocket socket, TaskCompletionSource probe, bool current)
        {
            var recovery = await LiveStreamRecovery.StartListeningAsync(queue, run, Now.AddSeconds(-1), token);
            var result = new TaskCompletionSource<LiveStreamRecovery.LagObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            recovery.ObserveTransportRecovery(audio.Capture, observation => result.TrySetResult(observation));
            var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
            {
                Check(request.RequestUri!.GetLeftPart(UriPartial.Authority) == run.Server &&
                    request.RequestUri.AbsolutePath == "/calljson", "The probe must use the original recorder and calljson endpoint.");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                var size = body.RootElement.GetProperty("length").GetInt32();
                if (size is 0 or 200)
                    Check(body.RootElement.GetProperty("columns").EnumerateArray().Any(column =>
                        column.GetProperty("name").GetString() == "Date" &&
                        column.GetProperty("search").GetProperty("value").GetString()!.Contains('|')),
                        "The transport recovery request must use the existing bounded recorder Date filter.");
                if (size == 0)
                {
                    probe.SetResult();
                    await release.Task.WaitAsync(cancellation);
                    return RecorderLagReply([]);
                }
                if (size != 200)
                    return RecorderLagReply([]);

                var stale = current ? clock.UtcNow.AddSeconds(-10) : Now.AddSeconds(15);
                return RecorderLagReply([
                    LagRow("recent-1", clock.UtcNow.AddSeconds(-2)),
                    LagRow("recent-2", clock.UtcNow.AddSeconds(-4)),
                    LagRow("old-2", stale.AddSeconds(3)),
                    LagRow("old-1", stale)]);
            }));
            var stream = new CallStreamService(Settings(run.Server), new RecordedCalls(), new AddressStub(),
                new ScriptSubscription(false, null), http, (_, _, _) => Task.FromResult<WebSocket?>(socket), clock,
                new(new Uri(run.Server), run.ServerId));
            return (stream, recovery, stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token), result.Task);
        }

        async Task ConsumeAsync(IAsyncEnumerator<CallItem> reader, DurableLiveQueue.Run scope, Task completed)
        {
            try
            {
                while (!completed.IsCompleted && await reader.MoveNextAsync())
                    if (reader.Current.LiveState != null)
                        await SaveIngestionAsync(queue, scope, reader.Current, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    internal static async Task TransportLagNeedsADurableCurrentWindow()
    {
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        using var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = tokenSource.Token;
        var run = new DurableLiveQueue.Run("https://custom.example", 1, null, ServerId: Guid.NewGuid());
        var recovery = await LiveStreamRecovery.StartListeningAsync(queue, run, Now, token);
        var audio = new PlaybackLagGeneration();
        var observations = new List<LiveStreamRecovery.LagObservation>();
        recovery.ObserveTransportRecovery(audio.Capture, observations.Add);
        await recovery.TransportLostAsync();
        Check(recovery.OutstandingWindow == null, "A first connection failure is not a transport recovery.");
        for (var i = 0; i < 3; i++)
        {
            recovery.TransportSucceeded(Now.AddSeconds(i));
            await recovery.TransportLostAsync();
            var gap = recovery.GetTransportGap(recovery.OutstandingWindow)!;
            Check(await queue.ReadRecoveryWindowAsync(run, token) == gap.Window,
                "A transport recovery must use the active durable checkpoint.");
            await recovery.TransportLostAsync();
            Check(recovery.OutstandingWindow == gap.Window, "Repeated failed attempts must not refresh the checkpoint.");
            await recovery.CompleteAsync(gap.Window, token);
            Check(observations.Last().Evidence == null && recovery.OutstandingWindow == null,
                "Completing a short dropout without evidence must preserve normal recovery.");
        }

        recovery.TransportSucceeded(Now.AddMinutes(10));
        await recovery.TransportLostAsync();
        var old = recovery.GetTransportGap(recovery.OutstandingWindow)!;
        await recovery.RequestAsync(Now);
        Check(!recovery.CommitLagIfCurrent(old, () => throw new InvalidOperationException("Obsolete commit")),
            "A newer regular background recovery must invalidate the transport proof.");
        var replacement = run with { Owner = 2, Server = "https://other.example" };
        await queue.StartListeningAsync(replacement, Now.AddMinutes(11), token);
        Check(await queue.ReadRecoveryWindowAsync(run, token) == null,
            "A replaced endpoint cannot retain authority over the same durable server identity.");
    }

    internal static async Task RecorderRetirementFencesLag()
    {
        using var storage = new IngestionDatabase();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var queue = storage.OpenQueue();
        var clock = new RecorderLagClock(Now);
        var manager = new ServerConnectionManager(clock);
        var a = new SavedScannerConnection(Guid.NewGuid(), "https://recorder-a.example", "A", null, true);
        var b = new SavedScannerConnection(Guid.NewGuid(), "https://recorder-b.example", "B", null, true);
        static Task Hold(ServerConnectionManager.Connection item) => Task.Delay(Timeout.InfiniteTimeSpan, item.Token);
        manager.Synchronize([a, b], token, Hold);
        var recorderB = manager.Snapshot().Single(connection => connection.Profile.Id == b.Id);
        foreach (var profiles in new IReadOnlyList<SavedScannerConnection>[] { [a with { Enabled = false }, b], [b] })
        {
            var recorderA = manager.Snapshot().Single(connection => connection.Profile.Id == a.Id);
            var run = new DurableLiveQueue.Run(a.Endpoint, recorderA.Generation, null, ServerId: a.Id);
            var recovery = await LiveStreamRecovery.StartListeningAsync(queue, run, Now.AddMinutes(-12), token);
            var audio = new PlaybackLagGeneration();
            recovery.ObserveTransportRecovery(audio.Capture, _ => { });
            recovery.TransportSucceeded(Now.AddMinutes(-10));
            await recovery.TransportLostAsync();
            var gap = recovery.GetTransportGap(recovery.OutstandingWindow)!;
            await recovery.CompleteAsync(gap.Window, token);
            Check(recovery.CommitLagIfCurrent(gap, () => { }), "The completed checkpoint initially belongs to this active owner.");

            manager.Synchronize(profiles, token, Hold);
            Check(!manager.RunIfCurrent(recorderA, () => throw new InvalidOperationException("Retired A committed")) &&
                manager.RunIfCurrent(recorderB, () => true), "Disabling or removing A must fence its old floor write while leaving B current.");
            recovery.TransportAuthenticationChanged();
            Check(!recovery.CommitLagIfCurrent(gap, () => throw new InvalidOperationException("Old authority committed")),
                "A credential change must invalidate the proof even before a connection token is canceled.");
            manager.Synchronize([a, b], token, Hold);
        }
        manager.Retire(_ => true);
    }

    private static HttpResponseMessage RecorderLagReply(object[] rows) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { data = rows, serverTimeZoneOffset = "+00:00", recordsFiltered = rows.Length }))
    };

    private static object LagRow(string id, DateTimeOffset recorded) =>
        new { DT_RowId = id, StartTimeUTC = recorded.ToUnixTimeSeconds(), CallDuration = 1, AudioFilename = id + ".wav" };

    private static LiveCallSnapshot TestAudio(DurableLiveQueue.Run run, string id) =>
        new(new CallCardMetadata(), null, CallCardIdFallback.None, run.Server + "/" + id + ".wav");

    private sealed class RecorderLagClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long GetTimestamp() => UtcNow.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
