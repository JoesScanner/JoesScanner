using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JoesScanner.Models;

namespace JoesScanner.Services
{
    /// <summary>
    /// Polls the Trunking Recorder calljson endpoint and streams new CallItem objects
    /// to the UI, using the server API fields for time, receiver, site, talkgroup,
    /// transcription and audio.
    /// </summary>
    public class CallStreamService : ICallStreamService
    {
        private readonly ISettingsService _settingsService;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        // Mirrors DEFAULT_CONFIG["poll_batch"]
        private const int PollBatchSize = 25;

        // Track which IDs we have already shown
        private readonly HashSet<string> _seenIds = new();
        private int _draw = 1;

        public CallStreamService(ISettingsService settingsService)
        {
            _settingsService = settingsService;

            _httpClient = new HttpClient();
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

        /// <summary>
        /// Continuously polls the server and yields new calls as CallItem objects.
        /// </summary>
        public async IAsyncEnumerable<CallItem> GetCallStreamAsync(
    [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // New connection: clear any previously seen IDs and suppress the first batch
            // so we only show calls that arrive after the user connects.
            _seenIds.Clear();
            var skipInitialBatch = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                var baseUrl = (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/');

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    // No server configured yet
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

                try
                {
                    rows = await FetchLatestAsync(callsUrl, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                }

                if (errorMessage != null)
                {
                    // Surface a simple error row in the UI
                    yield return new CallItem
                    {
                        Timestamp = DateTime.Now,
                        Talkgroup = "ERROR",
                        Transcription = $"Error talking to {callsUrl}: {errorMessage}",
                        AudioUrl = string.Empty
                    };

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }

                if (rows == null || rows.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                // For the first poll after connect, we only seed _seenIds and apply filters,
                // but we do NOT yield any calls. This prevents a backlog from populating on connect.
                var skipYieldForThisBatch = skipInitialBatch;
                skipInitialBatch = false;

                foreach (var r in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var rawId = r.DT_RowId?.Trim();
                    if (string.IsNullOrEmpty(rawId))
                        continue;

                    // Already shown this call
                    if (_seenIds.Contains(rawId))
                        continue;

                    _seenIds.Add(rawId);

                    // Start with no debug message for this call
                    var debugInfo = string.Empty;

                    // Transcription text from the server, may be empty if there is no transcription
                    var text = r.CallText?.Trim() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        // Server did not send any transcription text for this call
                        debugInfo = AppendDebug(debugInfo, "No transcription from server");
                    }

                    // Timestamp from StartTime / StartTimeUTC
                    var timestamp = ParseTimestamp(r.StartTime, r.StartTimeUTC);




                    // Talkgroup from label or ID
                    var talkgroup = !string.IsNullOrWhiteSpace(r.TargetLabel)
                        ? r.TargetLabel
                        : r.TargetID ?? string.Empty;

                    // Source radio ID / label
                    var source = !string.IsNullOrWhiteSpace(r.SourceLabel)
                        ? r.SourceLabel
                        : r.SourceID ?? string.Empty;

                    // Site name from SiteLabel / SiteID
                    var site = !string.IsNullOrWhiteSpace(r.SiteLabel)
                        ? r.SiteLabel
                        : r.SiteID ?? string.Empty;

                    // Voice receiver name
                    var voiceReceiver = r.VoiceReceiver?.Trim() ?? string.Empty;

                    // Call duration in seconds, if the server provides CallDuration
                    double durationSeconds = 0;
                    if (!string.IsNullOrWhiteSpace(r.CallDuration))
                    {
                        if (double.TryParse(r.CallDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                        {
                            durationSeconds = dur;
                        }
                    }

                    // Build audio URL from AudioFilename
                    string audioUrl = string.Empty;
                    if (!string.IsNullOrWhiteSpace(r.AudioFilename))
                    {
                        var fileName = r.AudioFilename.Trim();

                        // If TR returns a full URL, use it as-is
                        if (fileName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            audioUrl = fileName;
                        }
                        else
                        {
                            // Treat AudioFilename as a relative path from the TR web root
                            // Example: "audio/2025-11-14/12345.wav"
                            audioUrl = $"{baseUrl}/{fileName.TrimStart('/')}";
                        }
                    }

                    if (string.IsNullOrWhiteSpace(audioUrl))
                    {
                        // No usable audio URL even though the call exists
                        debugInfo = AppendDebug(debugInfo, "No audio URL from server");
                    }

                    // If we still do not have an audio URL, note that in debug info.
                    if (string.IsNullOrWhiteSpace(audioUrl))
                    {
                        var suffix = "No audio URL built from server data";
                        debugInfo = string.IsNullOrWhiteSpace(debugInfo)
                            ? suffix
                            : $"{debugInfo}; {suffix}";
                    }


                    // At this point the call has passed filters and is tracked in _seenIds.
                    // If this is the initial batch after connect, do not surface it in the UI.
                    if (skipYieldForThisBatch)
                        continue;

                    // Final CallItem consumed by the UI
                    yield return new CallItem
                    {
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

        private async Task<List<CallRow>> FetchLatestAsync(string callsUrl, CancellationToken cancellationToken)
        {
            var payload = BuildDataTablesPayload(_draw, PollBatchSize);
            _draw++;

            var jsonPayload = JsonSerializer.Serialize(payload, _jsonOptions);

            // Python uses data=body_json with form content type.
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await _httpClient.PostAsync(callsUrl, content, cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var rows = new List<CallRow>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement rowsElement;

            if (root.TryGetProperty("data", out rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
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

        private static DateTime ParseTimestamp(string? startTime, string? startTimeUtc)
        {
            var value = !string.IsNullOrWhiteSpace(startTimeUtc) ? startTimeUtc : startTime;

            if (string.IsNullOrWhiteSpace(value))
                return DateTime.Now;

            try
            {
                // ISO 8601
                if (value.Contains("T", StringComparison.Ordinal))
                {
                    var dto = DateTimeOffset.Parse(value.Replace("Z", "+00:00"), null);
                    return dto.ToLocalTime().DateTime;
                }

                // Unix seconds
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

        private static string? GetString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var prop))
                return null;

            switch (prop.ValueKind)
            {
                case JsonValueKind.String:
                    return prop.GetString();
                case JsonValueKind.Number:
                    return prop.ToString();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return prop.GetBoolean().ToString();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                default:
                    return prop.ToString();
            }
        }
        private static string AppendDebug(string existing, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return existing ?? string.Empty;

            if (string.IsNullOrWhiteSpace(existing))
                return message;

            // Combine multiple debug messages in a compact way
            return existing + " | " + message;
        }

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

        private sealed class DataTablesOrder
        {
            [JsonPropertyName("column")]
            public int Column { get; set; }

            [JsonPropertyName("dir")]
            public string Dir { get; set; } = "desc";
        }

        private sealed class DataTablesSearch
        {
            [JsonPropertyName("value")]
            public string Value { get; set; } = string.Empty;

            [JsonPropertyName("regex")]
            public bool Regex { get; set; }
        }

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
