using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace JoesScanner.Services
{
    // Syncs History lookup dropdown data (receivers/sites/talkgroups) to/from the WordPress Auth API.
    // Gated by telemetry. Default servers are always enabled; custom servers respect the user's telemetry setting.
    public sealed class AuthLookupsSyncService : IAuthLookupsSyncService, IDisposable
    {
        private const string DefaultAuthServerBaseUrl = "https://joesscanner.com";

        private const string DefaultStreamServerUrl = "https://app.joesscanner.com";

        private static readonly string[] ProvidedDefaultServerUrls =
        {
            DefaultStreamServerUrl
        };

        private static bool IsProvidedDefaultServerUrl(string? streamServerUrl)
        {
            var url = (streamServerUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            foreach (var candidate in ProvidedDefaultServerUrls)
            {
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri))
                    continue;

                if (string.Equals(uri.Host, candidateUri.Host, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static readonly TimeSpan ReportMinInterval = TimeSpan.FromHours(20);

        private readonly ISettingsService _settings;
        private readonly HttpClient _httpClient;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public AuthLookupsSyncService(ISettingsService settings, HttpClient httpClient)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            // Keep calls quick and non-blocking.
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public void Dispose()
        {
            // HttpClient lifetime is managed by DI; do not dispose.
        }

        private Uri GetLookupsEndpoint()
        {
            var baseUrl = (_settings.AuthServerBaseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = DefaultAuthServerBaseUrl;

            baseUrl = baseUrl.TrimEnd('/');
            return new Uri(baseUrl + "/wp-json/joes-scanner/v1/lookups");
        }

        private bool IsEnabled()
        {
            // User requested strict gating: when telemetry is off, this feature is off silently.
            var selectedServer = _settings.ServerUrl;
            if (IsProvidedDefaultServerUrl(selectedServer))
                return true;

            return _settings.TelemetryEnabled;
}

        private static string HashPayload(string receiversJson, string sitesJson, string talkgroupsJson, string groupsJson)
        {
            var combined = receiversJson + "\n" + sitesJson + "\n" + talkgroupsJson + "\n" + groupsJson;
            var bytes = Encoding.UTF8.GetBytes(combined);
            var hash = SHA256.HashData(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++)
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string TelemetryQueryString(ISettingsService settings)
        {
            var appVersion = AppInfo.Current.VersionString ?? string.Empty;
            var appBuild = AppInfo.Current.BuildString ?? string.Empty;
            var platform = DeviceInfo.Platform.ToString();
            var type = DeviceInfo.Idiom.ToString();
            var model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
            var osVersion = DeviceInfo.VersionString ?? string.Empty;

            // Keep the same parameter names as the existing Auth API endpoints.
            var deviceId = settings.DeviceInstallId ?? string.Empty;
            var sessionToken = settings.AuthSessionToken ?? string.Empty;

            var pairs = new List<(string k, string v)>
            {
                ("device_platform", platform),
                ("device_type", type),
                ("device_model", model),
                ("app_version", appVersion),
                ("app_build", appBuild),
                ("os_version", osVersion),
                ("device_id", deviceId),
                ("session_token", sessionToken)
            };

            var sb = new StringBuilder();
            for (var i = 0; i < pairs.Count; i++)
            {
                var (k, v) = pairs[i];
                if (i > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(k));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(v ?? string.Empty));
            }
            return sb.ToString();
        }

        private static string CombineDeviceModel(string? manufacturer, string? model)
        {
            var m = (manufacturer ?? string.Empty).Trim();
            var d = (model ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(m))
                return d;
            if (string.IsNullOrWhiteSpace(d))
                return m;

            if (d.StartsWith(m, StringComparison.OrdinalIgnoreCase))
                return d;

            return m + " " + d;
        }

        private static string GetLastReportUtcKey(string serverKey) => "lookups_report_last_utc|" + (serverKey ?? string.Empty);
        private static string GetLastReportHashKey(string serverKey) => "lookups_report_last_hash|" + (serverKey ?? string.Empty);

        private static DateTime? TryParseUtc(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return DateTime.TryParse(raw, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                ? dt.ToUniversalTime()
                : null;
        }

        public async Task<HistoryLookupData?> TryFetchSeedAsync(string serverKey, CancellationToken cancellationToken)
        {
            if (!IsEnabled())
            {
                AppLog.Add(() => "AuthLookups: seed fetch skipped (telemetry disabled).");
                return null;
            }

            if (string.IsNullOrWhiteSpace(serverKey))
                return null;

            try
            {
                var baseUri = GetLookupsEndpoint();

                var qs = TelemetryQueryString(_settings);
                var full = baseUri + "?server_key=" + Uri.EscapeDataString(serverKey) + "&" + qs;

                AppLog.Add(() => $"AuthLookups: seed GET start. server={serverKey}");

                using var resp = await _httpClient.GetAsync(full, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = string.Empty;
                    try { errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
                    if (errBody.Length > 300) errBody = errBody.Substring(0, 300);
                    AppLog.Add(() => $"AuthLookups: seed GET failed. status={(int)resp.StatusCode} body='{errBody}'");
                    return null;
                }

                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body))
                    return null;

                LookupsResponseDto? dto = null;
                try
                {
                    dto = JsonSerializer.Deserialize<LookupsResponseDto>(body, JsonOptions);
                }
                catch
                {
                    return null;
                }

                if (dto == null || !dto.Ok)
                {
                    AppLog.Add(() => "AuthLookups: seed GET returned ok=false.");
                    return null;
                }

                var receivers = DeserializeList(dto.ReceiversJson);
                var sites = DeserializeList(dto.SitesJson);
                var talkgroups = DeserializeList(dto.TalkgroupsJson);
                var groups = DeserializeGroups(dto.TalkgroupGroupsJson);

                // Treat empty seed as no seed.
                if ((receivers?.Count ?? 0) == 0 && (sites?.Count ?? 0) == 0 && (talkgroups?.Count ?? 0) == 0)
                    return null;

                AppLog.Add(() => $"AuthLookups: seed GET ok. server={serverKey} receivers={receivers?.Count ?? 0} sites={sites?.Count ?? 0} talkgroups={talkgroups?.Count ?? 0}");

                return new HistoryLookupData
                {
                    Receivers = receivers ?? new List<HistoryLookupItem>(),
                    Sites = sites ?? new List<HistoryLookupItem>(),
                    Talkgroups = talkgroups ?? new List<HistoryLookupItem>(),
                    TalkgroupGroups = groups ?? new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase)
                };
            }
            catch
            {
                return null;
            }
        }

        public async Task TryReportAsync(string serverKey, HistoryLookupData data, CancellationToken cancellationToken, bool force = false)
        {
            // Manual sync must always be allowed when the user explicitly requests it, even if telemetry is disabled.
            // Auto/background reporting remains telemetry-gated.
            if (!force && !IsEnabled())
            {
                AppLog.Add(() => "AuthLookups: report skipped (telemetry disabled).");
                return;
            }

            if (string.IsNullOrWhiteSpace(serverKey) || data == null)
                return;

            try
            {
                var receiversJson = JsonSerializer.Serialize(data.Receivers ?? Array.Empty<HistoryLookupItem>(), JsonOptions);
                var sitesJson = JsonSerializer.Serialize(data.Sites ?? Array.Empty<HistoryLookupItem>(), JsonOptions);
                var talkgroupsJson = JsonSerializer.Serialize(data.Talkgroups ?? Array.Empty<HistoryLookupItem>(), JsonOptions);

                var groupsObj = new Dictionary<string, List<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);
                if (data.TalkgroupGroups != null)
                {
                    foreach (var kvp in data.TalkgroupGroups)
                        groupsObj[kvp.Key] = kvp.Value?.ToList() ?? new List<HistoryLookupItem>();
                }
                var groupsJson = JsonSerializer.Serialize(groupsObj, JsonOptions);

                var hash = HashPayload(receiversJson, sitesJson, talkgroupsJson, groupsJson);
                if (force)
                {
                    AppLog.Add(() => $"AuthLookups: report forced. server={serverKey}");
                }

                var lastUtc = TryParseUtc(AppStateStore.GetString(GetLastReportUtcKey(serverKey), string.Empty));
                if (!force && lastUtc.HasValue && (DateTime.UtcNow - lastUtc.Value) < ReportMinInterval)
                {
                    AppLog.Add(() => $"AuthLookups: report skipped (throttled). server={serverKey} lastUtc={lastUtc.Value:O}");
                    return;
                }

                var lastHash = AppStateStore.GetString(GetLastReportHashKey(serverKey), string.Empty);
                if (!force && !string.IsNullOrWhiteSpace(lastHash) && string.Equals(lastHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    // Nothing changed; still update the last report timestamp so we do not spam retries.
                    AppStateStore.SetString(GetLastReportUtcKey(serverKey), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    AppLog.Add(() => $"AuthLookups: report skipped (unchanged). server={serverKey}");
                    return;
                }

                var payload = new
                {
                    server_key = serverKey,

                    // When true, server records a fresh updated_at even if payload hash is unchanged (manual Sync button).
                    force_touch = force,

                    // Telemetry identity
                    device_platform = DeviceInfo.Platform.ToString(),
                    device_type = DeviceInfo.Idiom.ToString(),
                    device_model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model),
                    app_version = AppInfo.Current.VersionString ?? string.Empty,
                    app_build = AppInfo.Current.BuildString ?? string.Empty,
                    os_version = DeviceInfo.VersionString ?? string.Empty,
                    device_id = _settings.DeviceInstallId ?? string.Empty,
                    session_token = _settings.AuthSessionToken ?? string.Empty,

                    receivers_json = receiversJson,
                    sites_json = sitesJson,
                    talkgroups_json = talkgroupsJson,
                    talkgroup_groups_json = groupsJson,
                    payload_hash = hash
                };

                var json = JsonSerializer.Serialize(payload, JsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                AppLog.Add(() => $"AuthLookups: report POST start. server={serverKey} bytes={json.Length}");

                using var resp = await _httpClient.PostAsync(GetLookupsEndpoint(), content, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = string.Empty;
                    try { errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
                    if (errBody.Length > 300) errBody = errBody.Substring(0, 300);
                    AppLog.Add(() => $"AuthLookups: report POST failed. status={(int)resp.StatusCode} body='{errBody}'");
                    return;
                }

                // Record success.
                AppStateStore.SetString(GetLastReportUtcKey(serverKey), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                AppStateStore.SetString(GetLastReportHashKey(serverKey), hash);

                AppLog.Add(() => $"AuthLookups: report POST ok. server={serverKey}");
            }
            catch
            {
                // silent
            }
        }

        private static List<HistoryLookupItem>? DeserializeList(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<HistoryLookupItem>();

            try
            {
                return JsonSerializer.Deserialize<List<HistoryLookupItem>>(json, JsonOptions) ?? new List<HistoryLookupItem>();
            }
            catch
            {
                return new List<HistoryLookupItem>();
            }
        }

        private static Dictionary<string, IReadOnlyList<HistoryLookupItem>>? DeserializeGroups(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, List<HistoryLookupItem>>>(json, JsonOptions)
                    ?? new Dictionary<string, List<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);

                return raw.ToDictionary(k => k.Key, v => (IReadOnlyList<HistoryLookupItem>)v.Value, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private sealed class LookupsResponseDto
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("server_key")]
            public string? ServerKey { get; set; }

            [JsonPropertyName("receivers_json")]
            public string? ReceiversJson { get; set; }

            [JsonPropertyName("sites_json")]
            public string? SitesJson { get; set; }

            [JsonPropertyName("talkgroups_json")]
            public string? TalkgroupsJson { get; set; }

            [JsonPropertyName("talkgroup_groups_json")]
            public string? TalkgroupGroupsJson { get; set; }
        }
    }
}
