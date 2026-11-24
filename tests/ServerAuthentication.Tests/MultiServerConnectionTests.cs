using System.Net.WebSockets;
using System.Threading.Channels;
using JoesScanner.Models;
using JoesScanner.Services;

internal static partial class MobileRecoveryTests
{
    private const string RecorderA = "https://one-recorder.test";
    private const string RecorderB = "https://two-recorder.test:8443";

    internal static Task SavedRecorderIdentity()
    {
        using var db = new IngestionDatabase();
        var paths = new ArrivalTestPaths(db.DbPath);
        var store = new ScannerConnectionStore(paths);
        var legacy = store.Load(RecorderA + "/").Single();
        Check(legacy.Enabled && legacy.Endpoint == RecorderA, "The upgrade must activate only the legacy selection.");
        var second = store.Save(RecorderB, "Recorder B", null, null);
        Check(legacy.Id != second.Id, "An independent connection must have its own permanent ID.");

        store.SetEnabled(legacy.Id, false);
        var edited = store.Save(RecorderA + "/new-path", null, null, legacy.Id);
        var reload = new ScannerConnectionStore(paths).Load(RecorderA);
        Check(edited.Id == legacy.Id && reload.Count == 2 &&
            reload.Single(row => row.Id == legacy.Id) == edited && !edited.Enabled && reload.Contains(second),
            "Editing or reloading one URL must preserve identity, enablement and the other recorder.");

        var directory = store.Save("https://listing-recorder.test", "Listed recorder", 42, null);
        var directoryUpdate = store.Save("https://new-listing-recorder.test", "Listed recorder", 42, null);
        Check(directoryUpdate.Id == directory.Id && directoryUpdate.DirectoryId == 42,
            "An approved directory endpoint change must preserve the configured server ID.");
        return Task.CompletedTask;
    }

    internal static async Task IndependentConnections()
    {
        using var test = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = test.Token;
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new RecorderTestClock();
        var manager = new ServerConnectionManager(clock, (delay, cancellation) =>
        {
            Check(delay == TimeSpan.FromSeconds(2), "An established connection restarts its own backoff.");
            retryEntered.TrySetResult();
            return retry.Task.WaitAsync(cancellation);
        });
        var a = new SavedScannerConnection(Guid.NewGuid(), RecorderA, "Recorder A", null, true);
        var b = new SavedScannerConnection(Guid.NewGuid(), RecorderB, "Recorder B", null, true);
        var failA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bCalls = Channel.CreateUnbounded<string>();
        var seenB = Channel.CreateUnbounded<string>();
        var startedA = Channel.CreateUnbounded<ServerConnectionManager.Connection>();
        var startedB = Channel.CreateUnbounded<ServerConnectionManager.Connection>();
        var attempts = 0;
        Task Monitor(ServerConnectionManager.Connection connection) => connection.Profile.Id == a.Id
            ? MonitorA(connection) : MonitorB(connection);

        async Task MonitorA(ServerConnectionManager.Connection connection)
        {
            manager.Report(connection, ServerConnectionStatus.Monitoring);
            startedA.Writer.TryWrite(connection);
            if (Interlocked.Increment(ref attempts) == 1)
            {
                await failA.Task.WaitAsync(connection.Token);
                throw new IOException("Synthetic disconnect.");
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, connection.Token);
        }

        async Task MonitorB(ServerConnectionManager.Connection connection)
        {
            manager.Report(connection, ServerConnectionStatus.Monitoring);
            startedB.Writer.TryWrite(connection);
            await foreach (var item in bCalls.Reader.ReadAllAsync(connection.Token))
                seenB.Writer.TryWrite(item);
        }

        manager.Synchronize([a, b], token, Monitor);
        var firstA = await startedA.Reader.ReadAsync(token);
        var firstB = await startedB.Reader.ReadAsync(token);
        var audio = new PlaybackOwnership();
        var lease = audio.Live!;
        var bPlaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bClip = audio.RunNativeAsync(lease, token, async cancel =>
        {
            bPlaying.TrySetResult();
            await finishB.Task.WaitAsync(cancel);
        });
        await bPlaying.Task.WaitAsync(token);
        failA.TrySetResult();
        await retryEntered.Task.WaitAsync(token);
        bCalls.Writer.TryWrite("during outage");
        Check(await seenB.Reader.ReadAsync(token) == "during outage" &&
            firstA.Status == ServerConnectionStatus.Reconnecting && firstB.Status == ServerConnectionStatus.Monitoring,
            "Recorder B must continue delivering calls while A reconnects.");

        clock.Timestamp = TimeSpan.FromMinutes(2).Ticks;
        manager.Report(firstA, ServerConnectionStatus.Reconnecting);
        Check(firstA.Status == ServerConnectionStatus.Unavailable, "Only A should reach the continuous outage threshold.");
        retry.TrySetResult();
        Check(ReferenceEquals(await startedA.Reader.ReadAsync(token), firstA) && firstA.Status == ServerConnectionStatus.Monitoring,
            "Transient recovery must reuse the connection generation and clear its outage.");
        Check(!bClip.IsCompleted && ReferenceEquals(audio.Live, lease) && manager.IsCurrent(firstB),
            "Recorder A must reconnect while Recorder B keeps its socket and current audio.");
        finishB.TrySetResult();
        await bClip.WaitAsync(token);

        var edited = a with { Endpoint = RecorderA + "/edited" };
        manager.Synchronize([edited, b], token, Monitor);
        var editedA = await startedA.Reader.ReadAsync(token);
        Check(editedA.Profile.Id == firstA.Profile.Id && editedA.Generation != firstA.Generation && manager.IsCurrent(firstB),
            "Editing a running recorder must replace only its connection.");
        manager.Synchronize([edited with { Enabled = false }, b], token, Monitor);
        await Task.WhenAll(firstA.Worker, editedA.Worker).WaitAsync(token);
        manager.Report(firstA, ServerConnectionStatus.Monitoring);
        Check(!manager.IsCurrent(firstA) && manager.IsCurrent(firstB) && !firstB.Token.IsCancellationRequested,
            "Disabling A must retire only A, including its delayed callbacks.");
        manager.Synchronize([edited, b], token, Monitor);
        var replacement = await startedA.Reader.ReadAsync(token);
        Check(replacement.Generation != editedA.Generation && replacement.Profile.Id == firstA.Profile.Id &&
            replacement.Profile.Endpoint.EndsWith("/edited") && manager.IsCurrent(firstB),
            "Reenabling an edited server must create a new generation without restarting B.");

        manager.Retire(_ => true);
        await Task.WhenAll(replacement.Worker, firstB.Worker).WaitAsync(token);
        Check(manager.Snapshot().Length == 0 && replacement.Token.IsCancellationRequested && firstB.Token.IsCancellationRequested,
            "Global Stop must cancel every independent transport.");
    }

    internal static async Task SimultaneousRecorderStreams()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var storage = new IngestionDatabase();
        var queue = storage.OpenQueue();
        var a = new DurableLiveQueue.Run(RecorderA, 31, null, ServerId: Guid.NewGuid());
        var b = new DurableLiveQueue.Run(RecorderB, 32, null, ServerId: Guid.NewGuid());
        var aRecovery = await LiveStreamRecovery.StartListeningAsync(queue, a, Now, token);
        var bRecovery = await LiveStreamRecovery.StartListeningAsync(queue, b, Now, token);

        // The legacy selected URL is B. Both adapters must still stay on their assigned endpoint.
        var settings = Settings(RecorderB);
        using var aSocket = new ScriptSocket();
        using var bSocket = new ScriptSocket();
        using var aStream = Stream(a, aSocket);
        using var bStream = Stream(b, bSocket);
        await using var aReader = aStream.GetCallStreamAsync([], token, aRecovery).GetAsyncEnumerator(token);
        await using var bReader = bStream.GetCallStreamAsync([], token, bRecovery).GetAsyncEnumerator(token);
        Check((await Task.WhenAll(aReader.MoveNextAsync().AsTask(), bReader.MoveNextAsync().AsTask())).All(open => open),
            "Both recorder streams must connect with one listening session.");
        aSocket.Send(Row("shared-id", Now, "one.mp3"));
        bSocket.Send(Row("shared-id", Now.AddHours(-1), "two.mp3"));
        Check(await aReader.MoveNextAsync() && await bReader.MoveNextAsync() &&
            aReader.Current.RecordingServer == RecorderA && bReader.Current.RecordingServer == RecorderB &&
            aReader.Current.BackendId == bReader.Current.BackendId,
            "The same recorder ID must be attributed to both independent sources.");
        await SaveIngestionAsync(queue, a, aReader.Current, token);
        await SaveIngestionAsync(queue, b, bReader.Current, token);
        var first = await queue.NextAsync([b, a], _ => 0, token);
        Check(first?.Scope == a && first.Row.Snapshot?.AudioUrl?.EndsWith("/one.mp3") == true,
            "Combined FIFO uses the first client arrival even when it came from the last inspected scope.");
        await queue.CommitCompletionAsync(a, "shared-id");
        var second = await queue.NextAsync([a, b], _ => 0, token);
        Check(second?.Scope == b && second.Row.Snapshot?.AudioUrl?.EndsWith("/two.mp3") == true,
            "Completion for one server must not consume the other server's identical ID.");

        CallStreamService Stream(DurableLiveQueue.Run scope, WebSocket socket) => new(settings, new RecordedCalls(),
            new AddressStub(), new ScriptSubscription(false, null),
            new HttpClient(new AsyncStubHandler((_, _) => Task.FromResult(Reply()))), (endpoint, _, _) =>
            {
                Check(endpoint == scope.Server, "The adapter used a different server's endpoint.");
                return Task.FromResult<WebSocket?>(socket);
            }, new FixedClock(Now), new(new Uri(scope.Server), scope.ServerId));
    }

    internal static async Task ScopedRecorderAuthorization()
    {
        var settings = Settings(RecorderB);
        var stub = (SettingsStub)(object)settings;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var shared = new Uri(RecorderA);
        stub.RecorderCredentials[(a, shared.AbsoluteUri)] = ("mock-a", "test-a");
        stub.RecorderCredentials[(b, shared.AbsoluteUri)] = ("mock-b", "test-b");
        var sent = new List<(string Host, string? User)>();
        using var handler = new StubHandler(request =>
        {
            var header = request.Headers.Authorization?.Parameter;
            var user = header == null ? null : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header)).Split(':')[0];
            sent.Add((request.RequestUri!.Host, user));
            return new(System.Net.HttpStatusCode.OK);
        });
        using var client = ServerAuthenticationHandler.CreateClient(settings, handler, new(shared, a));
        using var second = ServerAuthenticationHandler.CreateClient(settings, handler, new(shared, b));
        await client.GetAsync(RecorderA + "/audio.mp3");
        await second.GetAsync(RecorderA + "/other-recorder/audio.mp3");
        await client.GetAsync(RecorderB + "/foreign-audio.mp3");
        Check(sent.SequenceEqual(new[] { ("one-recorder.test", (string?)"mock-a"), ("one-recorder.test", "mock-b"), ("two-recorder.test", null) }),
            "A scoped request must use the configured recorder's credentials and never another origin's login.");
    }

    internal static async Task CombinedRecorderBoundaries()
    {
        using var storage = new IngestionDatabase();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var queue = storage.OpenQueue();
        var a = new DurableLiveQueue.Run(RecorderA, 100, null, ServerId: Guid.NewGuid());
        var b = new DurableLiveQueue.Run(RecorderB, 101, null, ServerId: Guid.NewGuid());
        await queue.StartListeningAsync(a, Now, token);
        await queue.StartListeningAsync(b, Now, token);
        await Add(a, "before-disable");
        await queue.StopAsync(a);
        await Add(b, "while-disabled");
        var newA = a with { Owner = 102 };
        await queue.StartListeningAsync(newA, Now, token);
        Check((await queue.NextAsync([newA, b], _ => 0, token))?.Row.Id == "while-disabled",
            "Disabling and reenabling A cannot restart B or replay A's old backlog.");
        await Add(newA, "while-in-history");
        await queue.SetPlaybackFloorAtLiveEdgeAsync([newA, b], token, skipOutstandingRecovery: true);
        Check(await queue.NextAsync([b, newA], _ => 0, token) == null,
            "History return must exclude backlog from every enabled server.");
        await Add(b, "after-history");
        Check((await queue.NextAsync([newA, b], _ => 0, token))?.Row.Id == "after-history",
            "The current session must keep accepting new recordings after History.");

        Task<bool> Add(DurableLiveQueue.Run scope, string id) =>
            queue.ObserveAsync(scope, id, Now, new(new(), scope.Server + "/audio.mp3", 0), token);
    }

    private sealed class RecorderTestClock : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
