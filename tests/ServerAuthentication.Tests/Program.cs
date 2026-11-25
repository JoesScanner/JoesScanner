using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;

if (args is ["--wordpress-favorites", var fixture])
{
    await WordPressFavoritesIntegration.RunAsync(fixture);
    return;
}

var cases = new (string Name, Func<Task> Run)[]
{
    ("Transmission settings honor the active Live view, saved recorder-scoped talkgroups, and Favorites changes", MonitoringDeviceTests.ScopesAndFavorites),
    ("Notification grouping uses a fixed 60-second window and separates unrelated recorders and talkgroups", MonitoringDeviceTests.FixedWindowsAndIsolation),
    ("Durable admission suppresses recovered and duplicate alerts without changing playback or completion", MonitoringDeviceTests.DurableAdmissionAndRecovery),
    ("Only Red outages and setup failures alert, once per independent connection episode", MonitoringDeviceTests.RedFailureEpisodes),
    ("Notification permission and Local Only fence delivery without blocking independent recorders", MonitoringDeviceTests.PermissionAndPrivacy),
    ("Overnight display restores power and brightness on unplug, background, Stop, and disable", MonitoringDeviceTests.DisplayTransitions),
    ("Built-in and directory requests use only endpoint-issued credentials", HostedSelection),
    ("Missing session, inactive subscription, or empty issuance blocks requests", MissingCredentials),
    ("A denied POST refreshes and replays the same body once", RotatedCredentials),
    ("A second denial limits refreshes without blocking other endpoints", DeniedCredentials),
    ("API denial after rotation does not replay or fall back", DeniedRefresh),
    ("Custom Basic Auth stays on the configured origin", CustomCredentials),
    ("Redirects never carry scanner credentials", RedirectIsolation),
    ("Port changes, downgrades, and embedded credentials are blocked", OriginIsolation),
    ("A changed WordPress session does not use credentials from the old session", SessionIsolation),
    ("The WebSocket handshake refreshes firewall credentials", WebSocketRotation),
    ("Rapid selections cancel obsolete playback and late completion cannot replace the latest selection", RapidPlaybackSelections),
    ("A canceled firewall waiter sends no audio request and does not poison the next selection", CanceledFirewallWait),
    ("Canceled credential issuance releases the firewall gate for the next selection", CanceledFirewallIssuance),
    ("HTTP timeouts and unexpected cancellation remain playback failures", UnexpectedPlaybackTimeout),
    ("Only completion or deliberate skip advances playback; explicit retry retains the failed item", MobileRecoveryTests.PlaybackOutcomes),
    ("Foreground recovery verifies hosted tiers and preserves custom history", MobileRecoveryTests.Entitlement),
    ("Ordinary reconnect distinguishes WebSocket Live events from HTTP history", MobileRecoveryTests.ReconnectProvenance),
    ("Simultaneous recorder reconnections advance only a durably verified lagging recorder", MobileRecoveryTests.VerifyRecorderLagAcrossSimultaneousReconnections),
    ("Repeated transport failures preserve durable checkpoints and fence obsolete playback-lag decisions", MobileRecoveryTests.TransportLagNeedsADurableCurrentWindow),
    ("Disabling or removing a recorder fences its pending floor write without retiring another recorder", MobileRecoveryTests.RecorderRetirementFencesLag),
    ("Protected metadata catch-up refuses older calls after a downgrade", MobileRecoveryTests.HistoricalMetadataCatchup),
    ("Directory History search, pagination, single download, and ZIP enforce verified entitlement", MobileRecoveryTests.HistorySearchAndExport),
    ("History saves recorder filters and selection by permanent identity", MobileRecoveryTests.RecorderHistoryPreferences),
    ("History scopes authentication, filters, and recording IDs and uses native multi-talkgroup search", MobileRecoveryTests.ScopedHistoryAndNativeTalkgroupFilter),
    ("History loads a fixed 250 then 100 and keeps the result collection stable and deduplicated", MobileRecoveryTests.HistorySnapshotPaging),
    ("History detects recorder archive changes at the page boundary", MobileRecoveryTests.HistoryPageRejectsArchiveDrift),
    ("History rejects results returned after a search is canceled", MobileRecoveryTests.LateHistorySearchCancellation),
    ("Live-to-History handoff rejects a recovery response revoked during an HTTP request", MobileRecoveryTests.EntitlementDuringHandoff),
    ("Foreground recovery aborts a half-open socket, merges late audio, and delivers subsequent live calls once", MobileRecoveryTests.ReconnectAndLateAudio),
    ("Production WebSocket and HTTP recovery feed durable arrival order", MobileRecoveryTests.StreamArrivalOrder),
    ("Paged recovery gives first observations their position and preserves overlapping requests", MobileRecoveryTests.PagedRecoveryArrivalAndOverlap),
    ("Partial recovery retries without blocking Live or moving admitted recordings", MobileRecoveryTests.PagedRecoveryRetriesPartialFailure),
    ("HTTP-only fallback durably admits eligible rows during partial recovery", MobileRecoveryTests.HttpOnlyRecoveryRetriesPartialFailure),
    ("Authorized HTTP recovery can admit a provisional row from the baseline poll", MobileRecoveryTests.RecoveryAdmitsPreviouslyProvisionalObservation),
    ("A timed-out automatic Jump preserves the outstanding HTTP window across restart", MobileRecoveryTests.RecoverySurvivesTimedJumpAndRestart),
    ("Manual Jump skips late recovery results and preserves overlapping live socket arrivals", MobileRecoveryTests.ManualJumpFencesOutstandingSocketRecovery),
    ("Recovery progress requires a durable receipt and respects account scope and explicit Stop", MobileRecoveryTests.RecoveryReceiptsAndOwnership),
    ("Saved recorder identities survive address edits and reloads", MobileRecoveryTests.SavedRecorderIdentity),
    ("Saved server ordering, enablement, managed addresses and removal persist", MobileRecoveryTests.SavedConnectionEditing),
    ("Reordering retains both running connections and independent indicators", MobileRecoveryTests.ReorderKeepsTransport),
    ("Live views share canonical cards and choose responsive server panels", MobileRecoveryTests.LiveDisplayProjections),
    ("The 500-card Live window preserves the playing card and durable queue", MobileRecoveryTests.LiveDisplayRetention),
    ("Switching Combined and Separate leaves native playback running", MobileRecoveryTests.LiveDisplaySwitchDuringPlayback),
    ("Independent connections keep monitoring during another recorder failure, retry and disable", MobileRecoveryTests.IndependentConnections),
    ("Simultaneous scoped recorder streams keep duplicate backend IDs separate and share FIFO", MobileRecoveryTests.SimultaneousRecorderStreams),
    ("Recorder HTTP authorization remains bound to its own configured origin", MobileRecoveryTests.ScopedRecorderAuthorization),
    ("Recorder credential vault isolates server IDs and origins across restart", SavedCredentialTests.RecorderCredentialVault),
    ("A recorder reenabled during Live and a History return both skip old automatic audio", MobileRecoveryTests.CombinedRecorderBoundaries),
    ("Fresh WebSocket monitoring skips the previous process backlog and plays new events in arrival order", MobileRecoveryTests.FreshSocketListening),
    ("HTTP polling baselines old snapshots while inherited recovery stays durable", MobileRecoveryTests.FreshHttpListening),
    ("An empty initial HTTP snapshot establishes the Live polling baseline", MobileRecoveryTests.EmptyHttpListeningBaseline),
    ("An evicted recording receives a full socket update while completed identities stay retired", MobileRecoveryTests.EvictedRecordingReceivesFullSocketUpdate),
    ("Hosted socket updates honor the foreground cutoff, entitlement, and signed URL sanitization", MobileRecoveryTests.HostedSocketUpdateTargetsOnlyEligiblePendingRows),
    ("Explicit Stop cancels paginated recovery and keeps the committed backlog", MobileRecoveryTests.StopDuringPagedRecovery),
    ("Paginated recovery fences account changes and rechecks subscription downgrades", MobileRecoveryTests.PagedRecoveryRechecksAccountAndTier),
    ("Playback authorization rechecks historical acquisition, retry, expiry, and downgrade", MobileRecoveryTests.PlaybackEntitlement),
    ("SQLite-only identities can reacquire missing and expired audio using existing calljson lookups", MobileRecoveryTests.RecordingAudioRefresh),
    ("Older durable recordings recover via bounded timestamp paging with account isolation", MobileRecoveryTests.OldDurableSignedAudio),
    ("Missing custom audio remains pending through Stop and a temporary failure until Retry, Skip or Jump", MobileRecoveryTests.UnavailableDurableAudio),
    ("Concurrent WebSocket audio updates preserve the freshest usable URL", MobileRecoveryTests.ConcurrentSignedAudioUpdates),
    ("Legacy connection credentials migrate into verified protected storage and survive restart", SavedCredentialTests.LegacyAndRestart),
    ("Protected storage write and read failures retain legacy data and never use plaintext", SavedCredentialTests.VaultFailures),
    ("Interrupted database cleanup retries without replacing protected credentials", SavedCredentialTests.InterruptedCleanup),
    ("An interrupted legacy recorder switch leaves the unscoped login inactive", SavedCredentialTests.UnscopedInterruptedSwitch),
    ("Unscoped vault failures preserve legacy rows without blocking other connections or re-entry", SavedCredentialTests.UnscopedPreservationFailure),
    ("Unscoped cleanup rolls back without blocking hosted credential migration and retries on restart", SavedCredentialTests.UnscopedCleanupRollback),
    ("Custom servers, hosted accounts, and removals are isolated in protected storage", SavedCredentialTests.ScopesAndRemoval),
    ("Credential and signed URL diagnostics redact authentication material", SavedCredentialTests.Redaction),
    ("Basic and Detailed diagnostics buffer safely while log storage is unavailable", DiagnosticReliabilityTests.ModesAndUnavailableStorage),
    ("Diagnostics rotate and prune oldest logs at seven days and 25 MB", DiagnosticReliabilityTests.RetentionAndRotation),
    ("Legacy SQLite upgrades preserve recorder identity, profiles, AutoPlay and the WAL snapshot", DatabaseRecoveryTests.SupportedUpgrade),
    ("Interrupted schema migration rolls back all DDL and restarts from the original version", DatabaseRecoveryTests.InterruptedTransaction),
    ("Partial database damage preserves valid settings, protected credentials, and offline Favorites", DatabaseRecoveryTests.PartialSettingsAndFavorites),
    ("Unreadable SQLite is archived and starts safely with a nonblocking recovery notice", DatabaseRecoveryTests.UnreadableDatabase),
    ("Interrupted recovery resumes and temporary or future-schema errors preserve storage", DatabaseRecoveryTests.InterruptedReplacementAndUnavailableDisk),
    ("Diagnostic ZIP sanitizes historical files and snapshots without changing the originals", BackupAndDiagnosticTests.SanitizedZip),
    ("Android backup and transfer exclude sensitive storage while preserving other app backups", BackupAndDiagnosticTests.AndroidBackupRules),
    ("SQLite WAL and a failed protected migration preserve integrity and permit a normal restart", BackupAndDiagnosticTests.WalMigration),
    ("Overlapping validation, late success and failure, and logout preserve the current account", WordPressOwnershipTests.ValidationOrdering),
    ("Subscription verification cannot publish into a switched account", WordPressOwnershipTests.SubscriptionSwitch),
    ("Playback cache isolates recorder, endpoint and account identities with no readable credentials or signed URLs", AudioCacheEntitlementTests.IdentityAsync),
    ("Protected cache reauthorizes playback and respects expiration, offline grace, tier and Local Only", AudioCacheEntitlementTests.AuthorizationAsync),
    ("Subsequent verification after 429, 503, timeout, and connection failure preserves saved credentials", WordPressOwnershipTests.SubscriptionFailuresAndRestart),
    ("Live, History, and cache share verified grace at 23:59 and deny after 24 hours", WordPressOwnershipTests.GraceAcrossHostedFeatures),
    ("401, 403 and confirmed revocation erase grace, and first-time access stays denied", WordPressOwnershipTests.AuthoritativeDenialsEndGrace),
    ("Verified subscription and credential expiration independently shorten grace", WordPressOwnershipTests.EarliestVerifiedDeadline),
    ("Hosted monitoring revalidates at 30 minutes and concurrent features share one request", WordPressOwnershipTests.SharedVerificationCadence),
    ("Revocation cancels protected native audio and media preparation while independent playback continues", WordPressOwnershipTests.RevocationCancelsProtectedWork),
    ("Revocation cancels an in-flight protected export body and discards the incomplete file", WordPressOwnershipTests.RevocationCancelsExportBody),
    ("Hosted tier changes fence old History work while allowed Live and recent audio continue", WordPressOwnershipTests.HostedTierChangesPreserveLive),
    ("Concurrent lookup requests keep their original authority and reject stale results", WordPressOwnershipTests.LookupSwitch),
    ("Communications discards old-session responses and restarts the cursor for a new account", WordPressOwnershipTests.CommunicationsSwitch),
    ("WordPress clients refuse cross-authority redirects that preserve request bodies", WordPressOwnershipTests.Redirect),
    ("Local save and signed-out sharing make no requests and public submission receives Pending Approval", ServerDirectoryTests.Sharing),
    ("The HTTPS My Servers handoff validates its origin and respects Local Only", ServerDirectoryTests.Handoff),
    ("Verified directory suspension and endpoint changes preserve independent monitoring and recorder identity", ServerDirectoryTests.DirectoryConnections),
    ("Local Favorites persist, update the shared Live view and prioritize correctly across recorders", FavoritesTests.LocalMembership),
    ("A free account shares directory Favorites across devices and links custom recorders explicitly", FavoritesTests.MatchingAcrossDevices),
    ("Offline Favorites merge disjoint changes and retain conflicts and deletion tombstones", FavoritesTests.OfflineConflictsAndTombstones),
    ("An uncertain sync acknowledgement cannot lose a subsequent local edit", FavoritesTests.LostResponseAndNewerDraft),
    ("Favorites caches and pending changes stay with verified accounts and reject unauthorized requests", FavoritesTests.AccountIsolation),
    ("Local Only migrates a telemetry opt-out, blocks cloud requests, and cancels in-flight traffic", FavoritesTests.PrivacyBlocksAndCancels),
    ("Leaving Local Only syncs the same account's queued Favorites without data loss", FavoritesTests.PrivacyKeepsOfflineChanges),
    ("A transient gateway denial keeps the verified Favorites cache and recovers its outbox", FavoritesTests.TransientForbiddenKeepsCache)
};

foreach (var (name, run) in cases)
{
    await run();
    Console.WriteLine("PASS " + name);
}

return;

static async Task HostedSelection()
{
    using var fixture = new Fixture();
    using var client = fixture.CreateClient(request =>
    {
        var expected = request.RequestUri!.Host == "directory.test" ? fixture.Rotated : fixture.Issued;
        Check(request.Headers.Authorization?.Equals(expected) == true, "Wrong credential source.");
        return Reply(HttpStatusCode.OK);
    });

    using var edited = new HttpRequestMessage(HttpMethod.Head, HostedServerRules.DefaultServerUrl);
    edited.Options.Set(ServerAuthenticationHandler.CustomProbe, true);
    edited.Headers.Authorization = fixture.Manual;
    using var hosted = await client.SendAsync(edited);
    using var directory = await client.GetAsync("https://directory.test:8443/history");
    using var cached = await client.GetAsync(HostedServerRules.DefaultServerUrl + "/talkgroupsjson");
    Check(fixture.IssuanceCount == 2 && fixture.DirectoryId == 42 &&
          fixture.DirectoryOrigin == "https://directory.test:8443/scanner", "The approved full recorder URL must identify firewall issuance.");
}

static async Task MissingCredentials()
{
    using var fixture = new Fixture();
    using var client = fixture.CreateClient(_ => throw new Exception("Scanner request must be blocked."));
    fixture.Settings.Session = string.Empty;
    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "verified");
    fixture.Settings.Session = Guid.NewGuid().ToString();
    fixture.Settings.ActiveSubscription = false;
    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "active subscription");
    Check(fixture.IssuanceCount == 0, "Ineligible accounts contacted WordPress.");

    fixture.Settings.ActiveSubscription = true;
    fixture.Issue = _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { ok = true })
    };
    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "usable");
    Check(fixture.IssuanceCount == 1, "Expected one issuance attempt.");
}

static async Task RotatedCredentials()
{
    using var fixture = new Fixture();
    var sends = 0;
    fixture.Issue = count => fixture.IssuedReply(count == 1 ? fixture.Issued : fixture.Rotated);
    using var client = fixture.CreateClient(request =>
    {
        sends++;
        Check(request.Method == HttpMethod.Post &&
              request.Content!.ReadAsStringAsync().GetAwaiter().GetResult() == "draw=1", "POST replay changed its method or body.");
        Check(request.Headers.Authorization?.Equals(sends == 1 ? fixture.Issued : fixture.Rotated) == true,
            "The request did not use the expected issued credential.");
        return Reply(sends == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
    });

    using var result = await client.PostAsync(HostedServerRules.DefaultServerUrl + "/calljson", new StringContent("draw=1"));
    Check(result.IsSuccessStatusCode && sends == 2 && fixture.IssuanceCount == 2, "Expected one refresh and retry.");
}

static async Task DeniedCredentials()
{
    using var fixture = new Fixture();
    var sends = 0;
    using var client = fixture.CreateClient(request =>
    {
        Check(request.Headers.Authorization?.Equals(fixture.Issued) == true, "Unexpected credential after denial.");
        sends++;
        return Reply(request.RequestUri!.AbsolutePath == "/audio" ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
    });

    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "rejected");
    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "attempted recently");
    using var audio = await client.GetAsync(HostedServerRules.DefaultServerUrl + "/audio");
    Check(sends == 4 && fixture.IssuanceCount == 2 && audio.IsSuccessStatusCode, "Denial caused excess refresh or blocked another endpoint.");
}

static async Task DeniedRefresh()
{
    using var fixture = new Fixture();
    fixture.Issue = count => count == 1 ? fixture.IssuedReply(fixture.Issued) : Reply(HttpStatusCode.Unauthorized);
    var sends = 0;
    using var client = fixture.CreateClient(_ =>
    {
        sends++;
        return Reply(HttpStatusCode.Unauthorized);
    });

    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "could not issue");
    Check(sends == 1 && fixture.IssuanceCount == 2, "Request ran again after WordPress denied refresh.");
}

static async Task CustomCredentials()
{
    using var fixture = new Fixture();
    fixture.Settings.ServerUrl = "http://scanner.test:8080/root";
    using var client = fixture.CreateClient(request =>
    {
        var isCustom = request.RequestUri!.Scheme == "http" &&
            request.RequestUri.Host == "scanner.test" && request.RequestUri.Port == 8080;
        Check(isCustom
            ? request.Headers.Authorization?.Equals(fixture.Manual) == true
            : request.Headers.Authorization == null, "Custom credentials exceeded their origin.");
        return Reply(isCustom ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
    });
    using var result = await client.GetAsync("http://scanner.test:8080/calljson");
    using var external = await client.GetAsync("https://cdn.test/clip");
    using var otherPort = await client.GetAsync("http://scanner.test:8081/clip");
    Check(result.StatusCode == HttpStatusCode.Unauthorized && fixture.IssuanceCount == 0, "Custom denial used the WordPress API.");
}

static async Task RedirectIsolation()
{
    using var fixture = new Fixture();
    var sends = 0;
    using var client = fixture.CreateClient(request =>
    {
        sends++;
        if (sends == 1)
        {
            Check(request.Headers.Authorization?.Equals(fixture.Issued) == true, "Initial request lacks issued credentials.");
            return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.test/clip") } };
        }

        Check(request.RequestUri!.Host == "cdn.test" && request.Headers.Authorization == null,
            "Credentials crossed a redirect.");
        return Reply(HttpStatusCode.OK);
    });

    using var result = await client.GetAsync(HostedServerRules.DefaultServerUrl + "/clip");
    Check(result.IsSuccessStatusCode && sends == 2 && fixture.IssuanceCount == 1, "Unexpected redirect outcome.");
}

static async Task OriginIsolation()
{
    using var fixture = new Fixture();
    using var client = fixture.CreateClient(_ => throw new Exception("Invalid origin was contacted."));
    await FailsAsync(() => client.GetAsync("http://app.joesscanner.com/calljson"), "HTTPS origin");
    await FailsAsync(() => client.GetAsync("https://app.joesscanner.com:444/calljson"), "HTTPS origin");
    var embedded = new UriBuilder(HostedServerRules.DefaultServerUrl) { UserName = Guid.NewGuid().ToString() }.Uri;
    try
    {
        using var response = await client.GetAsync(embedded);
        throw new Exception("Expected embedded credentials to be blocked.");
    }
    catch (HttpRequestException ex) when (ex.Message.Contains("embedded credentials", StringComparison.Ordinal))
    {
    }

    Check(fixture.IssuanceCount == 0, "Invalid origin contacted WordPress.");
}

static async Task SessionIsolation()
{
    using var fixture = new Fixture();
    fixture.Issue = count => fixture.IssuedReply(count == 1 ? fixture.Issued : fixture.Rotated);
    using var client = fixture.CreateClient(request =>
    {
        var expected = fixture.IssuanceCount == 1 ? fixture.Issued : fixture.Rotated;
        Check(request.Headers.Authorization?.Equals(expected) == true, "Unexpected credential for session.");
        return Reply(HttpStatusCode.OK);
    });
    using var first = await client.GetAsync(HostedServerRules.DefaultServerUrl);
    fixture.Settings.Session = Guid.NewGuid().ToString();
    using var second = await client.GetAsync(HostedServerRules.DefaultServerUrl);
    Check(fixture.IssuanceCount == 2, "The old session cache survived a session change.");
}

static async Task WebSocketRotation()
{
    using var fixture = new Fixture();
    fixture.Issue = count => fixture.IssuedReply(count == 1 ? fixture.Issued : fixture.Rotated);
    var sends = 0;
    using var invoker = new HttpMessageInvoker(fixture.CreateHandler(request =>
    {
        sends++;
        Check(request.RequestUri!.AbsolutePath == "/Calls", "Wrong upgrade destination.");
        Check(request.Headers.Authorization?.Equals(sends == 1 ? fixture.Issued : fixture.Rotated) == true,
            "The upgrade request used the wrong firewall credential.");
        if (sends == 1)
            return Reply(HttpStatusCode.Unauthorized);

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            request.Headers.GetValues("Sec-WebSocket-Key").Single() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
        {
            Content = new UpgradeContent()
        };
        response.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        response.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        response.Headers.TryAddWithoutValidation("Sec-WebSocket-Accept", accept);
        return response;
    }));

    using var socket = new ClientWebSocket();
    await socket.ConnectAsync(new Uri("wss://app.joesscanner.com/Calls"), invoker, CancellationToken.None);
    Check(socket.State == WebSocketState.Open && sends == 2 && fixture.IssuanceCount == 2, "Upgrade retry failed.");
}

static async Task RapidPlaybackSelections()
{
    var requests = new LivePlaybackRequests();
    using var queue = requests.Begin(manual: false);
    var earlyDownload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var nextDownload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var first = requests.Begin(manual: true);
    var firstToken = first.Token;
    var firstWork = FinishSelectionAsync(first, earlyDownload.Task);
    using var second = requests.Begin(manual: true);
    var secondToken = second.Token;
    var secondWork = FinishSelectionAsync(second, nextDownload.Task);
    using var last = requests.Begin(manual: true);
    Check(queue.Token.IsCancellationRequested && firstToken.IsCancellationRequested && secondToken.IsCancellationRequested,
        "A tap must cancel the queue and previous selections immediately.");

    Check(await FinishSelectionAsync(last, Task.FromResult("latest")) == "latest",
        "The latest selection should be the only request eligible to start playback.");
    nextDownload.SetResult("obsolete");
    earlyDownload.SetResult("even older");
    Check(await firstWork == null && await secondWork == null, "Late downloads must not start audio.");
    second.Dispose();
    first.Dispose();
    Check(last.CanContinue && requests.HasManualSelection, "Cleanup for an old request must leave the new request active.");
    last.Dispose();
    Check(!requests.HasManualSelection, "Queue playback can resume after the current selection finishes.");

    static async Task<string?> FinishSelectionAsync(LivePlaybackRequests.Request request, Task<string> download)
    {
        var path = await download;
        return request.CanContinue ? path : null;
    }
}

static async Task CanceledFirewallWait()
{
    using var fixture = new Fixture();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var issuer = new HttpClient(new AsyncStubHandler(async (_, token) =>
    {
        started.SetResult();
        await release.Task.WaitAsync(token);
        return fixture.IssuedReply(fixture.Issued);
    }));
    var auth = new ServerAuthentication((ISettingsService)(object)fixture.Settings, issuer);
    var sent = new List<string>();
    using var audioClient = new HttpClient(new ServerAuthenticationHandler(auth, new StubHandler(request =>
    {
        lock (sent) sent.Add(request.RequestUri!.AbsolutePath);
        return Reply(HttpStatusCode.OK);
    })));
    var holdingCall = audioClient.GetAsync(HostedServerRules.DefaultServerUrl + "/holding.wav");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var requests = new LivePlaybackRequests();
    using var earlier = requests.Begin(manual: true);
    var obsoleteAudio = audioClient.GetAsync(HostedServerRules.DefaultServerUrl + "/obsolete.wav", earlier.Token);
    using var latest = requests.Begin(manual: true);
    await ExpectPlaybackCancellationAsync(obsoleteAudio, earlier.Token);
    var latestAudio = audioClient.GetAsync(HostedServerRules.DefaultServerUrl + "/latest.wav", latest.Token);
    release.SetResult();
    using var firstResponse = await holdingCall.WaitAsync(TimeSpan.FromSeconds(5));
    using var latestResponse = await latestAudio.WaitAsync(TimeSpan.FromSeconds(5));
    Check(sent.Count == 2 && sent.Contains("/holding.wav") && sent.Contains("/latest.wav"),
        "The canceled auth waiter must never reach the audio transport.");
}

static async Task CanceledFirewallIssuance()
{
    using var fixture = new Fixture();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var issued = 0;
    using var issuer = new HttpClient(new AsyncStubHandler(async (_, token) =>
    {
        if (Interlocked.Increment(ref issued) == 1)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        return fixture.IssuedReply(fixture.Issued);
    }));
    var auth = new ServerAuthentication((ISettingsService)(object)fixture.Settings, issuer);
    using var client = new HttpClient(new ServerAuthenticationHandler(auth, new StubHandler(_ => Reply(HttpStatusCode.OK))));
    using var obsolete = new CancellationTokenSource();
    var earlier = client.GetAsync(HostedServerRules.DefaultServerUrl + "/early.wav", obsolete.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    obsolete.Cancel();
    await ExpectPlaybackCancellationAsync(earlier, obsolete.Token);
    using var result = await client.GetAsync(HostedServerRules.DefaultServerUrl + "/next.wav").WaitAsync(TimeSpan.FromSeconds(5));
    Check(result.IsSuccessStatusCode && issued == 2, "Canceled issuance must release the gate without applying an auth failure cooldown.");
}

static async Task UnexpectedPlaybackTimeout()
{
    using var client = new HttpClient(new AsyncStubHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Reply(HttpStatusCode.OK);
    })) { Timeout = TimeSpan.FromMilliseconds(30) };
    using var selection = new CancellationTokenSource();
    try
    {
        using var response = await client.GetAsync("https://audio.test/call.wav", selection.Token);
        throw new Exception("A request timeout was expected.");
    }
    catch (TaskCanceledException ex)
    {
        Check(LivePlaybackRequests.IsTimeout(ex) && !LivePlaybackRequests.IsExpectedCancellation(ex, selection.Token),
            "An actual HttpClient timeout must remain a failure when the selection is active.");
        selection.Cancel();
        Check(!LivePlaybackRequests.IsExpectedCancellation(ex, selection.Token),
            "A subsequent selection cancellation must not mask an HTTP timeout.");
        using var unrelated = new CancellationTokenSource();
        Check(!LivePlaybackRequests.IsExpectedCancellation(new OperationCanceledException(unrelated.Token), selection.Token),
            "An exception tied to an uncanceled token must remain a failure.");
    }
    Check(!LivePlaybackRequests.IsExpectedCancellation(new OperationCanceledException(), CancellationToken.None),
        "An unexpected cancellation without a caller cancellation must remain a failure.");
}

static async Task ExpectPlaybackCancellationAsync(Task<HttpResponseMessage> work, CancellationToken token)
{
    try
    {
        using var response = await work.WaitAsync(TimeSpan.FromSeconds(5));
        throw new Exception("Obsolete playback should have canceled.");
    }
    catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token))
    {
        // Both OperationCanceledException and TaskCanceledException are expected here.
    }
}

static async Task FailsAsync(Func<Task<HttpResponseMessage>> send, string expectedMessage)
{
    try
    {
        using var response = await send();
        throw new Exception("Expected firewall authentication to fail.");
    }
    catch (FirewallAuthenticationException ex) when (ex.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
    }
}

static HttpResponseMessage Reply(HttpStatusCode status) => new(status);

static void Check(bool predicate, string failure)
{
    if (!predicate)
        throw new Exception(failure);
}

sealed class Fixture : IDisposable
{
    private readonly ServerAuthentication _authentication;
    private readonly HttpClient _issuer;
    internal SettingsStub Settings { get; }
    internal AuthenticationHeaderValue Issued { get; } = RandomBasic();
    internal AuthenticationHeaderValue Rotated { get; } = RandomBasic();
    internal AuthenticationHeaderValue Manual => Basic(Settings.Username, Settings.Password);
    internal int IssuanceCount { get; private set; }
    internal int? DirectoryId { get; private set; }
    internal string? DirectoryOrigin { get; private set; }
    internal Func<int, HttpResponseMessage>? Issue { get; set; }

    internal Fixture()
    {
        var settings = DispatchProxy.Create<ISettingsService, SettingsStub>();
        Settings = (SettingsStub)(object)settings;
        _issuer = new HttpClient(new StubHandler(request =>
        {
            if (request.Method != HttpMethod.Post ||
                request.RequestUri!.AbsoluteUri != "https://wordpress.test/wp-json/joes-scanner/v1/server-firewall-credentials" ||
                request.Headers.Authorization != null)
            {
                throw new Exception("Wrong WordPress request or authentication mechanism.");
            }

            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var body = payload.RootElement;
            if (body.GetProperty("session_token").GetString() != Settings.Session ||
                body.GetProperty("device_id").GetString() != Settings.DeviceId ||
                body.TryGetProperty("username", out _) || body.TryGetProperty("password", out _))
            {
                throw new Exception("Wrong WordPress firewall issuance payload.");
            }

            if (body.GetProperty("directory_id").ValueKind == JsonValueKind.Number)
            {
                DirectoryId = body.GetProperty("directory_id").GetInt32();
                DirectoryOrigin = body.GetProperty("server_url").GetString();
            }

            IssuanceCount++;
            return Issue?.Invoke(IssuanceCount) ?? IssuedReply(DirectoryId.HasValue ? Rotated : Issued);
        }));
        _authentication = new ServerAuthentication(settings, _issuer);
    }

    internal HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> send) => new(CreateHandler(send));

    internal HttpClient CreateClient(HttpMessageHandler transport) => new(new ServerAuthenticationHandler(_authentication, transport));

    internal HttpMessageHandler CreateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new ServerAuthenticationHandler(_authentication, new StubHandler(send));

    internal HttpResponseMessage IssuedReply(AuthenticationHeaderValue sample)
    {
        var pair = Encoding.UTF8.GetString(Convert.FromBase64String(sample.Parameter!)).Split(':', 2);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { ok = true, username = pair[0], password = pair[1] })
        };
    }

    public void Dispose() => _issuer.Dispose();

    private static AuthenticationHeaderValue RandomBasic() => Basic(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

    private static AuthenticationHeaderValue Basic(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
}

class SettingsStub : DispatchProxy
{
    internal string Session { get; set; } = Guid.NewGuid().ToString();
    internal string DeviceId { get; } = Guid.NewGuid().ToString();
    internal string Username { get; set; } = Guid.NewGuid().ToString();
    internal string Password { get; } = Guid.NewGuid().ToString();
    internal string ServerUrl { get; set; } = HostedServerRules.DefaultServerUrl;
    internal Dictionary<(Guid Id, string Endpoint), (string Username, string Password)> RecorderCredentials { get; } = [];
    internal bool ActiveSubscription { get; set; } = true;
    internal DateTime? SubscriptionExpiry { get; set; }
    internal int SubscriptionTier { get; set; }
    internal bool VerifiedSubscription { get; set; }
    internal DateTime? SubscriptionCheckedUtc { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_AuthServerBaseUrl" => "https://wordpress.test",
        "get_LocalOnlyMode" => false,
        "add_LocalOnlyModeChanged" or "remove_LocalOnlyModeChanged" => null,
        "add_AccountIdentityChanged" or "remove_AccountIdentityChanged" => null,
        "get_ServerUrl" => ServerUrl,
        "get_BasicAuthUsername" => Username,
        "get_BasicAuthPassword" => Password,
        "get_AuthSessionToken" => Session,
        nameof(ISettingsService.ReadAccountSession) => ("https://wordpress.test", Session, 0L, 0L),
        "get_DeviceInstallId" => DeviceId,
        "get_SubscriptionExpiresUtc" => SubscriptionExpiry,
        "get_SubscriptionTierLevel" => SubscriptionTier,
        "get_SubscriptionLastStatusOk" or "get_SubscriptionLastValidatedOnline" => VerifiedSubscription,
        nameof(ISettingsService.HasActiveHostedSubscription) => ActiveSubscription,
        nameof(ISettingsService.ReadHostedSubscription) => new HostedSubscriptionSnapshot(VerifiedSubscription,
            VerifiedSubscription, SubscriptionTier, SubscriptionCheckedUtc, SubscriptionExpiry, null, "", "", ""),
        nameof(ISettingsService.TryApplyAccountIdentity) => ApplyAccountIdentity(args!),
        nameof(ISettingsService.TryGetServerCredentials) => ReadAccount(args!),
        nameof(ISettingsService.TryGetRecorderCredentials) => ReadRecorder(args!),
        nameof(ISettingsService.GetCachedDirectoryServers) => new[]
        {
            new ServerDirectoryEntry
            {
                DirectoryId = 42,
                Url = "https://directory.test:8443/scanner",
                UsesApiFirewallCredentials = true
            }
        },
        _ => throw new NotSupportedException(method.Name)
    };

    private static bool ApplyAccountIdentity(object?[] args)
    {
        if ((long)args[0]! != 0)
            return false;
        ((Action)args[1]!).Invoke();
        return true;
    }

    private bool ReadRecorder(object?[] args)
    {
        var found = RecorderCredentials.TryGetValue(((Guid)args[0]!, (string)args[1]!), out var credentials);
        args[2] = credentials.Username ?? "";
        args[3] = credentials.Password ?? "";
        return found;
    }

    private bool ReadAccount(object?[] args)
    {
        args[1] = Username;
        args[2] = Password;
        return Equals(args[0], HostedServerRules.DefaultServerUrl) ||
            args[0] is string candidate && ServerKeyHelper.Normalize(candidate) == ServerKeyHelper.Normalize(ServerUrl);
    }
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(send(request));
}

sealed class AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}

sealed class UpgradeContent : HttpContent
{
    protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => new MemoryStream();

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new NotSupportedException();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
