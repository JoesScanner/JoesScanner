using JoesScanner.Models;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoesScanner.Services
{
    // Polls the Trunking Recorder calljson endpoint and streams CallItem objects
    // to the UI, using server fields for time, receiver, site, talkgroup, transcription, and audio.
    public class CallStreamService : ICallStreamService
    {
        private readonly ISettingsService _settingsService;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        // Mirrors DEFAULT_CONFIG["poll_batch"] from the server.
        private const int PollBatchSize = 25;

        private const string ServiceAuthUsername = "secappass";
        private const string ServiceAuthPassword = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

        // Tracks which call IDs have already been surfaced to the UI.
        private readonly HashSet<string> _seenIds = new();

        // Tracks call IDs that were initially missing transcription so we can
        // detect when the server later sends an updated row with text.
        private readonly HashSet<string> _idsMissingTranscription = new();

        // DataTables draw counter used by the calljson endpoint.
        private int _draw = 1;

        // Creates a new call stream service with an HttpClient configured for the calljson endpoint.
        public CallStreamService(ISettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            _httpClient = new HttpClient
            {
                // Fail fast on non-responsive servers so the UI shows an error row.
                Timeout = TimeSpan.FromSeconds(5)
            };
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

        // Continuously polls the server and yields new calls as CallItem objects.
        // Skips initial backlog so only new calls after connection are surfaced.
        public async IAsyncEnumerable<CallItem> GetCallStreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // New connection: clear any previously seen IDs and suppress the first batch
            // so we only show calls that arrive after the user connects.
            _seenIds.Clear();
            _idsMissingTranscription.Clear();

            // We still skip the first non-empty batch so we do not backfill history.
            var skipInitialBatch = true;

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
                    AppLog.Add("CallStream: No Server URL configured in settings. Username is not applicable.");

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

                // Prefer the Trunking Recorder WebSocket feed when available.
                // This reduces polling load and gives us call-level update notifications.
                if (DateTime.UtcNow >= wsDisableUntilUtc)
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

                            AppLog.Add(
                                $"CallStream: WEBSOCKET CONNECTED. [ServerUrl={baseUrl}, Path=/Calls, Username={usernameForLog}]");

                            yield return new CallItem
                            {
                                Timestamp = DateTime.Now,
                                Talkgroup = "HEARTBEAT",
                                Transcription = "Connected (WebSocket); waiting for calls.",
                                AudioUrl = string.Empty
                            };
                        }

                        await foreach (var item in StreamFromWebSocketAsync(ws, baseUrl, callsUrl, cancellationToken))
                        {
                            yield return item;
                        }

                        // WebSocket ended; retry after a brief delay.
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        continue;
                    }

                    // Avoid hammering reconnect attempts if WS is not exposed.
                    wsDisableUntilUtc = DateTime.UtcNow.AddSeconds(30);
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
                        errorMessage = ex.Message;
                        Debug.WriteLine($"[CallStreamService] [HttpRetryFailed] {retryEx}");
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
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

                    AppLog.Add(
                        $"CallStream: {talkgroup} - {transcription} [ServerUrl={callsUrl}, Username={usernameForLog}]");

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

                        AppLog.Add(
                            $"CallStream: HEARTBEAT - connected to server, waiting for calls. " +
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

                        AppLog.Add(
                            $"CallStream: HEARTBEAT - connected to server, waiting for calls. " +
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

            if (string.IsNullOrWhiteSpace(audioUrl))
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
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            // Apply the same basic auth policy used by calljson.
            var authHeader = BuildBasicAuthHeaderValue(baseUri);
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                ws.Options.SetRequestHeader("Authorization", authHeader);
            }

            try
            {
                await ws.ConnectAsync(wsUri, cancellationToken);

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
                var isJoesScannerHost = serverUri.Host.EndsWith("app.joesscanner.com", StringComparison.OrdinalIgnoreCase);

                string username;
                string password;

                if (isJoesScannerHost)
                {
                    // Joe's Scanner hosted servers use the hard coded service account.
                    username = ServiceAuthUsername;
                    password = ServiceAuthPassword;
                }
                else
                {
                    // Custom servers use user configured basic auth credentials, if any.
                    username = _settingsService.BasicAuthUsername ?? string.Empty;
                    password = _settingsService.BasicAuthPassword ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                        return null;
                }

                var raw = $"{username}:{password}";
                var bytes = Encoding.ASCII.GetBytes(raw);
                var base64 = Convert.ToBase64String(bytes);
                return $"Basic {base64}";
            }
            catch
            {
                return null;
            }
        }

        private async IAsyncEnumerable<CallItem> StreamFromWebSocketAsync(
            ClientWebSocket ws,
            string baseUrl,
            string callsUrl,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var buffer = new byte[32 * 1024];
            var segment = new ArraySegment<byte>(buffer);
            var sb = new StringBuilder(64 * 1024);

            try
            {
                while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    sb.Clear();

                    WebSocketReceiveResult result;

                    do
                    {
                        result = await ws.ReceiveAsync(segment, cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            try
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                            }
                            catch
                            {
                            }

                            yield break;
                        }

                        if (result.Count > 0)
                        {
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        }
                    }
                    while (!result.EndOfMessage);

                    var message = sb.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(message))
                        continue;

                    JsonDocument? doc = null;

                    try
                    {
                        doc = JsonDocument.Parse(message);
                    }
                    catch
                    {
                        // Ignore malformed frames.
                        continue;
                    }

                    using (doc)
                    {
                        var root = doc.RootElement;

                        if (root.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var element in root.EnumerateArray())
                            {
                                await foreach (var item in ProcessWebSocketElementAsync(element, baseUrl, callsUrl, cancellationToken))
                                {
                                    yield return item;
                                }
                            }
                        }
                        else if (root.ValueKind == JsonValueKind.Object)
                        {
                            await foreach (var item in ProcessWebSocketElementAsync(root, baseUrl, callsUrl, cancellationToken))
                            {
                                yield return item;
                            }
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    ws.Dispose();
                }
                catch
                {
                }
            }
        }

        private async IAsyncEnumerable<CallItem> ProcessWebSocketElementAsync(
            JsonElement element,
            string baseUrl,
            string callsUrl,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Update notifications: {"Update": true, "DT_RowId": 123}
            var isUpdate = false;

            if (TryGetBoolean(element, "Update", out var updateFlag))
            {
                isUpdate = updateFlag;
            }

            var callId = GetString(element, "DT_RowId") ?? GetString(element, "DT_RowID");

            if (isUpdate)
            {
                if (string.IsNullOrWhiteSpace(callId))
                    yield break;

                // Only refresh calls that we have already surfaced to the UI.
                if (!_seenIds.Contains(callId.Trim()))
                    yield break;

                var row = await FetchCallByIdAsync(callsUrl, callId.Trim(), cancellationToken);
                if (row == null)
                    yield break;

                var updated = BuildCallItemFromRow(row, baseUrl, skipYieldForThisBatch: false);
                if (updated != null && updated.IsTranscriptionUpdate)
                {
                    yield return updated;
                }

                yield break;
            }

            // Full row messages: treat as a normal row insert.
            var parsed = ParseRow(element);
            var item = BuildCallItemFromRow(parsed, baseUrl, skipYieldForThisBatch: false);

            if (item != null && !item.IsTranscriptionUpdate)
            {
                yield return item;
            }
        }

        private static bool TryGetBoolean(JsonElement obj, string name, out bool value)
        {
            value = false;

            if (obj.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True)
                {
                    value = true;
                    return true;
                }

                if (prop.ValueKind == JsonValueKind.False)
                {
                    value = false;
                    return true;
                }

                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i))
                {
                    value = i != 0;
                    return true;
                }

                if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var b))
                {
                    value = b;
                    return true;
                }
            }

            // Case-insensitive fallback
            foreach (var p in obj.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var v = p.Value;

                if (v.ValueKind == JsonValueKind.True)
                {
                    value = true;
                    return true;
                }

                if (v.ValueKind == JsonValueKind.False)
                {
                    value = false;
                    return true;
                }

                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ii))
                {
                    value = ii != 0;
                    return true;
                }

                if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var bb))
                {
                    value = bb;
                    return true;
                }
            }

            return false;
        }




        private static string SanitizeAudioUrl(string audioUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(audioUrl))
                    return audioUrl;

                if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri))
                    return audioUrl;

                // Always strip embedded credentials.
                var builder = new UriBuilder(uri)
                {
                    UserName = string.Empty,
                    Password = string.Empty
                };

                return builder.Uri.ToString();
            }
            catch
            {
                return audioUrl;
            }
        }

        // Embeds basic auth credentials into the audio URL when basic auth is configured
        // and the audio URL host matches the configured server.
        private string ApplyBasicAuthToAudioUrl(string audioUrl, string baseUrl)
        {
            try
            {
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                    return audioUrl;

                if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri))
                    return audioUrl;

                // Only embed credentials if this audio URL is on the same scheme/host as the configured server.
                if (!string.Equals(baseUri.Host, audioUri.Host, StringComparison.OrdinalIgnoreCase))
                    return audioUrl;

                if (!string.Equals(baseUri.Scheme, audioUri.Scheme, StringComparison.OrdinalIgnoreCase))
                    return audioUrl;

                // Choose which credentials to use:
                // - For Joe's Scanner hosted servers (joesscanner.com), use the hard coded service account.
                // - For custom servers, use the user configured basic auth credentials.
                var isJoesScannerHost = baseUri.Host.EndsWith("app.joesscanner.com", StringComparison.OrdinalIgnoreCase);

                string username;
                string password;

                if (isJoesScannerHost)
                {
                    username = ServiceAuthUsername;
                    password = ServiceAuthPassword;
                }
                else
                {
                    username = _settingsService.BasicAuthUsername;
                    password = _settingsService.BasicAuthPassword ?? string.Empty;
                }

                // Only do anything if we have a username.
                if (string.IsNullOrWhiteSpace(username))
                    return audioUrl;

                var userEscaped = Uri.EscapeDataString(username);
                var passEscaped = Uri.EscapeDataString(password);

                // Authority already contains "host[:port]" but no user info.
                var authority = audioUri.Authority;

                var builder = new StringBuilder();
                builder.Append(audioUri.Scheme);
                builder.Append("://");
                builder.Append(userEscaped);
                builder.Append(':');
                builder.Append(passEscaped);
                builder.Append('@');
                builder.Append(authority);
                builder.Append(audioUri.PathAndQuery);
                builder.Append(audioUri.Fragment);

                return builder.ToString();
            }
            catch
            {
                // On any parsing or edge-case failure, fall back to the original URL so
                // we never break unprotected/custom servers.
                return audioUrl;
            }
        }

        // Calls the calljson endpoint with the expected DataTables payload and returns parsed rows.
        private async Task<List<CallRow>> FetchLatestAsync(string callsUrl, CancellationToken cancellationToken)
        {
            return await FetchLatestAsync(callsUrl, cancellationToken, start: 0, length: PollBatchSize);
        }

        private async Task<List<CallRow>> FetchLatestAsync(
            string callsUrl,
            CancellationToken cancellationToken,
            int start,
            int length)
        {
            var payload = BuildDataTablesPayload(_draw, start, length);
            _draw++;

            var jsonPayload = JsonSerializer.Serialize(payload, _jsonOptions);

            // Ensure auth header matches current settings for this request.
            UpdateAuthorizationHeader();

            // Python uses data=body_json with form content type.
            // Here we send the JSON string as the body with form content type,
            // which matches what has been working in your environment.
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await _httpClient.PostAsync(callsUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                var statusInt = (int)statusCode;
                string message;

                if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
                {
                    message =
                        $"Authentication failed when calling {callsUrl} (HTTP {statusInt} {response.ReasonPhrase}). " +
                        "Check basic auth username/password and any firewall auth configuration.";

                    Debug.WriteLine($"[CallStreamService] [AuthError] {message}");
                    throw new CallStreamAuthException(message);
                }

                if (statusCode == HttpStatusCode.NotFound)
                {
                    message =
                        $"The calljson endpoint was not found at {callsUrl} (HTTP {statusInt} {response.ReasonPhrase}). " +
                        "Verify the Trunking Recorder base URL and path.";
                    Debug.WriteLine($"[CallStreamService] [ServerError] {message}");
                }
                else
                {
                    message =
                        $"Server returned HTTP {statusInt} {response.ReasonPhrase} when calling {callsUrl}.";
                    Debug.WriteLine($"[CallStreamService] [ServerError] {message}");
                }

                throw new HttpRequestException(message);
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

        // Updates the HttpClient Authorization header based on the current basic auth settings.
        private void UpdateAuthorizationHeader()
        {
            // Clear any previous header first.
            _httpClient.DefaultRequestHeaders.Authorization = null;

            var serverUrl = _settingsService.ServerUrl ?? string.Empty;
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
                return;

            var isJoesScannerHost = serverUri.Host.EndsWith("app.joesscanner.com", StringComparison.OrdinalIgnoreCase);

            string username;
            string password;

            if (isJoesScannerHost)
            {
                // Joe's Scanner hosted servers use the hard coded service account.
                username = ServiceAuthUsername;
                password = ServiceAuthPassword;
            }
            else
            {
                // Custom servers use user configured basic auth credentials, if any.
                username = _settingsService.BasicAuthUsername;
                password = _settingsService.BasicAuthPassword ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(username))
                return;

            var raw = $"{username}:{password}";
            var bytes = Encoding.ASCII.GetBytes(raw);
            var base64 = Convert.ToBase64String(bytes);

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", base64);
        }

        // Maps a JSON row from the DataTables response into a CallRow object.
        private static CallRow ParseRow(JsonElement e)
        {
            return new CallRow
            {
                DT_RowId = GetString(e, "DT_RowId"),
                StartTime = GetString(e, "StartTime"),
                StartTimeUTC = GetString(e, "StartTimeUTC"),
                TargetID = GetString(e, "TargetID"),
                TargetLabel = GetString(e, "TargetLabel"),
                SourceID = GetString(e, "SourceID"),
                SourceLabel = GetString(e, "SourceLabel"),
                SiteID = GetString(e, "SiteID"),
                SiteLabel = GetString(e, "SiteLabel"),
                VoiceReceiver = GetString(e, "VoiceReceiver"),
                CallText = GetString(e, "CallText"),
                AudioFilename = GetString(e, "AudioFilename"),
                CallDuration = GetString(e, "CallDuration")
            };
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

        // Internal representation of a single call row from the calljson endpoint.
        private sealed class CallRow
        {
            public string? DT_RowId { get; set; }
            public string? StartTime { get; set; }
            public string? StartTimeUTC { get; set; }
            public string? TargetID { get; set; }
            public string? TargetLabel { get; set; }
            public string? SourceID { get; set; }
            public string? SourceLabel { get; set; }
            public string? SiteID { get; set; }
            public string? SiteLabel { get; set; }
            public string? VoiceReceiver { get; set; }
            public string? CallText { get; set; }
            public string? AudioFilename { get; set; }
            public string? CallDuration { get; set; }
        }
    }
}