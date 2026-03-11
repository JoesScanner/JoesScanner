using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

// Syncs observed Receiver/Site/Talkgroup associations to/from the WordPress Auth API.
// Always enabled (no rate limiting). Identity is still required by the server (device_id and/or session_token).
public sealed class AuthObservedTriplesSyncService : IAuthObservedTriplesSyncService, IDisposable
{
    private const string DefaultAuthServerBaseUrl = "https://joesscanner.com";

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

    private Uri GetEndpoint()
    {
        var baseUrl = (_settings.AuthServerBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultAuthServerBaseUrl;

        baseUrl = baseUrl.TrimEnd('/');
        return new Uri(baseUrl + "/wp-json/joes-scanner/v1/observed-triples-v2");
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

    private static string CanonicalizeIdentityPart(string? primary, string? fallback)
    {
        var value = string.IsNullOrWhiteSpace(primary) ? fallback : primary;
        value = (value ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        return string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task<IReadOnlyList<ObservedTriple>?> TryFetchSeedAsync(string serverKey, CancellationToken cancellationToken)
    {

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
                    LastSeenUtc = TryParseUtc(it.LastSeenUtcRaw ?? string.Empty) ?? DateTime.MinValue
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

        if (string.IsNullOrWhiteSpace(serverKey))
            return;

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

            var okBody = string.Empty;
            try { okBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
            if (okBody.Length > 300) okBody = okBody.Substring(0, 300);
            AppLog.Add(() => $"AuthObserved: report POST ok. server={serverKey} items={delta.Count} body='{okBody}'");

            AppStateStore.SetString(GetLastReportUtcKey(serverKey), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (maxLastSeen.HasValue)
                AppStateStore.SetString(GetLastCutoffUtcKey(serverKey), maxLastSeen.Value.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"AuthObserved: report POST exception. server={serverKey} ex={ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ObservedTriple>?> TrySyncExchangeAsync(string serverKey, bool force, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverKey))
            return null;

        var lastCutoffRaw = AppStateStore.GetString(GetLastCutoffUtcKey(serverKey), string.Empty);
        var sinceUtc = force
            ? DateTime.MinValue
            : (TryParseUtc(lastCutoffRaw) ?? DateTime.UtcNow.AddDays(-7));

        List<ObservedTripleReportItem> delta;
        DateTime? maxLastSeen;
        try
        {
            (delta, maxLastSeen) = await QueryObservedDeltaAsync(serverKey, sinceUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"AuthObserved: exchange query failed. server={serverKey} ex={ex.GetType().Name}: {ex.Message}");
            return null;
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
            AppLog.Add(() => $"AuthObserved: exchange POST start. server={serverKey} items={delta.Count} since={sinceUtc:O}");

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = string.Empty;
                try { errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
                if (errBody.Length > 300) errBody = errBody.Substring(0, 300);
                AppLog.Add(() => $"AuthObserved: exchange POST failed. status={(int)resp.StatusCode} body='{errBody}'");
                return null;
            }

            var body = string.Empty;
            try { body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
            if (string.IsNullOrWhiteSpace(body))
                return null;

            if (body.Length > 300)
                AppLog.Add(() => $"AuthObserved: exchange POST ok. server={serverKey} items={delta.Count} body='{body.Substring(0, 300)}'");
            else
                AppLog.Add(() => $"AuthObserved: exchange POST ok. server={serverKey} items={delta.Count} body='{body}'");

            var dto = JsonSerializer.Deserialize<ObservedTriplesExchangeResponseDto>(body, JsonOptions);
            if (dto == null || !dto.Ok || dto.Items == null)
                return null;

            var mergedMap = new Dictionary<string, ObservedTriple>(StringComparer.OrdinalIgnoreCase);

            static string MakeKey(string a, string b, string c) => (a ?? string.Empty) + "\n" + (b ?? string.Empty) + "\n" + (c ?? string.Empty);

foreach (var it in dto.Items)
{
    if (it == null)
        continue;

    var rxV = (it.ReceiverValue ?? string.Empty).Trim();
    var siteV = (it.SiteValue ?? string.Empty).Trim();
    var tgV = (it.TalkgroupValue ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(rxV) || string.IsNullOrWhiteSpace(siteV) || string.IsNullOrWhiteSpace(tgV))
        continue;

    var rxL = string.IsNullOrWhiteSpace(it.ReceiverLabel) ? rxV : it.ReceiverLabel.Trim();
    var siteL = string.IsNullOrWhiteSpace(it.SiteLabel) ? siteV : it.SiteLabel.Trim();
    var tgL = string.IsNullOrWhiteSpace(it.TalkgroupLabel) ? tgV : it.TalkgroupLabel.Trim();

    var seen = it.SeenCount;
    var last = TryParseUtc(it.LastSeenUtcRaw ?? string.Empty) ?? DateTime.MinValue;

    var key = MakeKey(rxV, siteV, tgV);
    if (!mergedMap.TryGetValue(key, out var existing))
    {
        mergedMap[key] = new ObservedTriple
        {
            ReceiverValue = rxV,
            ReceiverLabel = rxL,
            SiteValue = siteV,
            SiteLabel = siteL,
            TalkgroupValue = tgV,
            TalkgroupLabel = tgL,
            SeenCount = seen,
            LastSeenUtc = last
        };
        continue;
    }

    // Merge: keep max counts and latest last-seen, and prefer the more descriptive labels.
    if (seen > existing.SeenCount)
        existing.SeenCount = seen;

    if (last > existing.LastSeenUtc)
        existing.LastSeenUtc = last;

    if (!string.IsNullOrWhiteSpace(rxL) && rxL.Length > (existing.ReceiverLabel?.Length ?? 0))
        existing.ReceiverLabel = rxL;

    if (!string.IsNullOrWhiteSpace(siteL) && siteL.Length > (existing.SiteLabel?.Length ?? 0))
        existing.SiteLabel = siteL;

    if (!string.IsNullOrWhiteSpace(tgL) && tgL.Length > (existing.TalkgroupLabel?.Length ?? 0))
        existing.TalkgroupLabel = tgL;
}

var merged = mergedMap.Values
    .OrderBy(x => x.ReceiverLabel ?? x.ReceiverValue ?? string.Empty, StringComparer.OrdinalIgnoreCase)
    .ThenBy(x => x.SiteLabel ?? x.SiteValue ?? string.Empty, StringComparer.OrdinalIgnoreCase)
    .ThenBy(x => x.TalkgroupLabel ?? x.TalkgroupValue ?? string.Empty, StringComparer.OrdinalIgnoreCase)
    .ToList();

AppStateStore.SetString(GetLastReportUtcKey(serverKey), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
if (maxLastSeen.HasValue)
    AppStateStore.SetString(GetLastCutoffUtcKey(serverKey), maxLastSeen.Value.ToString("O", CultureInfo.InvariantCulture));

AppLog.Add(() => $"AuthObserved: exchange merged ok. server={serverKey} merged={merged.Count} serverLastReportUtc='{dto.ServerLastReportUtc ?? string.Empty}'");
return merged.Count == 0 ? null : merged;

        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"AuthObserved: exchange POST exception. server={serverKey} ex={ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyList<ObservedTriple>> GetLocalObservedAsync(string serverKey, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverKey))
            return Array.Empty<ObservedTriple>();

        // Use the same selection logic as reporting. Keep it fast and bounded.
        var items = new List<ObservedTriple>();

        await using var conn = new SqliteConnection($"Data Source={_dbPathProvider.DbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT
  COALESCE(NULLIF(voice_receiver, ''), NULLIF(source_label, ''), NULLIF(system_label, ''), '') AS receiver_value,
  COALESCE(NULLIF(site_id, ''), NULLIF(site_label, ''), '') AS site_value,
  COALESCE(NULLIF(site_label, ''), NULLIF(site_id, ''), '') AS site_label,
  COALESCE(NULLIF(target_id, ''), NULLIF(target_label, ''), '') AS talkgroup_value,
  COALESCE(NULLIF(target_label, ''), NULLIF(target_id, ''), '') AS talkgroup_label,
  COUNT(1) AS seen_count,
  MAX(received_at_utc) AS last_seen_utc
FROM calls
WHERE server_key = $server_key
  AND received_at_utc > $since_utc
  AND COALESCE(NULLIF(voice_receiver, ''), NULLIF(source_label, ''), NULLIF(system_label, ''), '') <> ''
  AND COALESCE(NULLIF(site_id, ''), NULLIF(site_label, ''), '') <> ''
  AND COALESCE(NULLIF(target_id,''), NULLIF(target_label,''), '') <> ''
GROUP BY receiver_value, site_value, talkgroup_value
ORDER BY last_seen_utc DESC
LIMIT 5000;";
        cmd.Parameters.AddWithValue("$server_key", serverKey);
        cmd.Parameters.AddWithValue("$since_utc", sinceUtc.ToString("O", CultureInfo.InvariantCulture));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var receiverValueRaw = reader.GetString(0).Trim();
            var siteValueRaw = reader.GetString(1).Trim();
            var siteLabelRaw = reader.GetString(2).Trim();
            var talkgroupValueRaw = reader.GetString(3).Trim();
            var talkgroupLabelRaw = reader.GetString(4).Trim();
            var seenCount = reader.GetInt32(5);
            var lastSeenRaw = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);

            var receiverCanonical = CanonicalizeIdentityPart(receiverValueRaw, receiverValueRaw);
            var siteCanonical = CanonicalizeIdentityPart(siteLabelRaw, siteValueRaw);
            var talkgroupCanonical = CanonicalizeIdentityPart(talkgroupLabelRaw, talkgroupValueRaw);
            if (string.IsNullOrWhiteSpace(receiverCanonical) || string.IsNullOrWhiteSpace(siteCanonical) || string.IsNullOrWhiteSpace(talkgroupCanonical))
                continue;

            var lastSeenUtc = TryParseUtc(lastSeenRaw) ?? DateTime.UtcNow;

            items.Add(new ObservedTriple
            {
                ReceiverValue = receiverCanonical,
                ReceiverLabel = receiverCanonical,
                SiteValue = siteCanonical,
                SiteLabel = siteCanonical,
                TalkgroupValue = talkgroupCanonical,
                TalkgroupLabel = talkgroupCanonical,
                SeenCount = seenCount,
                LastSeenUtc = lastSeenUtc
            });
        }

        return items;
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
  COALESCE(NULLIF(target_id, ''), NULLIF(target_label, ''), '') AS talkgroup_value,
  COALESCE(NULLIF(target_label, ''), NULLIF(target_id, ''), '') AS talkgroup_label,
  COUNT(1) AS seen_count,
  MAX(received_at_utc) AS last_seen_utc
FROM calls
WHERE server_key = $server_key
  AND received_at_utc > $since_utc
  AND COALESCE(NULLIF(voice_receiver, ''), NULLIF(source_label, ''), NULLIF(system_label, ''), '') <> ''
  AND COALESCE(NULLIF(site_id, ''), NULLIF(site_label, ''), '') <> ''
  AND COALESCE(NULLIF(target_id,''), NULLIF(target_label,''), '') <> ''
GROUP BY receiver_value, site_value, talkgroup_value
ORDER BY last_seen_utc DESC
LIMIT 5000;";
        cmd.Parameters.AddWithValue("$server_key", serverKey);
        cmd.Parameters.AddWithValue("$since_utc", sinceUtc.ToString("O", CultureInfo.InvariantCulture));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var receiverValueRaw = reader.GetString(0).Trim();
            var siteValueRaw = reader.GetString(1).Trim();
            var siteLabelRaw = reader.GetString(2).Trim();
            var talkgroupValueRaw = reader.GetString(3).Trim();
            var talkgroupLabelRaw = reader.GetString(4).Trim();
            var seenCount = reader.GetInt32(5);
            var lastSeenRaw = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);

            var receiverCanonical = CanonicalizeIdentityPart(receiverValueRaw, receiverValueRaw);
            var siteCanonical = CanonicalizeIdentityPart(siteLabelRaw, siteValueRaw);
            var talkgroupCanonical = CanonicalizeIdentityPart(talkgroupLabelRaw, talkgroupValueRaw);
            if (string.IsNullOrWhiteSpace(receiverCanonical) || string.IsNullOrWhiteSpace(siteCanonical) || string.IsNullOrWhiteSpace(talkgroupCanonical))
                continue;

            var lastSeenUtc = TryParseUtc(lastSeenRaw) ?? DateTime.UtcNow;
            if (!maxLastSeen.HasValue || lastSeenUtc > maxLastSeen.Value)
                maxLastSeen = lastSeenUtc;

            items.Add(new ObservedTripleReportItem
            {
                ReceiverValue = receiverCanonical,
                ReceiverLabel = receiverCanonical,
                SiteValue = siteCanonical,
                SiteLabel = siteCanonical,
                TalkgroupValue = talkgroupCanonical,
                TalkgroupLabel = talkgroupCanonical,
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

    private sealed class ObservedTriplesExchangeResponseDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("inserted")] public int Inserted { get; set; }
        [JsonPropertyName("updated")] public int Updated { get; set; }
        [JsonPropertyName("server_last_reported_at_utc")] public string? ServerLastReportUtc { get; set; }
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
        // The WordPress API may return UTC timestamps in either ISO 8601 (recommended)
        // or legacy "yyyy-MM-dd HH:mm:ss" format. Parse manually to avoid hard failures.
        [JsonPropertyName("last_seen_utc")] public string? LastSeenUtcRaw { get; set; }
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
