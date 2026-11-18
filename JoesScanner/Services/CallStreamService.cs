using JoesScanner.Models;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
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
        private readonly HttpClient _httpClient;
        private readonly HttpMessageInvoker _webSocketInvoker;
        private readonly JsonSerializerOptions _jsonOptions;

        // Mirrors DEFAULT_CONFIG["poll_batch"] from the server.
        private const int PollBatchSize = 25;

        // DataTables draw counter used by the calljson endpoint.
        private int _draw = 1;

        // Creates a new call stream service with an HttpClient configured for the calljson endpoint.
        public CallStreamService(ISettingsService settingsService, ILocalCallsRepository localCallsRepository, IAddressDetectionService addressDetectionService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _localCallsRepository = localCallsRepository ?? throw new ArgumentNullException(nameof(localCallsRepository));
            _addressDetectionService = addressDetectionService ?? throw new ArgumentNullException(nameof(addressDetectionService));
            _webSocketInvoker = new HttpMessageInvoker(new ServerAuthenticationHandler(
                ServerAuthentication.For(_settingsService),
                new SocketsHttpHandler { AllowAutoRedirect = false }));

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

            _httpClient = ServerAuthenticationHandler.CreateClient(_settingsService, socketsHandler);
#else
            _httpClient = ServerAuthenticationHandler.CreateClient(_settingsService);
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
            int length)
        {
            var payload = BuildDataTablesPayload(Interlocked.Increment(ref _draw), start, length);

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

            using var content = new FormUrlEncodedContent(pairs);
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
                {
                    rows.Add(ParseRow(item));
                }
            }
            else if (root.TryGetProperty("rows", out rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rowsElement.EnumerateArray())
                {
                    rows.Add(ParseRow(item));
                }
            }

            return rows;
        }

        // Continuously polls the server and yields new calls as CallItem objects.
        // Skips initial backlog so only new calls after connection are surfaced.
        public IAsyncEnumerable<CallItem> GetCallStreamAsync(
            IReadOnlyList<CallItem> existingCalls, CancellationToken cancellationToken)
        {
            var serverUrl = (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/');
            var liveCalls = new LiveCallIndex();
            liveCalls.Seed(existingCalls, serverUrl);
            return StreamCallsAsync(serverUrl, liveCalls, cancellationToken);
        }

        private async IAsyncEnumerable<CallItem> StreamCallsAsync(string currentServerUrl, LiveCallIndex liveCalls,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var isAppleTestAccount = IsAppleIosTestAccount();
            AppLog.Add(() => $"AppleTest: enabled={isAppleTestAccount}");

            // Only existing cards can receive updates from the first polling batch.
            var skipInitialBatch = true;

            // Apple review test account behavior.
            // For this one account only, backfill several hours of calls so playback can
            // start immediately even if there are no brand new calls arriving.
            if (isAppleTestAccount)
            {
                skipInitialBatch = false;
                await foreach (var backfilled in BackfillRecentHoursAsync(currentServerUrl, liveCalls, cancellationToken))
                {
                    yield return backfilled;
                }
            }

            // Tracks whether we have already reported a "connected but idle" heartbeat
            // for this connection lifetime.
            var hasReportedIdleConnection = false;

            // When WebSocket is unavailable, fall back to polling and retry WS after a short cooldown.
            var wsDisableUntilUtc = DateTime.MinValue;

            while (!cancellationToken.IsCancellationRequested)
            {
                var baseUrl = (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/');
                if (!string.Equals(currentServerUrl, baseUrl, StringComparison.Ordinal))
                {
                    liveCalls = new LiveCallIndex();
                    skipInitialBatch = true;
                    wsDisableUntilUtc = DateTime.MinValue;
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
                if (allowWebSocket && DateTime.UtcNow >= wsDisableUntilUtc)
                {
                    ClientWebSocket? ws = null;

                    try
                    {
                        ws = await TryOpenWebSocketAsync(baseUrl, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLog.DebugWriteLine(() => $"[CallStreamService] [WebSocket] Connect failed: {ex}");

                        AppLog.Add(() => $"CallStream: WebSocket connect failed. ex={ex.GetType().Name}: {ex.Message}");
                    }

                    if (ws != null)
                    {
                    using var socket = ws;
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

                        await foreach (var item in StreamFromWebSocketWithRecoveryAsync(
                            socket,
                            baseUrl,
                            callsUrl,
                            liveCalls,
                            cancellationToken))
                        {
                            yield return item;
                        }

                        // WebSocket ended; retry after a brief delay.
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        continue;
                    }

                    // Avoid hammering reconnect attempts if WS is not exposed.
                    wsDisableUntilUtc = DateTime.UtcNow.AddSeconds(5);
                }


                List<CallRow>? rows = null;
                string? errorMessage = null;
                var isAuthError = false;
                var pollVersion = LiveCallState.NextVersion();

                try
                {
                    rows = await FetchLatestAsync(callsUrl, cancellationToken);
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
                    errorMessage = ex.Message;
                }
                catch (CallStreamAuthException ex)
                {
                    isAuthError = true;
                    errorMessage = ex.Message;
                    AppLog.DebugWriteLine(() => $"[CallStreamService] [AuthError] {ex}");
                    AppLog.Add(() => $"CallStream: auth error. {ex.Message}");
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
                        rows = await FetchLatestAsync(callsUrl, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Caller requested cancellation (disconnect, shutdown, etc.).
                        yield break;
                    }
                    catch (FirewallAuthenticationException retryAuth)
                    {
                        isAuthError = true;
                        errorMessage = retryAuth.Message;
                    }
                    catch (CallStreamAuthException retryAuth)
                    {
                        isAuthError = true;
                        errorMessage = retryAuth.Message;
                        AppLog.DebugWriteLine(() => $"[CallStreamService] [AuthError] {retryAuth}");
                        AppLog.Add(() => $"CallStream: auth error after retry. {retryAuth.Message}");
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
                        Transcription = transcription,
                        AudioUrl = string.Empty
                    };

                    var delaySeconds = isAuthError ? 15 : 5;
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    continue;
                }

                // If there are no rows, we may still want to signal "connected but idle".
                if (rows == null || rows.Count == 0)
                {
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

                    skipYieldForThisBatch = true;
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

                foreach (var r in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var item = BuildCallItemFromRow(r, baseUrl, liveCalls, pollVersion, skipYieldForThisBatch);

                    if (item != null)
                    {
                        yield return item;
                    }
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
                    page = await FetchLatestAsync(callsUrl, cancellationToken, start, PollBatchSize);
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback: some self-hosted servers ignore JSON paging and require classic form encoding.
                    try
                    {
                        page = await FetchLatestFormAsync(callsUrl, cancellationToken, start, PollBatchSize);
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

            // Only keep calls within the window, then order them oldest to newest.
            var filtered = rows
                .Select(r => new { Row = r, TimeUtc = ParseTimestampUtc(r.StartTime, r.StartTimeUTC) })
                .Where(x => x.TimeUtc >= cutoffUtc)
                .OrderBy(x => x.TimeUtc)
                .Select(x => x.Row)
                .ToList();

            AppLog.Add(() => $"AppleTest: backfill rows={filtered.Count}");

            foreach (var r in filtered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = BuildCallItemFromRow(r, baseUrl, liveCalls, LiveCallState.NextVersion());
                if (item != null)
                    yield return item;
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

        private static DateTimeOffset ParseTimestampUtc(string? startTime, string? startTimeUtc)
        {
            var value = !string.IsNullOrWhiteSpace(startTimeUtc) ? startTimeUtc : startTime;
            if (string.IsNullOrWhiteSpace(value))
                return DateTimeOffset.UtcNow;

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

            return DateTimeOffset.UtcNow;
        }

        public async Task RefreshCallMetadataAsync(IReadOnlyList<CallItem> calls, CancellationToken cancellationToken)
        {
            var baseUrl = (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/');
            var targets = new Dictionary<string, LiveCallState>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in calls)
            {
                if (call.LiveState is { } state && state.ServerUrl == baseUrl)
                    targets.TryAdd(state.Id, state);
            }
            if (targets.Count == 0)
                return;

            var version = LiveCallState.NextVersion();
            var rows = await FetchCatchupRowsAsync(baseUrl + "/calljson", targets.Keys, false, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (baseUrl != (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/'))
                return;
            foreach (var row in rows)
            {
                if (row.DT_RowId?.Trim() is { } id && targets.TryGetValue(id, out var state))
                    state.Observe(ToLiveSnapshot(row), version);
            }
        }

        private CallItem? BuildCallItemFromRow(CallRow r, string baseUrl, LiveCallIndex liveCalls, long version,
            bool skipYieldForThisBatch = false)
        {
            var rawId = r.DT_RowId?.Trim();
            if (string.IsNullOrEmpty(rawId))
                return null;

            if (!liveCalls.MarkSeen(rawId))
                return ObserveExistingCall(r, liveCalls.Find(rawId), version);
            if (skipYieldForThisBatch)
                return null;

            var state = new LiveCallState(baseUrl, rawId);
            state.Observe(ToLiveSnapshot(r), version);
            liveCalls.Track(state);
            var snapshot = state.Read();
            var metadata = snapshot.Metadata;

            // Build audio URL from AudioFilename.
            string audioUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(r.AudioFilename))
            {
                var fileName = r.AudioFilename.Trim();

                // If TR returns a full URL, use it as-is.
                if (fileName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    audioUrl = fileName;
                }
                else
                {
                    // Treat AudioFilename as a relative path from the TR web root.
                    audioUrl = $"{baseUrl}/{fileName.TrimStart('/')}";
                }
            }

            // Never keep credentialed URLs in memory or logs.
            // Playback downloads audio with an Authorization header when needed.
            audioUrl = SanitizeAudioUrl(audioUrl);

            var debugInfo = string.Empty;
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                debugInfo = AppendDebug(debugInfo, "No audio URL from server");
                debugInfo = AppendDebug(debugInfo, "No audio URL built from server data");
            }

            var item = new CallItem
            {
                BackendId = rawId,
                LiveState = state,
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

        private static LiveCallSnapshot ToLiveSnapshot(CallRow row) =>
            row.ToLiveSnapshot(ParseTimestamp(row.StartTime, row.StartTimeUTC), NormalizeCallText(row.CallText));

        private CallItem? ObserveExistingCall(CallRow row, LiveCallState? state, long version)
        {
            if (state == null || !state.Observe(ToLiveSnapshot(row), version, out var transcript))
                return null;

            var item = new CallItem
            {
                BackendId = state.Id,
                LiveState = state,
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
                AudioFilename = CapTextRaw(r.AudioFilename),
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
            bool includeLatestPage, CancellationToken cancellationToken)
        {
            const int pageSize = 200;
            const int maxPages = 5;
            var remaining = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var result = new List<CallRow>();

            for (var page = 0; page < maxPages; page++)
            {
                var rows = await FetchLatestAsync(callsUrl, cancellationToken, page * pageSize, pageSize).ConfigureAwait(false);
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

        private async Task<ClientWebSocket?> TryOpenWebSocketAsync(string baseUrl, CancellationToken cancellationToken)
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
            CancellationToken cancellationToken)
        {
            return await FetchLatestAsync(callsUrl, cancellationToken, 0, PollBatchSize);
        }

        private async Task<List<CallRow>> FetchLatestAsync(
            string callsUrl,
            CancellationToken cancellationToken,
            int start,
            int length)
        {
            // Attempt JSON body first (works reliably on the hosted gateway in our environment).
            try
            {
                var payload = BuildDataTablesPayload(Interlocked.Increment(ref _draw), start, length);

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

                return rows;
            }
            catch (Exception ex) when (ex is CallStreamAuthException or FirewallAuthenticationException or OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fallback: DataTables form encoding.
                return await FetchLatestFormAsync(callsUrl, cancellationToken, start, length);
            }
        }

        private async IAsyncEnumerable<CallItem> StreamFromWebSocketWithRecoveryAsync(
            ClientWebSocket ws, string baseUrl, string callsUrl, LiveCallIndex liveCalls,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = session.Token;
            var buffer = new byte[64 * 1024];
            var receive = ReadWebSocketRowsAsync(ws, buffer, token);
            Task<List<CallRow>>? catchup = null;
            LiveCallState[] targets = [];
            long catchupVersion = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Keep one receive and at most one catch-up request in flight. Prefer live rows
                    // if both are ready, so the older HTTP snapshot can only fill untouched fields.
                    if (catchup == null || receive.IsCompleted || await Task.WhenAny(receive, catchup) == receive)
                    {
                        var (rows, version) = await receive;
                        if (rows == null)
                            yield break;

                        foreach (var row in rows)
                        {
                            token.ThrowIfCancellationRequested();
                            await PersistWebSocketRowAsync(row, baseUrl, token);

                            CallItem? call;
                            if (row.IsUpdateNotification)
                            {
                                var state = row.DT_RowId?.Trim() is { } id ? liveCalls.Find(id) : null;
                                call = ObserveExistingCall(row, state, version);
                                if (state != null)
                                    liveCalls.QueueNotification(state, DateTimeOffset.UtcNow);
                            }
                            else
                            {
                                call = BuildCallItemFromRow(row, baseUrl, liveCalls, version);
                            }

                            if (call != null)
                                yield return call;
                        }

                        receive = ReadWebSocketRowsAsync(ws, buffer, token);
                    }

                    if (catchup is { IsCompleted: true })
                    {
                        foreach (var row in await catchup)
                        {
                            token.ThrowIfCancellationRequested();
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
                    }

                    if (catchup == null && liveCalls.BeginCatchup(DateTimeOffset.UtcNow) is { } scheduled)
                    {
                        targets = scheduled;
                        catchupVersion = LiveCallState.NextVersion();
                        catchup = FetchWebSocketCatchupAsync(callsUrl, targets, token);
                    }
                }
            }
            finally
            {
                session.Cancel();
                await ((Task)receive).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (catchup != null)
                    await ((Task)catchup).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                foreach (var target in targets)
                    target.CompleteNotification();
                liveCalls.ClearNotifications();
            }
        }

        private async Task<List<CallRow>> FetchWebSocketCatchupAsync(
            string callsUrl, LiveCallState[] targets, CancellationToken cancellationToken)
        {
            try
            {
                return await FetchCatchupRowsAsync(callsUrl, targets.Select(state => state.Id),
                    targets.Length == 0, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return [];
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"CallStream: WebSocket metadata catch-up failed ({ex.GetType().Name}).");
                return [];
            }
        }

        private static async Task<(List<CallRow>? Rows, long Version)> ReadWebSocketRowsAsync(
            ClientWebSocket ws, byte[] buffer, CancellationToken cancellationToken)
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

private static string SanitizeAudioUrl(string audioUrl)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
                return string.Empty;

            try
            {
                if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri))
                    return audioUrl;

                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var builder = new UriBuilder(uri)
                    {
                        UserName = string.Empty,
                        Password = string.Empty
                    };
                    return builder.Uri.ToString();
                }

                return audioUrl;
            }
            catch
            {
                return audioUrl;
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
        private static DataTablesPayload BuildDataTablesPayload(int draw, int start, int length)
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
                    Search = new DataTablesSearch { Value = string.Empty, Regex = false }
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
