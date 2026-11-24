using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JoesScanner.Helpers;

namespace JoesScanner.Services
{
    public sealed class SubscriptionService : ISubscriptionService, IDisposable
    {
        private readonly ISettingsService _settings;
        private readonly ITelemetryService _telemetryService;
        private readonly HttpClient _httpClient;

        public SubscriptionService(ISettingsService settings, ITelemetryService telemetryService)
            : this(settings, telemetryService, WordPressSession.CreateClient())
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        internal SubscriptionService(ISettingsService settings, ITelemetryService telemetryService, HttpClient httpClient)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

            _httpClient = httpClient;
        }

        public void Dispose()
        {
            try { _httpClient.Dispose(); } catch { }
        }

        public Task<SubscriptionCheckResult> EnsureSubscriptionAsync(CancellationToken cancellationToken) =>
            VerifyAsync(_settings.ServerUrl, requireSelection: true, cancellationToken);

        public Task<SubscriptionCheckResult> EnsureSubscriptionForRecorderAsync(string server, CancellationToken cancellationToken) =>
            VerifyAsync(server, requireSelection: false, cancellationToken);

        private async Task<SubscriptionCheckResult> VerifyAsync(string server, bool requireSelection, CancellationToken cancellationToken)
        {
            var attempt = WordPressSession.Capture(_settings);
            string user, password;
            Uri endpoint;
            try
            {
                endpoint = attempt.Endpoint("auth");
                _settings.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out user, out password);
            }
            catch (CredentialStorageException ex)
            {
                return new(false, "credential_storage_unavailable", ex.Message);
            }

            password = TextNormalizationHelper.NormalizeSmartQuotes(password);
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
                return new(false, "missing_credentials", _settings.SavedCredentialWarning ??
                    "Enter your username and password in Settings before connecting.");

            bool IsSelected() => requireSelection
                ? string.Equals(server, _settings.ServerUrl, StringComparison.Ordinal)
                : HostedServerRules.RequiresApiFirewallCredentials(_settings, server);
            static SubscriptionCheckResult Replaced() =>
                new(false, "account_changed", "The recorder or hosted account changed during verification.");
            var payload = new
            {
                username = user,
                password,
                device_platform = DeviceInfo.Platform.ToString(),
                device_type = DeviceInfo.Idiom.ToString(),
                device_model = DeviceInfoHelper.CombineManufacturerAndModel(DeviceInfo.Manufacturer, DeviceInfo.Model),
                app_version = AppInfo.Current.VersionString ?? string.Empty,
                app_build = AppInfo.Current.BuildString ?? string.Empty,
                os_version = DeviceInfo.VersionString ?? string.Empty,
                device_id = _settings.DeviceInstallId,
                session_token = attempt.Token
            };

            if (!attempt.IsCurrent || !IsSelected())
                return Replaced();

            HttpStatusCode status;
            AuthResponseDto? responseBody = null;
            try
            {
                var options = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                using var content = new StringContent(JsonSerializer.Serialize(payload, options), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
                status = response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    responseBody = JsonSerializer.Deserialize<AuthResponseDto>(body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException) { }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or IOException or TaskCanceledException)
            {
                return ApplyOffline();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!attempt.IsCurrent || !IsSelected())
                return Replaced();

            var code = (int)status;
            if (code == 429 || code >= 500)
                return new(false, "http_" + code, $"The account service is temporarily unavailable (HTTP {code}).");

            var subscription = responseBody?.Subscription;
            var now = DateTime.UtcNow;
            var expires = DateParseHelper.TryParseUtc(subscription?.ExpiresAt);
            var trial = string.Equals(subscription?.Status, "trialing", StringComparison.OrdinalIgnoreCase);
            var effectiveEnd = DateParseHelper.TryParseUtc(subscription?.PeriodEndAt) ?? expires ??
                DateParseHelper.TryParseUtc(subscription?.TrialEndsAt);
            var issuedToken = (responseBody?.SessionToken ?? "").Trim();
            string Safe(string? value) => DiagnosticRedaction.SanitizeAuthenticationMessage(value, user, password, attempt.Token, issuedToken);
            var allowed = code is >= 200 and < 300 && responseBody is { Ok: true } &&
                subscription is { Active: true } && !(effectiveEnd < now);
            string message;
            if (allowed)
            {
                message = Safe(BuildPlanSummary(subscription));
                if (string.IsNullOrWhiteSpace(message))
                    message = Safe(responseBody?.Message);
                if (string.IsNullOrWhiteSpace(message))
                    message = "OK";
            }
            else if (effectiveEnd < now)
                message = $"Subscription expired on {effectiveEnd.Value.ToLocalTime():yyyy-MM-dd}.";
            else
                message = Safe(responseBody?.Message ?? (code is >= 200 and < 300
                    ? "The account server did not confirm an active subscription."
                    : $"Authentication request denied (HTTP {code})."));

            var published = false;
            var snapshot = new HostedSubscriptionSnapshot(allowed, allowed, allowed ? subscription!.TierLevel : 0, now,
                allowed ? expires : effectiveEnd, trial ? null : DateParseHelper.TryParseUtc(subscription?.PeriodEndAt) ?? expires,
                Safe(subscription?.LevelLabel ?? subscription?.Level), Safe(subscription?.PriceId), message);
            if (!attempt.TryApply(() =>
                {
                    if (!IsSelected())
                        return;
                    _settings.SaveHostedSubscription(snapshot);
                    published = true;
                }) || !published)
                return Replaced();

            if (allowed && !string.IsNullOrWhiteSpace(issuedToken))
                await _telemetryService.AdoptSessionTokenAsync(issuedToken, "auth_session_token_issued", cancellationToken,
                    attempt.Version).ConfigureAwait(false);

            var current = _settings.ReadAccountSession();
            if (!IsSelected() || (!attempt.IsCurrent &&
                (current.Version != attempt.Version + 1 || !allowed || issuedToken.Length == 0 || current.Token != issuedToken)))
                return Replaced();
            return new(allowed, allowed ? null : "http_" + code, message)
            {
                VerifiedOnlineTier = allowed ? snapshot.Tier : null
            };

            SubscriptionCheckResult ApplyOffline()
            {
                SubscriptionCheckResult result = Replaced();
                attempt.TryApply(() =>
                {
                    if (!IsSelected())
                        return;
                    var cached = _settings.ReadHostedSubscription();
                    var until = cached.RenewalUtc ?? cached.ExpiresUtc;
                    var usable = cached.IsAllowed && until.HasValue && until.Value >= DateTime.UtcNow;
                    var note = usable ? BuildOfflineMessage(until!.Value) :
                        "Auth server unreachable and no valid cached subscription is available.";
                    _settings.SaveHostedSubscription(cached with
                    {
                        IsAllowed = usable,
                        VerifiedOnline = false,
                        Tier = usable ? cached.Tier : 0,
                        Message = note
                    });
                    result = new(usable, usable ? "offline_cached" : "unreachable", note);
                });
                return result;
            }
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
        internal sealed class FlexibleIntConverter : JsonConverter<int>
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
