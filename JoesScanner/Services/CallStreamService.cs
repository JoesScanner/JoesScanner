using JoesScanner.Models;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JoesScanner.Data;
using JoesScanner.Services;
using JoesScanner.Helpers;

namespace JoesScanner.Services
{
    // Polls the Trunking Recorder calljson endpoint and streams CallItem objects
    // to the UI, using server fields for time, receiver, site, talkgroup, transcription, and audio.
    public class CallStreamService : ICallStreamService
    {
        private const string AppleIosTestAccountEmail = "iostest@joesscanner.com";
        private const string AppleIosTestAccountEmailLegacy = "iostest@jeosscanner.com";

        private readonly ISettingsService _settingsService;
        private readonly ILocalCallsRepository _localCallsRepository;
        private readonly IAddressDetectionService _addressDetectionService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly HttpClient _httpClient;
        private readonly HttpMessageInvoker _webSocketInvoker;
        private readonly Func<string, bool, CancellationToken, Task<WebSocket?>> _openWebSocket;
        private readonly TimeProvider _clock;
        private readonly Uri? _recorder;

        private string SelectedEndpoint => (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/');
        private string Endpoint => _recorder?.AbsoluteUri.TrimEnd('/') ?? SelectedEndpoint;

        private bool MatchesEndpoint(string server) =>
            _recorder != null ? RecordingStorageReference.ServerKey(server) == Endpoint :
                RecordingPlaybackAccess.IsSelected(_settingsService, server);

        public ICallStreamService ForRecorder(string endpoint, Guid? serverId = null) =>
            new CallStreamService(_settingsService, _localCallsRepository, _addressDetectionService, _subscriptionService,
                null, null, _clock, new(new Uri(endpoint, UriKind.Absolute), serverId));

        public void Dispose()
        {
            _webSocketInvoker.Dispose();
            _httpClient.Dispose();
        }
        private readonly JsonSerializerOptions _jsonOptions;

        // Mirrors DEFAULT_CONFIG["poll_batch"] from the server.
        private const int PollBatchSize = 25;
        private static readonly TimeSpan RecoveryRetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan LiveOnlyPollWindow = TimeSpan.FromSeconds(30);

        // DataTables draw counter used by the calljson endpoint.
        private int _draw = 1;

        // Creates a new call stream service with an HttpClient configured for the calljson endpoint.
        public CallStreamService(ISettingsService settingsService, ILocalCallsRepository localCallsRepository,
            IAddressDetectionService addressDetectionService, ISubscriptionService subscriptionService)
            : this(settingsService, localCallsRepository, addressDetectionService, subscriptionService, null, null, TimeProvider.System)
        {
        }

        internal CallStreamService(ISettingsService settingsService, ILocalCallsRepository localCallsRepository,
            IAddressDetectionService addressDetectionService, ISubscriptionService subscriptionService, HttpClient? httpClient,
            Func<string, bool, CancellationToken, Task<WebSocket?>>? openWebSocket, TimeProvider clock, RecorderRequestScope? recorder = null)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _localCallsRepository = localCallsRepository ?? throw new ArgumentNullException(nameof(localCallsRepository));
            _addressDetectionService = addressDetectionService ?? throw new ArgumentNullException(nameof(addressDetectionService));
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
            _openWebSocket = openWebSocket ?? TryOpenWebSocketAsync;
            _clock = clock;
            _recorder = recorder?.Endpoint;
            _webSocketInvoker = new HttpMessageInvoker(new ServerAuthenticationHandler(
                ServerAuthentication.For(_settingsService),
                new SocketsHttpHandler { AllowAutoRedirect = false }, recorder));

            // IMPORTANT (Apple platforms + HTTP custom servers)
            // iOS and MacCatalyst can still surface NSURLErrorDomain -1022 (ATS) for cleartext HTTP
            // even when Info.plist contains NSAllowsArbitraryLoads, depending on handler/runtime.
            // For custom (non-HTTPS) Trunking Recorder endpoints, we want a stable long-term path.
            // Using SocketsHttpHandler forces a managed stack that bypasses NSURLSession/ATS.
#if IOS || MACCATALYST
            var socketsHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = httpClient ?? ServerAuthenticationHandler.CreateClient(_settingsService, socketsHandler, recorder);
#else
            _httpClient = httpClient ?? ServerAuthenticationHandler.CreateClient(_settingsService, recorder: recorder);
#endif
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            _httpClient.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            _httpClient.DefaultRequestHeaders.Add("DNT", "1");
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");

            _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            };
        }


        // Calls calljson using standard DataTables form encoding.
        // Kept as a fallback for servers that ignore JSON bodies for paging.
        private async Task<List<CallRow>> FetchLatestFormAsync(
            string callsUrl,
            CancellationToken cancellationToken,
            int start,
            int length,
            string? dateSearch = null, bool requireRows = false) =>
            (await FetchFormPageAsync(callsUrl, cancellationToken, start, length, dateSearch, requireRows: requireRows)).Rows;

        private async Task<RecordingPage> FetchFormPageAsync(
            string callsUrl, CancellationToken cancellationToken, int start, int length, string? dateSearch,
            Uri? referrer = null, bool requireRows = false)
        {
            var payload = BuildDataTablesPayload(Interlocked.Increment(ref _draw), start, length, dateSearch);

            var pairs = new List<KeyValuePair<string, string>>
            {
                new("draw", payload.Draw.ToString(CultureInfo.InvariantCulture)),
                new("start", payload.Start.ToString(CultureInfo.InvariantCulture)),
                new("length", payload.Length.ToString(CultureInfo.InvariantCulture)),
                new("search[value]", payload.Search?.Value ?? string.Empty),
                new("search[regex]", payload.Search?.Regex == true ? "true" : "false"),
                new("smartSort", payload.SmartSort ? "true" : "false"),
            };

            for (var i = 0; i < payload.Columns.Count; i++)
            {
                var c = payload.Columns[i];

                pairs.Add(new($"columns[{i}][data]", c.Data?.ToString() ?? string.Empty));
                pairs.Add(new($"columns[{i}][name]", c.Name ?? string.Empty));
                pairs.Add(new($"columns[{i}][searchable]", c.Searchable ? "true" : "false"));
                pairs.Add(new($"columns[{i}][orderable]", c.Orderable ? "true" : "false"));
                pairs.Add(new($"columns[{i}][search][value]", c.Search?.Value ?? string.Empty));
                pairs.Add(new($"columns[{i}][search][regex]", c.Search?.Regex == true ? "true" : "false"));
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, callsUrl) { Content = new FormUrlEncodedContent(pairs) };
            request.Headers.Referrer = referrer;
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                var statusInt = (int)statusCode;

                if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
                {
                    throw new CallStreamAuthException(
                        $"Authentication failed when calling {callsUrl} (HTTP {statusInt} {response.ReasonPhrase}).");
                }

                throw new HttpRequestException(
                    $"Server returned HTTP {statusInt} {response.ReasonPhrase} when calling {callsUrl}.", null, statusCode);
            }

            return await ReadRecordingPageAsync(response.Content, length, cancellationToken, requireRows);
        }

        // Continuously polls the server and yields new calls as CallItem objects.
        // Skips initial backlog so only new calls after connection are surfaced.
        public IAsyncEnumerable<CallItem> GetCallStreamAsync(
            IReadOnlyList<CallItem> existingCalls, CancellationToken cancellationToken, LiveStreamRecovery? recovery = null)
        {
            var serverUrl = Endpoint;
            var liveCalls = new LiveCallIndex();
            liveCalls.Seed(existingCalls, serverUrl);
            return StreamCallsAsync(serverUrl, liveCalls, recovery, cancellationToken);
        }

        private async IAsyncEnumerable<CallItem> StreamCallsAsync(string currentServerUrl, LiveCallIndex liveCalls,
            LiveStreamRecovery? recovery,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var recoveryServer = currentServerUrl;
            var protectedRecovery = recovery != null && HostedServerRules.RequiresApiFirewallCredentials(_settingsService, recoveryServer);
            var recoveryAccount = protectedRecovery ? RecordingPlaybackAccess.AccountKey(_settingsService, recoveryServer, _recorder != null) : null;
            bool InRecoveryScope() => recovery == null ||
                MatchesEndpoint(recoveryServer) &&
                HostedServerRules.RequiresApiFirewallCredentials(_settingsService, recoveryServer) == protectedRecovery &&
                (!protectedRecovery || recoveryAccount == RecordingPlaybackAccess.AccountKey(_settingsService, recoveryServer, _recorder != null));

            var isAppleTestAccount = IsAppleIosTestAccount();
            AppLog.Add(() => $"AppleTest: enabled={isAppleTestAccount}");

            // Only existing cards can receive updates from the first polling batch.
            var skipInitialBatch = true;

            // Apple review test account behavior.
            // For this one account only, backfill several hours of calls so playback can
            // start immediately even if there are no brand new calls arriving.
            if (isAppleTestAccount && recovery?.ListeningSince == null)
            {
                skipInitialBatch = false;
                if (recovery?.OutstandingWindow == null)
                {
                    await foreach (var backfilled in BackfillRecentHoursAsync(currentServerUrl, liveCalls, cancellationToken))
                        yield return backfilled;
                }
            }

            // Tracks whether we have already reported a "connected but idle" heartbeat
            // for this connection lifetime.
            var hasReportedIdleConnection = false;
            var consecutiveFailures = 0;

            // When WebSocket is unavailable, fall back to polling and retry WS after a short cooldown.
            var wsDisableUntilUtc = DateTime.MinValue;
            DateTimeOffset? foregroundPollFloor = null;
            var livePollSince = recovery?.ListeningSince ?? _clock.GetUtcNow();
            RecordingPlaybackAccess.HistoryGrant? pollGrant = null;

            while (!cancellationToken.IsCancellationRequested && InRecoveryScope())
            {
                var baseUrl = Endpoint;
                if (!string.Equals(currentServerUrl, baseUrl, StringComparison.Ordinal))
                {
                    liveCalls = new LiveCallIndex();
                    skipInitialBatch = true;
                    wsDisableUntilUtc = DateTime.MinValue;
                    foregroundPollFloor = null;
                    livePollSince = _clock.GetUtcNow();
                    pollGrant = null;
                }
                currentServerUrl = baseUrl;

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    AppLog.Add(() => "CallStream: No Server URL configured in settings. Username is not applicable.");

                    // No server configured yet.
                    yield return new CallItem
                    {
                        Timestamp = DateTime.Now,
                        Talkgroup = "INFO",
                        Transcription = "Configure Server URL in Settings (for example http://127.0.0.1:80)",
                        AudioUrl = string.Empty
                    };

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }

                var callsUrl = baseUrl + "/calljson";
                var (window, newRecoveryRequest) = recovery?.BeginAttempt() ?? default;
                var recoveryRequestedAt = newRecoveryRequest ? _clock.GetUtcNow() : default;
                if (newRecoveryRequest)
                    wsDisableUntilUtc = DateTime.MinValue;

                // On iOS/MacCatalyst, cleartext (ws://) WebSockets can still be blocked even when HTTP polling is allowed.
                // For HTTP custom servers, we intentionally skip WebSockets and rely on calljson polling instead.
                var allowWebSocket = true;
                if ((OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()) &&
                    baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    allowWebSocket = false;

                    // Avoid spamming this message continuously.
                    if (wsDisableUntilUtc == DateTime.MinValue)
                    {
                        AppLog.Add(() => "CallStream: INFO - HTTP server detected on Apple platform. WebSocket will be disabled; using polling (calljson).");
                    }

                    wsDisableUntilUtc = DateTime.UtcNow.AddYears(10);
                }

                // Prefer the Trunking Recorder WebSocket feed when available.
                // This reduces polling load and gives us call-level update notifications.
                var socketOpenedAfter = _clock.GetUtcNow();
                using var socket = allowWebSocket && DateTime.UtcNow >= wsDisableUntilUtc
                    ? await OpenSocketForLiveAsync(baseUrl, recovery != null, cancellationToken)
                    : null;

                if (window != null)
                {
                    // Establish the replacement live connection before checking the
                    // account or fetching history. The server can already send calls.
                    pollGrant = await GetHistoryGrantAsync(baseUrl, cancellationToken);
                    if (!InRecoveryScope())
                        yield break;
                    if (newRecoveryRequest && recovery?.IsInherited(window) != true)
                    {
                        foregroundPollFloor = pollGrant != null ? window.Since : recoveryRequestedAt;
                        livePollSince = recoveryRequestedAt;
                    }
                }

                if (socket != null)
                {
                    if (window != null && pollGrant == null)
                        recovery?.AllowPlaybackWithoutHistory(window);
                    consecutiveFailures = 0;
                    // We are live; do not skip later polling batches as "initial backlog."
                    skipInitialBatch = false;
                    if (!hasReportedIdleConnection)
                    {
                        hasReportedIdleConnection = true;
                        AppLog.Add(() => $"CallStream: WEBSOCKET CONNECTED. [ServerUrl={baseUrl}, Path=/Calls]");
                        yield return new CallItem
                        {
                            Timestamp = DateTime.Now,
                            Talkgroup = "HEARTBEAT",
                            Transcription = "Connected (WebSocket); waiting for calls.",
                            AudioUrl = string.Empty
                        };
                    }

                    await foreach (var item in StreamConnectionAsync(socket, baseUrl, callsUrl,
                        liveCalls, recovery, window, foregroundPollFloor, socketOpenedAfter, pollGrant, InRecoveryScope, cancellationToken))
                        yield return item;

                    if (!InRecoveryScope())
                        yield break;
                    livePollSince = _clock.GetUtcNow();
                    if (recovery?.Pending.IsCompleted != true)
                    {
                        if (recovery != null)
                            wsDisableUntilUtc = DateTime.UtcNow.AddSeconds(5); // Poll instead of trusting a failing socket.
                        else
                            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    }
                    continue;
                }

                // Avoid hammering reconnect attempts if WS is not exposed.
                if (allowWebSocket && DateTime.UtcNow >= wsDisableUntilUtc)
                    wsDisableUntilUtc = DateTime.UtcNow.AddSeconds(5);

                if (window != null && pollGrant != null &&
                    (recovery?.NeedsPollingBaseline != true || !recovery.IsInherited(window)))
                {
                    await foreach (var call in StreamConnectionAsync(null, baseUrl, callsUrl,
                        liveCalls, recovery, window, foregroundPollFloor, socketOpenedAfter, pollGrant, InRecoveryScope, cancellationToken))
                        yield return call;
                    if (!InRecoveryScope())
                        yield break;
                    if (recovery?.Pending.IsCompleted == true)
                        continue;
                }

                List<CallRow>? rows = null;
                string? errorMessage = null;
                var isAuthError = false;
                var setupRequired = false;
                var pollVersion = LiveCallState.NextVersion();

                try
                {
                    pollGrant = await GetHistoryGrantAsync(baseUrl, cancellationToken, pollGrant);
                    rows = await PollCallsAsync(baseUrl, callsUrl, pollGrant, livePollSince, cancellationToken,
                        requireRows: recovery?.NeedsPollingBaseline == true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller requested cancellation (disconnect, shutdown, etc.).
                    AppLog.DebugWriteLine("[CallStreamService] Streaming cancelled by caller.");
                    yield break;
                }
                catch (OperationCanceledException ex)
                {
                    // Most likely an HttpClient timeout or similar; treat as recoverable.
                    errorMessage = "The connection to the audio server timed out. The app will retry automatically.";
                    AppLog.DebugWriteLine(() => $"[CallStreamService] [Timeout] {ex}");
                    AppLog.Add(() => $"CallStream: poll timed out. ex={ex.GetType().Name}: {ex.Message}");
                }
                catch (FirewallAuthenticationException ex)
                {
                    isAuthError = true;
                    setupRequired = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                    errorMessage = ex.Message;
                }
                catch (CallStreamAuthException ex)
                {
                    isAuthError = true;
                    setupRequired = true;
                    errorMessage = ex.Message;
                    AppLog.DebugWriteLine(() => $"[CallStreamService] [AuthError] {ex}");
                    AppLog.Add(() => $"CallStream: auth error. {ex.Message}");
                }
                catch (RecordingAccessException ex)
                {
                    isAuthError = true;
                    errorMessage = ex.Message;
                }
                catch (HttpRequestException ex)
                {
                    // Retry once on transient transport failures (for example: "Socket closed").
                    AppLog.DebugWriteLine(() => $"[CallStreamService] [HttpError] {ex}");
                    AppLog.Add(() => $"CallStream: http error. ex={ex.GetType().Name}: {ex.Message}");

                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                        pollVersion = LiveCallState.NextVersion();
                        rows = await PollCallsAsync(baseUrl, callsUrl, pollGrant, livePollSince, cancellationToken,
                            requireRows: recovery?.NeedsPollingBaseline == true);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Caller requested cancellation (disconnect, shutdown, etc.).
                        yield break;
                    }
                    catch (FirewallAuthenticationException retryAuth)
                    {
                        isAuthError = true;
                        setupRequired = retryAuth.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                        errorMessage = retryAuth.Message;
                    }
                    catch (CallStreamAuthException retryAuth)
                    {
                        isAuthError = true;
                        setupRequired = true;
                        errorMessage = retryAuth.Message;
                        AppLog.DebugWriteLine(() => $"[CallStreamService] [AuthError] {retryAuth}");
                        AppLog.Add(() => $"CallStream: auth error after retry. {retryAuth.Message}");
                    }
                    catch (RecordingAccessException retryAuth)
                    {
                        isAuthError = true;
                        errorMessage = retryAuth.Message;
                    }
                    catch (Exception retryEx)
                    {
                        errorMessage = BuildTransportErrorMessage(retryEx);
                        AppLog.DebugWriteLine(() => $"[CallStreamService] [HttpRetryFailed] {retryEx}");
                        AppLog.Add(() => $"CallStream: http retry failed. ex={retryEx.GetType().Name}: {retryEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = BuildTransportErrorMessage(ex);
                    AppLog.DebugWriteLine(() => $"[CallStreamService] [UnexpectedError] {ex}");
                    AppLog.Add(() => $"CallStream: unexpected error. ex={ex.GetType().Name}: {ex.Message}");
                }

                // Surface any error from the poll as a synthetic CallItem row.
                if (errorMessage != null)
                {
                    hasReportedIdleConnection = false;
                    var talkgroup = isAuthError ? "AUTH" : "ERROR";

                    var transcription = isAuthError
                        ? errorMessage
                        : $"Error talking to {callsUrl}: {errorMessage}";

                    AppLog.Add(() => $"CallStream: {talkgroup} - {transcription} [ServerUrl={callsUrl}]");

                    // Clear error row for the UI.
                    yield return new CallItem
                    {
                        Timestamp = DateTime.Now,
                        Talkgroup = talkgroup,
                        IsConnectionActionRequired = setupRequired,
                        Transcription = transcription,
                        AudioUrl = string.Empty
                    };

                    var delaySeconds = isAuthError ? 15 : 5;
                    if (_recorder != null)
                    {
                        delaySeconds = Math.Min(30, delaySeconds << consecutiveFailures);
                        consecutiveFailures = Math.Min(consecutiveFailures + 1, 3);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    continue;
                }

                if (!InRecoveryScope())
                    yield break;

                if (window != null && pollGrant == null)
                    recovery?.AllowPlaybackWithoutHistory(window);

                var establishingBaseline = recovery?.NeedsPollingBaseline == true && pollGrant != null;
                if (rows != null && recovery?.ListeningSince == null && foregroundPollFloor is { } floor)
                {
                    rows.RemoveAll(row =>
                        !(row.DT_RowId?.Trim() is { } id && liveCalls.Find(id) != null) &&
                        !(TryParseTimestampUtc(row.StartTime, row.StartTimeUTC) >= floor));
                }

                consecutiveFailures = 0;
                // If there are no rows, we may still want to signal "connected but idle".
                if (rows == null || rows.Count == 0)
                {
                    if (rows != null && establishingBaseline && pollGrant!.IsCurrent(_settingsService))
                        recovery!.CompletePollingBaseline();
                    // First successful but empty poll after starting: tell the UI that we
                    // are connected, even though there are no calls yet.
                    if (!hasReportedIdleConnection)
                    {
                        hasReportedIdleConnection = true;

                        AppLog.Add(() => $"CallStream: HEARTBEAT - connected to server, waiting for calls. " +
                            $"[ServerUrl={baseUrl}/calljson]");

                        yield return new CallItem
                        {
                            Timestamp = DateTime.Now,
                            Talkgroup = "HEARTBEAT",
                            Transcription = "Connected; waiting for calls.",
                            AudioUrl = string.Empty
                        };
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                // At this point we have a non-empty set of rows.
                // Decide whether this is the initial backlog batch we want to skip.
                bool skipYieldForThisBatch = false;
                var reportReconnection = !skipInitialBatch && !hasReportedIdleConnection;

                if (skipInitialBatch)
                {
                    // First non-empty successful poll after starting: also tell the UI
                    // we are connected even though we are about to skip this backlog.
                    if (!hasReportedIdleConnection)
                    {
                        hasReportedIdleConnection = true;

                        AppLog.Add(() => $"CallStream: HEARTBEAT - connected to server, waiting for calls. " +
                            $"[ServerUrl={baseUrl}/calljson]");

                        yield return new CallItem
                        {
                            Timestamp = DateTime.Now,
                            Talkgroup = "HEARTBEAT",
                            Transcription = "Connected; waiting for calls.",
                            AudioUrl = string.Empty
                        };
                    }

                    skipYieldForThisBatch = pollGrant != null && recovery?.ListeningSince == null;
                    skipInitialBatch = false;
                }

                // Persist every received row in one batch (fast, avoids UI jank).
                try
                {
                    var toPersist = new List<DbCallRecord>(rows.Count);
                    foreach (var r in rows)
                    {
                        var record = MapToDbRecord(r, baseUrl);
                        if (record != null)
                            toPersist.Add(record);
                    }

                    if (toPersist.Count > 0)
                        await _localCallsRepository.UpsertCallsAsync(baseUrl, toPersist, cancellationToken);
                }
                catch
                {
                    // Never let local persistence break streaming.
                }

                var baselineSaved = true;
                foreach (var r in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!InRecoveryScope())
                        yield break;
                    if (pollGrant != null)
                    {
                        if (!pollGrant.IsCurrent(_settingsService))
                            break;
                        if (!AllowsHistoryRow(pollGrant, r))
                            continue;
                    }

                    // General calljson snapshots are History. Only the separately
                    // bounded current-only poll can establish a new Live observation.
                    var outstanding = recovery?.OutstandingWindow;
                    var cutoff = window?.Since;
                    var inRecoveryWindow = cutoff != null && TryParseTimestampUtc(r.StartTime, r.StartTimeUTC) >= cutoff;
                    var recoveryId = inRecoveryWindow && pollGrant != null &&
                        recovery?.IsDurable == true && !recovery.IsInherited(window) && outstanding?.Id == window?.Id
                        ? outstanding?.Id : null;
                    var item = BuildCallItemFromRow(r, baseUrl, liveCalls, pollVersion, skipYieldForThisBatch && !inRecoveryWindow,
                        pollGrant != null ? RecordingOrigin.History : RecordingOrigin.Live,
                        forwardDetachedUpdate: establishingBaseline || recoveryId != null,
                        allowProvisionalAdmission: establishingBaseline || inRecoveryWindow || pollGrant == null);

                    if (item != null)
                    {
                        item.IsListeningBaseline = establishingBaseline && recoveryId == null;
                        if (item.IsListeningBaseline || recoveryId != null)
                            RequireHistoricalAdmission(item, liveCalls, recoveryId, baseUrl);
                        yield return item;
                        baselineSaved &= !establishingBaseline || item.WasDurablyAdmitted;
                    }
                }
                if (establishingBaseline && baselineSaved && pollGrant!.IsCurrent(_settingsService))
                    recovery!.CompletePollingBaseline();

                if (reportReconnection)
                {
                    hasReportedIdleConnection = true;
                    yield return new CallItem
                    {
                        Timestamp = DateTime.Now, Talkgroup = "HEARTBEAT",
                        Transcription = "Connected; waiting for calls.", AudioUrl = string.Empty
                    };
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        private bool IsAppleIosTestAccount()
        {
            var username = _settingsService.BasicAuthUsername ?? string.Empty;
            username = username.Trim();
            return string.Equals(username, AppleIosTestAccountEmail, StringComparison.OrdinalIgnoreCase)
                || string.Equals(username, AppleIosTestAccountEmailLegacy, StringComparison.OrdinalIgnoreCase);
        }

        private async IAsyncEnumerable<CallItem> BackfillRecentHoursAsync(string baseUrl, LiveCallIndex liveCalls,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                yield break;

            var callsUrl = baseUrl + "/calljson";

            // Use a 2 hour window to satisfy the review goal.
            AppLog.Add(() => "AppleTest: starting 2 hour backfill");
            var cutoffUtc = DateTimeOffset.UtcNow.AddHours(-2);
            var grant = await GetHistoryGrantAsync(baseUrl, cancellationToken);
            if (grant == null)
                yield break;

            var rows = new List<CallRow>();
            var start = 0;
            var pages = 0;

            // Safety caps so a very quiet system does not cause large downloads.
            const int maxPages = 20;
            const int maxRows = 500;

            while (!cancellationToken.IsCancellationRequested && pages < maxPages && rows.Count < maxRows)
            {
                List<CallRow> page;

                try
                {
                    // Primary: use the same request format as the normal poller.
                    // On the hosted Joe's Scanner gateway, the form-encoded DataTables request can 502,
                    // while the existing JSON-as-form payload works reliably.
                    page = await FetchHistoryRowsAsync(callsUrl, grant, cancellationToken, start, PollBatchSize);
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback: some self-hosted servers ignore JSON paging and require classic form encoding.
                    try
                    {
                        ValidateHistoryGrant(grant, cancellationToken);
                        var dateSearch = GetHistoryDateSearch(grant);
                        page = await FetchLatestFormAsync(callsUrl, cancellationToken, start, PollBatchSize, dateSearch);
                        ValidateHistoryGrant(grant, cancellationToken);
                        page.RemoveAll(row => !AllowsHistoryRow(grant, row));
                    }
                    catch (Exception ex2)
                    {
                        AppLog.Add(() => $"AppleTest: backfill failed: {ex2.Message}");
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Add(() => $"AppleTest: backfill failed: {ex.Message}");
                    yield break;
                }

                if (page.Count == 0)
                    break;

                rows.AddRange(page);

                var oldestUtc = GetOldestTimestampUtc(page);
                if (oldestUtc.HasValue && oldestUtc.Value <= cutoffUtc)
                    break;

                start += PollBatchSize;
                pages++;
            }

            if (rows.Count == 0)
                yield break;

            // Keep the recorder's response order for rows within the window.
            var filtered = rows.Where(row => TryParseTimestampUtc(row.StartTime, row.StartTimeUTC) >= cutoffUtc).ToList();

            AppLog.Add(() => $"AppleTest: backfill rows={filtered.Count}");

            foreach (var r in filtered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!grant.IsCurrent(_settingsService))
                    yield break;

                var item = BuildCallItemFromRow(r, baseUrl, liveCalls, LiveCallState.NextVersion(), origin: RecordingOrigin.History);
                if (item != null)
                    yield return item;
            }
        }

        private Task<RecordingPlaybackAccess.HistoryGrant?> GetHistoryGrantAsync(string server, CancellationToken token,
            RecordingPlaybackAccess.HistoryGrant? previous = null) =>
            ForegroundRecoveryEntitlement.TryVerifyAsync(_settingsService, _subscriptionService, server, _clock, token, previous, _recorder != null);

        private void ValidateHistoryGrant(RecordingPlaybackAccess.HistoryGrant grant, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            grant.Validate(_settingsService);
        }

        private string? GetHistoryDateSearch(RecordingPlaybackAccess.HistoryGrant grant) =>
            grant.Cutoff is { } cutoff ? RecordingPlaybackAccess.DateSearch(cutoff, _clock.GetUtcNow()) : null;

        private static bool AllowsHistoryRow(RecordingPlaybackAccess.HistoryGrant grant, CallRow row) =>
            grant.Allows(TryParseTimestampUtc(row.StartTime, row.StartTimeUTC));

        private async Task<List<CallRow>> FetchHistoryRowsAsync(string callsUrl, RecordingPlaybackAccess.HistoryGrant grant,
            CancellationToken token, int start, int length, bool filterToGrant = true, bool requireRows = false)
        {
            ValidateHistoryGrant(grant, token);
            var rows = await FetchLatestAsync(callsUrl, token, start, length, GetHistoryDateSearch(grant),
                () => ValidateHistoryGrant(grant, token), requireRows: requireRows || !filterToGrant).ConfigureAwait(false);
            ValidateHistoryGrant(grant, token);
            if (filterToGrant)
                rows.RemoveAll(row => !AllowsHistoryRow(grant, row));
            return rows;
        }

        private async Task<List<CallRow>> PollCallsAsync(string baseUrl, string callsUrl,
            RecordingPlaybackAccess.HistoryGrant? grant, DateTimeOffset liveSince, CancellationToken token, bool requireRows = false)
        {
            if (grant != null)
                return await FetchHistoryRowsAsync(callsUrl, grant, token, 0, PollBatchSize, requireRows: requireRows).ConfigureAwait(false);

            // The firewall still authorizes ongoing Live monitoring. When History
            // cannot be verified, request only new recordings since polling began
            // or the old socket was retired. Reject out-of-window responses locally.
            var upper = _clock.GetUtcNow();
            var recent = upper - LiveOnlyPollWindow;
            var lower = liveSince > recent ? liveSince : recent;
            if (RecordingPlaybackAccess.RoundStartToSecond(lower) > upper)
                return [];
            void ValidateRecorder()
            {
                token.ThrowIfCancellationRequested();
                if (!MatchesEndpoint(baseUrl))
                    throw RecordingPlaybackAccess.VerificationRequired();
            }

            ValidateRecorder();
            var rows = await FetchLatestAsync(callsUrl, token, 0, PollBatchSize,
                RecordingPlaybackAccess.DateSearch(lower, upper), ValidateRecorder).ConfigureAwait(false);
            ValidateRecorder();
            rows.RemoveAll(row => TryParseTimestampUtc(row.StartTime, row.StartTimeUTC) is not { } time ||
                time < lower || time > upper);
            return rows;
        }

        private sealed record IncomingRows(List<CallRow> Rows, long Version, RecordingPlaybackAccess.HistoryGrant? Grant = null)
        {
            internal TaskCompletionSource<bool> Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task<bool> ReceiveRecoveryPagesAsync(string callsUrl, DateTimeOffset since,
            RecordingPlaybackAccess.HistoryGrant grant, ChannelWriter<IncomingRows> writer, Func<bool> inScope,
            CancellationToken token)
        {
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (var page = 0; inScope(); page++)
                {
                    var version = LiveCallState.NextVersion();
                    List<CallRow> records;
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(20));
                        records = await FetchHistoryRowsAsync(callsUrl, grant, deadline.Token, page * PollBatchSize, PollBatchSize,
                            filterToGrant: false).ConfigureAwait(false);
                    }

                    if (!inScope())
                        return false;
                    // Both transports register their parsed batches on the same
                    // writer. Do this before scanning the page or fetching the next.
                    var batch = new IncomingRows(records, version, grant);
                    if (!writer.TryWrite(batch) || !await batch.Consumed.Task.WaitAsync(token).ConfigureAwait(false))
                        return false;
                    if (records.Count < PollBatchSize)
                        return true;

                    // A full page is not proof that the gap is reconciled. Yield
                    // between groups of requests and stop if offsets make no progress.
                    var progressed = false;
                    foreach (var row in records)
                        if (row.DT_RowId?.Trim() is { Length: > 0 } id)
                            progressed |= seenIds.Add(id);
                    if (!progressed)
                        return false;
                    if ((page + 1) % 4 == 0)
                        await Task.Delay(RecoveryRetryDelay, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                                        ex is HttpRequestException or CallStreamAuthException or FirewallAuthenticationException or
                                            RecordingAccessException or OperationCanceledException or JsonException)
            {
                AppLog.Add(() => $"Foreground call recovery ended early ({ex.GetType().Name}).");
            }
            return false;
        }

        private async IAsyncEnumerable<CallItem> PublishRecentRecoveryAsync(string baseUrl, LiveCallIndex liveCalls,
            RecordingPlaybackAccess.HistoryGrant grant, IncomingRows batch, DateTimeOffset since, Func<bool> inScope,
            [EnumeratorCancellation] CancellationToken token)
        {
            foreach (var row in batch.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (!inScope() || !grant.IsCurrent(_settingsService))
                    yield break;
                var timestamp = TryParseTimestampUtc(row.StartTime, row.StartTimeUTC);
                if (!(timestamp >= since) || !grant.Allows(timestamp))
                    continue;
                await PersistWebSocketRowAsync(row, baseUrl, token);
                if (!inScope() || !grant.IsCurrent(_settingsService))
                    yield break;
                if (BuildCallItemFromRow(row, baseUrl, liveCalls, batch.Version, origin: RecordingOrigin.History,
                    forwardDetachedUpdate: true, allowProvisionalAdmission: true) is { } call)
                    yield return call;
            }
        }

        private DateTimeOffset? GetOldestTimestampUtc(List<CallRow> page)
        {
            DateTimeOffset? oldest = null;

            foreach (var r in page)
            {
                var ts = ParseTimestampUtc(r.StartTime, r.StartTimeUTC);
                if (oldest == null || ts < oldest.Value)
                    oldest = ts;
            }

            return oldest;
        }

        private static DateTimeOffset ParseTimestampUtc(string? startTime, string? startTimeUtc) =>
            TryParseTimestampUtc(startTime, startTimeUtc) ?? DateTimeOffset.UtcNow;

        internal static DateTimeOffset? TryParseTimestampUtc(string? startTime, string? startTimeUtc)
        {
            var value = !string.IsNullOrWhiteSpace(startTimeUtc) ? startTimeUtc : startTime;
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                if (value.Contains("T", StringComparison.Ordinal))
                {
                    // ISO 8601
                    var dto = DateTimeOffset.Parse(value.Replace("Z", "+00:00"), CultureInfo.InvariantCulture);
                    return dto.ToUniversalTime();
                }

                // Unix seconds
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                }
            }
            catch
            {
            }

            return null;
        }

        public async Task<string?> RefreshRecordingAudioAsync(CallItem item, string server, DateTimeOffset? recordedAt,
            CancellationToken cancellationToken)
        {
            server = server.TrimEnd('/');
            var key = RecordingStorageReference.ServerKey(server);
            var state = item.LiveState;
            var itemServer = item.RecordingServer ?? state?.ServerUrl;
            if (key == null || !Uri.TryCreate(server, UriKind.Absolute, out var recorder) ||
                recorder.UserInfo.Length != 0 || recorder.Query.Length != 0 || recorder.Fragment.Length != 0 ||
                !MatchesEndpoint(server) || string.IsNullOrWhiteSpace(item.BackendId) ||
                itemServer != null && RecordingStorageReference.ServerKey(itemServer) != key ||
                state != null && (state.Id != item.BackendId || RecordingStorageReference.ServerKey(state.ServerUrl) != key))
                return null;

            recordedAt ??= item.RecordingTimeUtc;
            var access = item.RecordingAccess;
            var protectedServer = access?.Protected == true || HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server);
            RecordingPlaybackAccess.HistoryGrant? grant = null;
            if (protectedServer)
            {
                RecordingPlaybackAccess.RequireAccount(_settingsService, server, access?.Account, _recorder != null);
                grant = await RecordingPlaybackAccess.VerifyHistoryAsync(_settingsService, _subscriptionService, server,
                    cancellationToken, _clock, protectedSource: true, independent: _recorder != null).ConfigureAwait(false);
                if (grant.Account != access?.Account)
                    throw RecordingPlaybackAccess.VerificationRequired();
            }

            var version = LiveCallState.NextVersion();
            var priorUrl = state?.Read().AudioUrl ?? item.AudioUrl;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var token = deadline.Token;

            void Validate()
            {
                token.ThrowIfCancellationRequested();
                if (!MatchesEndpoint(server) || grant == null &&
                    HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server))
                    throw new RecordingAccessException("The recorder or hosted account changed during recording lookup.");
                grant?.Validate(_settingsService);
                if (grant?.Allows(recordedAt) == false)
                    throw new RecordingAccessException("This recording is outside the Standard 24-hour history window.");
            }

            async Task<RecordingPage> ReadPageAsync(string? dateSearch, int start, int length)
            {
                Validate();
                var page = await FetchRecordingAudioPageAsync(server, dateSearch, start, length, Validate, token)
                    .ConfigureAwait(false);
                Validate();
                return page;
            }

            try
            {
                CallRow? row;
                if (recordedAt is { } time)
                {
                    var second = DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds());
                    var lower = second.AddSeconds(-2);
                    if (grant?.Cutoff is { } cutoff && cutoff > lower)
                        lower = cutoff;
                    var upper = second.AddSeconds(2);
                    if (grant?.Tier == 1 && _clock.GetUtcNow() < upper)
                        upper = _clock.GetUtcNow();

                    // The Date filter is in recorder wall time. A zero-row query
                    // can supply its UTC offset without searching recent recordings.
                    var localOffset = time.ToLocalTime().Offset;
                    string DateRange(TimeSpan offset) => RecordingPlaybackAccess.DateSearch(lower, upper, offset);
                    var probe = await ReadPageAsync(DateRange(localOffset), 0, 0).ConfigureAwait(false);
                    if (grant?.Cutoff is { } latestCutoff && latestCutoff > lower)
                        lower = latestCutoff;
                    row = await FindRecordingAsync(DateRange(probe.UtcOffset ?? localOffset), lower, upper)
                        .ConfigureAwait(false);

                    // The browser's shared-call flow also accepts an epoch Date
                    // value. This covers older DST offsets on compatible servers.
                    // Restrict it to unrestricted history because the server
                    // controls the time window for an epoch lookup.
                    if (row == null && grant?.Tier != 1 && probe.UtcOffset != null)
                        row = await FindRecordingAsync(second.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                            maxPages: 2)
                            .ConfigureAwait(false);
                }
                else
                {
                    // An ID-only notification or older custom row may lack a time.
                    row = await FindRecordingAsync(null).ConfigureAwait(false);
                }

                if (row == null)
                {
                    Validate();
                    if (state?.Read().AudioUrl is { } observed && observed != priorUrl)
                        return observed;
                    var reason = recordedAt == null ? "The recording has no stored time and was absent from the bounded lookup."
                        : "The recorder did not return this recording in the bounded time lookup.";
                    throw new RecordingAudioUnavailableException(detail: reason);
                }

                var snapshot = ToLiveSnapshot(row, server);
                Validate();
                if (state == null)
                    return snapshot.AudioUrl;
                state.ObserveRecoveredAudio(snapshot, version, priorUrl);
                var latest = state.Read().AudioUrl;
                return latest != priorUrl ? latest : snapshot.AudioUrl;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested &&
                (deadline.IsCancellationRequested || ex.InnerException is TimeoutException))
            {
                throw new HttpRequestException("Recording metadata lookup timed out.", ex);
            }

            async Task<CallRow?> FindRecordingAsync(string? dateSearch, DateTimeOffset? lower = null,
                DateTimeOffset? upper = null, int maxPages = 5)
            {
                const int pageSize = 200;
                var seen = new HashSet<(string? Id, string? Time)>();
                for (int pageIndex = 0, start = 0; pageIndex < maxPages; pageIndex++)
                {
                    var page = await ReadPageAsync(dateSearch, start, pageSize).ConfigureAwait(false);
                    var progressed = false;
                    var inWindow = lower == null;
                    foreach (var candidate in page.Rows)
                    {
                        progressed |= seen.Add((candidate.DT_RowId, candidate.StartTimeUTC ?? candidate.StartTime));
                        var timestamp = TryParseTimestampUtc(candidate.StartTime, candidate.StartTimeUTC);
                        if (lower != null && !(timestamp >= lower && timestamp <= upper))
                            continue;
                        inWindow = true;
                        if (!string.Equals(candidate.DT_RowId?.Trim(), item.BackendId, StringComparison.Ordinal))
                            continue;
                        if (recordedAt != null && timestamp?.ToUnixTimeSeconds() != recordedAt.Value.ToUnixTimeSeconds() ||
                            grant?.Allows(timestamp) == false)
                            continue;
                        return candidate;
                    }

                    start += page.Rows.Count;
                    if (!inWindow || !progressed || (page.FilteredCount is { } total ? start >= total : page.Rows.Count < pageSize))
                        break;
                }
                return null;
            }
        }

        private sealed record RecordingPage(List<CallRow> Rows, TimeSpan? UtcOffset, int? FilteredCount);

        private async Task<RecordingPage> FetchRecordingAudioPageAsync(string server, string? dateSearch, int start, int length,
            Action validate, CancellationToken token)
        {
            var url = server + "/calljson";
            var separator = dateSearch?.IndexOf('|') ?? -1;
            var referrer = separator >= 0 && dateSearch != null
                ? new Uri($"{server}/?DateStart={WebUtility.UrlEncode(dateSearch[..separator])}&DateEnd={WebUtility.UrlEncode(dateSearch[(separator + 1)..])}")
                : new Uri(server + "/");
            var payload = BuildDataTablesPayload(Interlocked.Increment(ref _draw), start, length, dateSearch);
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            foreach (var contentType in new[] { "application/x-www-form-urlencoded", "application/json" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, contentType)
                };
                request.Headers.Referrer = referrer;
                validate();
                using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType)
                    continue;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new RecordingAccessException("The recorder denied the recording lookup.");
                response.EnsureSuccessStatusCode();
                return await ReadRecordingPageAsync(response.Content, length, token).ConfigureAwait(false);
            }
            validate();
            try { return await FetchFormPageAsync(url, token, start, length, dateSearch, referrer).ConfigureAwait(false); }
            catch (CallStreamAuthException)
            {
                throw new RecordingAccessException("The recorder denied the recording lookup.");
            }
        }

        private static async Task<RecordingPage> ReadRecordingPageAsync(HttpContent content, int length, CancellationToken token,
            bool requireRows = false)
        {
            var json = await content.ReadAsStringAsync(token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new HttpRequestException("The recorder returned an invalid call-list response.");
                var rows = new List<CallRow>();
                if ((root.TryGetProperty("data", out var data) || root.TryGetProperty("rows", out data)) &&
                    data.ValueKind == JsonValueKind.Array)
                    foreach (var row in data.EnumerateArray().Take(length))
                        rows.Add(ParseRow(row));
                else if (requireRows)
                    throw new HttpRequestException("The recorder returned a call-list response without rows.");
                var offsetText = GetString(root, "serverTimeZoneOffset")?.Trim().TrimStart('+');
                TimeSpan? offset = TimeSpan.TryParseExact(offsetText, @"hh\:mm", CultureInfo.InvariantCulture, out var positive)
                    ? positive : TimeSpan.TryParse(offsetText, CultureInfo.InvariantCulture, out var signed) ? signed : null;
                if (offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14) || offset?.Ticks % TimeSpan.TicksPerMinute != 0)
                    offset = null;
                return new(rows, offset, int.TryParse(GetString(root, "recordsFiltered"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var count) ? count : null);
            }
            catch (JsonException ex)
            {
                throw new HttpRequestException("The recorder returned an invalid call-list response.", ex);
            }
        }

        public async Task RefreshCallMetadataAsync(IReadOnlyList<CallItem> calls, CancellationToken cancellationToken)
        {
            var baseUrl = Endpoint;
            var targets = new Dictionary<string, LiveCallState>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in calls)
            {
                if (call.LiveState is { } state && state.ServerUrl == baseUrl)
                    targets.TryAdd(state.Id, state);
            }
            if (targets.Count == 0)
                return;

            var version = LiveCallState.NextVersion();
            var grant = await GetHistoryGrantAsync(baseUrl, cancellationToken);
            if (grant == null)
                return;
            var rows = await FetchCatchupRowsAsync(baseUrl + "/calljson", targets.Keys, false, cancellationToken, grant: grant)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (cancellationToken.IsCancellationRequested || !MatchesEndpoint(baseUrl))
                return;
            foreach (var row in rows)
            {
                if (!grant.IsCurrent(_settingsService))
                    return;
                if (!AllowsHistoryRow(grant, row))
                    continue;
                if (row.DT_RowId?.Trim() is { } id && targets.TryGetValue(id, out var state))
                    state.Observe(ToLiveSnapshot(row, state.ServerUrl), version);
            }
        }

        private CallItem? BuildCallItemFromRow(CallRow r, string baseUrl, LiveCallIndex liveCalls, long version,
            bool skipYieldForThisBatch = false, RecordingOrigin origin = RecordingOrigin.Live, bool forwardDetachedUpdate = false,
            bool allowProvisionalAdmission = false)
        {
            var rawId = r.DT_RowId?.Trim();
            if (string.IsNullOrEmpty(rawId))
                return null;

            var firstObservation = liveCalls.MarkSeen(rawId);
            if (!firstObservation && !(allowProvisionalAdmission && liveCalls.NeedsAdmission(rawId)))
                return forwardDetachedUpdate ? ForwardExistingRow(r, baseUrl, liveCalls, version) :
                    ObserveExistingCall(r, liveCalls.Find(rawId), version);
            if (skipYieldForThisBatch)
            {
                liveCalls.ConfirmAdmission(rawId, false);
                return null;
            }

            rawId = liveCalls.OriginalId(rawId);
            var state = liveCalls.Find(rawId) ?? new LiveCallState(baseUrl, rawId);
            state.Observe(ToLiveSnapshot(r, baseUrl), version);
            liveCalls.Track(state);
            var snapshot = state.Read();
            var metadata = snapshot.Metadata;

            var audioUrl = snapshot.AudioUrl ?? string.Empty;

            var debugInfo = string.Empty;
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                debugInfo = AppendDebug(debugInfo, "No audio URL from server");
                debugInfo = AppendDebug(debugInfo, "No audio URL built from server data");
            }

            var item = new CallItem
            {
                BackendId = rawId,
                LiveAdmission = idWasSaved => liveCalls.ConfirmAdmission(state.Id, idWasSaved),
                LiveState = state,
                RecordingTimeUtc = TryParseTimestampUtc(r.StartTime, r.StartTimeUTC),
                RecordingServer = baseUrl,
                RecordingAccess = new RecordingAccessContext(origin, HostedServerRules.RequiresApiFirewallCredentials(_settingsService, baseUrl)),
                Timestamp = metadata.Timestamp ?? DateTime.Now,
                CallDurationSeconds = metadata.DurationSeconds ?? 0,
                Talkgroup = metadata.Talkgroup ?? string.Empty,
                Source = metadata.Radio ?? string.Empty,
                Site = metadata.Site ?? string.Empty,
                VoiceReceiver = metadata.Receiver ?? string.Empty,
                CardMetadata = metadata,
                Transcription = snapshot.Transcription ?? string.Empty,
                AudioUrl = audioUrl,
                DebugInfo = debugInfo
            };

            ApplyAddressDetection(item);
            return item;
        }

        private CallItem? ForwardExistingRow(CallRow row, string server, LiveCallIndex liveCalls, long version)
        {
            if (row.DT_RowId?.Trim() is not { Length: > 0 } id)
                return null;

            // An evicted card can still be pending in SQLite. The caller saves
            // this as an existing-only observation in the active account scope.
            var state = liveCalls.Find(id) ?? new LiveCallState(server, liveCalls.OriginalId(id));
            return ObserveExistingCall(row, state, version);
        }

        private void RequireHistoricalAdmission(CallItem item, LiveCallIndex liveCalls, string? windowId, string server)
        {
            item.RecoveryWindowId = windowId;
            item.RecordingAccess ??= new RecordingAccessContext(RecordingOrigin.History,
                HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server));
            item.LiveAdmission = accepted => liveCalls.ConfirmAdmission(item.BackendId, accepted);
        }

        private void ApplyAddressDetection(CallItem item)
        {
            try
            {
                _addressDetectionService.Apply(item);
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AddressDetection: Apply failed. {ex.Message}"); } catch { }
            }
        }

        private static LiveCallSnapshot ToLiveSnapshot(CallRow row, string serverUrl) =>
            row.ToLiveSnapshot(ParseTimestamp(row.StartTime, row.StartTimeUTC), NormalizeCallText(row.CallText))
                with { AudioUrl = BuildLiveAudioUrl(row.AudioFilename, serverUrl) };

        private static string? BuildLiveAudioUrl(string? filename, string serverUrl)
        {
            if (string.IsNullOrWhiteSpace(filename))
                return null;

            var path = filename.Trim();
            if (path.StartsWith("//", StringComparison.Ordinal) && Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
                path = server.Scheme + ":" + path;
            else if (!path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                path = $"{serverUrl}/{path.TrimStart('/')}";
            return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri.AbsoluteUri
                : null;
        }

        private CallItem? ObserveExistingCall(CallRow row, LiveCallState? state, long version)
        {
            if (state == null || !state.Observe(ToLiveSnapshot(row, state.ServerUrl), version, out var transcript))
                return null;

            var item = new CallItem
            {
                BackendId = state.Id,
                LiveState = state,
                RecordingTimeUtc = TryParseTimestampUtc(row.StartTime, row.StartTimeUTC),
                IsLiveUpdate = true,
                Transcription = transcript ?? string.Empty
            };
            if (transcript != null)
                ApplyAddressDetection(item);
            return item;
        }

        // Normalizes incoming transcription text so a single oversized payload cannot stall the UI.
        // We intentionally keep this inexpensive: whitespace normalization, a hard input cap, and repetition culling.
        private static string NormalizeCallText(string? text)
        {
            return TranscriptionSanitizer.Sanitize(text);
        }

        private static string? CapTextRaw(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var s = text.Trim();
            const int maxChars = 20000;
            if (s.Length > maxChars)
                s = s.Substring(0, maxChars);
            return s;
        }

        private DbCallRecord? MapToDbRecord(CallRow r, string serverKey)
        {
            var rawId = r.DT_RowId?.Trim();
            if (string.IsNullOrWhiteSpace(rawId))
                return null;

            // Canonical transcription for Phase 1: for call stream rows, CallText is the best available.
            var normalizedTranscription = NormalizeCallText(r.CallText);
            var transcription = string.IsNullOrWhiteSpace(normalizedTranscription) ? null : normalizedTranscription;

            int? lcn = null;
            if (!string.IsNullOrWhiteSpace(r.LCN) && int.TryParse(r.LCN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lcnValue))
                lcn = lcnValue;

            double? frequency = null;
            if (!string.IsNullOrWhiteSpace(r.Frequency) && double.TryParse(r.Frequency, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqValue))
                frequency = freqValue;

            double? audioStartPos = null;
            if (!string.IsNullOrWhiteSpace(r.AudioStartPos) && double.TryParse(r.AudioStartPos, NumberStyles.Float, CultureInfo.InvariantCulture, out var posValue))
                audioStartPos = posValue;

            double? durationSeconds = null;
            if (!string.IsNullOrWhiteSpace(r.CallDuration) && double.TryParse(r.CallDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                durationSeconds = dur;

            // Prefer StartTimeUTC if present; fall back to StartTime (some endpoints only include StartTime).
            var startUtc = !string.IsNullOrWhiteSpace(r.StartTimeUTC) ? r.StartTimeUTC : r.StartTime;

            var now = DateTimeOffset.UtcNow;

            return new DbCallRecord
            {
                ServerKey = (serverKey ?? string.Empty).Trim().TrimEnd('/'),
                BackendId = rawId,
                StartTimeUtc = CapTextRaw(startUtc),
                TimeText = CapTextRaw(r.Time),
                DateText = CapTextRaw(r.Date),
                TargetId = CapTextRaw(r.TargetID),
                TargetLabel = CapTextRaw(r.TargetLabel),
                TargetTag = CapTextRaw(r.TargetTag),
                SourceId = CapTextRaw(r.SourceID),
                SourceLabel = CapTextRaw(r.SourceLabel),
                SourceTag = CapTextRaw(r.SourceTag),
                Lcn = lcn,
                Frequency = frequency,
                CallAudioType = CapTextRaw(r.CallAudioType),
                CallType = CapTextRaw(r.CallType),
                SystemId = CapTextRaw(r.SystemID),
                SystemLabel = CapTextRaw(r.SystemLabel),
                SystemType = CapTextRaw(r.SystemType),
                SiteId = CapTextRaw(r.SiteID),
                SiteLabel = CapTextRaw(r.SiteLabel),
                VoiceReceiver = CapTextRaw(r.VoiceReceiver),
                AudioFilename = RecordingStorageReference.ForCache(r.AudioFilename),
                AudioStartPos = audioStartPos,
                CallDurationSeconds = durationSeconds,
                CallText = CapTextRaw(r.CallText),
                Transcription = transcription,
                ReceivedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        private async Task PersistWebSocketRowAsync(CallRow row, string serverKey, CancellationToken cancellationToken)
        {
            try
            {
                var rawId = row.DT_RowId?.Trim();
                if (string.IsNullOrWhiteSpace(rawId))
                    return;

                if (row.IsUpdateNotification)
                {
                    await _localCallsRepository.MarkTranscriptionUpdateNotifiedAsync(serverKey, rawId, cancellationToken);
                    return;
                }

                var record = MapToDbRecord(row, serverKey);
                if (record != null)
                    await _localCallsRepository.UpsertCallsAsync(serverKey, new[] { record }, cancellationToken);
            }
            catch
            {
                // Never let local persistence break streaming.
            }
        }

        private async Task<List<CallRow>> FetchCatchupRowsAsync(string callsUrl, IEnumerable<string> ids,
            bool includeLatestPage, CancellationToken cancellationToken, string? dateSearch = null,
            RecordingPlaybackAccess.HistoryGrant? grant = null)
        {
            const int pageSize = 200;
            const int maxPages = 5;
            var remaining = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var result = new List<CallRow>();

            for (var page = 0; page < maxPages; page++)
            {
                var rows = grant == null
                    ? await FetchLatestAsync(callsUrl, cancellationToken, page * pageSize, pageSize, dateSearch).ConfigureAwait(false)
                    : await FetchHistoryRowsAsync(callsUrl, grant, cancellationToken, page * pageSize, pageSize).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    var matched = row.DT_RowId?.Trim() is { } id && remaining.Remove(id);
                    if (matched || (includeLatestPage && page == 0))
                        result.Add(row);
                }

                if (remaining.Count == 0 || rows.Count < pageSize)
                    break;
            }

            return result;
        }

        private async Task<WebSocket?> OpenSocketForLiveAsync(string baseUrl, bool validateLiveness, CancellationToken token)
        {
            try
            {
                return await _openWebSocket(baseUrl, validateLiveness, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                AppLog.DebugWriteLine(() => $"Live WebSocket connection failed ({ex.GetType().Name}).");
                return null;
            }
        }

        private async Task<WebSocket?> TryOpenWebSocketAsync(string baseUrl, bool validateLiveness, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                return null;

            var builder = new UriBuilder(baseUri);

            if (string.Equals(builder.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                builder.Scheme = "wss";
            }
            else if (string.Equals(builder.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                builder.Scheme = "ws";
            }
            else
            {
                return null;
            }

            builder.Path = "/Calls";
            builder.Query = string.Empty;
            builder.Fragment = string.Empty;

            var wsUri = builder.Uri;

            var ws = new ClientWebSocket();
            // Keepalive helps prevent intermediate proxies from timing out idle connections.
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            if (validateLiveness)
                ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                await ws.ConnectAsync(wsUri, _webSocketInvoker, connectCts.Token);

                if (ws.State == WebSocketState.Open)
                {
                    return ws;
                }
            }
            catch
            {
                try
                {
                    ws.Dispose();
                }
                catch
                {
                }
            }

            return null;
        }


        private async Task<List<CallRow>> FetchLatestAsync(
            string callsUrl,
            CancellationToken cancellationToken,
            int start,
            int length,
            string? dateSearch = null,
            Action? validateFallback = null, bool requireRows = false)
        {
            // Attempt JSON body first (works reliably on the hosted gateway in our environment).
            try
            {
                var payload = BuildDataTablesPayload(Interlocked.Increment(ref _draw), start, length, dateSearch);

                var jsonBody = JsonSerializer.Serialize(payload, _jsonOptions);
                using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(callsUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = response.StatusCode;
                    var statusInt = (int)statusCode;

                    if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
                    {
                        throw new CallStreamAuthException(
                            $"Authentication failed when calling {callsUrl} (HTTP {statusInt} {response.ReasonPhrase}).");
                    }

                    // Fall back to form encoding for servers that do not accept JSON payloads.
                    throw new HttpRequestException(
                        $"Server returned HTTP {statusInt} {response.ReasonPhrase} when calling {callsUrl}.");
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var rows = new List<CallRow>();

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rowsElement.EnumerateArray())
                        rows.Add(ParseRow(item));
                }
                else if (root.TryGetProperty("rows", out rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rowsElement.EnumerateArray())
                        rows.Add(ParseRow(item));
                }
                else if (requireRows)
                    throw new HttpRequestException("The recorder returned a call-list response without rows.");

                return rows;
            }
            catch (Exception ex) when (ex is CallStreamAuthException or FirewallAuthenticationException or OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fallback: DataTables form encoding.
                validateFallback?.Invoke();
                return await FetchLatestFormAsync(callsUrl, cancellationToken, start, length, dateSearch, requireRows);
            }
        }

        private async IAsyncEnumerable<CallItem> StreamConnectionAsync(
            WebSocket? ws, string baseUrl, string callsUrl, LiveCallIndex liveCalls, LiveStreamRecovery? recovery,
            LiveStreamRecovery.Window? window, DateTimeOffset? foregroundFloor, DateTimeOffset socketOpenedAfter,
            RecordingPlaybackAccess.HistoryGrant? historyGrant, Func<bool> inScope,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = session.Token;
            using var socketLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Each producer waits for acknowledgment before registering another
            // batch. One reader serializes admission in registration order.
            var incoming = Channel.CreateUnbounded<IncomingRows>(new UnboundedChannelOptions { SingleReader = true });
            var receive = ws == null ? Task.CompletedTask : ReceiveSocketBatchesAsync(ws, incoming.Writer, socketLifetime.Token);
            var gap = window != null && historyGrant != null ? RecoverAsync(historyGrant) : null;
            Task? retry = window != null && gap == null && ws != null ? Task.Delay(RecoveryRetryDelay, token) : null;
            Task<bool>? ready = null;
            var reconnecting = false;
            var protectedRecorder = HostedServerRules.RequiresApiFirewallCredentials(_settingsService, baseUrl);
            Task<(RecordingPlaybackAccess.HistoryGrant? Grant, List<CallRow> Rows)>? catchup = null;
            LiveCallState[] targets = [];
            long catchupVersion = 0;

            try
            {
                while (!token.IsCancellationRequested && inScope())
                {
                    if (gap is { IsCompleted: true })
                    {
                        if (await gap && !token.IsCancellationRequested && inScope())
                            await recovery!.CompleteAsync(window!, token);
                        else if (!receive.IsCompleted)
                            retry = Task.Delay(RecoveryRetryDelay, token);
                        gap = null;
                    }

                    var foreground = gap == null ? recovery?.Pending : null;
                    if (ws != null && !reconnecting && foreground?.IsCompleted == true)
                    {
                        // Let recovery finish first, then drain any socket batch
                        // already registered before handing off to the new socket.
                        reconnecting = true;
                        ws.Abort();
                        socketLifetime.Cancel();
                        await receive.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }

                    if (!receive.IsCompleted && retry?.IsCompleted == true && !reconnecting)
                    {
                        retry = null;
                        gap = RecoverAsync();
                    }

                    if (incoming.Reader.TryRead(out var batch))
                    {
                        ready = null;
                        if (batch.Grant is { } grant)
                        {
                            var reconciled = true;
                            await foreach (var call in PublishRecentRecoveryAsync(baseUrl, liveCalls, grant,
                                batch, window!.Since, inScope, token))
                            {
                                if (recovery!.IsDurable)
                                    RequireHistoricalAdmission(call, liveCalls, window.Id, baseUrl);
                                yield return call;
                                reconciled &= !recovery!.IsDurable || call.WasDurablyAdmitted;
                            }
                            batch.Consumed.SetResult(reconciled && inScope() && grant.IsCurrent(_settingsService));
                        }
                        else
                        {
                            // Receive one more socket batch while the consumer
                            // persists the current one.
                            batch.Consumed.SetResult(true);
                            await foreach (var call in PublishSocketRowsAsync(batch))
                                yield return call;
                        }
                    }

                    var receiveFinished = receive.IsCompleted;
                    if (receiveFinished)
                    {
                        if (!reconnecting)
                            await receive;
                        if (gap == null)
                            yield break;
                    }

                    if (!receive.IsCompleted && catchup is { IsCompleted: true })
                    {
                        var (grant, rows) = await catchup;
                        foreach (var row in rows)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!inScope() || grant == null || !grant.IsCurrent(_settingsService))
                                break;
                            if (!AllowsHistoryRow(grant, row))
                                continue;
                            // Catch-up pages can include historical rows that were never Live cards.
                            // Leave them untracked so a subsequent full WebSocket row can insert a new call.
                            var state = row.DT_RowId?.Trim() is { } id ? liveCalls.Find(id) : null;
                            if (targets.Length == 0 && state?.Read().Transcription != null)
                                continue;
                            if (ObserveExistingCall(row, state, catchupVersion) is { } call)
                                yield return call;
                        }

                        foreach (var target in targets)
                            target.CompleteNotification();
                        catchup = null;
                        continue;
                    }

                    if (!receive.IsCompleted && catchup == null && liveCalls.BeginCatchup(DateTimeOffset.UtcNow) is { } scheduled)
                    {
                        targets = scheduled;
                        catchupVersion = LiveCallState.NextVersion();
                        catchup = FetchWebSocketCatchupAsync(baseUrl, callsUrl, targets, token);
                    }

                    ready ??= incoming.Reader.WaitToReadAsync(token).AsTask();
                    var wake = new List<Task>(6) { ready };
                    if (gap != null)
                        wake.Add(gap);
                    if (!receiveFinished)
                    {
                        wake.Add(receive);
                        if (foreground != null)
                            wake.Add(foreground);
                        if (retry != null)
                            wake.Add(retry);
                        if (catchup != null)
                            wake.Add(catchup);
                    }
                    await Task.WhenAny(wake);
                }
            }
            finally
            {
                session.Cancel();
                await ((Task)receive).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (gap != null)
                    await ((Task)gap).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (catchup != null)
                    await ((Task)catchup).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                foreach (var target in targets)
                    target.CompleteNotification();
                liveCalls.ClearNotifications();
            }

            async Task<bool> RecoverAsync(RecordingPlaybackAccess.HistoryGrant? grant = null)
            {
                if (!inScope())
                    return false;
                grant ??= await GetHistoryGrantAsync(baseUrl, token);
                return grant != null && inScope() && await ReceiveRecoveryPagesAsync(callsUrl, window!.Since,
                    grant, incoming.Writer, inScope, token);
            }

            async IAsyncEnumerable<CallItem> PublishSocketRowsAsync(IncomingRows batch)
            {
                var checkedHistory = false;
                foreach (var row in batch.Rows)
                {
                    token.ThrowIfCancellationRequested();
                    if (!inScope())
                        yield break;
                    var recordedAt = TryParseTimestampUtc(row.StartTime, row.StartTimeUTC);
                    // A hosted socket can replay complete historical rows. Apply
                    // its entitlement check even when only an existing row may be updated.
                    var origin = !protectedRecorder || recordedAt >= socketOpenedAfter
                        ? RecordingOrigin.Live : RecordingOrigin.History;
                    var history = !row.IsUpdateNotification && origin == RecordingOrigin.History;
                    var listeningBaseline = history && recovery?.ListeningSince is { } listeningSince && recordedAt < listeningSince;
                    if (history && !checkedHistory)
                    {
                        historyGrant = await GetHistoryGrantAsync(baseUrl, token, historyGrant);
                        checkedHistory = true;
                    }
                    bool Authorized() => inScope() && (!history || historyGrant != null &&
                        historyGrant.IsCurrent(_settingsService) && historyGrant.Allows(recordedAt));
                    if (!Authorized())
                        continue;

                    var existingOnly = history && !listeningBaseline && foregroundFloor is { } floor && recordedAt < floor;
                    if (!existingOnly)
                        await PersistWebSocketRowAsync(row, baseUrl, token);
                    if (!Authorized())
                        continue;

                    CallItem? call;
                    if (row.IsUpdateNotification)
                    {
                        var id = row.DT_RowId?.Trim();
                        var state = id != null ? liveCalls.Find(id) : null;
                        call = ObserveExistingCall(row, state, batch.Version);
                        if (state != null)
                            liveCalls.QueueNotification(state, DateTimeOffset.UtcNow);
                        else if (id is { Length: > 0 })
                        {
                            var update = new LiveCallState(baseUrl, liveCalls.OriginalId(id));
                            update.Observe(ToLiveSnapshot(row, baseUrl), batch.Version);
                            call = new CallItem
                            {
                                BackendId = update.Id, RecordingServer = baseUrl, LiveState = update,
                                IsRecordingNotification = true
                            };
                        }
                    }
                    else if (existingOnly)
                    {
                        call = ForwardExistingRow(row, baseUrl, liveCalls, batch.Version);
                    }
                    else
                    {
                        call = BuildCallItemFromRow(row, baseUrl, liveCalls, batch.Version, origin: origin,
                            forwardDetachedUpdate: true, allowProvisionalAdmission: true);
                    }

                    if (call != null)
                    {
                        call.IsListeningBaseline = listeningBaseline;
                        if (listeningBaseline)
                            RequireHistoricalAdmission(call, liveCalls, windowId: null, baseUrl);
                        yield return call;
                    }
                }
            }
        }

        private static async Task ReceiveSocketBatchesAsync(WebSocket socket, ChannelWriter<IncomingRows> writer, CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            while (!token.IsCancellationRequested)
            {
                var (rows, version) = await ReadWebSocketRowsAsync(socket, buffer, token).ConfigureAwait(false);
                if (rows == null)
                    return;
                var batch = new IncomingRows(rows, version);
                if (!writer.TryWrite(batch) || !await batch.Consumed.Task.WaitAsync(token).ConfigureAwait(false))
                    return;
            }
        }

        private async Task<(RecordingPlaybackAccess.HistoryGrant? Grant, List<CallRow> Rows)> FetchWebSocketCatchupAsync(
            string server, string callsUrl, LiveCallState[] targets, CancellationToken cancellationToken)
        {
            try
            {
                var grant = await GetHistoryGrantAsync(server, cancellationToken);
                if (grant == null)
                    return (null, []);
                var rows = await FetchCatchupRowsAsync(callsUrl, targets.Select(state => state.Id),
                    targets.Length == 0, cancellationToken, grant: grant).ConfigureAwait(false);
                return (grant, rows);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (null, []);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"CallStream: WebSocket metadata catch-up failed ({ex.GetType().Name}).");
                return (null, []);
            }
        }

        private static async Task<(List<CallRow>? Rows, long Version)> ReadWebSocketRowsAsync(
            WebSocket ws, byte[] buffer, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadWebSocketMessageAsync(ws, buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is WebSocketException or IOException or OperationCanceledException)
            {
                AppLog.DebugWriteLine(() => $"Live WebSocket receive ended ({ex.GetType().Name}).");
                return (null, 0);
            }
        }

        private static async Task<(List<CallRow>? Rows, long Version)> ReadWebSocketMessageAsync(
            WebSocket ws, byte[] buffer, CancellationToken cancellationToken)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken).ConfigureAwait(false);
                    return (null, 0);
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var version = LiveCallState.NextVersion();
            var rows = new List<CallRow>();
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            if (string.IsNullOrWhiteSpace(text))
                return (rows, version);

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        root = data;
                    else
                        rows.Add(ParseRow(root));
                }
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in root.EnumerateArray())
                    {
                        if (row.ValueKind == JsonValueKind.Object)
                            rows.Add(ParseRow(row));
                    }
                }

                return (rows, version);
            }
            catch (JsonException ex)
            {
                AppLog.Add(() => $"CallStream: WebSocket parse error ({ex.GetType().Name}).");
                return (null, 0);
            }
        }

        // Maps a JSON row from the DataTables response into a CallRow object. from the DataTables response into a CallRow object.
        private static CallRow ParseRow(JsonElement e)
        {
            var row = new CallRow
            {
                DT_RowId = GetString(e, "DT_RowId"),
                IsUpdateNotification = IsTruthy(GetString(e, "Update")),
                StartTime = GetString(e, "StartTime"),
                StartTimeUTC = GetString(e, "StartTimeUTC"),
                Time = GetString(e, "Time"),
                Date = GetString(e, "Date"),
                AudioStartPos = GetString(e, "AudioStartPos"),
                CallText = GetString(e, "CallText"),
                AudioFilename = GetString(e, "AudioFilename")
            };
            if (string.IsNullOrWhiteSpace(row.AudioFilename))
                row.AudioFilename = GetString(e, "AudioUrl");
            row.ReadCardFields(e, GetString);
            return row;
        }

        private static bool IsTruthy(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            value = value.Trim();
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        // Parses timestamps from either ISO 8601 or Unix seconds and converts them to local time.
        private static DateTime? ParseTimestamp(string? startTime, string? startTimeUtc)
        {
            var value = !string.IsNullOrWhiteSpace(startTimeUtc) ? startTimeUtc : startTime;

            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                // ISO 8601.
                if (value.Contains("T", StringComparison.Ordinal))
                {
                    var dto = DateTimeOffset.Parse(value.Replace("Z", "+00:00"), null);
                    return dto.ToLocalTime().DateTime;
                }

                // Unix seconds.
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    var dto = DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToLocalTime();
                    return dto.DateTime;
                }
            }
            catch
            {
            }

            return null;
        }

        // Safely gets a string representation of a JSON property, handling various value kinds.
        private static string? GetString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var prop))
            {
                // Some TR endpoints and WebSocket messages vary casing (for example DT_RowID vs DT_RowId).
                foreach (var p in obj.EnumerateObject())
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        prop = p.Value;
                        goto Found;
                    }
                }

                return null;
            }

        Found:
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.ToString(),
                JsonValueKind.True or JsonValueKind.False => prop.GetBoolean().ToString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => prop.ToString()
            };
        }

        // Combines debug messages into a single string separated by " | ".
        private static string AppendDebug(string existing, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return existing ?? string.Empty;

            if (string.IsNullOrWhiteSpace(existing))
                return message;

            // Combine multiple debug messages in a compact way.
            return existing + " | " + message;
        }

        // Thrown when the server responds with an authentication failure.
        // Lets the caller distinguish auth problems from other HTTP errors.
        private sealed class CallStreamAuthException : Exception
        {
            public CallStreamAuthException(string message) : base(message)
            {
            }
        }

        // DataTables payload envelope for the calljson request.
        private sealed class DataTablesPayload
        {
            [JsonPropertyName("draw")]
            public int Draw { get; set; }

            [JsonPropertyName("columns")]
            public List<DataTablesColumn> Columns { get; set; } = new();

            [JsonPropertyName("order")]
            public List<DataTablesOrder> Order { get; set; } = new();

            [JsonPropertyName("start")]
            public int Start { get; set; }

            [JsonPropertyName("length")]
            public int Length { get; set; }

            [JsonPropertyName("search")]
            public DataTablesSearch Search { get; set; } = new();

            [JsonPropertyName("SmartSort")]
            public bool SmartSort { get; set; }
        }

        // DataTables column descriptor used by the calljson endpoint.
        private sealed class DataTablesColumn
        {
            [JsonPropertyName("data")]
            public object? Data { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("searchable")]
            public bool Searchable { get; set; }

            [JsonPropertyName("orderable")]
            public bool Orderable { get; set; }

            [JsonPropertyName("search")]
            public DataTablesSearch Search { get; set; } = new();
        }

        // DataTables ordering descriptor for a column.
        private sealed class DataTablesOrder
        {
            [JsonPropertyName("column")]
            public int Column { get; set; }

            [JsonPropertyName("dir")]
            public string Dir { get; set; } = "desc";
        }

        // DataTables search descriptor used for global and per-column search.
        private sealed class DataTablesSearch
        {
            [JsonPropertyName("value")]
            public string Value { get; set; } = string.Empty;

            [JsonPropertyName("regex")]
            public bool Regex { get; set; }
        }

        // Builds the DataTables payload used by the calljson endpoint.
        private static DataTablesPayload BuildDataTablesPayload(int draw, int start, int length, string? dateSearch = null)
        {
            var cols = new[]
            {
                ((object?)null, "Details"),
                ((object?)"Time", "Time"),
                ((object?)"Date", "Date"),
                ((object?)"TargetID", "Target"),
                ((object?)"TargetLabel", "TargetLabel"),
                ((object?)"TargetTag", "TargetTag"),
                ((object?)"SourceID", "Source"),
                ((object?)"SourceLabel", "SourceLabel"),
                ((object?)"SourceTag", "SourceTag"),
                ((object?)"CallDuration", "CallLength"),
                ((object?)"CallAudioType", "VoiceType"),
                ((object?)"CallType", "CallType"),
                ((object?)"SiteID", "Site"),
                ((object?)"SiteLabel", "SiteLabel"),
                ((object?)"SystemID", "System"),
                ((object?)"SystemLabel", "SystemLabel"),
                ((object?)"SystemType", "SystemType"),
                ((object?)"AudioStartPos", "AudioStartPos"),
                ((object?)"LCN", "LCN"),
                ((object?)"Frequency", "Frequency"),
                ((object?)"VoiceReceiver", "Receiver"),
                ((object?)"CallText", "CallText"),
                ((object?)"AudioFilename", "Filename"),
            };

            var payload = new DataTablesPayload
            {
                Draw = draw,
                Start = Math.Max(0, start),
                Length = Math.Max(0, length),
                Search = new DataTablesSearch { Value = string.Empty, Regex = false },
                SmartSort = false
            };

            foreach (var (data, name) in cols)
            {
                payload.Columns.Add(new DataTablesColumn
                {
                    Data = data,
                    Name = name,
                    Searchable = true,
                    Orderable = false,
                    Search = new DataTablesSearch { Value = name == "Date" ? dateSearch ?? string.Empty : string.Empty, Regex = false }
                });
            }

            return payload;
        }



        private static string BuildTransportErrorMessage(Exception ex)
        {
            // Default to the exception message, but add diagnostics that help pinpoint the real root cause.
            // This is intentionally verbose in logs because iOS/MacCatalyst networking failures often surface as generic messages.
            var sb = new StringBuilder();

            void AppendLine(string s)
            {
                if (sb.Length > 0)
                    sb.Append(" | ");
                sb.Append(s);
            }

            AppendLine(ex.Message);

            // Walk inner exceptions for additional signal.
            var depth = 0;
            var cur = ex.InnerException;
            while (cur != null && depth < 6)
            {
                AppendLine($"{cur.GetType().FullName}: {cur.Message}");
                cur = cur.InnerException;
                depth++;
            }

#if IOS || MACCATALYST
            // Try to extract NSError domain/code without taking a direct compile-time dependency.
            // This works across Xamarin/.NET for iOS where failures often wrap Foundation.NSErrorException.
            try
            {
                object? nserrObj = null;

                // Search the exception chain (including the outer) for a type named *NSErrorException.
                Exception? scan = ex;
                while (scan != null)
                {
                    var t = scan.GetType();
                    if (t.FullName != null && t.FullName.EndsWith("NSErrorException", StringComparison.Ordinal))
                    {
                        nserrObj = scan;
                        break;
                    }
                    scan = scan.InnerException;
                }

                if (nserrObj != null)
                {
                    var t = nserrObj.GetType();
                    var errorProp = t.GetProperty("Error");
                    var err = errorProp?.GetValue(nserrObj);
                    if (err != null)
                    {
                        var errType = err.GetType();
                        var domain = errType.GetProperty("Domain")?.GetValue(err)?.ToString();
                        var codeObj = errType.GetProperty("Code")?.GetValue(err);
                        var code = codeObj != null ? Convert.ToInt64(codeObj, CultureInfo.InvariantCulture) : 0;

                        AppendLine($"NSErrorDomain={domain}, Code={code}");

                        // Try to surface the failing URL if available in UserInfo.
                        var userInfoObj = errType.GetProperty("UserInfo")?.GetValue(err);
                        if (userInfoObj is System.Collections.IDictionary dict)
                        {
                            foreach (var key in new[] { "NSErrorFailingURLStringKey", "NSErrorFailingURLKey" })
                            {
                                if (dict.Contains(key) && dict[key] != null)
                                {
                                    AppendLine($"{key}={dict[key]}");
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Swallow: logging helper must never throw.
            }
#endif

            return sb.ToString();
        }

// Internal representation of a single call row from the calljson endpoint.
        private sealed class CallRow : TrunkingRecorderCallFields
        {
            public string? DT_RowId { get; set; }
            public bool IsUpdateNotification { get; set; }
            public string? StartTime { get; set; }
            public string? StartTimeUTC { get; set; }
            public string? Time { get; set; }
            public string? Date { get; set; }
            public string? AudioStartPos { get; set; }
            public string? CallText { get; set; }
            public string? AudioFilename { get; set; }
        }
    }
}
