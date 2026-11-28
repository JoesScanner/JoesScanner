using System.Net;
using System.Net.Http.Json;
using JoesScanner.Models;
using JoesScanner.Services;

internal static partial class MobileRecoveryTests
{
    internal static async Task RecorderLookupOwnership()
    {
        Check(CallStreamService.GetRecorderWebSocketUri("https://recorder.example/")?.AbsoluteUri ==
            "wss://recorder.example/Calls", "An origin recorder must keep its existing upgrade route.");
        foreach (var archive in new[] { "north", "south", "Archive/Case%20Sensitive" })
            Check(CallStreamService.GetRecorderWebSocketUri($"http://recorder.example:8080/{archive}/?ignored=1#section")?.AbsoluteUri ==
                $"ws://recorder.example:8080/{archive}/Calls", "The WebSocket must use the same archive path as HTTP.");
        Check(CallStreamService.GetRecorderWebSocketUri("https://user:password@recorder.example/archive") == null,
            "An upgrade destination with embedded credentials must be rejected.");
        foreach (var change in new[] { "switch", "roundtrip", "password", "endpoint", "reconnect", "late", "cancel", "unrelated", "current" })
            await CheckLookupOwner(change);
    }

    internal static async Task HistoryCatalogRequestOwnership()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        ISettingsService settings = fixture.Load();
        const string endpoint = "https://recorder.example";
        var recorder = new HistoryRecorder(Guid.NewGuid(), endpoint);
        DirectoryRecorderPolicy.For(settings).Update([new(recorder.Id, endpoint, "Recorder", null, true)]);
        var alpha = Guid.NewGuid().ToString("N");
        var bravo = Guid.NewGuid().ToString("N");
        var secret = Guid.NewGuid().ToString("N");
        void Select(string user) => settings.SetRecorderCredentials(recorder.Id, endpoint, user, secret);
        Select(alpha);
        var requests = 0;
        var bravoRequests = 0;
        var oldRefresh = true;
        using var http = ServerAuthenticationHandler.CreateClient(settings, new AsyncStubHandler((request, _) =>
        {
            requests++;
            var asBravo = HistoryBasic(bravo + ":" + secret).Equals(request.Headers.Authorization);
            if (asBravo) bravoRequests++;
            if (oldRefresh && request.RequestUri!.AbsolutePath.EndsWith("/receiversjson")) Select(bravo);
            if (oldRefresh && request.RequestUri!.AbsolutePath.EndsWith("/sitesjson")) Select(alpha);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = JsonContent.Create(new { results = new[] { new { id = "1", text = asBravo ? "Bravo" : "Alpha" } } }) });
        }));
        var service = new CallHistoryService(settings, new RecordedCalls(), new ScopedHistoryLookups(),
            new ScriptSubscription(false, null), http, new FixedClock(Now));
        HistoryLookupData? retired = null;
        try { retired = await service.GetRecorderLookupsAsync(recorder, refresh: true); }
        catch (Exception ex) when (ex is OperationCanceledException or RecordingAccessException) { }
        Check(retired == null && bravoRequests == 0, "A catalog continued under replacement credentials.");
        Select(alpha);
        oldRefresh = false;
        var previous = requests;
        var fresh = await service.GetRecorderLookupsAsync(recorder, refresh: false);
        var cached = await service.GetRecorderLookupsAsync(recorder, refresh: false);
        Check(requests - previous == 3 && fresh.Sites.Any(item => item.Label == "Alpha") &&
            cached.Sites.All(item => item.Label != "Bravo"), "A retired catalog populated the original account's cache.");
    }

    internal static async Task HistorySearchRequestOwnership()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        ISettingsService settings = fixture.Load();
        const string endpoint = "https://recorder.example";
        var recorder = new HistoryRecorder(Guid.NewGuid(), endpoint);
        DirectoryRecorderPolicy.For(settings).Update([new(recorder.Id, endpoint, "Recorder", null, true)]);
        var secret = Guid.NewGuid().ToString("N");
        var alpha = Guid.NewGuid().ToString("N");
        var bravo = Guid.NewGuid().ToString("N");
        void Select(string user) => settings.SetRecorderCredentials(recorder.Id, endpoint, user, secret);
        Select(alpha);
        var gated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentAsBravo = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new HttpClient(new BeforeHistoryAuthorization(gated, release, new ServerAuthenticationHandler(
            ServerAuthentication.For(settings), new AsyncStubHandler(async (request, token) =>
            {
                sentAsBravo |= HistoryBasic(bravo + ":" + secret).Equals(request.Headers.Authorization);
                var page = await ReadHistoryRequest(request, token);
                sent.TrySetResult();
                await returnPage.Task.WaitAsync(timeout.Token);
                return HistoryReply(page, 1, [Row(sentAsBravo ? "bravo-private" : "alpha-private", Now.AddMinutes(-1), "audio.wav")]);
            }))));
        var service = new CallHistoryService(settings, new RecordedCalls(), new ScopedHistoryLookups(),
            new ScriptSubscription(false, null), http, new FixedClock(Now));
        var pending = service.SearchAsync(recorder, SiteFilter(recorder), ArchiveDates(), timeout.Token);
        await gated.Task.WaitAsync(timeout.Token);
        Select(bravo);
        release.SetResult();
        await Task.WhenAny(pending, sent.Task).WaitAsync(timeout.Token);
        Select(alpha);
        returnPage.SetResult();
        HistorySearchResult? obsolete = null;
        try { obsolete = await pending; }
        catch (Exception ex) when (ex is OperationCanceledException or RecordingAccessException) { }
        Check(obsolete == null && !sentAsBravo, "A retired History search published another account's recording.");
        var current = await service.SearchAsync(recorder, SiteFilter(recorder), ArchiveDates(), timeout.Token);
        Check(current.Calls.Single().BackendId == "alpha-private", "A fresh search should use the restored login.");
        settings.SetRecorderCredentials(recorder.Id, endpoint, alpha, Guid.NewGuid().ToString("N"));
        await HistoryThrows<RecordingAccessException>(() => service.LoadMoreAsync(
            new(current.Snapshot, current.Cursor, current.Calls, true), timeout.Token));
    }

    private sealed class BeforeHistoryAuthorization(TaskCompletionSource entered, TaskCompletionSource release, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            entered.TrySetResult();
            await release.Task;
            return await base.SendAsync(request, token);
        }
    }

    internal static async Task ObservedDiscoveryOwnership()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        ISettingsService settings = fixture.Load();
        var recorder = new HistoryRecorder(Guid.NewGuid(), "https://recorder.example/alpha");
        var calls = 0;
        using var http = new HttpClient(new AsyncStubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }));
        using var service = new AuthObservedTriplesSyncService(settings, fixture, http);
        void Select(string user) => settings.SetRecorderCredentials(recorder.Id, recorder.Endpoint, user, "synthetic");
        Select("Alpha");
        var alpha = CallHistoryService.CaptureRecorderAccess(settings, recorder);
        Select("Bravo");
        var bravo = CallHistoryService.CaptureRecorderAccess(settings, recorder);
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.DbPath}");
        await db.OpenAsync();
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO calls(server_key, backend_id, voice_receiver, site_id, target_id, received_at_utc, updated_at_utc)
            VALUES($scope, 'shared', 'receiver', 'site', $talkgroup, '2026-01-01', '2026-01-01');
            """;
        foreach (var (scope, group) in new[] { (alpha.MetadataKey, "Alpha"), (bravo.MetadataKey, "Bravo"), (recorder.Endpoint, "Legacy") })
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$talkgroup", group);
            insert.ExecuteNonQuery();
        }
        var groups = await service.GetLocalObservedAsync(bravo, DateTime.MinValue, default);
        Check(groups.Single().TalkgroupValue == "Bravo", "Observed discovery read another account or an unowned historical row.");
        await service.TryReportAsync(bravo, true, default);
        Check(await service.TryFetchSeedAsync(bravo, default) == null &&
            await service.TrySyncExchangeAsync(bravo, true, default) == null && calls == 0,
            "A private custom discovery reached the shared catalog service.");
        Select("Alpha");
        var restored = CallHistoryService.CaptureRecorderAccess(settings, recorder);
        Check((await service.GetLocalObservedAsync(restored, DateTime.MinValue, default)).Single().TalkgroupValue == "Alpha" &&
            !alpha.TryPublish(() => throw new InvalidOperationException("A retired discovery reached publication.")),
            "A fresh login must restore its data without reviving earlier operations.");
    }

    private static async Task CheckLookupOwner(string change)
    {
        using var fixture = new SavedCredentialTests.Fixture();
        ISettingsService settings = fixture.Load();
        const string endpoint = "https://recorder.example";
        var id = Guid.NewGuid();
        var profile = new SavedScannerConnection(id, endpoint, "Recorder", null, true);
        var policy = DirectoryRecorderPolicy.For(settings);
        policy.Update([profile]);
        var userA = Guid.NewGuid().ToString("N");
        var userB = Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        void Save(string name, string secret) => settings.SetRecorderCredentials(id, endpoint, name, secret);
        Save(userA, password);
        var account = RecordingPlaybackAccess.RecorderAccountKey(settings, id, endpoint);
        var access = new RecordingAccessContext(RecordingOrigin.Live, false, account);
        var source = new LiveCallState(endpoint, "shared");
        source.Observe(SignedSnapshot(endpoint + "/first.wav"), LiveCallState.NextVersion());
        var before = source.Read();
        var card = new CallItem { ServerId = id, BackendId = source.Id, RecordingServer = endpoint, RecordingAccess = access,
            RecordingTimeUtc = Now, LiveState = source };
        card.ApplyRecordingAudio(before);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var rows = 0;
        var replacementLogin = false;
        using var http = ServerAuthenticationHandler.CreateClient(settings, new AsyncStubHandler(async (request, ct) =>
        {
            var (_, _, length) = await ReadLookupRequest(request, ct);
            if (length == 0)
            {
                if (change != "late")
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return LookupReply(0, null);
            }
            ++rows;
            var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(userB + ":" + password));
            replacementLogin = request.Headers.Authorization?.Parameter == expected;
            if (change == "late")
            {
                entered.TrySetResult();
                await release.Task;
            }
            return LookupReply(1, null, Row(source.Id, Now, replacementLogin ? "other.wav" : "returned.wav"));
        }), new(new Uri(endpoint), id));
        using var stream = new CallStreamService(settings, new RecordedCalls(), new AddressStub(),
            new ScriptSubscription(false, null), http, null, new FixedClock(Now), new(new Uri(endpoint), id));
        var pending = stream.RefreshRecordingAudioAsync(card, endpoint, Now, token.Token);
        await entered.Task.WaitAsync(token.Token);
        switch (change)
        {
            case "late":
            case "switch": Save(userB, password); break;
            case "roundtrip": Save(userB, password); Save(userA, password); break;
            case "password": Save(userA, Guid.NewGuid().ToString("N")); break;
            case "endpoint": policy.Update([profile with { Endpoint = endpoint + "/moved" }]); break;
            case "reconnect": policy.Update([profile with { Enabled = false }]); policy.Update([profile]); break;
            case "cancel": token.Cancel(); break;
            case "unrelated": settings.SetRecorderCredentials(Guid.NewGuid(), endpoint, userB, password); break;
        }
        release.TrySetResult();
        string? result = null;
        try { result = await pending; }
        catch (RecordingAccessException) { }
        catch (OperationCanceledException) { }
        if (change is "unrelated" or "current")
            Check(rows == 1 && result == endpoint + "/returned.wav", "A current account's independent lookup must succeed.");
        else
            Check(rows == (change == "late" ? 1 : 0) && !replacementLogin && result == null && source.Read() == before && card.AudioUrl == before.AudioUrl,
                $"Stale {change} lookup crossed its owner: rows={rows}, replacementLogin={replacementLogin}, stateChanged={source.Read() != before}.");
    }
}

