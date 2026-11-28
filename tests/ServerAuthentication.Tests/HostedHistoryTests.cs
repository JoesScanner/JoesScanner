using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;

internal static partial class MobileRecoveryTests
{
    internal static async Task ReconnectProvenance()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = new ScriptSocket();
        using var replacement = new ScriptSocket();
        var settings = Settings(HostedServerRules.DefaultServerUrl);
        var subscription = new ScriptSubscription(true, 2, settings);
        var old = Now.AddHours(-25);
        first.Send(Row("initial-replay", old, "old.mp3"));
        first.Send(Row("actual-live", Now, "live.mp3"));
        first.SendClose();

        var opens = 0;
        var polls = 0;
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            Check(opens >= 2 && subscription.Checks > 0,
                "Historical fallback must follow verified authorization.");
            var filter = await DateFilter(request, cancellation);
            if (++polls == 1)
            {
                Check(filter.Length == 0, "Verified Premium should retain full History.");
                return Reply(Row("fallback-replay", old.AddMinutes(1), null));
            }
            Check(filter == RecordingPlaybackAccess.DateSearch(Now.AddHours(-24), Now),
                "A locally observed downgrade must invalidate the polling grant.");
            return Reply(Row("fallback-replay", old.AddMinutes(1), "denied.mp3"));
        }));

        var repository = new RecordedCalls();
        var stream = new CallStreamService(settings, repository, new AddressStub(), subscription, http,
            (_, _, _) => Task.FromResult<WebSocket?>(++opens switch { 1 => first, 2 => null, _ => replacement }),
            new FixedClock(Now));
        await using var reader = stream.GetCallStreamAsync([], token.Token).GetAsyncEnumerator(token.Token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Missing handshake.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "initial-replay", RecordingAccess.Origin: RecordingOrigin.History },
            "A historical row delivered during the initial handshake is not a Live event.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "actual-live", RecordingAccess.Origin: RecordingOrigin.Live },
            "The current WebSocket observation lost its Live provenance.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "fallback-replay", RecordingAccess.Origin: RecordingOrigin.History },
            "HTTP fallback after an ordinary disconnect must import historical rows as History.");
        var imported = reader.Current;

        subscription.Tier = 1;
        ((SettingsStub)(object)settings).SubscriptionTier = 1;
        await Denied(() => RecordingPlaybackAccess.VerifyAsync(settings, subscription, settings.ServerUrl,
            imported.RecordingTimeUtc, imported.RecordingAccess!, currentLive: true, requireAccount: false,
            token.Token, new FixedClock(Now)));

        replacement.Send(Row("fallback-replay", Now, "reused.mp3"));
        replacement.Send(Row("next-live", Now, "next.mp3"));
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "next-live", RecordingAccess.Origin: RecordingOrigin.Live } &&
            polls > 1 && imported.LiveState!.Read().AudioUrl == null && repository.Ids.Count(id => id == "fallback-replay") == 1,
            "An unrelated timestamp cannot update the historical recording or stop valid Live delivery.");
        subscription.Tier = 2;
        ((SettingsStub)(object)settings).SubscriptionTier = 2;
        replacement.Send(Row("fallback-replay", old.AddMinutes(1), "late.mp3"));
        Check(await reader.MoveNextAsync() && reader.Current.IsLiveUpdate &&
              ReferenceEquals(reader.Current.LiveState, imported.LiveState) &&
              imported.RecordingAccess?.Origin == RecordingOrigin.History &&
              imported.LiveState!.Read().AudioUrl == settings.ServerUrl.TrimEnd('/') + "/late.mp3",
            "A later WebSocket update must preserve the identity and historical provenance of the fallback row.");
        Check(repository.Ids.Count(id => id == "fallback-replay") == 2,
            "Only the Premium import and an entitled update for the same recording may reach the cache.");
    }

    internal static async Task HistoricalMetadataCatchup()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var socket = new ScriptSocket();
        var settings = Settings("https://directory.test:8443/scanner");
        var subscription = new ScriptSubscription(true, 2, settings);
        var old = Now.AddHours(-25);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var http = new HttpClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            Check(request.RequestUri == new Uri(settings.ServerUrl + "/calljson") &&
                  await DateFilter(request, cancellation) == RecordingPlaybackAccess.DateSearch(Now.AddHours(-24), Now),
                "Directory catch-up did not send the verified Standard range.");
            Interlocked.Increment(ref count);
            requested.TrySetResult();
            return Reply(Row("old-incomplete", old, "late.mp3"), Row("untracked", old, "other.mp3"));
        }));
        socket.Send(Row("old-incomplete", old, null));
        var stream = new CallStreamService(settings, new RecordedCalls(), new AddressStub(), subscription, http,
            (_, _, _) => Task.FromResult<WebSocket?>(socket), new FixedClock(Now));
        await using var reader = stream.GetCallStreamAsync([], token.Token).GetAsyncEnumerator(token.Token);
        Check(await reader.MoveNextAsync(), "Missing handshake.");
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "old-incomplete", RecordingAccess.Protected: true },
            "A verified directory history recording was not admitted.");
        var item = reader.Current;
        subscription.Tier = 1;
        await stream.RefreshCallMetadataAsync([item], token.Token);
        Check(item.LiveState!.Read().AudioUrl == null && count == 1,
            "Foreground metadata refresh merged out-of-window audio.");
        requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Send(new { DT_RowId = "old-incomplete", Update = true });
        var next = reader.MoveNextAsync().AsTask();
        await requested.Task.WaitAsync(token.Token);
        socket.Send(Row("sentinel", Now, "sentinel.mp3"));
        Check(await next && reader.Current.BackendId == "sentinel" && count == 2 &&
              item.LiveState.Read().AudioUrl == null && item.RecordingAccess?.Origin == RecordingOrigin.History,
            "A WebSocket catch-up applied unauthorized audio or upgraded provenance.");
    }

    internal static async Task HistorySearchAndExport()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fixture = new Fixture();
        var stub = fixture.Settings;
        stub.ServerUrl = "https://directory.test:8443/scanner";
        var settings = (ISettingsService)stub;
        var subscription = new ScriptSubscription(true, 2, settings);
        var clock = new FixedClock(Now);
        var recent = Row("recent", Now.AddHours(-1), "recent.mp3");
        var older = Row("older", Now.AddHours(-25), "older.mp3");
        var metadataRequests = 0;
        var media = new List<string>();
        using var http = fixture.CreateClient(new AsyncStubHandler(async (request, cancellation) =>
        {
            if (request.RequestUri!.Host == "directory.test")
                Check(fixture.Rotated.Equals(request.Headers.Authorization),
                    "An authorized directory request bypassed the API-issued firewall credentials.");
            if (request.Method == HttpMethod.Get)
            {
                media.Add(Path.GetFileName(request.RequestUri!.AbsolutePath));
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }
            metadataRequests++;
            var date = await DateFilter(request, cancellation);
            if (subscription.Tier == 1 && request.RequestUri!.Host != "custom.example")
                Check(date == $"{Now.AddHours(-23).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}|{Now.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}",
                    "Standard directory History must carry the Date constraint on every request.");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            var offset = document.RootElement.GetProperty("start").GetInt32();
            var size = document.RootElement.GetProperty("length").GetInt32();
            var data = new[] { recent, older }.Skip(offset).Take(size).ToArray();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { recordsFiltered = 2, data }) };
        }));
        var history = new CallHistoryService(settings, new RecordedCalls(), new NoHistoryLookups(), subscription, http, clock);
        var downloads = new CallDownloadService(settings, subscription, http, clock);
        var recorder = new HistoryRecorder(Guid.NewGuid(), stub.ServerUrl);
        var day = DateOnly.FromDateTime(Now.LocalDateTime);
        var dates = new HistoryDateRange(day.AddDays(-3), day, TimeSpan.Zero);
        var filters = new HistorySearchFilters { Site = new HistoryLookupItem { Value = "site", RecorderId = recorder.Id } };
        // A future local clock time refers to the same time yesterday for Standard.
        var standardStart = Now.AddHours(1).LocalDateTime.TimeOfDay;
        var standardDates = dates with { StartTime = standardStart };
        var premium = await history.SearchAsync(recorder, filters, dates, token.Token);
        Check(premium.Calls.Count == 2 && premium.Calls.All(call => call.RecordingAccess?.Protected == true),
            "Verified Premium directory History must admit both rows with protected provenance.");
        var oldCall = premium.Calls.Single(call => call.BackendId == "older");
        var recentCall = premium.Calls.Single(call => call.BackendId == "recent");
        subscription.Tier = 1;
        var standard = await history.SearchAsync(recorder, filters, standardDates, token.Token);
        var nextPage = await history.LoadMoreAsync(standard, token.Token);
        Check(standard.Calls.Select(call => call.BackendId).SequenceEqual(["recent"]) &&
              !standard.HasMore && nextPage.Calls.Count == 0,
            "History search and subsequent pages must refuse old rows even if the endpoint returns them.");

        var directory = Directory.CreateTempSubdirectory("JoesScanner.HistoryExport-");
        try
        {
            FileSystem.CacheDirectory = directory.FullName;
            await downloads.DownloadSingleAsync(oldCall, token.Token);
            Check(media.Count == 0 && UiDialogs.Alerts[^1].Contains("24-hour"),
                "Single export acquired a stale Premium result after downgrade.");
            await downloads.DownloadSingleAsync(recentCall, token.Token);
            await downloads.DownloadRangeZipAsync([oldCall, recentCall], token.Token);
            Check(media.SequenceEqual(["recent.mp3", "recent.mp3"]) && Share.Default.Shared.Count == 2,
                "A mixed ZIP must acquire and export only the authorized recording.");
            using (var zip = ZipFile.OpenRead(Share.Default.Shared[^1]))
                Check(zip.Entries.Count == 1 && zip.Entries[0].Name.StartsWith("002_"), "ZIP contained an unauthorized entry.");

            var before = metadataRequests;
            stub.SubscriptionExpiry = Now.UtcDateTime;
            await RejectHistory(() => history.SearchAsync(recorder, filters, standardDates, token.Token));
            await downloads.DownloadSingleAsync(recentCall, token.Token);
            stub.SubscriptionExpiry = null;
            subscription.Tier = null; // cached/offline success without a verified tier
            await RejectHistory(() => history.SearchAsync(recorder, filters, standardDates, token.Token));
            await downloads.DownloadRangeZipAsync([recentCall], token.Token);
            subscription.Tier = 0;
            await RejectHistory(() => history.SearchAsync(recorder, filters, standardDates, token.Token));
            Check(metadataRequests == before && media.Count == 2 && Share.Default.Shared.Count == 2,
                "Expired, offline, and Level 0 accounts must be denied before a protected request.");

            stub.ServerUrl = "https://custom.example";
            before = subscription.Checks;
            var customRecorder = new HistoryRecorder(Guid.NewGuid(), stub.ServerUrl);
            var customFilters = new HistorySearchFilters { Site = filters.Site! with { RecorderId = customRecorder.Id } };
            var custom = await history.SearchAsync(customRecorder, customFilters, dates, token.Token);
            Check(custom.Calls.Count == 2 && custom.Calls.All(call => call.RecordingAccess?.Protected == false),
                "Custom-server history was restricted.");
            await downloads.DownloadSingleAsync(custom.Calls.Single(call => call.BackendId == "older"), token.Token);
            Check(subscription.Checks == before && media[^1] == "older.mp3",
                "A genuine custom export must preserve its existing permissions.");
        }
        finally
        {
            directory.Delete(recursive: true);
            Share.Default.Shared.Clear();
            UiDialogs.Alerts.Clear();
        }
    }

    internal static async Task EntitlementDuringHandoff()
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var settings = Settings(HostedServerRules.DefaultServerUrl);
        var subscription = new ScriptSubscription(true, 2, settings);
        var ownership = new PlaybackOwnership();
        var oldLive = ownership.Live!;
        using var socket = new ScriptSocket();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncStubHandler(async (_, cancellation) =>
        {
            requested.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return Reply(Row("now-restricted", Now.AddHours(-25), "old.mp3"));
        }));
        var repository = new RecordedCalls();
        var stream = new CallStreamService(settings, repository, new AddressStub(), subscription, http,
            (_, _, _) => Task.FromResult<WebSocket?>(socket), new FixedClock(Now));
        var recovery = new LiveStreamRecovery();
        recovery.Request(Now.AddDays(-2));
        await using var reader = stream.GetCallStreamAsync([], token.Token, recovery).GetAsyncEnumerator(token.Token);
        Check(await reader.MoveNextAsync(), "Missing handshake.");
        var recovering = reader.MoveNextAsync().AsTask();
        await requested.Task.WaitAsync(token.Token);
        var history = ownership.EnterHistory();
        await ownership.RunNativeAsync(history, token.Token, _ => Task.CompletedTask);
        subscription.Tier = 1;
        await subscription.EnsureSubscriptionAsync(token.Token);
        socket.Send(Row("after-handoff", Now, "live.mp3"));
        release.TrySetResult();
        Check(await recovering && reader.Current.BackendId == "after-handoff" &&
              repository.Ids.SequenceEqual(["after-handoff"]) && oldLive.Token.IsCancellationRequested &&
              ownership.IsCurrent(history),
            "A now-unauthorized recovery row was committed during the History handoff.");
        ownership.LeaveHistory(history);
        Check(ownership.Live is { } next && !ReferenceEquals(oldLive, next), "Live must resume with a fresh ownership lease.");
    }

    private static async Task<string> DateFilter(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("columns")[2].GetProperty("search").GetProperty("value").GetString()!;
    }

    private static async Task RejectHistory(Func<Task> execute)
    {
        try { await execute(); }
        catch (RecordingAccessException) { return; }
        throw new InvalidOperationException("Expected hosted History to reject the request.");
    }

    private sealed class NoHistoryLookups : IHistoryLookupsRepository
    {
        public Task<(HistoryLookupData?, DateTime?)> GetAsync(string serverKey, CancellationToken token = default) =>
            Task.FromResult<(HistoryLookupData?, DateTime?)>((null, null));

        public Task UpsertAsync(string serverKey, HistoryLookupData data, DateTime fetchedUtc, CancellationToken token = default, Func<Action, bool>? publish = null) =>
            Task.CompletedTask;
    }
}
