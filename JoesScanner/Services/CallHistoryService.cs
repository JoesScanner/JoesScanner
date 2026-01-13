using JoesScanner.Models;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoesScanner.Services
{
    // Loads recent calls from the Trunking Recorder calljson endpoint for the History page.
    // This is intentionally aligned with CallStreamService so it works on the same servers.
    public sealed class CallHistoryService : ICallHistoryService
    {
        private readonly ISettingsService _settingsService;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        private int _draw = 1;

        private const string ServiceAuthUsername = "secappass";
        private const string ServiceAuthPassword = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

        public CallHistoryService(ISettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            var handler = new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            // Match CallStreamService headers as closely as practical.
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            _httpClient.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            _httpClient.DefaultRequestHeaders.Add("DNT", "1");
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");

            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

            _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task<IReadOnlyList<CallItem>> GetLatestCallsAsync(int count, CancellationToken cancellationToken = default)
        {
            count = Math.Clamp(count, 1, 200);

            var baseUrl = (_settingsService.ServerUrl ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                return Array.Empty<CallItem>();

            var callsUrl = baseUrl + "/calljson";

            UpdateAuthorizationHeader();

            var payload = BuildDataTablesPayload(_draw, count);
            _draw++;

            var jsonPayload = JsonSerializer.Serialize(payload, _jsonOptions);

            // TR expects the JSON payload sent as the request body with a form content type,
            // which matches what is already working in CallStreamService.
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await _httpClient.PostAsync(callsUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusInt = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    throw new InvalidOperationException($"History auth failed (HTTP {statusInt} {response.ReasonPhrase}).");

                var raw = string.Empty;
                try { raw = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }

                throw new InvalidOperationException(
                    $"History HTTP {statusInt} {response.ReasonPhrase}. {Truncate(raw, 250)}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<CallItem>();

            var rows = new List<CallRow>();

            using (var doc = JsonDocument.Parse(json))
            {
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
            }

            if (rows.Count == 0)
                return Array.Empty<CallItem>();

            var result = new List<CallItem>(rows.Count);

            foreach (var r in rows)
            {
                var rawId = r.DT_RowId?.Trim();
                if (string.IsNullOrWhiteSpace(rawId))
                    rawId = Guid.NewGuid().ToString("N");

                var timestamp = ParseTimestamp(r.StartTime, r.StartTimeUTC);

                var talkgroup = !string.IsNullOrWhiteSpace(r.TargetLabel)
                    ? r.TargetLabel
                    : r.TargetID ?? string.Empty;

                var source = !string.IsNullOrWhiteSpace(r.SourceLabel)
                    ? r.SourceLabel
                    : r.SourceID ?? string.Empty;

                var site = !string.IsNullOrWhiteSpace(r.SiteLabel)
                    ? r.SiteLabel
                    : r.SiteID ?? string.Empty;

                var voiceReceiver = r.VoiceReceiver?.Trim() ?? string.Empty;

                double durationSeconds = 0;
                if (!string.IsNullOrWhiteSpace(r.CallDuration) &&
                    double.TryParse(r.CallDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                {
                    durationSeconds = dur;
                }

                var text = r.CallText?.Trim() ?? string.Empty;

                string audioUrl = string.Empty;
                if (!string.IsNullOrWhiteSpace(r.AudioFilename))
                {
                    var fileName = r.AudioFilename.Trim();

                    if (fileName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        audioUrl = fileName;
                    }
                    else
                    {
                        audioUrl = $"{baseUrl}/{fileName.TrimStart('/')}";
                    }
                }

                audioUrl = ApplyBasicAuthToAudioUrl(audioUrl, baseUrl);

                result.Add(new CallItem
                {
                    BackendId = rawId,
                    Timestamp = timestamp,
                    CallDurationSeconds = durationSeconds,
                    Talkgroup = talkgroup,
                    Source = source,
                    Site = site,
                    VoiceReceiver = voiceReceiver,
                    Transcription = text,
                    AudioUrl = audioUrl,
                    DebugInfo = string.Empty,
                    IsHistory = true
                });
            }

            return result;
        }

        private void UpdateAuthorizationHeader()
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;

            var serverUrl = _settingsService.ServerUrl ?? string.Empty;
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
                return;

            var isJoesScannerHost = serverUri.Host.EndsWith("app.joesscanner.com", StringComparison.OrdinalIgnoreCase);

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

            if (string.IsNullOrWhiteSpace(username))
                return;

            var raw = $"{username}:{password}";
            var bytes = Encoding.ASCII.GetBytes(raw);
            var base64 = Convert.ToBase64String(bytes);

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64);
        }

        private string ApplyBasicAuthToAudioUrl(string audioUrl, string baseUrl)
        {
            try
            {
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                    return audioUrl;

                if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri))
                    return audioUrl;

                if (!string.Equals(baseUri.Host, audioUri.Host, StringComparison.OrdinalIgnoreCase))
                    return audioUrl;

                if (!string.Equals(baseUri.Scheme, audioUri.Scheme, StringComparison.OrdinalIgnoreCase))
                    return audioUrl;

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

                if (string.IsNullOrWhiteSpace(username))
                    return audioUrl;

                var userEscaped = Uri.EscapeDataString(username);
                var passEscaped = Uri.EscapeDataString(password);

                var authority = audioUri.Authority;

                var builder = new StringBuilder();
                builder.Append(audioUri.Scheme);
                builder.Append("://");
                builder.Append(userEscaped);

                if (!string.IsNullOrWhiteSpace(password))
                {
                    builder.Append(':');
                    builder.Append(passEscaped);
                }

                builder.Append('@');
                builder.Append(authority);
                builder.Append(audioUri.PathAndQuery);

                return builder.ToString();
            }
            catch
            {
                return audioUrl;
            }
        }

        private static string Truncate(string? value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (maxChars <= 0)
                return string.Empty;

            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
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

        private static DateTime ParseTimestamp(string? startTime, string? startTimeUtc)
        {
            var value = !string.IsNullOrWhiteSpace(startTimeUtc) ? startTimeUtc : startTime;

            if (string.IsNullOrWhiteSpace(value))
                return DateTime.Now;

            try
            {
                if (value.Contains("T", StringComparison.Ordinal))
                {
                    var dto = DateTimeOffset.Parse(value.Replace("Z", "+00:00"), null);
                    return dto.ToLocalTime().DateTime;
                }

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

        // This payload builder is intentionally the same shape as CallStreamService.
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

        private sealed class DataTablesPayload
        {
            [JsonPropertyName("draw")]
            public int Draw { get; set; }

            [JsonPropertyName("start")]
            public int Start { get; set; }

            [JsonPropertyName("length")]
            public int Length { get; set; }

            [JsonPropertyName("search")]
            public DataTablesSearch Search { get; set; } = new();

            [JsonPropertyName("columns")]
            public List<DataTablesColumn> Columns { get; set; } = new();

            [JsonPropertyName("smartSort")]
            public bool SmartSort { get; set; }
        }

        private sealed class DataTablesSearch
        {
            [JsonPropertyName("value")]
            public string Value { get; set; } = string.Empty;

            [JsonPropertyName("regex")]
            public bool Regex { get; set; }
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
