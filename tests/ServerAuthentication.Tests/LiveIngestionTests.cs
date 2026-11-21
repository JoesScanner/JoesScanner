using System.Net.WebSockets;
using System.Text.Json;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static partial class MobileRecoveryTests
{
    internal static async Task PagedRecoveryArrivalAndOverlap()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        using var firstSocket = new ScriptSocket();
        using var replacement = new ScriptSocket();
        using var storage = new IngestionDatabase();
        var run = new DurableLiveQueue.Run(server, 1, null);
        var queue = storage.OpenQueue();
        await queue.OpenAsync(run, Now, token);

        var secondPageStarted = NewArrivalSignal();
        var releaseSecondPage = NewArrivalSignal();
        var opens = 0;
        var a = Row("a", Now.AddMinutes(-45), "a.mp3");
        var b = Row("b", Now, "b.mp3");
        var c = Row("c", Now.AddMinutes(-55), "c.mp3");
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
            if (opens == 2)
                return Reply(Row("d", Now.AddMinutes(-50), "d.mp3"), c);
            if (offset == 0)
                return FullRecoveryPage(a, size);
            secondPageStarted.TrySetResult();
            await releaseSecondPage.Task.WaitAsync(cancellation);
            return Reply(c, b, new { DT_RowId = "a", StartTimeUTC = Now.AddMinutes(-45).ToUnixTimeSeconds(), CallText = "late transcript" });
        }));
        var stream = IngestionStream(Settings(server), http, _ => ++opens == 1 ? firstSocket : replacement);
        var recovery = new LiveStreamRecovery();
        recovery.Request(Now.AddHours(-1));
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Missing handshake.");
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "a", "Page one must be published before fetching page two.");
        var firstCard = reader.Current;
        var first = await SaveIngestionAsync(queue, run, firstCard, token);

        var reading = reader.MoveNextAsync().AsTask();
        await secondPageStarted.Task.WaitAsync(token);
        recovery.Request(Now.AddMinutes(-10));
        firstSocket.Send(b);
        Check(await reading && reader.Current.BackendId == "b" && !firstSocket.WasAborted,
            "The active recovery must continue and accept socket rows while page two waits.");
        var secondCard = reader.Current;
        var second = await SaveIngestionAsync(queue, run, secondCard, token);
        releaseSecondPage.SetResult();
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "c", "The remainder of the first recovery must be published.");
        var thirdCard = reader.Current;
        var third = await SaveIngestionAsync(queue, run, thirdCard, token);
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "a", IsLiveUpdate: true },
            "An HTTP duplicate must enrich its existing recording.");
        Check((await SaveIngestionAsync(queue, run, reader.Current, token)).ArrivalSequence == first.ArrivalSequence,
            "Late metadata must not replace the first position.");

        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "d" && opens == 2 && firstSocket.WasAborted,
            "The overlapping request must retain the earlier cutoff when opening the replacement socket.");
        var fourth = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(first.ArrivalSequence < second.ArrivalSequence && second.ArrivalSequence < third.ArrivalSequence &&
              third.ArrivalSequence < fourth.ArrivalSequence, "Durable order must be A, B, C, D across the two transports.");
        GC.KeepAlive(firstCard);
        GC.KeepAlive(secondCard);
        GC.KeepAlive(thirdCard);

        var resumed = run with { Owner = 2 };
        var reopened = storage.OpenQueue();
        await reopened.OpenAsync(resumed, Now, token);
        var head = await reopened.NextAsync(resumed, 0, token);
        Check(head is { Id: "a", Snapshot.Transcription: "late transcript" } &&
              (await reopened.FindTransportTargetAsync(resumed, 0, third.ArrivalSequence, older: true, token))?.Id == "b" &&
              (await reopened.FindTransportTargetAsync(resumed, 0, second.ArrivalSequence, older: false, token))?.Id == "c",
            "Reopened autoplay and transport must use the original positions.");
        var jump = await reopened.JumpToLiveAsync(resumed, 0, skipSelected: false, token);
        Check(jump is { Target.Id: "d", PlayTarget: true } && (await reopened.NextAsync(resumed, 0, token))?.Id == "d",
            "Jump must select the last observed recording and advance past the earlier backlog.");
    }

    internal static async Task PagedRecoveryRetriesPartialFailure()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        using var socket = new ScriptSocket();
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var run = new DurableLiveQueue.Run(server, 1, null);
        await queue.OpenAsync(run, Now, token);
        var attempts = 0;
        var failedRequests = 0;
        var failedPage = NewArrivalSignal();
        var a = Row("first-page", Now.AddMinutes(-5), "a.mp3");
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
            if (offset == 0)
            {
                attempts++;
                return FullRecoveryPage(a, size);
            }
            if (attempts == 1)
            {
                if (++failedRequests == 2)
                    failedPage.TrySetResult();
                throw new HttpRequestException("Test-only page failure for JSON and form.");
            }
            return Reply(Row("retried-page", Now.AddMinutes(-40), "c.mp3"));
        }));
        var stream = IngestionStream(Settings(server), http, _ => socket);
        var recovery = new LiveStreamRecovery();
        recovery.Request(Now.AddHours(-1));
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && await reader.MoveNextAsync() && reader.Current.BackendId == "first-page",
            "The first parsed page must be available before a later request can fail.");
        var firstCard = reader.Current;
        var first = await SaveIngestionAsync(queue, run, firstCard, token);
        var next = reader.MoveNextAsync().AsTask();
        await failedPage.Task.WaitAsync(token);
        socket.Send(Row("live-during-retry", Now, "b.mp3"));
        Check(await next && reader.Current.BackendId == "live-during-retry", "A failed HTTP page must not block the live socket.");
        var second = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "retried-page" && attempts == 2 && !socket.WasAborted,
            "A failed attempt must retry the original cutoff without another foreground request.");
        var third = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(first.ArrivalSequence < second.ArrivalSequence && second.ArrivalSequence < third.ArrivalSequence &&
              (await queue.NextAsync(run, 0, token))?.Id == "first-page", "Retry must retain the position of already persisted rows.");
        GC.KeepAlive(firstCard);
    }

    internal static async Task EvictedRecordingReceivesFullSocketUpdate()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        using var socket = new ScriptSocket();
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var run = new DurableLiveQueue.Run(server, 1, null);
        await queue.OpenAsync(run, Now, token);
        using var http = new HttpClient(new StubHandler(_ => Reply()));
        var stream = IngestionStream(Settings(server), http, _ => socket);
        await using var reader = stream.GetCallStreamAsync([], token).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync(), "Missing handshake.");
        socket.Send(Row("Evicted", Now, null));
        var (weakState, original) = await AcceptWeakAsync();

        socket.Send(new { data = Enumerable.Range(0, 101).Select(i => Row($"new-{i}", Now, "other.mp3")).ToArray() });
        for (var i = 0; i < 101; i++)
            Check(await reader.MoveNextAsync() && reader.Current.BackendId == $"new-{i}", "Missing following socket row.");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check(!weakState.TryGetTarget(out _), "The original stream state must no longer be in memory.");

        socket.Send(new
        {
            DT_RowId = "evicted", StartTimeUTC = Now.ToUnixTimeSeconds(), AudioFilename = "ready.mp3", CallText = "late metadata"
        });
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "Evicted", IsLiveUpdate: true },
            "A full socket duplicate must reach durable ingestion as an existing-only update with the original identity.");
        var update = await SaveIngestionAsync(queue, run, reader.Current, token);
        var head = await queue.NextAsync(run, 0, token);
        Check(update is { Changed: true } && update.ArrivalSequence == original &&
              head is { Id: "Evicted", Snapshot.Transcription: "late metadata" } && head.Snapshot.AudioUrl == server + "/ready.mp3",
            "An evicted recording must receive the URL and metadata at its original position.");
        await queue.CommitCompletionAsync(run, "Evicted");
        socket.Send(Row("Evicted", Now, "signed.mp3?lease=test"));
        Check(await reader.MoveNextAsync() && !(await SaveIngestionAsync(queue, run, reader.Current, token)).Changed &&
              await queue.NextAsync(run, 0, token) == null, "A completed recording must never reopen from a duplicate.");

        async Task<(WeakReference<LiveCallState>, long)> AcceptWeakAsync()
        {
            Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "Evicted", AudioUrl.Length: 0 },
                "Missing unavailable recording.");
            var saved = await SaveIngestionAsync(queue, run, reader.Current, token);
            return (new(reader.Current.LiveState!), saved.ArrivalSequence!.Value);
        }
    }

    internal static async Task StopDuringPagedRecovery()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var socket = new ScriptSocket();
        using var storage = new IngestionDatabase();
        var run = new DurableLiveQueue.Run(server, 1, null);
        var queue = storage.OpenQueue();
        await queue.OpenAsync(run, Now, timeout.Token);
        var secondPage = NewArrivalSignal();
        var requestCanceled = NewArrivalSignal();
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
            if (offset == 0)
                return FullRecoveryPage(Row("before-stop", Now.AddMinutes(-5), "first.mp3"), size);
            secondPage.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); }
            catch (OperationCanceledException) { requestCanceled.TrySetResult(); throw; }
            throw new InvalidOperationException("The pending HTTP request should be canceled.");
        }));
        var opens = 0;
        var stream = IngestionStream(Settings(server), http, _ => { opens++; return socket; });
        var recovery = new LiveStreamRecovery();
        recovery.Request(Now.AddHours(-1));
        await using var reader = stream.GetCallStreamAsync([], stopped.Token, recovery).GetAsyncEnumerator(stopped.Token);
        Check(await reader.MoveNextAsync() && await reader.MoveNextAsync() && reader.Current.BackendId == "before-stop",
            "The current run must admit the first recovery page.");
        await SaveIngestionAsync(queue, run, reader.Current, stopped.Token);
        var read = reader.MoveNextAsync().AsTask();
        await secondPage.Task.WaitAsync(timeout.Token);
        recovery.Request(Now.AddMinutes(-1));
        stopped.Cancel();
        await queue.StopAsync(run);
        await requestCanceled.Task.WaitAsync(timeout.Token);
        try { Check(!await read, "A canceled run must stop yielding recordings."); }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        Check(opens == 1 && await queue.NextAsync(run, 0, timeout.Token) == null,
            "A pending foreground request must not restart a stopped monitoring run.");
        var restarted = run with { Owner = 2 };
        await queue.OpenAsync(restarted, Now, timeout.Token);
        Check((await queue.NextAsync(restarted, 0, timeout.Token))?.Id == "before-stop",
            "Rows committed before Stop remain pending for a future explicit run.");
    }

    internal static async Task HostedSocketUpdateTargetsOnlyEligiblePendingRows()
    {
        const string server = HostedServerRules.DefaultServerUrl;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        using var socket = new ScriptSocket();
        using var storage = new IngestionDatabase();
        var settings = Settings(server);
        var subscription = new ScriptSubscription(true, 1, settings);
        var run = new DurableLiveQueue.Run(server, 1, 1, Protected: true, Account: RecordingPlaybackAccess.AccountKey(settings, server));
        var queue = storage.OpenQueue();
        await queue.OpenAsync(run, Now, token);
        var recordedAt = Now.AddHours(-2);
        await queue.ObserveAsync(run, "pending", recordedAt, SignedSnapshot(null), token, RecordingOrigin.History);
        await queue.ObserveAsync(run, "restricted", Now.AddHours(-30), SignedSnapshot(null), token, RecordingOrigin.History);
        var position = await queue.GetArrivalSequenceAsync(run, "pending", token);
        var floor = DurableLiveQueue.HistoricalFloor(true, 1, true, 1, Now);
        using var http = new HttpClient(new StubHandler(_ => Reply()));
        var recovery = new LiveStreamRecovery();
        recovery.Request(Now.AddMinutes(-10));
        var stream = IngestionStream(settings, http, _ => socket, subscription);
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync(), "Missing handshake.");

        socket.Send(new { data = new[] { Row("restricted", Now.AddHours(-30), "blocked.mp3"),
            Row("unknown", recordedAt, "unknown.mp3"), Row("pending", recordedAt, "signed.mp3?lease=opaque") } });
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "unknown", IsLiveUpdate: true } &&
              (await SaveIngestionAsync(queue, run, reader.Current, token)).ArrivalSequence == null,
            "The Standard window must reject the restricted row and old socket rows cannot create new queue identities.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "pending", IsLiveUpdate: true } &&
              (await SaveIngestionAsync(queue, run, reader.Current, token)).ArrivalSequence == position &&
              (await queue.NextAsync(run, floor, token)) is { Id: "pending", Snapshot.AudioUrl: null },
            "A signed URL must remain transient even when it updates an older authorized pending row.");
        socket.Send(Row("pending", recordedAt, "ready.mp3"));
        Check(await reader.MoveNextAsync() && (await SaveIngestionAsync(queue, run, reader.Current, token)).Changed &&
              (await queue.NextAsync(run, floor, token)) is { Id: "pending", ArrivalSequence: var updatedPosition, Snapshot.AudioUrl: not null } &&
              updatedPosition == position, "An existing authorized identity must be playable at its original position.");
    }

    internal static async Task PagedRecoveryRechecksAccountAndTier()
    {
        await RunAsync(switchAccount: true);
        await RunAsync(switchAccount: false);

        static async Task RunAsync(bool switchAccount)
        {
            const string server = HostedServerRules.DefaultServerUrl;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var token = timeout.Token;
            var settings = Settings(server);
            var subscription = new ScriptSubscription(true, 2, settings);
            var account = RecordingPlaybackAccess.AccountKey(settings, server)!;
            var run = new DurableLiveQueue.Run(server, 1, 2, Protected: true, Account: account);
            using var socket = new ScriptSocket();
            using var storage = new IngestionDatabase();
            var queue = storage.OpenQueue();
            await queue.OpenAsync(run, Now, token);
            var secondPage = NewArrivalSignal();
            var release = NewArrivalSignal();
            var attempts = 0;
            using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
            {
                var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
                if (offset == 0 && ++attempts == 1)
                    return FullRecoveryPage(Row("original-premium", Now.AddHours(-32), "first.mp3"), size);
                if (offset == 0)
                    return Reply(Row("restricted-later", Now.AddHours(-30), "restricted.mp3"),
                        Row("eligible-standard", Now.AddHours(-2), "standard.mp3"));
                secondPage.TrySetResult();
                await release.Task.WaitAsync(cancellation);
                return Reply(Row("restricted-later", Now.AddHours(-30), "restricted.mp3"));
            }));
            var stream = IngestionStream(settings, http, _ => socket, subscription);
            var recovery = new LiveStreamRecovery();
            recovery.Request(Now.AddDays(-2));
            await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
            Check(await reader.MoveNextAsync() && await reader.MoveNextAsync() && reader.Current.BackendId == "original-premium",
                "Premium must admit the first authorized page.");
            await SaveIngestionAsync(queue, run, reader.Current, token);
            var read = reader.MoveNextAsync().AsTask();
            await secondPage.Task.WaitAsync(token);

            if (switchAccount)
                ((SettingsStub)(object)settings).Username = "replacement-test-account";
            else
            {
                subscription.Tier = 1;
                await subscription.EnsureSubscriptionAsync(token);
            }
            release.SetResult();

            if (switchAccount)
            {
                Check(!await read && attempts == 1, "A retired account's recovery cannot continue under a different account.");
                var replacement = run with { Owner = 2, Account = RecordingPlaybackAccess.AccountKey(settings, server) };
                await queue.OpenAsync(replacement, Now, token);
                Check(await queue.NextAsync(replacement, 0, token) == null &&
                      (await queue.NextAsync(run, 0, token))?.Id == "original-premium",
                    "An account switch must retain the original scope's committed row without transferring it.");
            }
            else
            {
                Check(await read && reader.Current.BackendId == "eligible-standard" && attempts == 2,
                    "A downgrade must discard the stale response and retry using the currently authorized window.");
                await SaveIngestionAsync(queue, run, reader.Current, token);
                var floor = DurableLiveQueue.HistoricalFloor(true, 2, true, 1, Now);
                Check((await queue.NextAsync(run, floor, token))?.Id == "eligible-standard",
                    "Only the authorized recording may reach autoplay after the downgrade.");
            }
            Check(await queue.GetArrivalSequenceAsync(run, "restricted-later", token) == null,
                "A revoked HTTP response must not reach durable storage.");
        }
    }

    private static CallStreamService IngestionStream(ISettingsService settings, HttpClient http, Func<string, ScriptSocket?> open,
        ScriptSubscription? subscription = null) => new(settings, new RecordedCalls(), new AddressStub(),
            subscription ?? new ScriptSubscription(false, null), http,
            (server, _, _) => Task.FromResult<WebSocket?>(open(server)), new FixedClock(Now));

    private static TaskCompletionSource NewArrivalSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<(int Offset, int Size)> ReadRecoveryPageRequestAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        if (request.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var document = JsonDocument.Parse(body);
            return (document.RootElement.GetProperty("start").GetInt32(), document.RootElement.GetProperty("length").GetInt32());
        }
        var fields = body.Split('&').Select(field => field.Split('=', 2)).ToDictionary(field => field[0], field => field[1]);
        return (int.Parse(fields["start"]), int.Parse(fields["length"]));
    }

    private static HttpResponseMessage FullRecoveryPage(object first, int size) => Reply(new[] { first }
        .Concat(Enumerable.Range(0, size - 1).Select(i => Row($"outside-{i}", Now.AddDays(-4), "outside.mp3"))).ToArray());

    internal static async Task HttpOnlyRecoveryRetriesPartialFailure()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var run = new DurableLiveQueue.Run(server, 1, null);
        await queue.OpenAsync(run, Now, token);
        var first = Row("first", Now.AddMinutes(-5), "first.mp3");
        var missed = Row("fallback", Now.AddMinutes(-30), "fallback.mp3");
        var requests = 0;
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            var (offset, size) = await ReadRecoveryPageRequestAsync(request, cancellation);
            if (offset > 0)
                throw new HttpRequestException("Test-only second-page failure.");
            return ++requests switch
            {
                1 => FullRecoveryPage(first, size),
                2 or 3 => Reply(missed),
                _ => Reply(missed, Row("later", Now.AddMinutes(-40), "later.mp3"))
            };
        }));
        var stream = IngestionStream(Settings(server), http, _ => null);
        var recovery = new LiveStreamRecovery();
        recovery.Request(Now.AddHours(-1));
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "first", "First recovery page was lost.");
        var a = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Fallback polling did not connect.");
        recovery.Request(Now.AddMinutes(-10));
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "fallback" && requests == 2,
            "The initial fallback poll suppressed an eligible recording from the incomplete window.");
        var b = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "later",
            "Retry must accept additional recordings without replaying the fallback result.");
        var c = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(a.ArrivalSequence < b.ArrivalSequence && b.ArrivalSequence < c.ArrivalSequence,
            "Observed HTTP rows must retain FIFO positions despite incorrect recorder timestamps.");
    }

    internal static async Task RecoveryAdmitsPreviouslyProvisionalObservation()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var run = new DurableLiveQueue.Run(server, 1, null);
        await queue.OpenAsync(run, Now, token);
        var earlier = Row("provisional", Now.AddMinutes(-20), "eligible.mp3");
        var requests = 0;
        using var http = new HttpClient(new AsyncStubHandler((_, _) =>
        {
            var response = ++requests <= 2 ? Reply(earlier) :
                Reply(earlier, Row("later", Now.AddMinutes(-25), "later.mp3"));
            return Task.FromResult(response);
        }));
        var recovery = new LiveStreamRecovery();
        var stream = IngestionStream(Settings(server), http, _ => null);
        await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT",
            "The baseline poll should signal before suppressing its backlog.");
        recovery.Request(Now.AddHours(-1));
        Check(await reader.MoveNextAsync() && requests == 2 && reader.Current.BackendId == "provisional",
            "A prior baseline observation must remain eligible when the authorized recovery returns it.");
        var card = reader.Current;
        var first = await SaveIngestionAsync(queue, run, card, token);
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "later", "A saved duplicate must not replay.");
        var second = await SaveIngestionAsync(queue, run, reader.Current, token);
        Check(first.ArrivalSequence < second.ArrivalSequence, "An eligible provisional row must precede a later observation.");
        GC.KeepAlive(card);
    }

    private static async Task<DurableLiveQueue.Observation> SaveIngestionAsync(DurableLiveQueue queue, DurableLiveQueue.Run run,
        CallItem item, CancellationToken token, Action<Exception>? report = null)
    {
        var saved = await queue.ObserveWithPositionAsync(run, item.BackendId, item.RecordingTimeUtc,
            item.LiveState!.Read(), token, item.RecordingAccess?.Origin ?? RecordingOrigin.Unknown,
            existingOnly: (item.IsLiveUpdate || item.IsRecordingNotification) && item.RecoveryWindowId == null,
            report: report, recoveryWindowId: item.RecoveryWindowId);
        item.ConfirmLiveAdmission(saved.ArrivalSequence.HasValue);
        return saved;
    }

    private sealed class IngestionDatabase : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("JoesScanner-PagedIngestion-");
        internal string DbPath => Path.Combine(_directory.FullName, "queue.db");
        internal DurableLiveQueue OpenQueue() => new(new ArrivalTestPaths(DbPath));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _directory.Delete(recursive: true);
        }
    }
}
