using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

// Syncs observed Receiver/Site/Talkgroup associations to/from the WordPress Auth API.
// Gated by telemetry. Default servers are always enabled; custom servers respect the user's telemetry setting.
public sealed class AuthObservedTriplesSyncService : IAuthObservedTriplesSyncService, IDisposable
{
    private const string DefaultAuthServerBaseUrl = "https://joesscanner.com";
    private const string DefaultStreamServerUrl = "https://app.joesscanner.com";

    private static readonly string[] ProvidedDefaultServerUrls =
    {
        DefaultStreamServerUrl
    };

    private static readonly TimeSpan ReportMinInterval = TimeSpan.FromHours(20);

    private readonly ISettingsService _settings;
    private readonly IDatabasePathProvider _dbPathProvider;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public AuthObservedTriplesSyncService(ISettingsService settings, IDatabasePathProvider dbPathProvider, HttpClient httpClient)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dbPathProvider = dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public void Dispose()
    {
        // HttpClient lifetime managed by DI.
    }

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

    private bool IsEnabled()
    {
        var selectedServer = _settings.ServerUrl;
        if (IsProvidedDefaultServerUrl(selectedServer))
            return true;

        return _settings.TelemetryEnabled;
    }

    private Uri GetEndpoint()
    {
        var baseUrl = (_settings.AuthServerBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultAuthServerBaseUrl;

        baseUrl = baseUrl.TrimEnd('/');
        return new Uri(baseUrl + "/wp-json/joes-scanner/v1/observed-triples");
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

    private static string TelemetryQueryString(ISettingsService settings)
    {
        var appVersion = AppInfo.Current.VersionString ?? string.Empty;
        var appBuild = AppInfo.Current.BuildString ?? string.Empty;
        var platform = DeviceInfo.Platform.ToString();
        var type = DeviceInfo.Idiom.ToString();
        var model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
        var osVersion = DeviceInfo.VersionString ?? string.Empty;

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

    private static string GetLastReportUtcKey(string serverKey) => "observed_report_last_utc|" + (serverKey ?? string.Empty);
    private static string GetLastCutoffUtcKey(string serverKey) => "observed_report_cutoff_utc|" + (serverKey ?? string.Empty);

    private static DateTime? TryParseUtc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTime.TryParse(raw, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt.ToUniversalTime()
            : null;
    }

    public async Task<IReadOnlyList<ObservedTriple>?> TryFetchSeedAsync(string serverKey, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            AppLog.Add(() => "AuthObserved: seed fetch skipped (telemetry disabled)." );
            return null;
        }

        if (string.IsNullOrWhiteSpace(serverKey))
            return null;

        try
        {
            var baseUri = GetEndpoint();
            var qs = TelemetryQueryString(_settings);
            var full = baseUri + "?server_key=" + Uri.EscapeDataString(serverKey) + "&limit=5000&" + qs;

            AppLog.Add(() => $"AuthObserved: seed GET start. server={serverKey}");

            using var resp = await _httpClient.GetAsync(full, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = string.Empty;
                try { errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
                if (errBody.Length > 300) errBody = errBody.Substring(0, 300);
                AppLog.Add(() => $"AuthObserved: seed GET failed. status={(int)resp.StatusCode} body='{errBody}'");
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var dto = JsonSerializer.Deserialize<ObservedTriplesResponseDto>(body, JsonOptions);
            if (dto == null || !dto.Ok || dto.Items == null || dto.Items.Count == 0)
                return null;

            var items = new List<ObservedTriple>(dto.Items.Count);
            foreach (var it in dto.Items)
            {
                if (it == null)
                    continue;

                var rxV = (it.ReceiverValue ?? string.Empty).Trim();
                var siteV = (it.SiteValue ?? string.Empty).Trim();
                var tgV = (it.TalkgroupValue ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(rxV) || string.IsNullOrWhiteSpace(siteV) || string.IsNullOrWhiteSpace(tgV))
                    continue;

                items.Add(new ObservedTriple
                {
                    ReceiverValue = rxV,
                    ReceiverLabel = string.IsNullOrWhiteSpace(it.ReceiverLabel) ? rxV : it.ReceiverLabel.Trim(),
                    SiteValue = siteV,
                    SiteLabel = string.IsNullOrWhiteSpace(it.SiteLabel) ? siteV : it.SiteLabel.Trim(),
                    TalkgroupValue = tgV,
                    TalkgroupLabel = string.IsNullOrWhiteSpace(it.TalkgroupLabel) ? tgV : it.TalkgroupLabel.Trim(),
                    SeenCount = it.SeenCount,
                    LastSeenUtc = it.LastSeenUtc ?? DateTime.MinValue
                });
            }

            AppLog.Add(() => $"AuthObserved: seed GET ok. server={serverKey} items={items.Count}");
            return items.Count == 0 ? null : items;
        }
        catch
        {
            return null;
        }
    }

    public async Task TryReportAsync(string serverKey, bool force, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            AppLog.Add(() => "AuthObserved: report skipped (telemetry disabled)." );
            return;
        }

        if (string.IsNullOrWhiteSpace(serverKey))
            return;

        var lastUtcRaw = AppStateStore.GetString(GetLastReportUtcKey(serverKey), string.Empty);
        var lastUtc = TryParseUtc(lastUtcRaw);
        if (!force && lastUtc.HasValue && (DateTime.UtcNow - lastUtc.Value) < ReportMinInterval)
        {
            AppLog.Add(() => $"AuthObserved: report skipped (throttle). server={serverKey} last={lastUtc.Value:O}");
            return;
        }

        // When force is requested (manual sync), backfill the full local history so a server with
        // existing local history can immediately populate the observed triples table.
        // Otherwise, we default to deltas since the last cutoff, with a conservative 7 day fallback.
        var lastCutoffRaw = AppStateStore.GetString(GetLastCutoffUtcKey(serverKey), string.Empty);
        var sinceUtc = force
            ? DateTime.MinValue
            : (TryParseUtc(lastCutoffRaw) ?? DateTime.UtcNow.AddDays(-7));

        // Pull local deltas from the calls table.
        List<ObservedTripleReportItem> delta;
        DateTime? maxLastSeen;
        try
        {
            (delta, maxLastSeen) = await QueryObservedDeltaAsync(serverKey, sinceUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"AuthObserved: report query failed. server={serverKey} ex={ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (delta.Count == 0)
        {
            AppLog.Add(() => $"AuthObserved: report skipped (no delta). server={serverKey} since={sinceUtc:O}");
            // Still mark attempt time so we don't spam every connect.
            AppStateStore.SetString(GetLastReportUtcKey(serverKey), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return;
        }

        var payload = new ObservedTriplesReportRequestDto
        {
            ServerKey = serverKey,
            DeviceId = _settings.DeviceInstallId ?? string.Empty,
            SessionToken = _settings.AuthSessionToken ?? string.Empty,
            ForceTouch = force,
            Items = delta
        };

        var endpoint = GetEndpoint();

        try
        {
            AppLog.Add(() => $"AuthObserved: report POST start. server={serverKey} items={delta.Count} since={sinceUtc:O}");

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = string.Empty;
                try { errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
                if (errBody.Length > 300) errBody = errBody.Substring(0, 300);
                AppLog.Add(() => $"AuthObserved: report POST failed. status={(int)resp.StatusCode} body='{errBody}'");
                return;
            }

            AppLog.Add(() => $"AuthObserved: report POST ok. server={serverKey} items={delta.Count}");

            AppStateStore.SetString(GetLastReportUtcKey(serverKey), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (maxLastSeen.HasValue)
                AppStateStore.SetString(GetLastCutoffUtcKey(serverKey), maxLastSeen.Value.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"AuthObserved: report POST exception. server={serverKey} ex={ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<(List<ObservedTripleReportItem> Items, DateTime? MaxLastSeenUtc)> QueryObservedDeltaAsync(string serverKey, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var items = new List<ObservedTripleReportItem>();
        DateTime? maxLastSeen = null;

        await using var conn = new SqliteConnection($"Data Source={_dbPathProvider.DbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        // Some backends do not populate voice_receiver or site_label consistently.
        // For observed triples we need a stable Receiver/Site/Talkgroup association for filtering.
        // We use sensible fallbacks so we can still report meaningful triples rather than sending none.
        cmd.CommandText = @"
SELECT
  COALESCE(NULLIF(voice_receiver, ''), NULLIF(source_label, ''), NULLIF(system_label, ''), '') AS receiver_value,
  COALESCE(NULLIF(site_id, ''), NULLIF(site_label, ''), '') AS site_value,
  COALESCE(NULLIF(site_label, ''), NULLIF(site_id, ''), '') AS site_label,
  COALESCE(target_id, '') AS talkgroup_value,
  COALESCE(NULLIF(target_label, ''), target_id, '') AS talkgroup_label,
  COUNT(1) AS seen_count,
  MAX(received_at_utc) AS last_seen_utc
FROM calls
WHERE server_key = $server_key
  AND received_at_utc > $since_utc
  AND COALESCE(NULLIF(voice_receiver, ''), NULLIF(source_label, ''), NULLIF(system_label, ''), '') <> ''
  AND COALESCE(NULLIF(site_id, ''), NULLIF(site_label, ''), '') <> ''
  AND COALESCE(target_id,'') <> ''
GROUP BY receiver_value, site_value, site_label, talkgroup_value, talkgroup_label
ORDER BY last_seen_utc DESC
LIMIT 5000;";
        cmd.Parameters.AddWithValue("$server_key", serverKey);
        cmd.Parameters.AddWithValue("$since_utc", sinceUtc.ToString("O", CultureInfo.InvariantCulture));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var receiverValue = reader.GetString(0).Trim();
            var siteValueRaw = reader.GetString(1).Trim();
            var siteLabel = reader.GetString(2).Trim();
            var talkgroupValue = reader.GetString(3).Trim();
            var talkgroupLabel = reader.GetString(4).Trim();
            var seenCount = reader.GetInt32(5);
            var lastSeenRaw = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);

            if (string.IsNullOrWhiteSpace(receiverValue) || string.IsNullOrWhiteSpace(siteLabel) || string.IsNullOrWhiteSpace(talkgroupValue))
                continue;

            var siteValue = string.IsNullOrWhiteSpace(siteValueRaw) ? siteLabel : siteValueRaw;

            var lastSeenUtc = TryParseUtc(lastSeenRaw) ?? DateTime.UtcNow;
            if (!maxLastSeen.HasValue || lastSeenUtc > maxLastSeen.Value)
                maxLastSeen = lastSeenUtc;

            items.Add(new ObservedTripleReportItem
            {
                ReceiverValue = receiverValue,
                ReceiverLabel = receiverValue,
                SiteValue = siteValue,
                SiteLabel = siteLabel,
                TalkgroupValue = talkgroupValue,
                TalkgroupLabel = string.IsNullOrWhiteSpace(talkgroupLabel) ? talkgroupValue : talkgroupLabel,
                SeenCountDelta = seenCount,
                LastSeenUtc = lastSeenUtc
            });
        }

        return (items, maxLastSeen);
    }

    private sealed class ObservedTriplesResponseDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("items")] public List<ObservedTriplesItemDto>? Items { get; set; }
    }

    private sealed class ObservedTriplesItemDto
    {
        [JsonPropertyName("receiver_value")] public string? ReceiverValue { get; set; }
        [JsonPropertyName("receiver_label")] public string? ReceiverLabel { get; set; }
        [JsonPropertyName("site_value")] public string? SiteValue { get; set; }
        [JsonPropertyName("site_label")] public string? SiteLabel { get; set; }
        [JsonPropertyName("talkgroup_value")] public string? TalkgroupValue { get; set; }
        [JsonPropertyName("talkgroup_label")] public string? TalkgroupLabel { get; set; }
        [JsonPropertyName("seen_count")] public int SeenCount { get; set; }
        [JsonPropertyName("last_seen_utc")] public DateTime? LastSeenUtc { get; set; }
    }

    private sealed class ObservedTriplesReportRequestDto
    {
        [JsonPropertyName("server_key")] public string ServerKey { get; set; } = string.Empty;
        [JsonPropertyName("device_id")] public string DeviceId { get; set; } = string.Empty;
        [JsonPropertyName("session_token")] public string SessionToken { get; set; } = string.Empty;
        // When true, the server may bypass per-device rate limiting for this request.
        // Used only for user-initiated manual sync.
        [JsonPropertyName("force_touch")] public bool ForceTouch { get; set; }
        [JsonPropertyName("items")] public List<ObservedTripleReportItem> Items { get; set; } = new();
    }

    private sealed class ObservedTripleReportItem
    {
        [JsonPropertyName("receiver_value")] public string ReceiverValue { get; set; } = string.Empty;
        [JsonPropertyName("receiver_label")] public string ReceiverLabel { get; set; } = string.Empty;
        [JsonPropertyName("site_value")] public string SiteValue { get; set; } = string.Empty;
        [JsonPropertyName("site_label")] public string SiteLabel { get; set; } = string.Empty;
        [JsonPropertyName("talkgroup_value")] public string TalkgroupValue { get; set; } = string.Empty;
        [JsonPropertyName("talkgroup_label")] public string TalkgroupLabel { get; set; } = string.Empty;
        [JsonPropertyName("seen_count_delta")] public int SeenCountDelta { get; set; }
        [JsonPropertyName("last_seen_utc")] public DateTime LastSeenUtc { get; set; }
    }
}
