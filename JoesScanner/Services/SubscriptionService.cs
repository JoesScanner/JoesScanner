using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoesScanner.Services
{
    public sealed class SubscriptionService : ISubscriptionService, IDisposable
    {
        // Change this number when you want a different grace window
        private const int SubscriptionGraceDays = 3;

        private const string DefaultAuthServerBaseUrl = "https://joesscanner.com";

        private readonly ISettingsService _settings;
        private readonly ITelemetryService _telemetryService;
        private readonly HttpClient _httpClient;

        public SubscriptionService(ISettingsService settings, ITelemetryService telemetryService)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public void Dispose()
        {
            try { _httpClient.Dispose(); } catch { }
        }

        private Uri GetAuthEndpoint()
        {
            var baseUrl = (_settings.AuthServerBaseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = DefaultAuthServerBaseUrl;

            baseUrl = baseUrl.TrimEnd('/');
            return new Uri(baseUrl + "/wp-json/joes-scanner/v1/auth");
        }

        public async Task<SubscriptionCheckResult> EnsureSubscriptionAsync(CancellationToken cancellationToken)
        {
            var basicUser = (_settings.BasicAuthUsername ?? string.Empty).Trim();
            var basicPass = (_settings.BasicAuthPassword ?? string.Empty).Trim();

            // Credentials not set means auth is not in use.
            if (string.IsNullOrWhiteSpace(basicUser) || string.IsNullOrWhiteSpace(basicPass))
            {
                return new SubscriptionCheckResult(true, null, "Auth disabled");
            }

            var appVersion = AppInfo.Current.VersionString ?? string.Empty;
            var appBuild = AppInfo.Current.BuildString ?? string.Empty;

            var platform = DeviceInfo.Platform.ToString();
            var type = DeviceInfo.Idiom.ToString();
            var model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
            var osVersion = DeviceInfo.VersionString ?? string.Empty;

            var payload = new
            {
                username = basicUser,
                password = basicPass,
                device_platform = platform,
                device_type = type,
                device_model = model,
                app_version = appVersion,
                app_build = appBuild,
                os_version = osVersion,
                device_id = _settings.DeviceInstallId,

                // Allows the server to correlate attempts across the same app run.
                session_token = _settings.AuthSessionToken
            };

            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _httpClient.PostAsync(GetAuthEndpoint(), content, cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                AuthResponseDto? authResponse = null;
                try
                {
                    authResponse = JsonSerializer.Deserialize<AuthResponseDto>(
                        responseBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                }

                var statusInt = (int)response.StatusCode;
                var nowUtc = DateTime.UtcNow;

                // Any HTTP response counts as "server contacted", so there is no grace window on a denial.
                if (!response.IsSuccessStatusCode)
                {
                    _settings.SubscriptionLastCheckUtc = nowUtc;
                    _settings.SubscriptionLastStatusOk = false;

                    var sub = authResponse?.Subscription;
                    var planLabel = (sub?.LevelLabel ?? sub?.Level ?? string.Empty).Trim();
                    var priceText = (sub?.PriceId ?? string.Empty).Trim();

                    _settings.SubscriptionLastLevel = planLabel;
                    _settings.SubscriptionPriceId = priceText;

                    _settings.SubscriptionExpiresUtc = TryParseUtc(sub?.ExpiresAt);
                    _settings.SubscriptionRenewalUtc = null;

                    _settings.SubscriptionLastMessage = (authResponse?.Message ?? response.ReasonPhrase ?? "Denied").Trim();

                    return new SubscriptionCheckResult(false, "http_" + statusInt.ToString(CultureInfo.InvariantCulture), _settings.SubscriptionLastMessage);
                }

                if (authResponse == null)
                {
                    _settings.SubscriptionLastCheckUtc = nowUtc;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionLastLevel = string.Empty;
                    _settings.SubscriptionExpiresUtc = null;
                    _settings.SubscriptionRenewalUtc = null;
                    _settings.SubscriptionLastMessage = "Invalid server response";

                    return new SubscriptionCheckResult(false, "invalid_response", _settings.SubscriptionLastMessage);
                }

                if (!authResponse.Ok)
                {
                    _settings.SubscriptionLastCheckUtc = nowUtc;
                    _settings.SubscriptionLastStatusOk = false;

                    var sub = authResponse.Subscription;
                    var planLabel = (sub?.LevelLabel ?? sub?.Level ?? string.Empty).Trim();
                    var priceText = (sub?.PriceId ?? string.Empty).Trim();

                    _settings.SubscriptionLastLevel = planLabel;
                    _settings.SubscriptionPriceId = priceText;

                    _settings.SubscriptionExpiresUtc = TryParseUtc(sub?.ExpiresAt);
                    _settings.SubscriptionRenewalUtc = null;

                    _settings.SubscriptionLastMessage = (authResponse.Message ?? "Denied").Trim();

                    return new SubscriptionCheckResult(false, authResponse.Error ?? "denied", _settings.SubscriptionLastMessage);
                }

                var subscription = authResponse.Subscription;

                // Some failures can still be HTTP 200, but the subscription is inactive.
                if (subscription != null && !subscription.Active)
                {
                    _settings.SubscriptionLastCheckUtc = nowUtc;
                    _settings.SubscriptionLastStatusOk = false;

                    var planLabel = (subscription.LevelLabel ?? subscription.Level ?? string.Empty).Trim();
                    var priceText = (subscription.PriceId ?? string.Empty).Trim();

                    _settings.SubscriptionLastLevel = planLabel;
                    _settings.SubscriptionPriceId = priceText;

                    _settings.SubscriptionExpiresUtc = TryParseUtc(subscription.ExpiresAt);
                    _settings.SubscriptionRenewalUtc = null;

                    _settings.SubscriptionLastMessage = (authResponse.Message ?? "No active subscription").Trim();

                    return new SubscriptionCheckResult(false, authResponse.Error ?? "no_active_subscription", _settings.SubscriptionLastMessage);
                }

                // Success
                _settings.SubscriptionLastCheckUtc = nowUtc;
                _settings.SubscriptionLastStatusOk = true;

                var level = (subscription?.LevelLabel ?? subscription?.Level ?? string.Empty).Trim();
                var price = (subscription?.PriceId ?? string.Empty).Trim();

                _settings.SubscriptionLastLevel = level;
                _settings.SubscriptionPriceId = price;

                _settings.SubscriptionExpiresUtc = TryParseUtc(subscription?.ExpiresAt);

                var statusText = (subscription?.Status ?? string.Empty).Trim().ToLowerInvariant();
                var renewalRaw = statusText == "trialing"
                    ? (subscription?.TrialEndsAt ?? subscription?.PeriodEndAt ?? subscription?.ExpiresAt)
                    : (subscription?.PeriodEndAt ?? subscription?.ExpiresAt ?? subscription?.TrialEndsAt);

                var renewalUtc = TryParseUtc(renewalRaw);

                // For trialing subscriptions, treat renewal as unknown and rely on the message summary instead.
                _settings.SubscriptionRenewalUtc = statusText == "trialing" ? null : renewalUtc;

                var planSummary = BuildPlanSummary(subscription);

                if (string.IsNullOrWhiteSpace(planSummary))
                    planSummary = (authResponse.Message ?? string.Empty).Trim();

                // If we already had richer cached text, do not downgrade it to OK.
                if (string.IsNullOrWhiteSpace(planSummary))
                {
                    var prior = (_settings.SubscriptionLastMessage ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(prior) && !string.Equals(prior, "OK", StringComparison.OrdinalIgnoreCase))
                        planSummary = prior;
                }

                if (string.IsNullOrWhiteSpace(planSummary))
                    planSummary = "OK";

                _settings.SubscriptionLastMessage = planSummary;

                // If the server issued a session token, adopt it so telemetry uses the same mechanism for all devices.
                var issuedToken = (authResponse.SessionToken ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(issuedToken))
                {
                    await _telemetryService.AdoptSessionTokenAsync(issuedToken, "auth_session_token_issued", cancellationToken).ConfigureAwait(false);
                }

                return new SubscriptionCheckResult(true, null, planSummary);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Unreachable auth server. Allow grace if we recently had a successful check.
                if (_settings.SubscriptionLastStatusOk &&
                    _settings.SubscriptionLastCheckUtc.HasValue &&
                    (DateTime.UtcNow - _settings.SubscriptionLastCheckUtc.Value) <= TimeSpan.FromDays(SubscriptionGraceDays))
                {
                    return new SubscriptionCheckResult(true, "grace", "Auth server unreachable. Using grace period.");
                }

                _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                _settings.SubscriptionLastStatusOk = false;
                _settings.SubscriptionLastLevel = string.Empty;
                _settings.SubscriptionExpiresUtc = null;
                _settings.SubscriptionRenewalUtc = null;
                _settings.SubscriptionLastMessage = "Auth server unreachable";

                return new SubscriptionCheckResult(false, "unreachable", _settings.SubscriptionLastMessage);
            }
        }

        private static string BuildPlanSummary(AuthSubscriptionDto? sub)
        {
            if (sub == null)
                return string.Empty;

            var planLabel = (sub.LevelLabel ?? sub.Level ?? string.Empty).Trim();
            var priceText = (sub.PriceId ?? string.Empty).Trim();
            var statusText = (sub.Status ?? string.Empty).Trim().ToLowerInvariant();

            var periodRaw = statusText == "trialing"
                ? (sub.TrialEndsAt ?? sub.PeriodEndAt ?? sub.ExpiresAt ?? string.Empty)
                : (sub.PeriodEndAt ?? sub.ExpiresAt ?? sub.TrialEndsAt ?? string.Empty);

            var dtUtc = TryParseUtc(periodRaw);
            var formattedDate = dtUtc.HasValue
                ? DateTime.SpecifyKind(dtUtc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd")
                : string.Empty;

            var dateLabel = statusText == "trialing"
                ? "Trial end date:"
                : "Renewal:";

            if (!string.IsNullOrEmpty(planLabel) &&
                !string.IsNullOrEmpty(priceText) &&
                !string.IsNullOrEmpty(formattedDate))
            {
                return $"Plan: {planLabel} - {priceText} - {dateLabel} {formattedDate}";
            }

            if (!string.IsNullOrEmpty(planLabel) &&
                !string.IsNullOrEmpty(priceText))
            {
                return $"Plan: {planLabel} - {priceText}";
            }

            if (!string.IsNullOrEmpty(planLabel) &&
                !string.IsNullOrEmpty(formattedDate))
            {
                return $"Plan: {planLabel} - {dateLabel} {formattedDate}";
            }

            if (!string.IsNullOrEmpty(planLabel))
            {
                return $"Plan: {planLabel}";
            }

            if (!string.IsNullOrEmpty(formattedDate))
            {
                return $"{dateLabel} {formattedDate}";
            }

            return string.Empty;
        }

        private static DateTime? TryParseUtc(string? raw)
        {
            var s = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(s))
                return null;

            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) ||
                DateTimeOffset.TryParse(s, out dto))
            {
                return dto.UtcDateTime;
            }

            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ||
                DateTime.TryParse(s, out dt))
            {
                if (dt.Kind == DateTimeKind.Unspecified)
                    dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

                return dt.ToUniversalTime();
            }

            return null;
        }

        private static string CombineDeviceModel(string? manufacturer, string? model)
        {
            var mfg = (manufacturer ?? string.Empty).Trim();
            var mdl = (model ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(mfg))
                return mdl;

            if (string.IsNullOrWhiteSpace(mdl))
                return mfg;

            return mfg + " " + mdl;
        }

        private sealed class AuthResponseDto
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("session_token")]
            public string? SessionToken { get; set; }

            [JsonPropertyName("subscription")]
            public AuthSubscriptionDto? Subscription { get; set; }
        }

        private sealed class AuthSubscriptionDto
        {
            [JsonPropertyName("active")]
            public bool Active { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }

            [JsonPropertyName("level")]
            public string? Level { get; set; }

            [JsonPropertyName("level_label")]
            public string? LevelLabel { get; set; }

            [JsonPropertyName("price_id")]
            public string? PriceId { get; set; }

            [JsonPropertyName("period_end_at")]
            public string? PeriodEndAt { get; set; }

            [JsonPropertyName("trial_ends_at")]
            public string? TrialEndsAt { get; set; }

            [JsonPropertyName("expires_at")]
            public string? ExpiresAt { get; set; }
        }
    }
}
