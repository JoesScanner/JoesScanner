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
using Microsoft.Data.Sqlite;

internal static partial class MobileRecoveryTests
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
            (HostedServerRules.DefaultServerUrl, 1, true, ["recent", "boundary"], 1),
            (HostedServerRules.DefaultServerUrl, 2, true, ["recent", "boundary", "outside"], 1),
            ("https://directory.test:8443/scanner", 1, true, ["recent", "boundary"], 1),
            (HostedServerRules.DefaultServerUrl, null, true, [], 1),
            (HostedServerRules.DefaultServerUrl, 0, true, [], 1),
            (HostedServerRules.DefaultServerUrl, 2, false, [], 1),
            ("https://custom.example", null, false, ["recent", "boundary", "outside"], 0)
        })
        {
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var settings = Settings(origin);
            using var socket = new ScriptSocket();
            socket.Send(Row("new-live", Now.AddSeconds(10), "live.mp3"));
            var subscription = new ScriptSubscription(allowed, tier, settings);
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
            Check(await reader.MoveNextAsync() && reader.Current.BackendId == "new-live",
                "An already received WebSocket row must precede the recovery response.");
            var actual = new List<string>();
            for (var i = 0; i < expected.Length; i++)
            {
                Check(await reader.MoveNextAsync(), "A recovery row was lost.");
                actual.Add(reader.Current.BackendId!);
            }
            Check(actual.SequenceEqual(expected) && subscription.Checks == checks,
                "Recovery must preserve response order within the authorized window.");
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
        var clock = new FixedClock(Now);
        var resumedAt = Now.AddHours(2);
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            Check(await DateFilter(request, cancellation) == RecordingPlaybackAccess.DateSearch(resumedAt.AddSeconds(-30), resumedAt),
                "An unverified account may poll only the current Live window.");
            return Reply(Row("old", Now.AddHours(1), "old.mp3"), Row("new", resumedAt, "new.mp3"));
        }));
        var opens = 0;
        var stream = new CallStreamService(Settings(HostedServerRules.DefaultServerUrl), repository, new AddressStub(),
            new ScriptSubscription(false, null), http,
            (_, _, _) =>
            {
                if (++opens == 1)
                    return Task.FromResult<WebSocket?>(oldSocket);
                clock.UtcNow = resumedAt;
                return Task.FromResult<WebSocket?>(null);
            }, clock);
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

    internal static async Task StreamArrivalOrder()
    {
        const string server = "https://custom.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var socket = new ScriptSocket();
        var first = Row("a", Now.AddYears(30), "a.mp3");
        socket.Send(new { data = new[]
        {
            first,
            new { DT_RowId = "b", StartTimeUTC = Now.ToOffset(TimeSpan.FromHours(-7)).ToString("O"), AudioFilename = "b.mp3" },
            new { DT_RowId = "c", StartTimeUTC = Now.ToOffset(TimeSpan.FromHours(9)).ToString("O"), AudioFilename = "c.mp3" },
            Row("d", Now.AddYears(-30), null)
        } });
        using var http = new HttpClient(new StubHandler(_ => Reply(
            Row("f", Now.AddMinutes(-1), "f.mp3"), first, Row("e", Now.AddHours(-3), "e.mp3"),
            Row("d", Now, "d.mp3"))));
        var stream = new CallStreamService(Settings(server), new RecordedCalls(), new AddressStub(),
            new ScriptSubscription(false, null), http, (_, _, _) => Task.FromResult<WebSocket?>(socket), new FixedClock(Now));
        var recovery = new LiveStreamRecovery();
        recovery.Request(Now.AddHours(-4));
        var directory = Directory.CreateTempSubdirectory("JoesScanner.StreamArrival-");
        try
        {
            var queue = new DurableLiveQueue(new ArrivalTestPaths(Path.Combine(directory.FullName, "queue.db")));
            var run = new DurableLiveQueue.Run(server, 1, null);
            await queue.OpenAsync(run, Now, token);
            await using var reader = stream.GetCallStreamAsync([], token, recovery).GetAsyncEnumerator(token);
            Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Missing stream handshake.");
            var expected = new[] { "a", "b", "c", "d", "f", "e" };
            foreach (var id in expected.Append("d"))
            {
                Check(await reader.MoveNextAsync() && reader.Current.BackendId == id,
                    $"The production stream must emit {id} in arrival order.");
                var call = reader.Current;
                var observed = await queue.ObserveWithPositionAsync(run, id, call.RecordingTimeUtc, call.LiveState!.Read(), token,
                    call.RecordingAccess?.Origin ?? RecordingOrigin.Unknown, existingOnly: call.IsLiveUpdate);
                Check(observed.ArrivalSequence.HasValue, "The accepted identity must have a durable position.");
            }

            foreach (var id in expected)
            {
                var next = await queue.NextAsync(run, 0, token);
                Check(next?.Id == id && next.Snapshot?.AudioUrl != null,
                    "Durable playback must match stream arrival order and see the late audio update.");
                await queue.CommitCompletionAsync(run, id);
            }
            Check(await queue.NextAsync(run, 0, token) == null, "Duplicate HTTP and WebSocket rows must share one position.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private sealed record ArrivalTestPaths(string DbPath) : IDatabasePathProvider;

    internal static async Task PlaybackEntitlement()
    {
        var server = HostedServerRules.DefaultServerUrl;
        var settings = Settings(server);
        var stub = (SettingsStub)(object)settings;
        var subscription = new ScriptSubscription(true, 2, settings);
        var account = RecordingPlaybackAccess.AccountKey(settings, server);
        var history = new RecordingAccessContext(RecordingOrigin.History, true, account);
        var clock = new FixedClock(Now);
        var old = Now.AddHours(-30);

        Task<DateTimeOffset?> Authorize(DateTimeOffset date, RecordingAccessContext source, bool currentLive = false) =>
            RecordingPlaybackAccess.VerifyAsync(settings, subscription, server, date, source,
                currentLive, requireAccount: true, CancellationToken.None, clock);

        Check(await Authorize(old, history, currentLive: true) == null, "Premium history should be permitted.");
        subscription.Tier = 1;
        await Denied(() => Authorize(old, history, currentLive: true));
        Check(await Authorize(Now.AddHours(-24), history) == Now.AddHours(-24),
            "Standard must admit the exact 24-hour boundary.");
        await Denied(() => Authorize(Now.AddHours(-24).AddTicks(-1), history));
        subscription.Tier = null;
        await Denied(() => Authorize(Now.AddHours(-1), history));
        Check(await Authorize(old, history with { Origin = RecordingOrigin.Live }, currentLive: true) == null,
            "A legitimately received current Live recording may still play.");
        await Denied(() => Authorize(old, history with { Origin = RecordingOrigin.Live }));
        subscription.Tier = 2;
        stub.SubscriptionExpiry = Now.UtcDateTime;
        await Denied(() => Authorize(Now.AddMinutes(-1), history));
        stub.SubscriptionExpiry = null;
        await Denied(() => Authorize(old, history with { Account = "another-account" }));
        Check(await Authorize(old, history) == null,
            "A denied attempt must be retryable after legitimate online verification returns.");
        Check(await RecordingPlaybackAccess.VerifyAsync(Settings("https://custom.example"), subscription,
            "https://custom.example", old, new(RecordingOrigin.History, false), false, false,
            CancellationToken.None, clock) == null, "Custom history should remain unrestricted.");
    }

    private static async Task Denied(Func<Task<DateTimeOffset?>> playback)
    {
        try { await playback(); }
        catch (RecordingAccessException) { return; }
        throw new InvalidOperationException("Historical acquisition should have been denied.");
    }

    internal static async Task RecordingAudioRefresh()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var server = "https://custom.example";
        var settings = Settings(server);
        using var socket = new ScriptSocket();
        socket.Send(new { DT_RowId = "sqlite-only", Update = true });
        socket.Send(new { DT_RowId = "sqlite-only", Update = true });
        var lookups = 0;
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            Check(request.RequestUri == new Uri(server + "/calljson"), "Lookup must stay on the selected recorder.");
            Check(await DateFilter(request, cancellation) == "",
                "An ID-only custom notification uses the compatibility lookup until a timestamp is available.");
            lookups++;
            return Reply(Row("unrelated", Now, "other.mp3"),
                Row("sqlite-only", Now.AddMinutes(-1), $"clip.mp3?testLease={lookups}"));
        }));
        var stream = new CallStreamService(settings, new RecordedCalls(), new AddressStub(), new ScriptSubscription(false, null), http,
            (_, _, _) => Task.FromResult<WebSocket?>(socket), new FixedClock(Now));
        await using var reader = stream.GetCallStreamAsync([], token.Token).GetAsyncEnumerator(token.Token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Missing Live handshake.");
        Check(await reader.MoveNextAsync() && reader.Current is { IsRecordingNotification: true, BackendId: "sqlite-only" },
            "An ID-only notification must be delivered for a record absent from the in-memory index.");
        var item = reader.Current;
        var cachedUrl = RecordingStorageReference.ForJournal(server + "/expired.mp3?lease=old");
        Check(cachedUrl == null, "A restart must receive no persisted signed URL.");
        var acquired = await RecordingAudioRecovery.AcquireAsync(cachedUrl,
            t => stream.RefreshRecordingAudioAsync(item, server, null, t),
            (url, _) => Task.FromResult<string?>(url.EndsWith("?testLease=1", StringComparison.Ordinal) ? "downloaded" : null),
            token.Token);
        Check(acquired == "downloaded" && lookups == 1, "A restored recording with no cached URL must acquire fresh audio.");

        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
        {
            var events = new List<HttpStatusCode?>();
            var attempt = 0;
            acquired = await RecordingAudioRecovery.AcquireAsync(server + "/expired.mp3?lease=old", t =>
            {
                if (attempt++ == 0)
                    throw new HttpRequestException("Temporary network failure.");
                return stream.RefreshRecordingAudioAsync(item, server, null, t);
            }, (url, _) =>
            {
                if (url.Contains("expired.mp3"))
                {
                    events.Add(status);
                    throw new HttpRequestException("Expired audio link.", null, status);
                }
                events.Add(null);
                return Task.FromResult<string?>("downloaded");
            }, token.Token);
            Check(acquired == "downloaded" && attempt == 2 && events.SequenceEqual([status, null]),
                "403/404 and a temporary refresh failure must reacquire once without replaying stale media.");
        }
        Check(await stream.RefreshRecordingAudioAsync(item, "https://second.example", null, token.Token) == null && lookups == 3,
            "An overlapping ID on another recorder must not refresh from the current recorder.");
        var refreshes = 0;
        acquired = await RecordingAudioRecovery.AcquireAsync(server + "/expired.mp3?lease=old", _ =>
        {
            refreshes++;
            return Task.FromResult<string?>(server + "/fresh.mp3?lease=new");
        }, (url, _) => url.Contains("expired.mp3")
            ? throw new FirewallAuthenticationException("Signed media denied", HttpStatusCode.Forbidden)
            : Task.FromResult<string?>("downloaded"), token.Token);
        Check(acquired == "downloaded" && refreshes == 1,
            "A host-local signed 403 can refresh through metadata without changing firewall authentication.");
        try
        {
            await RecordingAudioRecovery.AcquireAsync(server + "/plain.mp3",
                _ => throw new InvalidOperationException("Genuine firewall denial must not try metadata."),
                (_, _) => throw new FirewallAuthenticationException("Firewall denied", HttpStatusCode.Forbidden), token.Token);
            throw new InvalidOperationException("Expected the original firewall denial.");
        }
        catch (FirewallAuthenticationException) { }
        refreshes = 0;
        try
        {
            await RecordingAudioRecovery.AcquireAsync(null, _ =>
            {
                refreshes++;
                return Task.FromResult<string?>(null);
            }, (_, _) => throw new InvalidOperationException("Missing audio cannot begin acquisition."), token.Token);
            throw new InvalidOperationException("Unavailable audio must be reported to the caller.");
        }
        catch (RecordingAudioUnavailableException)
        {
            Check(refreshes == 2, "Missing recordings must use a bounded retry budget.");
        }
        Check(RecordingStorageReference.ForCache(server + "/clip.mp3?lease=secret") == null,
            "A signed URL must be excluded from persisted references.");
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
        internal DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ScriptSubscription(bool allowed, int? tier, ISettingsService? settings = null) : ISubscriptionService
    {
        internal int Checks { get; private set; }
        internal int? Tier { get; set; } = tier;
        internal bool Allowed { get; set; } = allowed;

        public Task<SubscriptionCheckResult> EnsureSubscriptionAsync(CancellationToken cancellationToken)
        {
            Checks++;
            if (settings is SettingsStub stub)
            {
                stub.SubscriptionTier = Tier ?? 0;
                stub.VerifiedSubscription = Allowed && Tier is 1 or 2;
            }
            return Task.FromResult(new SubscriptionCheckResult(Allowed) { VerifiedOnlineTier = Tier });
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
        internal void SendClose() => _messages.Writer.TryWrite([]);

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (!_messages.Reader.TryRead(out var bytes))
            {
                IdleReceive.TrySetResult();
                bytes = await _messages.Reader.ReadAsync(token);
            }
            if (bytes.Length == 0)
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
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
