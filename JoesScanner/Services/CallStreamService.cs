using JoesScanner.Models;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using JoesScanner.Data;

namespace JoesScanner.Services
{
    // Polls the Trunking Recorder calljson endpoint and streams CallItem objects
    // to the UI, using server fields for time, receiver, site, talkgroup, transcription, and audio.
    public class CallStreamService : ICallStreamService
    {
        private const string ServiceAuthUsername = "secappass";
        private const string ServiceAuthPassword = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

        private const string AppleIosTestAccountEmail = "iostest@joesscanner.com";
        private const string AppleIosTestAccountEmailLegacy = "iostest@jeosscanner.com";

        private readonly ISettingsService _settingsService;
        private readonly ILocalCallsRepository _localCallsRepository;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        // Mirrors DEFAULT_CONFIG["poll_batch"] from the server.
        private const int PollBatchSize = 25;

        // When true, the next GetCallStreamAsync run should preserve the IDs learned
        // during a warm start so we do not re-emit the same recent calls.
        private volatile bool _preserveSeenIdsForNextConnect;

        // Tracks which call IDs have already been surfaced to the UI.
        private readonly HashSet<string> _seenIds = new();

        // Tracks call IDs that were initially missing transcription so we can
        // detect when the server later sends an updated row with text.
        private readonly HashSet<string> _idsMissingTranscription = new();

        // DataTables draw counter used by the calljson endpoint.
        private int _draw = 1;

        // Creates a new call stream service with an HttpClient configured for the calljson endpoint.
        public CallStreamService(ISettingsService settingsService, ILocalCallsRepository localCallsRepository)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _localCallsRepository = localCallsRepository ?? throw new ArgumentNullException(nameof(localCallsRepository));

            // IMPORTANT (Apple platforms + HTTP custom servers)
            // iOS and MacCatalyst can still surface NSURLErrorDomain -1022 (ATS) for cleartext HTTP
            // even when Info.plist contains NSAllowsArbitraryLoads, depending on handler/runtime.
            // For custom (non-HTTPS) Trunking Recorder endpoints, we want a stable long-term path.
            // Using SocketsHttpHandler forces a managed stack that bypasses NSURLSession/ATS.
#if IOS || MACCATALYST
            var socketsHandler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(socketsHandler)
            {
                // Fail fast on non-responsive servers so the UI shows an error row.
                Timeout = TimeSpan.FromSeconds(5)
            };
#else
            _httpClient = new HttpClient
            {
                // Fail fast on non-responsive servers so the UI shows an error row.
                Timeout = TimeSpan.FromSeconds(5)
            };
#endif
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
            var payload = BuildDataTablesPayload(_draw, start, length);
            _draw++;

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

            // Ensure auth header matches current settings for this request.
            UpdateAuthorizationHeader();

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
        public async IAsyncEnumerable<CallItem> GetCallStreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var isAppleTestAccount = IsAppleIosTestAccount();
            AppLog.Add(() => $"AppleTest: enabled={isAppleTestAccount}, user={_settingsService.BasicAuthUsername}");

            // New connection: by default clear any previously seen IDs and suppress the first batch
            // so we only show calls that arrive after the user connects.
            // If a warm start was performed immediately before this stream begins, preserve the
            // warm-start IDs so we do not duplicate the initial backfilled calls.
            var skipInitialBatch = true;

            if (_preserveSeenIdsForNextConnect)
            {
                _preserveSeenIdsForNextConnect = false;
                skipInitialBatch = false;
            }
            else
            {
                _seenIds.Clear();
                _idsMissingTranscription.Clear();
            }

            // Apple review test account behavior.
            // For this one account only, backfill several hours of calls so playback can
            // start immediately even if there are no brand new calls arriving.
            if (isAppleTestAccount)
            {
                skipInitialBatch = false;
                await foreach (var backfilled in BackfillRecentHoursAsync(cancellationToken))
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
                var username = _settingsService.BasicAuthUsername;
                var usernameForLog = string.IsNullOrWhiteSpace(username) ? "(none)" : username;

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
                        Debug.WriteLine($"[CallStreamService] [WebSocket] Connect failed: {ex}");
                    }

                    if (ws != null)
                    {
                        // We are live; do not skip later polling batches as "initial backlog."
                        skipInitialBatch = false;

                        if (!hasReportedIdleConnection)
                        {
                            hasReportedIdleConnection = true;

                            AppLog.Add(() => $"CallStream: WEBSOCKET CONNECTED. [ServerUrl={baseUrl}, Path=/Calls, Username={usernameForLog}]");

                            yield return new CallItem
                            {
                                Timestamp = DateTime.Now,
                                Talkgroup = "HEARTBEAT",
                                Transcription = "Connected (WebSocket); waiting for calls.",
                                AudioUrl = string.Empty
                            };
                        }

                        await foreach (var item in StreamFromWebSocketWithRecoveryAsync(
                            ws,
                            baseUrl,
                            callsUrl,
                            usernameForLog,
                            isAppleTestAccount,
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

                try
                {
                    rows = await FetchLatestAsync(callsUrl, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller requested cancellation (disconnect, shutdown, etc.).
                    Debug.WriteLine("[CallStreamService] Streaming cancelled by caller.");
                    yield break;
                }
                catch (OperationCanceledException ex)
                {
                    // Most likely an HttpClient timeout or similar; treat as recoverable.
                    errorMessage = "The connection to the audio server timed out. The app will retry automatically.";
                    Debug.WriteLine($"[CallStreamService] [Timeout] {ex}");
                }
                catch (CallStreamAuthException ex)
                {
                    isAuthError = true;
                    errorMessage = ex.Message;
                    Debug.WriteLine($"[CallStreamService] [AuthError] {ex}");
                }
                catch (HttpRequestException ex)
                {
                    // Retry once on transient transport failures (for example: "Socket closed").
                    Debug.WriteLine($"[CallStreamService] [HttpError] {ex}");

                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                        rows = await FetchLatestAsync(callsUrl, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Caller requested cancellation (disconnect, shutdown, etc.).
                        yield break;
                    }
                    catch (CallStreamAuthException retryAuth)
                    {
                        isAuthError = true;
                        errorMessage = retryAuth.Message;
                        Debug.WriteLine($"[CallStreamService] [AuthError] {retryAuth}");
                    }
                    catch (Exception retryEx)
                    {
                        errorMessage = BuildTransportErrorMessage(retryEx);
                        Debug.WriteLine($"[CallStreamService] [HttpRetryFailed] {retryEx}");
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = BuildTransportErrorMessage(ex);
                    Debug.WriteLine($"[CallStreamService] [UnexpectedError] {ex}");
                }

                // Surface any error from the poll as a synthetic CallItem row.
                if (errorMessage != null)
                {
                    var talkgroup = isAuthError ? "AUTH" : "ERROR";

                    var transcription = isAuthError
                        ? "Authentication to the audio server failed. " +
                          "Verify the basic auth username/password and any firewall authentication."
                        : $"Error talking to {callsUrl}: {errorMessage}";

                    AppLog.Add(() => $"CallStream: {talkgroup} - {transcription} [ServerUrl={callsUrl}, Username={usernameForLog}]");

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
                            $"[ServerUrl={baseUrl}/calljson, Username={usernameForLog}]");

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
                            $"[ServerUrl={baseUrl}/calljson, Username={usernameForLog}]");

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

                    var item = BuildCallItemFromRow(r, baseUrl, skipYieldForThisBatch);

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

        private async IAsyncEnumerable<CallItem> BackfillRecentHoursAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var baseUrl = (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/');
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

                var item = BuildCallItemFromRow(r, baseUrl, skipYieldForThisBatch: false);
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

        public async Task<string?> TryFetchTranscriptionByIdAsync(string backendId, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backendId))
                    return null;

                var baseUrl = (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/');
                if (string.IsNullOrWhiteSpace(baseUrl))
                    return null;

                var callsUrl = baseUrl + "/calljson";

                var row = await FetchCallByIdAsync(callsUrl, backendId.Trim(), cancellationToken);
                if (row == null)
                    return null;

                var normalized = NormalizeCallText(row.CallText);
                return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }


        // Builds a CallItem from a calljson row (or a WebSocket "full row" message).
        // Returns null when the row is neither new nor a transcription update from the client's perspective.
        private CallItem? BuildCallItemFromRow(CallRow r, string baseUrl, bool skipYieldForThisBatch)
        {
            var rawId = r.DT_RowId?.Trim();
            if (string.IsNullOrEmpty(rawId))
                return null;

            // Determine whether this row is new or an update for an existing call.
            var isNew = !_seenIds.Contains(rawId);
            var wasMissingTranscription = _idsMissingTranscription.Contains(rawId);

            if (isNew)
            {
                _seenIds.Add(rawId);
            }

            // Start with no debug message for this call.
            var debugInfo = string.Empty;

            // Transcription text from the server, may be empty if there is no transcription.
            var text = NormalizeCallText(r.CallText);

            if (string.IsNullOrWhiteSpace(text))
            {
                // Server did not send any transcription text for this call.
                debugInfo = AppendDebug(debugInfo, "No transcription from server");

                // For brand new calls with no text, remember that we are waiting
                // for a transcription update on this ID.
                if (isNew)
                {
                    _idsMissingTranscription.Add(rawId);
                }
            }
            else
            {
                // We now have transcription text; if we were previously waiting,
                // stop tracking this ID as missing transcription.
                if (wasMissingTranscription)
                {
                    _idsMissingTranscription.Remove(rawId);
                }
            }

            // Timestamp from StartTime / StartTimeUTC.
            var timestamp = ParseTimestamp(r.StartTime, r.StartTimeUTC);

            // Talkgroup from label or ID.
            var talkgroup = !string.IsNullOrWhiteSpace(r.TargetLabel)
                ? r.TargetLabel
                : r.TargetID ?? string.Empty;

            // Source radio ID / label.
            var source = !string.IsNullOrWhiteSpace(r.SourceLabel)
                ? r.SourceLabel
                : r.SourceID ?? string.Empty;

            // Site name from SiteLabel / SiteID.
            var site = !string.IsNullOrWhiteSpace(r.SiteLabel)
                ? r.SiteLabel
                : r.SiteID ?? string.Empty;

            // Voice receiver name.
            var voiceReceiver = r.VoiceReceiver?.Trim() ?? string.Empty;

            // Call duration in seconds, if the server provides CallDuration.
            double durationSeconds = 0;
            if (!string.IsNullOrWhiteSpace(r.CallDuration))
            {
                if (double.TryParse(r.CallDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                {
                    durationSeconds = dur;
                }
            }

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

            if (isNew && string.IsNullOrWhiteSpace(audioUrl))
            {
                debugInfo = AppendDebug(debugInfo, "No audio URL from server");
                debugInfo = AppendDebug(debugInfo, "No audio URL built from server data");
            }

            // Decide whether this row should be yielded:
            // - Brand new call: always yield (subject to initial backlog skip)
            // - Existing ID that just transitioned from "no text" to "has text":
            //   yield as a transcription update
            // - Existing ID with no text or no change: skip
            var isTranscriptionUpdate =
                !isNew &&
                wasMissingTranscription &&
                !string.IsNullOrWhiteSpace(text);

            if (!isNew && !isTranscriptionUpdate)
            {
                return null;
            }

            // Do not surface prior calls in the UI on the initial batch.
            if (skipYieldForThisBatch && isNew)
            {
                return null;
            }

            return new CallItem
            {
                BackendId = rawId,
                IsTranscriptionUpdate = isTranscriptionUpdate,
                Timestamp = timestamp,
                CallDurationSeconds = durationSeconds,
                Talkgroup = talkgroup,
                Source = source,
                Site = site,
                VoiceReceiver = voiceReceiver,
                Transcription = text,
                AudioUrl = audioUrl,
                DebugInfo = debugInfo
            };
        }

        // Normalizes incoming transcription text so a single oversized payload cannot stall the UI.
        // We intentionally keep this inexpensive: whitespace normalization plus a hard input cap.
        private static string NormalizeCallText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var s = text.Trim();

            const int maxIncomingChars = 20000;
            if (s.Length > maxIncomingChars)
                s = s.Substring(0, maxIncomingChars);

            var sb = new StringBuilder(s.Length);
            var lastWasSpace = true; // trim leading whitespace
            foreach (var ch in s)
            {
                var c = ch;
                if (c == '\n' || c == '\n' || c == '\t')
                    c = ' ';

                if (char.IsWhiteSpace(c))
                {
                    if (lastWasSpace)
                        continue;

                    sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                sb.Append(c);
                lastWasSpace = false;
            }

            return sb.ToString().Trim();
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

        private async Task<CallRow?> FetchCallByIdAsync(string callsUrl, string callId, CancellationToken cancellationToken)
        {
            const int pageSize = 200;
            const int maxPages = 5; // up to 1000 rows

            for (var page = 0; page < maxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var start = page * pageSize;
                var rows = await FetchLatestAsync(callsUrl, cancellationToken, start, pageSize);

                if (rows.Count == 0)
                    return null;

                foreach (var r in rows)
                {
                    if (string.Equals(r.DT_RowId?.Trim(), callId, StringComparison.OrdinalIgnoreCase))
                    {
                        return r;
                    }
                }

                if (rows.Count < pageSize)
                    return null;
            }

            return null;
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

            // Apply the same basic auth policy used by calljson.
            var authHeader = BuildBasicAuthHeaderValue(baseUri);
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                ws.Options.SetRequestHeader("Authorization", authHeader);
            }

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                await ws.ConnectAsync(wsUri, connectCts.Token);

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

        private string? BuildBasicAuthHeaderValue(Uri serverUri)
        {
            try
            {
                // Hosted Joe's Scanner server: Trunking Recorder endpoints use a service account,
                // but ONLY after the user has a currently valid authorization via the Auth API.
                if (string.Equals(serverUri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase))
                {
                    if (_settingsService.SubscriptionTierLevel < 1)
                    {
                        AppLog.Add(() => "CallStream: WS auth not applied (hosted). reason=tier<1");
                        return null;
                    }

                    var ok = _settingsService.SubscriptionLastStatusOk;
                    var expires = _settingsService.SubscriptionExpiresUtc;

                    if (!ok)
                    {
                        AppLog.Add(() => "CallStream: WS auth not applied (hosted). reason=sub_status_not_ok");
                        return null;
                    }

                    if (!expires.HasValue)
                    {
                        AppLog.Add(() => "CallStream: WS auth not applied (hosted). reason=no_expiry");
                        return null;
                    }

                    if (expires.Value.ToUniversalTime() <= DateTime.UtcNow)
                    {
                        AppLog.Add(() => $"CallStream: WS auth not applied (hosted). reason=expired expiresUtc={expires.Value:o}");
                        return null;
                    }

                    // NOTE: These are the hard-coded service credentials for app.joesscanner.com and must not be changed.
                    const string serviceUser = "secappass";
                    const string servicePass = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";
                    var serviceToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{serviceUser}:{servicePass}"));
                    AppLog.Add(() => "CallStream: WS auth applied (hosted service creds).");
                    return $"Basic {serviceToken}";
                }

                // Custom servers: only apply Basic Auth if user provided a username.
                var user = (_settingsService.BasicAuthUsername ?? string.Empty).Trim();
                var pass = (_settingsService.BasicAuthPassword ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(user))
                {
                    AppLog.Add(() => "CallStream: WS auth not applied (custom). reason=username_blank");
                    return null;
                }

                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
                AppLog.Add(() => "CallStream: WS auth applied (custom creds).");
                return $"Basic {token}";
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"CallStream: BuildBasicAuthHeaderValue failed. ex={ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }


        private void UpdateAuthorizationHeader()
        {
            // This header is for Trunking Recorder endpoints (calljson, audio download, WS).
            // Hosted default server uses the service account. Custom server uses user-provided Basic Auth if set.
            try
            {
                var baseUrl = (_settingsService.ServerUrl ?? string.Empty).Trim();
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                {
                    _httpClient.DefaultRequestHeaders.Authorization = null;
                    return;
                }

                var header = BuildBasicAuthHeaderValue(uri);
                if (string.IsNullOrWhiteSpace(header))
                {
                    _httpClient.DefaultRequestHeaders.Authorization = null;
                    return;
                }

                // header format: "Basic <token>"
                var token = header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                    ? header.Substring("Basic ".Length).Trim()
                    : header.Trim();

                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
            catch
            {
                _httpClient.DefaultRequestHeaders.Authorization = null;
            }
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
                var payload = BuildDataTablesPayload(_draw, start, length);
                _draw++;

                UpdateAuthorizationHeader();

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
            catch (CallStreamAuthException)
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
            ClientWebSocket ws,
            string baseUrl,
            string callsUrl,
            string usernameForLog,
            bool isAppleTestAccount,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Minimal WS loop: parse message payloads into CallRow and reuse BuildCallItemFromRow.
            // On some servers, the WS feed may not include transcription updates, so we periodically
            // poll calljson for any IDs we are still tracking as missing transcription.
            var buffer = new byte[64 * 1024];

            var nextTranscriptionCatchupUtc = DateTime.UtcNow.AddSeconds(2);

            while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                        }
                        catch
                        {
                        }

                        yield break;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    List<CallItem> toYield;

                    try
                    {
                        toYield = new List<CallItem>();

                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;

                        if (root.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in root.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Object)
                                    continue;

                                var row = ParseRow(item);

                                await PersistWebSocketRowAsync(row, baseUrl, cancellationToken);

                                if (!row.IsUpdateNotification)
                                {
                                    var call = BuildCallItemFromRow(row, baseUrl, skipYieldForThisBatch: false);
                                    if (call != null)
                                        toYield.Add(call);
                                }
                            }
                        }
                        else if (root.ValueKind == JsonValueKind.Object)
                        {
                            // Some feeds wrap rows under a property (for example: "data").
                            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in data.EnumerateArray())
                                {
                                    if (item.ValueKind != JsonValueKind.Object)
                                        continue;

                                    var row = ParseRow(item);

                                    await PersistWebSocketRowAsync(row, baseUrl, cancellationToken);

                                    if (!row.IsUpdateNotification)
                                    {
                                        var call = BuildCallItemFromRow(row, baseUrl, skipYieldForThisBatch: false);
                                        if (call != null)
                                            toYield.Add(call);
                                    }
                                }
                            }
                            else
                            {
                                var row = ParseRow(root);

                                await PersistWebSocketRowAsync(row, baseUrl, cancellationToken);

                                if (!row.IsUpdateNotification)
                                {
                                    var call = BuildCallItemFromRow(row, baseUrl, skipYieldForThisBatch: false);
                                    if (call != null)
                                        toYield.Add(call);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CallStreamService] [WebSocket] Parse error: {ex}");
                        // Break out so outer loop can fall back/reconnect.
                        yield break;
                    }

                    foreach (var call in toYield)
                        yield return call;
                }

                // Transcription catch-up: if WS doesn't deliver the calltext update, polling calljson will.
                // Keep this lightweight: only do work when we still have IDs in the missing set.
                if (_idsMissingTranscription.Count > 0 && DateTime.UtcNow >= nextTranscriptionCatchupUtc)
                {
                    nextTranscriptionCatchupUtc = DateTime.UtcNow.AddSeconds(2);

                    List<CallItem>? catchupYield = null;

                    try
                    {
                        // Grab the newest page and let BuildCallItemFromRow decide if any rows now qualify
                        // as transcription updates for IDs we were waiting on.
                        var rows = await FetchLatestAsync(callsUrl, cancellationToken, start: 0, length: 200);
                        catchupYield = new List<CallItem>();

                        foreach (var r in rows)
                        {
                            var call = BuildCallItemFromRow(r, baseUrl, skipYieldForThisBatch: false);
                            if (call != null && call.IsTranscriptionUpdate)
                                catchupYield.Add(call);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CallStreamService] [WebSocket] Transcription catch-up failed: {ex.Message}");
                    }

                    if (catchupYield != null)
                    {
                        foreach (var call in catchupYield)
                            yield return call;
                    }
                }
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
            return new CallRow
            {
                DT_RowId = GetString(e, "DT_RowId"),
                IsUpdateNotification = IsTruthy(GetString(e, "Update")),
                StartTime = GetString(e, "StartTime"),
                StartTimeUTC = GetString(e, "StartTimeUTC"),
                Time = GetString(e, "Time"),
                Date = GetString(e, "Date"),
                TargetID = GetString(e, "TargetID"),
                TargetLabel = GetString(e, "TargetLabel"),
                TargetTag = GetString(e, "TargetTag"),
                SourceID = GetString(e, "SourceID"),
                SourceLabel = GetString(e, "SourceLabel"),
                SourceTag = GetString(e, "SourceTag"),
                LCN = GetString(e, "LCN"),
                Frequency = GetString(e, "Frequency"),
                CallAudioType = GetString(e, "CallAudioType"),
                SystemID = GetString(e, "SystemID"),
                SystemLabel = GetString(e, "SystemLabel"),
                SystemType = GetString(e, "SystemType"),
                AudioStartPos = GetString(e, "AudioStartPos"),
                SiteID = GetString(e, "SiteID"),
                SiteLabel = GetString(e, "SiteLabel"),
                VoiceReceiver = GetString(e, "VoiceReceiver"),
                CallType = GetString(e, "CallType"),
                CallText = GetString(e, "CallText"),
                AudioFilename = GetString(e, "AudioFilename"),
                CallDuration = GetString(e, "CallDuration")
            };
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
        private static DateTime ParseTimestamp(string? startTime, string? startTimeUtc)
        {
            var value = !string.IsNullOrWhiteSpace(startTimeUtc) ? startTimeUtc : startTime;

            if (string.IsNullOrWhiteSpace(value))
                return DateTime.Now;

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

            return DateTime.Now;
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
        private sealed class CallRow
        {
            public string? DT_RowId { get; set; }
            public bool IsUpdateNotification { get; set; }
            public string? StartTime { get; set; }
            public string? StartTimeUTC { get; set; }
            public string? Time { get; set; }
            public string? Date { get; set; }
            public string? TargetID { get; set; }
            public string? TargetLabel { get; set; }
            public string? TargetTag { get; set; }
            public string? SourceID { get; set; }
            public string? SourceLabel { get; set; }
            public string? SourceTag { get; set; }
            public string? LCN { get; set; }
            public string? Frequency { get; set; }
            public string? CallAudioType { get; set; }
            public string? SystemID { get; set; }
            public string? SystemLabel { get; set; }
            public string? SystemType { get; set; }
            public string? AudioStartPos { get; set; }
            public string? SiteID { get; set; }
            public string? SiteLabel { get; set; }
            public string? VoiceReceiver { get; set; }
            public string? CallType { get; set; }
            public string? CallText { get; set; }
            public string? AudioFilename { get; set; }
            public string? CallDuration { get; set; }
        }
    }
}
