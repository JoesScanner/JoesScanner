using JoesScanner.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

                List<CallRow>? rows = null;
                string? errorMessage = null;
                var isAuthError = false;

                try
                {
                    rows = await FetchLatestAsync(callsUrl, cancellationToken);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
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
                    errorMessage = ex.Message;
                    Debug.WriteLine($"[CallStreamService] [HttpError] {ex}");
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

                    var rawId = r.DT_RowId?.Trim();
                    if (string.IsNullOrEmpty(rawId))
                        continue;

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
                    var text = r.CallText?.Trim() ?? string.Empty;

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
                            // Example: "audio/2025-11-14/12345.wav"
                            audioUrl = $"{baseUrl}/{fileName.TrimStart('/')}";
                        }
                    }

                    // If Basic Auth is configured, embed credentials in the audio URL
                    // so the platform media player can reach pfSense protected audio.
                    audioUrl = ApplyBasicAuthToAudioUrl(audioUrl, baseUrl);

                    System.Diagnostics.Debug.WriteLine($"[CallStreamService] AudioUrl for {rawId}: {audioUrl}");

                    if (string.IsNullOrWhiteSpace(audioUrl))
                    {
                        // No usable audio URL even though the call exists.
                        debugInfo = AppendDebug(debugInfo, "No audio URL from server");

                        var suffix = "No audio URL built from server data";
                        debugInfo = AppendDebug(debugInfo, suffix);
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
                        // Nothing new for this call from the client's perspective.
                        continue;
                    }

                    // Do not surface prior calls in the UI on the initial batch.
                    if (skipYieldForThisBatch && isNew)
                        continue;

                    // Final CallItem consumed by the UI.
                    yield return new CallItem
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

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        // Embeds basic auth credentials into the audio URL when basic auth is configured
        // and the audio URL host matches the configured server.
        private string ApplyBasicAuthToAudioUrl(string audioUrl, string baseUrl)
        {
            try
            {
                // Only do anything if a basic auth username is configured.
                var username = _settingsService.BasicAuthUsername;
                var password = _settingsService.BasicAuthPassword ?? string.Empty;

                if (string.IsNullOrWhiteSpace(username))
                    return audioUrl;

                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                    return audioUrl;

                if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri))
                    return audioUrl;

                // Only embed credentials if this audio URL is on the same scheme/host as the configured server.
                if (!string.Equals(baseUri.Host, audioUri.Host, StringComparison.OrdinalIgnoreCase))
                    return audioUrl;

                if (!string.Equals(baseUri.Scheme, audioUri.Scheme, StringComparison.OrdinalIgnoreCase))
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
            var payload = BuildDataTablesPayload(_draw, PollBatchSize);
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

            var username = _settingsService.BasicAuthUsername;
            if (string.IsNullOrWhiteSpace(username))
                return;

            var password = _settingsService.BasicAuthPassword ?? string.Empty;
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
                return null;

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
        private static DataTablesPayload BuildDataTablesPayload(int draw, int length)
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
                Start = 0,
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