using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static partial class MobileRecoveryTests
{
    internal static async Task OldDurableSignedAudio()
    {
        var folder = Directory.CreateTempSubdirectory("JoesScanner-SignedAudio-");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var token = timeout.Token;
            var paths = new SignedAudioPaths(Path.Combine(folder.FullName, "queue.db"));
            var server = HostedServerRules.DefaultServerUrl;
            var recorded = Now.AddDays(-4);
            var recorderOffset = TimeSpan.FromMinutes(330);
            var expectedRange = RecordingPlaybackAccess.DateSearch(recorded.AddSeconds(-2), recorded.AddSeconds(2), recorderOffset);
            var queue = new DurableLiveQueue(paths);
            using var first = new Fixture();
            using var second = new Fixture();
            var settingsA = (ISettingsService)first.Settings;
            var settingsB = (ISettingsService)second.Settings;
            var a = new DurableLiveQueue.Run(server, 1, 2, Protected: true, Account: RecordingPlaybackAccess.AccountKey(settingsA, server));
            var b = a with { Owner = 2, Account = RecordingPlaybackAccess.AccountKey(settingsB, server) };
            var subscriptionA = new ScriptSubscription(true, 2, settingsA);
            var subscriptionB = new ScriptSubscription(true, 2, settingsB);
            const string id = "shared-id";
            foreach (var run in new[] { a, b })
            {
                await queue.OpenAsync(run, Now, token);
                await queue.ObserveAsync(run, id, recorded, SignedSnapshot("https://media.example/audio.mp3?lease=expired"),
                    token, RecordingOrigin.History);
            }

            queue = new DurableLiveQueue(paths);
            a = a with { Owner = 3 };
            b = b with { Owner = 4 };
            foreach (var run in new[] { a, b })
                await queue.OpenAsync(run, Now, token);
            var restored = (await queue.NextAsync(a, 0, token))!;
            var cardA = restored.ToCallItem(server);
            var cardB = (await queue.NextAsync(b, 0, token))!.ToCallItem(server);
            Check(cardA.AudioUrl.Length == 0 && cardB.AudioUrl.Length == 0,
                "Signed locations must be absent after reopening the journal.");
            var accountARequests = new List<int>();
            var accountBRequests = new List<int>();
            using var clientA = first.CreateClient(new AsyncStubHandler((request, cancellation) =>
                ServeRecording(request, cancellation, first, accountARequests, "A%252F")));
            using var clientB = second.CreateClient(new AsyncStubHandler((request, cancellation) =>
                ServeRecording(request, cancellation, second, accountBRequests, "B%252F")));
            var streamA = CreateStream(settingsA, subscriptionA, clientA);
            var streamB = CreateStream(settingsB, subscriptionB, clientB);

            var acquired = await RecordingAudioRecovery.AcquireAsync(cardA.AudioUrl,
                ct => streamA.RefreshRecordingAudioAsync(cardA, server, cardA.RecordingTimeUtc, ct),
                async (url, ct) =>
                {
                    using var response = await clientA.GetAsync(url, ct);
                    response.EnsureSuccessStatusCode();
                    return "audio-A";
                }, token);
            Check(acquired == "audio-A" && accountARequests.SequenceEqual([-1, 0, 2]) &&
                  cardA.LiveState!.Read().AudioUrl!.EndsWith("?lease=A%252F", StringComparison.Ordinal),
                "A retained recording beyond the newest 1,000 must be located in its timestamp window and correctly paged.");
            Check(restored.Order == (await queue.NextAsync(a, 0, token))!.Order, "Lookup must preserve the durable queue position.");
            await queue.ObserveAsync(a, id, null, cardA.LiveState!.Read(), token, existingOnly: true);
            Check((await queue.NextAsync(a, 0, token))?.Snapshot?.AudioUrl == null,
                "Merging recovered metadata must never persist the signed query.");

            await RejectHistory(() => streamB.RefreshRecordingAudioAsync(cardA, server, recorded, token));
            Check(accountBRequests.Count == 0, "A different account must not issue a lookup for this card.");
            var refreshedB = await streamB.RefreshRecordingAudioAsync(cardB, server, recorded, token);
            Check(refreshedB?.EndsWith("?lease=B%252F", StringComparison.Ordinal) == true && accountBRequests.SequenceEqual([-1, 0, 2]),
                "The same recording ID must independently recover B's authorized URL.");
            subscriptionA.Tier = 1;
            await RejectHistory(() => streamA.RefreshRecordingAudioAsync(cardA, server, recorded, token));
            Check(accountARequests.Count == 3 && await queue.IsPendingAsync(a, id, 0, token),
                "A downgrade must deny the old lookup before calljson and leave the row pending.");

            async Task<HttpResponseMessage> ServeRecording(HttpRequestMessage request, CancellationToken cancellation,
                Fixture fixture, List<int> pages, string lease)
            {
                if (request.Method == HttpMethod.Get)
                {
                    Check(request.RequestUri!.Query == "?lease=" + lease && request.Headers.Authorization == null,
                        "The media request must preserve the signed query and keep firewall credentials on the recorder.");
                    return new(HttpStatusCode.OK);
                }

                Check(request.RequestUri == new Uri(server + "/calljson") &&
                    request.Headers.Authorization?.Equals(fixture.Issued) == true,
                    "Lookups must use the selected recorder's API-issued firewall authorization.");
                var (date, start, length) = await ReadLookupRequest(request, cancellation);
                pages.Add(length == 0 ? -1 : start);
                Check(date.Length != 0, "A dated lookup must never search across the unfiltered newest results.");
                if (length == 0)
                    return LookupReply(0, "+05:30");
                Check(date == expectedRange && length == 200, "Lookup must use the recorder's wall time and a bounded page.");
                return start == 0
                    ? LookupReply(3, "+05:30", Row("same-second", recorded, "wrong.mp3"), Row(id, recorded.AddSeconds(1), "reused.mp3"))
                    : LookupReply(3, "+05:30", Row(id, recorded, "https://media.example/audio.mp3?lease=" + lease));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            folder.Delete(recursive: true);
        }
    }

    internal static async Task UnavailableDurableAudio()
    {
        var folder = Directory.CreateTempSubdirectory("JoesScanner-UnavailableAudio-");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var token = timeout.Token;
            const string server = "https://custom.example";
            var queue = new DurableLiveQueue(new SignedAudioPaths(Path.Combine(folder.FullName, "queue.db")));
            var run = new DurableLiveQueue.Run(server, 1, null);
            await queue.OpenAsync(run, Now, token);
            var old = Now.AddDays(-5);
            await queue.ObserveAsync(run, "old", old, SignedSnapshot(null), token, RecordingOrigin.History);
            var card = (await queue.NextAsync(run, 0, token))!.ToCallItem(server);
            var settings = Settings(server);
            var subscription = new ScriptSubscription(false, null);
            var pageCalls = 0;
            var temporaryFailure = false;
            var mode = "missing";
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
            {
                var (date, _, length) = await ReadLookupRequest(request, cancellation);
                Check(date.Contains('|'), "Dated custom recovery should use the existing History date filter.");
                if (length == 0)
                    return LookupReply(0, null);
                pageCalls++;
                if (mode == "blocked")
                {
                    blocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                }
                if (mode == "missing")
                    return LookupReply(0, null);
                if (!temporaryFailure)
                {
                    temporaryFailure = true;
                    throw new HttpRequestException("Temporary recorder failure.");
                }
                return LookupReply(1, null, Row("old", old, "track.mp3?lease=fresh"));
            }));
            var stream = CreateStream(settings, subscription, http);
            Task<string> Acquire(CancellationToken ct) => RecordingAudioRecovery.AcquireAsync(null,
                t => stream.RefreshRecordingAudioAsync(card, server, old, t),
                (_, _) => Task.FromResult<string?>("downloaded"), ct);

            Exception? failure = null;
            var outcome = await PlaybackAttempt.RunAsync(async () => { await Acquire(token); }, token, ex => failure = ex);
            Check(failure is RecordingAudioUnavailableException && failure.Message.Contains("bounded time lookup") &&
                  !outcome.AdvancesQueue() && pageCalls == 1 && await queue.IsPendingAsync(run, "old", 0, token),
                "A missing or deleted row must stop the current lookup without repeating the scan or marking it heard.");

            mode = "blocked";
            var requests = new LivePlaybackRequests();
            using (var request = requests.Begin(manual: true, token))
            {
                var lookup = Acquire(request.Token);
                await blocked.Task.WaitAsync(token);
                await queue.ObserveAsync(run, "new", Now, SignedSnapshot(null), token, RecordingOrigin.Live);
                requests.RetireCurrent();
                try { await lookup; throw new InvalidOperationException("Stopped lookup still finished."); }
                catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
            }
            Check((await queue.NextAsync(run, 0, token))?.Id == "old" &&
                  await queue.IsPendingAsync(run, "new", 0, token),
                "Stop must cancel the HTTP operation while allowing ingestion and retaining queue order.");

            mode = "retry";
            Check(await Acquire(token) == "downloaded" && temporaryFailure && pageCalls == 4 &&
                  subscription.Checks == 0, "Retry must recover from a transient connection failure for a genuine custom server.");
            await queue.MarkAsync(run, "old", PlaybackOutcome.Skipped, token);
            Check((await queue.NextAsync(run, 0, token))?.Id == "new", "An explicit Skip can advance past an unheard recording.");
            Check((await queue.JumpToLiveAsync(run, 0, skipSelected: true, token))?.Target.Id == "new" &&
                  await queue.NextAsync(run, 0, token) == null, "Jump to Live must still retire the selected backlog.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            folder.Delete(recursive: true);
        }
    }

    internal static async Task ConcurrentSignedAudioUpdates()
    {
        const string server = "https://directory.test:8443/scanner";
        var settings = Settings(server);
        var subscription = new ScriptSubscription(true, 1, settings);
        var recorded = Now.AddHours(-3);
        var state = new LiveCallState(server, "example");
        const string stale = "https://media.example/old.mp3?lease=stale";
        var oldSnapshot = SignedSnapshot(stale);
        state.Observe(oldSnapshot, LiveCallState.NextVersion());
        var card = new CallItem
        {
            BackendId = state.Id, RecordingServer = server, RecordingTimeUtc = recorded, LiveState = state, AudioUrl = stale,
            RecordingAccess = new(RecordingOrigin.History, true, RecordingPlaybackAccess.AccountKey(settings, server))
        };
        var latest = stale;
        using var http = new HttpClient(new AsyncStubHandler(async (request, token) =>
        {
            var (date, _, length) = await ReadLookupRequest(request, token);
            Check(date.Contains('|'), "Standard lookup must always use a bounded time range.");
            if (length == 0)
                return LookupReply(0, "-08:00");
            Check(date == RecordingPlaybackAccess.DateSearch(recorded.AddSeconds(-2), recorded.AddSeconds(2), TimeSpan.FromHours(-8)),
                "Standard request must use the recorder's offset with a timestamp-scoped range.");
            state.Observe(SignedSnapshot(latest), LiveCallState.NextVersion());
            return LookupReply(1, "-08:00", Row(state.Id, recorded, "https://media.example/fresh.mp3?lease=lookup"));
        }));
        var stream = CreateStream(settings, subscription, http);
        var refreshed = await stream.RefreshRecordingAudioAsync(card, server, recorded, default);
        Check(refreshed?.EndsWith("?lease=lookup", StringComparison.Ordinal) == true,
            "Duplicate metadata repeating an expired URL must not discard freshly recovered audio.");
        latest = "https://media.example/newest.mp3?lease=ws";
        Check(await stream.RefreshRecordingAudioAsync(card, server, recorded, default) == latest,
            "A different, newer WebSocket URL must win over an older HTTP observation.");
    }

    private static CallStreamService CreateStream(ISettingsService settings, ISubscriptionService subscription, HttpClient client) =>
        new(settings, new RecordedCalls(), new AddressStub(), subscription, client,
            (_, _, _) => Task.FromResult<WebSocket?>(null), new FixedClock(Now));

    private static async Task<(string Date, int Start, int Length)> ReadLookupRequest(HttpRequestMessage request, CancellationToken token)
    {
        Check(request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded",
            "A dated request must use the History browser's content type.");
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var payload = document.RootElement;
        return (payload.GetProperty("columns")[2].GetProperty("search").GetProperty("value").GetString()!,
            payload.GetProperty("start").GetInt32(), payload.GetProperty("length").GetInt32());
    }

    private static HttpResponseMessage LookupReply(int count, string? offset, params object[] rows) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { recordsFiltered = count, serverTimeZoneOffset = offset, data = rows }) };

    private static LiveCallSnapshot SignedSnapshot(string? url) =>
        new(new CallCardMetadata { Talkgroup = "Dispatch" }, null, CallCardIdFallback.None, url);

    private sealed record SignedAudioPaths(string DbPath) : IDatabasePathProvider;
}
