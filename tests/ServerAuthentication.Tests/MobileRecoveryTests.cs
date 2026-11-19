using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using JoesScanner.Data;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;

internal static class MobileRecoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    internal static async Task PlaybackOutcomes()
    {
        foreach (var failure in new Exception[]
        {
            new HttpRequestException("Record not available"),
            new InvalidOperationException("Decoder error"),
            new OperationCanceledException("Unexpected cancellation"),
            new TimeoutException("Player watchdog")
        })
        {
            Exception? reported = null;
            var outcome = await PlaybackAttempt.RunAsync(() => Task.FromException(failure), CancellationToken.None,
                ex => reported = ex);
            Check(outcome == PlaybackOutcome.Failed && !outcome.AdvancesQueue() && reported == failure,
                "An unplayed item with a failure must remain at its current position.");
        }

        using var canceled = new CancellationTokenSource();
        var interrupted = await PlaybackAttempt.RunAsync(() =>
        {
            canceled.Cancel();
            return Task.FromCanceled(canceled.Token);
        }, canceled.Token, _ => throw new Exception("Expected stop should not report a failure."));
        Check(interrupted == PlaybackOutcome.Canceled && !interrupted.AdvancesQueue(),
            "An interrupted request must not advance the queue.");

        var retry = await PlaybackAttempt.RunAsync(() => Task.CompletedTask, CancellationToken.None,
            _ => throw new Exception("Successful retry should not report a failure."));
        Check(retry == PlaybackOutcome.Completed && retry.AdvancesQueue() && PlaybackOutcome.Skipped.AdvancesQueue(),
            "Completion and deliberate skips can advance the queue.");
    }

    internal static async Task Entitlement()
    {
        foreach (var (origin, tier, allowed, expected, checks) in new (string, int?, bool, string[], int)[]
        {
            (HostedServerRules.DefaultServerUrl, 1, true, ["boundary", "recent"], 1),
            (HostedServerRules.DefaultServerUrl, 2, true, ["outside", "boundary", "recent"], 1),
            ("https://directory.test:8443/scanner", 1, true, ["boundary", "recent"], 1),
            (HostedServerRules.DefaultServerUrl, null, true, [], 1),
            (HostedServerRules.DefaultServerUrl, 0, true, [], 1),
            (HostedServerRules.DefaultServerUrl, 2, false, [], 1),
            ("https://custom.example", null, false, ["outside", "boundary", "recent"], 0)
        })
        {
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var settings = Settings(origin);
            using var socket = new ScriptSocket();
            socket.Send(Row("new-live", Now.AddSeconds(10), "live.mp3"));
            var subscription = new ScriptSubscription(allowed, tier);
            var httpRequests = 0;
            using var http = new HttpClient(new StubHandler(_ =>
            {
                httpRequests++;
                Check(subscription.Checks == checks, "Account verification must precede hosted recovery HTTP.");
                return Reply(Row("recent", Now.AddHours(-1), "recent.mp3"),
                    Row("boundary", Now.AddHours(-24), "boundary.mp3"),
                    Row("outside", Now.AddHours(-24).AddSeconds(-1), "older.mp3"));
            }));
            var repository = new RecordedCalls();
            var stream = new CallStreamService(settings, repository, new AddressStub(), subscription, http,
                (_, _, _) => Task.FromResult<WebSocket?>(socket), new FixedClock(Now));
            var recovery = new LiveStreamRecovery();
            recovery.Request(Now.AddDays(-2));
            await using var reader = stream.GetCallStreamAsync([], token.Token, recovery).GetAsyncEnumerator(token.Token);
            Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Missing stream handshake.");
            var actual = new List<string>();
            while (await reader.MoveNextAsync() && reader.Current.BackendId != "new-live")
                actual.Add(reader.Current.BackendId!);
            Check(reader.Current.BackendId == "new-live" && actual.SequenceEqual(expected) && subscription.Checks == checks,
                "Recovery applied the wrong tier boundary or failed to deliver subsequent Live calls.");
            Check(httpRequests == (expected.Length == 0 ? 0 : 1) &&
                  repository.Ids.Where(id => id != "new-live").SequenceEqual(expected),
                "Disallowed recovery rows must not be fetched or persisted beyond the account's boundary.");
        }

        await DeniedRecoveryPollingFallback();
    }

    private static async Task DeniedRecoveryPollingFallback()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var oldSocket = new ScriptSocket();
        var repository = new RecordedCalls();
        using var http = new HttpClient(new StubHandler(_ => Reply(
            Row("old", Now.AddHours(-25), "old.mp3"),
            Row("new", Now.AddSeconds(1), "new.mp3"))));
        var opens = 0;
        var stream = new CallStreamService(Settings(HostedServerRules.DefaultServerUrl), repository, new AddressStub(),
            new ScriptSubscription(false, null), http,
            (_, _, _) => Task.FromResult<WebSocket?>(++opens == 1 ? oldSocket : null), new FixedClock(Now));
        var recovery = new LiveStreamRecovery();
        await using var reader = stream.GetCallStreamAsync([], token.Token, recovery).GetAsyncEnumerator(token.Token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Missing initial connection.");
        var next = reader.MoveNextAsync().AsTask();
        await oldSocket.IdleReceive.Task.WaitAsync(token.Token);
        recovery.Request(Now.AddDays(-2));
        Check(await next && reader.Current.BackendId == "new" && repository.Ids.SequenceEqual(["new"]),
            "When a replacement socket fails to open, polling must still reject unentitled backlog and deliver new Live calls.");
    }

    internal static async Task ReconnectAndLateAudio()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var oldSocket = new ScriptSocket();
        using var replacement = new ScriptSocket();
        var first = Row("seen", Now.AddMinutes(-8), "first.mp3");
        var incomplete = Row("recovered", Now.AddMinutes(-7), null);
        oldSocket.Send(first);
        var opens = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            Check(opens == 2 && oldSocket.WasAborted,
                "Gap recovery must run with the replacement connection, after aborting the old receive.");
            return Reply(incomplete, first);
        }));
        var stream = new CallStreamService(Settings("https://custom.example"), new RecordedCalls(), new AddressStub(),
            new ScriptSubscription(false, null), http, (_, validateLiveness, _) =>
            {
                Check(validateLiveness, "A foreground-capable stream must use socket liveness checks.");
                return Task.FromResult<WebSocket?>(++opens == 1 ? oldSocket : replacement);
            }, new FixedClock(Now));
        var recovery = new LiveStreamRecovery();
        await using var reader = stream.GetCallStreamAsync([], token.Token, recovery).GetAsyncEnumerator(token.Token);
        Check(await reader.MoveNextAsync(), "Missing stream handshake.");
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "seen", "Missing initial Live call.");

        var pending = reader.MoveNextAsync().AsTask();
        await oldSocket.IdleReceive.Task.WaitAsync(token.Token);
        recovery.Request(Now.AddMinutes(-10));
        Check(await pending && reader.Current.BackendId == "recovered" && reader.Current.AudioUrl.Length == 0,
            "The replacement stream must deduplicate the old call and admit the recovered row only once.");
        var card = reader.Current;
        replacement.Send(new
        {
            DT_RowId = "recovered", StartTimeUTC = Now.AddMinutes(-7).ToUnixTimeSeconds(),
            AudioFilename = "", AudioUrl = "https://custom.example/late.mp3"
        });
        replacement.Send(Row("recovered", Now.AddMinutes(-7), "late.mp3"));
        replacement.Send(Row("subsequent", Now.AddMinutes(1), "next.mp3"));
        Check(await reader.MoveNextAsync() && reader.Current.IsLiveUpdate &&
              ReferenceEquals(reader.Current.LiveState, card.LiveState), "Late audio must use the original card's identity.");
        var otherServer = new CallItem
        {
            BackendId = card.BackendId,
            LiveState = new LiveCallState("https://other.example", card.BackendId!)
        };
        Check(ReferenceEquals(reader.Current.LiveState!.FindCard([otherServer], [card], new object()), card),
            "A pending card must receive the update even when another server has the same ID.");
        card.ApplyLiveUpdate();
        Check(card.AudioUrl == "https://custom.example/late.mp3" && card.ConsumeLateAudio(),
            "The audio-only update must make the card playable.");
        card.ApplyLiveUpdate();
        Check(!card.ConsumeLateAudio() && await reader.MoveNextAsync() && reader.Current.BackendId == "subsequent" && opens == 2,
            "A replayed row must not requeue late audio or suppress a subsequent Live call.");
    }

    private static ISettingsService Settings(string url)
    {
        var settings = DispatchProxy.Create<ISettingsService, SettingsStub>();
        ((SettingsStub)(object)settings).ServerUrl = url;
        return settings;
    }

    private static object Row(string id, DateTimeOffset timestamp, string? file) =>
        new { DT_RowId = id, StartTimeUTC = timestamp.ToUnixTimeSeconds(), AudioFilename = file, CallText = "Call", CallDuration = 5 };

    private static HttpResponseMessage Reply(params object[] rows) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = rows }) };

    private static void Check(bool condition, string failure)
    {
        if (!condition)
            throw new InvalidOperationException(failure);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ScriptSubscription(bool allowed, int? tier) : ISubscriptionService
    {
        internal int Checks { get; private set; }

        public Task<SubscriptionCheckResult> EnsureSubscriptionAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult(new SubscriptionCheckResult(allowed) { VerifiedOnlineTier = tier });
        }
    }

    private sealed class AddressStub : IAddressDetectionService
    {
        public void Apply(CallItem item) { }
    }

    private sealed class RecordedCalls : ILocalCallsRepository
    {
        internal List<string> Ids { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertCallsAsync(string serverKey, IReadOnlyList<DbCallRecord> calls, CancellationToken cancellationToken)
        {
            Ids.AddRange(calls.Select(call => call.BackendId));
            return Task.CompletedTask;
        }
        public Task MarkTranscriptionUpdateNotifiedAsync(string serverKey, string backendId, CancellationToken token) => Task.CompletedTask;
        public Task<string?> GetLookupCacheAsync(string serverKey, string cacheKey, CancellationToken token) => Task.FromResult<string?>(null);
        public Task UpsertLookupCacheAsync(string serverKey, string cacheKey, DateTimeOffset fetchedAt, string jsonPayload, CancellationToken token) =>
            Task.CompletedTask;
    }

    private sealed class ScriptSocket : WebSocket
    {
        private readonly Channel<byte[]> _messages = Channel.CreateUnbounded<byte[]>();
        internal bool WasAborted { get; private set; }
        internal TaskCompletionSource IdleReceive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WasAborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        internal void Send(object row) => _messages.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(row));

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (!_messages.Reader.TryRead(out var bytes))
            {
                IdleReceive.TrySetResult();
                bytes = await _messages.Reader.ReadAsync(token);
            }
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }

        public override void Abort() => WasAborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
