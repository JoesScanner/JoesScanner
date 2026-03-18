using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JoesScanner.Helpers;

namespace JoesScanner.Services
{
    public sealed class SubscriptionService : ISubscriptionService, IDisposable
    {
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
            var basicPass = TextNormalizationHelper.NormalizeSmartQuotes((_settings.BasicAuthPassword ?? string.Empty).Trim());

            // Credentials are required for Joe's Scanner hosted servers.
            // If missing, do not allow connecting.
            if (string.IsNullOrWhiteSpace(basicUser) || string.IsNullOrWhiteSpace(basicPass))
            {
                return new SubscriptionCheckResult(false, "missing_credentials",
                    "Enter your username and password in Settings before connecting to the default server.");
            }

            // Persist the last authenticated username so other services can
            // reliably know which account is in use, even when scanner calls use
            // a separate service credential.
            _settings.LastAuthUsername = basicUser;

            var appVersion = AppInfo.Current.VersionString ?? string.Empty;
            var appBuild = AppInfo.Current.BuildString ?? string.Empty;

            var platform = DeviceInfo.Platform.ToString();
            var type = DeviceInfo.Idiom.ToString();
            var model = DeviceInfoHelper.CombineManufacturerAndModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
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
                // Use UnsafeRelaxedJsonEscaping so special characters like apostrophes in
                // passwords are sent as literal characters (e.g. ') rather than Unicode escapes
                // (e.g. '). The default JavaScriptEncoder escapes ' which causes
                // authentication failures for passwords containing apostrophes.
                var authJsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = JsonSerializer.Serialize(payload, authJsonOptions);
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
                    _settings.SubscriptionLastValidatedOnline = false;
                    _settings.SubscriptionTierLevel = 0;

                    var sub = authResponse?.Subscription;
                    var planLabel = (sub?.LevelLabel ?? sub?.Level ?? string.Empty).Trim();
                    var priceText = (sub?.PriceId ?? string.Empty).Trim();

                    _settings.SubscriptionLastLevel = planLabel;
                    _settings.SubscriptionPriceId = priceText;

                    _settings.SubscriptionExpiresUtc = DateParseHelper.TryParseUtc(sub?.ExpiresAt);
                    _settings.SubscriptionRenewalUtc = null;

                    _settings.SubscriptionLastMessage = (authResponse?.Message ?? response.ReasonPhrase ?? "Denied").Trim();

                    return new SubscriptionCheckResult(false, "http_" + statusInt.ToString(CultureInfo.InvariantCulture), _settings.SubscriptionLastMessage);
                }

                if (authResponse == null)
                {
                    _settings.SubscriptionLastCheckUtc = nowUtc;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionLastValidatedOnline = false;
                    _settings.SubscriptionTierLevel = 0;
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
                    _settings.SubscriptionLastValidatedOnline = false;
                    _settings.SubscriptionTierLevel = 0;

                    var sub = authResponse.Subscription;
                    var planLabel = (sub?.LevelLabel ?? sub?.Level ?? string.Empty).Trim();
                    var priceText = (sub?.PriceId ?? string.Empty).Trim();

                    _settings.SubscriptionLastLevel = planLabel;
                    _settings.SubscriptionPriceId = priceText;

                    _settings.SubscriptionExpiresUtc = DateParseHelper.TryParseUtc(sub?.ExpiresAt);
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
                    _settings.SubscriptionLastValidatedOnline = false;
                    _settings.SubscriptionTierLevel = 0;

                    var planLabel = (subscription.LevelLabel ?? subscription.Level ?? string.Empty).Trim();
                    var priceText = (subscription.PriceId ?? string.Empty).Trim();

                    _settings.SubscriptionLastLevel = planLabel;
                    _settings.SubscriptionPriceId = priceText;

                    _settings.SubscriptionExpiresUtc = DateParseHelper.TryParseUtc(subscription.ExpiresAt);
                    _settings.SubscriptionRenewalUtc = null;

                    _settings.SubscriptionLastMessage = (authResponse.Message ?? "No active subscription").Trim();

                    return new SubscriptionCheckResult(false, authResponse.Error ?? "no_active_subscription", _settings.SubscriptionLastMessage);
                }

                // Defensive expiry check: reject if the returned validity window is already in the past,
                // even when the server reports ok:true and active:true. This guards against the server
                // returning a false positive for an expired subscription.
                if (subscription != null)
                {
                    var periodEnd = DateParseHelper.TryParseUtc(subscription.PeriodEndAt)
                                    ?? DateParseHelper.TryParseUtc(subscription.ExpiresAt)
                                    ?? DateParseHelper.TryParseUtc(subscription.TrialEndsAt);

                    if (periodEnd.HasValue && periodEnd.Value < nowUtc)
                    {
                        _settings.SubscriptionLastCheckUtc = nowUtc;
                        _settings.SubscriptionLastStatusOk = false;
                        _settings.SubscriptionLastValidatedOnline = false;
                        _settings.SubscriptionTierLevel = 0;

                        var planLabel = (subscription.LevelLabel ?? subscription.Level ?? string.Empty).Trim();
                        var priceText = (subscription.PriceId ?? string.Empty).Trim();

                        _settings.SubscriptionLastLevel = planLabel;
                        _settings.SubscriptionPriceId = priceText;
                        _settings.SubscriptionExpiresUtc = periodEnd;
                        _settings.SubscriptionRenewalUtc = null;

                        var localDate = periodEnd.Value.ToLocalTime().ToString("yyyy-MM-dd");
                        _settings.SubscriptionLastMessage = $"Subscription expired on {localDate}.";

                        return new SubscriptionCheckResult(false, "expired", _settings.SubscriptionLastMessage);
                    }
                }

                // Success
                _settings.SubscriptionLastCheckUtc = nowUtc;
                _settings.SubscriptionLastStatusOk = true;
                _settings.SubscriptionLastValidatedOnline = true;

                var level = (subscription?.LevelLabel ?? subscription?.Level ?? string.Empty).Trim();
                var price = (subscription?.PriceId ?? string.Empty).Trim();

                _settings.SubscriptionLastLevel = level;
                _settings.SubscriptionPriceId = price;
                _settings.SubscriptionTierLevel = subscription?.TierLevel ?? 0;

                _settings.SubscriptionExpiresUtc = DateParseHelper.TryParseUtc(subscription?.ExpiresAt);

                var statusText = (subscription?.Status ?? string.Empty).Trim().ToLowerInvariant();
                var renewalRaw = statusText == "trialing"
                    ? (subscription?.TrialEndsAt ?? subscription?.PeriodEndAt ?? subscription?.ExpiresAt)
                    : (subscription?.PeriodEndAt ?? subscription?.ExpiresAt ?? subscription?.TrialEndsAt);

                var renewalUtc = DateParseHelper.TryParseUtc(renewalRaw);

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
                // Unreachable auth server.
                // Policy (strict):
                // - If the cached subscription has a known validity end date and it is still current, allow.
                // - If the cached subscription is expired, deny (do not connect to the default server).
                // - If no validity end date is available, deny until the auth server can be contacted again.

                var nowUtc = DateTime.UtcNow;

                // Strict gating: offline cached access is allowed for connecting, but it is not considered
                // a validated online state for premium feature unlocks.
                _settings.SubscriptionLastValidatedOnline = false;

                if (_settings.SubscriptionLastStatusOk)
                {
                    var validUntilUtc = GetCachedValidUntilUtc();

                    if (validUntilUtc.HasValue)
                    {
                        if (nowUtc <= validUntilUtc.Value)
                        {
                            _settings.SubscriptionLastMessage = BuildOfflineMessage(validUntilUtc.Value);
                            return new SubscriptionCheckResult(true, "offline_cached", _settings.SubscriptionLastMessage);
                        }

                        var localDate = DateTime.SpecifyKind(validUntilUtc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd");
                        _settings.SubscriptionLastMessage = $"Auth server unreachable and cached subscription is expired (expired {localDate}).";
                        _settings.SubscriptionLastCheckUtc = nowUtc;
                        _settings.SubscriptionLastStatusOk = false;
                        _settings.SubscriptionTierLevel = 0;
                        return new SubscriptionCheckResult(false, "expired_offline", _settings.SubscriptionLastMessage);
                    }

                    _settings.SubscriptionLastMessage = "Auth server unreachable and cached subscription has no validity date. Reconnect when online to verify your subscription.";
                    _settings.SubscriptionLastCheckUtc = nowUtc;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionTierLevel = 0;
                    return new SubscriptionCheckResult(false, "no_validity_offline", _settings.SubscriptionLastMessage);
                }

                _settings.SubscriptionLastCheckUtc = nowUtc;
                _settings.SubscriptionLastStatusOk = false;
                _settings.SubscriptionTierLevel = 0;
                _settings.SubscriptionLastMessage = "Auth server unreachable and no valid cached subscription is available.";

                return new SubscriptionCheckResult(false, "unreachable", _settings.SubscriptionLastMessage);
            }
        }

        private DateTime? GetCachedValidUntilUtc()
        {
            // Prefer renewal (period end) if available; otherwise fall back to expires.
            // Both are stored as UTC by SubscriptionService.
            if (_settings.SubscriptionRenewalUtc.HasValue)
                return _settings.SubscriptionRenewalUtc.Value;

            if (_settings.SubscriptionExpiresUtc.HasValue)
                return _settings.SubscriptionExpiresUtc.Value;

            return null;
        }

        private static string BuildOfflineMessage(DateTime validUntilUtc)
        {
            var localDate = DateTime.SpecifyKind(validUntilUtc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd");
            return $"Auth server unreachable. Using cached subscription (valid until {localDate}).";
        }

        private static string BuildPlanSummary(AuthSubscriptionDto? sub)
        {
            if (sub == null)
                return string.Empty;

            var planLabel = (sub.LevelLabel ?? sub.Level ?? string.Empty).Trim();
            var priceText = (sub.PriceId ?? string.Empty).Trim();
            var statusText = (sub.Status ?? string.Empty).Trim().ToLowerInvariant();

            var includePriceText =
                !string.IsNullOrEmpty(priceText) &&
                !string.Equals(planLabel, priceText, StringComparison.OrdinalIgnoreCase) &&
                !planLabel.Contains(priceText, StringComparison.OrdinalIgnoreCase) &&
                !priceText.Contains(planLabel, StringComparison.OrdinalIgnoreCase);

            var periodRaw = statusText == "trialing"
                ? (sub.TrialEndsAt ?? sub.PeriodEndAt ?? sub.ExpiresAt ?? string.Empty)
                : (sub.PeriodEndAt ?? sub.ExpiresAt ?? sub.TrialEndsAt ?? string.Empty);

            var dtUtc = DateParseHelper.TryParseUtc(periodRaw);
            var formattedDate = dtUtc.HasValue
                ? DateTime.SpecifyKind(dtUtc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd")
                : string.Empty;

            var dateLabel = statusText == "trialing"
                ? "Trial end date:"
                : "Renewal:";

            if (!string.IsNullOrEmpty(planLabel) &&
                includePriceText &&
                !string.IsNullOrEmpty(formattedDate))
            {
                return $"Plan: {planLabel} - {priceText} - {dateLabel} {formattedDate}";
            }

            if (!string.IsNullOrEmpty(planLabel) &&
                includePriceText)
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

            [JsonPropertyName("tier_level")]
            [JsonConverter(typeof(FlexibleIntConverter))]
            public int TierLevel { get; set; }

            [JsonPropertyName("period_end_at")]
            public string? PeriodEndAt { get; set; }

            [JsonPropertyName("trial_ends_at")]
            public string? TrialEndsAt { get; set; }

            [JsonPropertyName("expires_at")]
            public string? ExpiresAt { get; set; }
        }

        // Some deployments may serialize numeric fields as strings.
        // Accept both JSON number and JSON string for tier level.
        private sealed class FlexibleIntConverter : JsonConverter<int>
        {
            public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                try
                {
                    if (reader.TokenType == JsonTokenType.Number)
                    {
                        if (reader.TryGetInt32(out var n))
                            return n;

                        // Fall back to a wider parse if needed.
                        var l = reader.GetInt64();
                        if (l > int.MaxValue)
                            return int.MaxValue;
                        if (l < int.MinValue)
                            return int.MinValue;
                        return (int)l;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var s = (reader.GetString() ?? string.Empty).Trim();
                        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                            return n;

                        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                        {
                            if (l > int.MaxValue)
                                return int.MaxValue;
                            if (l < int.MinValue)
                                return int.MinValue;
                            return (int)l;
                        }

                        return 0;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                        return 0;
                }
                catch
                {
                }

                // Unknown token type or parse failure.
                try { reader.Skip(); } catch { }
                return 0;
            }

            public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            {
                writer.WriteNumberValue(value);
            }
        }
    }
}
