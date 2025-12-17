using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;

namespace JoesScanner.Services
{
    public sealed class SubscriptionService : ISubscriptionService
    {
        // Change this number when you want a different grace window
        private const int SubscriptionGraceDays = 3;

        private static readonly Uri AuthEndpoint =
            new("https://joesscanner.com/wp-json/joes-scanner/v1/auth");

        private static readonly Uri PingEndpoint =
            new("https://joesscanner.com/wp-json/joes-scanner/v1/ping");

        private readonly object _heartbeatGate = new();
        private CancellationTokenSource? _heartbeatCts;
        private Task? _heartbeatTask;
        private bool _pingDisabled;


        private readonly ISettingsService _settings;
        private readonly HttpClient _httpClient;

        public SubscriptionService(ISettingsService settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public async Task<SubscriptionCheckResult> EnsureSubscriptionAsync(
            CancellationToken cancellationToken)
        {
            var username = _settings.BasicAuthUsername;
            var password = _settings.BasicAuthPassword;

            // These are the user's Joe's Scanner account credentials from settings.
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return new SubscriptionCheckResult(
                    isAllowed: false,
                    errorCode: "missing_credentials",
                    message: "Scanner account username and password are not configured."
                );
            }

            var lastCheck = _settings.SubscriptionLastCheckUtc;
            var lastOk = _settings.SubscriptionLastStatusOk;

            // This flag is only used when the auth server cannot be contacted at all.
            var withinGrace =
                lastOk &&
                lastCheck.HasValue &&
                DateTime.UtcNow - lastCheck.Value <= TimeSpan.FromDays(SubscriptionGraceDays);

            try
            {
                var appVersion = AppInfo.Current.VersionString ?? string.Empty;
                var appBuild = AppInfo.Current.BuildString ?? string.Empty;

                var platform = DeviceInfo.Platform.ToString();
                var type = DeviceInfo.Idiom.ToString();
                var model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
                var osVersion = DeviceInfo.VersionString ?? string.Empty;

                var payload = new
                {
                    username,
                    password,

                    // Backwards compatible fields
                    client = "JoesScannerApp",
                    version = appVersion,

                    // Fields the WordPress plugin expects for reporting
                    device_platform = platform,
                    device_type = type,
                    device_model = model,
                    app_version = appVersion,
                    app_build = appBuild,
                    os_version = osVersion,
                    device_id = _settings.DeviceInstallId,

                    // Optional but recommended so the server can correlate the same session
                    session_token = _settings.AuthSessionToken
                };

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _httpClient.PostAsync(AuthEndpoint, content, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                AuthResponse? authResponse = null;
                try
                {
                    authResponse = JsonSerializer.Deserialize<AuthResponse>(
                        responseBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    // If the body is not valid JSON, we still have an HTTP response.
                    // That counts as "server contacted", so no grace here.
                }

                var statusInt = (int)response.StatusCode;

                // ------------------------------------------------------------
                // 1) HTTP response received (server was contacted)
                // ------------------------------------------------------------

                // Any non-success HTTP status means: server responded, but not OK.
                // Per your rules, there is NO grace for this case.
                if (!response.IsSuccessStatusCode)
                {
                    _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionLastLevel = authResponse?.Subscription?.Level ?? string.Empty;
                    _settings.SubscriptionLastMessage =
                        authResponse?.Message ?? response.ReasonPhrase ?? "Denied";

                    SetSessionToken(null);

                    return new SubscriptionCheckResult(
                        isAllowed: false,
                        errorCode: authResponse?.Error ?? "auth_http_error",
                        message: authResponse?.Message ?? $"Auth server error HTTP {statusInt}."
                    );
                }

                // HTTP is 2xx but the payload may still indicate failure.
                if (authResponse == null || authResponse.Ok == false)
                {
                    _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionLastLevel = authResponse?.Subscription?.Level ?? string.Empty;
                    _settings.SubscriptionLastMessage = authResponse?.Message ?? "Denied";

                    SetSessionToken(null);

                    return new SubscriptionCheckResult(
                        isAllowed: false,
                        errorCode: authResponse?.Error ?? "subscription_denied",
                        message: authResponse?.Message ?? "Subscription not active."
                    );
                }

                var sub = authResponse.Subscription;
                var active = sub?.Active == true;

                _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                _settings.SubscriptionLastStatusOk = active;

                if (!active)
                {
                    // Keep some basic info for diagnostics, but do not mark as ok.
                    _settings.SubscriptionLastLevel = sub?.Level ?? string.Empty;
                    _settings.SubscriptionLastMessage = sub?.Status ?? "inactive";

                    SetSessionToken(null);

                    return new SubscriptionCheckResult(
                        isAllowed: false,
                        errorCode: "inactive_subscription",
                        message: "Subscription is not active."
                    );
                }

                // ----------------------------------------------------------------
                // Active subscription: build the same plan summary format used by
                // SettingsViewModel.ValidateServerUrlAsync so the Settings page
                // shows consistent plan + trial/renewal information on startup.
                // ----------------------------------------------------------------

                // Prefer the human label sent by the API, fall back to level (price id) if needed.
                var planLabelRaw = sub?.LevelLabel ?? sub?.Level ?? string.Empty;
                var periodEndRaw = sub?.PeriodEndAt ?? sub?.TrialEndsAt ?? string.Empty;
                var statusRaw = sub?.Status ?? string.Empty;
                var priceIdRaw = sub?.PriceId ?? string.Empty;

                var planLabel = planLabelRaw.Trim();
                var statusText = statusRaw.Trim().ToLowerInvariant();
                var periodEnd = periodEndRaw.Trim();

                string formattedDate = string.Empty;
                if (!string.IsNullOrEmpty(periodEnd))
                {
                    if (DateTime.TryParse(
                            periodEnd,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var dt)
                        || DateTime.TryParse(periodEnd, out dt))
                    {
                        if (dt.Kind == DateTimeKind.Unspecified)
                            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

                        // Same display format as the Settings page
                        formattedDate = dt.ToLocalTime().ToString("yyyy-MM-dd");
                    }
                }

                // Keep the same labels as the Settings page logic
                string dateLabel = statusText == "trialing"
                    ? "Trial end date:"
                    : "Renewal:";

                string planSummary;
                if (!string.IsNullOrEmpty(planLabel) && !string.IsNullOrEmpty(formattedDate))
                {
                    planSummary = $"Plan: {planLabel} - {dateLabel} {formattedDate}";
                }
                else if (!string.IsNullOrEmpty(planLabel))
                {
                    planSummary = $"Plan: {planLabel}";
                }
                else if (!string.IsNullOrEmpty(formattedDate))
                {
                    planSummary = $"{dateLabel} {formattedDate}";
                }
                else
                {
                    planSummary = string.Empty;
                }

                // Cache fields for SettingsViewModel.UpdateSubscriptionSummaryFromSettings()
                _settings.SubscriptionPriceId = priceIdRaw;
                _settings.SubscriptionLastLevel = planLabel;
                _settings.SubscriptionRenewalUtc = null; // you are not currently using this for display
                _settings.SubscriptionLastMessage = planSummary;


                SetSessionToken(authResponse.SessionToken);

                // Everything checks out: active subscription.
                return new SubscriptionCheckResult(isAllowed: true);
            }
            catch (OperationCanceledException)
            {
                // ------------------------------------------------------------
                // 2) Auth server could NOT be contacted (timeout)
                // ------------------------------------------------------------
                if (withinGrace)
                {
                    return new SubscriptionCheckResult(
                        isAllowed: true,
                        errorCode: "network_timeout_grace",
                        message: "Auth server timeout; using cached subscription ok status."
                    );
                }

                return new SubscriptionCheckResult(
                    isAllowed: false,
                    errorCode: "network_timeout",
                    message: "Auth server timeout and no valid cached subscription."
                );
            }
            catch (Exception)
            {
                // ------------------------------------------------------------
                // 3) Auth server could NOT be contacted (network error, DNS, etc.)
                // ------------------------------------------------------------
                if (withinGrace)
                {
                    return new SubscriptionCheckResult(
                        isAllowed: true,
                        errorCode: "network_error_grace",
                        message: "Auth server unreachable; using cached subscription ok status."
                    );
                }

                return new SubscriptionCheckResult(
                    isAllowed: false,
                    errorCode: "network_error",
                    message: "Auth server unreachable and no valid cached subscription."
                );
            }
        }


        private static string CombineDeviceModel(string manufacturer, string model)
        {
            var mfg = (manufacturer ?? string.Empty).Trim();
            var mdl = (model ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(mfg))
                return mdl;

            if (string.IsNullOrWhiteSpace(mdl))
                return mfg;

            return mfg + " " + mdl;
        }

        private void SetSessionToken(string? token)
        {
            token = (token ?? string.Empty).Trim();
            _settings.AuthSessionToken = token;

            if (string.IsNullOrWhiteSpace(token))
            {
                StopHeartbeat();
                return;
            }

            EnsureHeartbeatRunning();
        }

        private void EnsureHeartbeatRunning()
        {
            lock (_heartbeatGate)
            {
                if (_pingDisabled)
                    return;

                if (_heartbeatTask != null && !_heartbeatTask.IsCompleted)
                    return;

                if (string.IsNullOrWhiteSpace(_settings.AuthSessionToken))
                    return;

                _heartbeatCts = new CancellationTokenSource();
                var token = _heartbeatCts.Token;

                _heartbeatTask = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        var ok = await TrySendPingAsync(token);
                        if (!ok)
                        {
                            _pingDisabled = true;
                            break;
                        }

                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(1), token);
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                    }
                }, token);
            }
        }

        private void StopHeartbeat()
        {
            lock (_heartbeatGate)
            {
                try
                {
                    _heartbeatCts?.Cancel();
                }
                catch
                {
                }
                finally
                {
                    _heartbeatCts = null;
                    _heartbeatTask = null;
                }
            }
        }

        private async Task<bool> TrySendPingAsync(CancellationToken cancellationToken)
        {
            var sessionToken = (_settings.AuthSessionToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sessionToken))
                return true;

            var appVersion = AppInfo.Current.VersionString ?? string.Empty;
            var appBuild = AppInfo.Current.BuildString ?? string.Empty;

            var platform = DeviceInfo.Platform.ToString();
            var type = DeviceInfo.Idiom.ToString();
            var model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
            var osVersion = DeviceInfo.VersionString ?? string.Empty;

            var payload = new
            {
                session_token = sessionToken,
                device_id = _settings.DeviceInstallId,

                device_platform = platform,
                device_type = type,
                device_model = model,

                app_version = appVersion,
                app_build = appBuild,
                os_version = osVersion
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(PingEndpoint, content, cancellationToken);

            if ((int)response.StatusCode == 404 || (int)response.StatusCode == 405)
                return false;

            return true;
        }

        private sealed class AuthResponse
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
            public SubscriptionDto? Subscription { get; set; }
        }

        private sealed class SubscriptionDto
        {
            [JsonPropertyName("active")]
            public bool Active { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }

            // Raw price id the PHP sends as "level"
            [JsonPropertyName("level")]
            public string? Level { get; set; }

            // Human label the PHP sends as "level_label"
            [JsonPropertyName("level_label")]
            public string? LevelLabel { get; set; }

            [JsonPropertyName("price_id")]
            public string? PriceId { get; set; }

            // Keep these as strings; we parse them manually.
            [JsonPropertyName("period_end_at")]
            public string? PeriodEndAt { get; set; }

            [JsonPropertyName("trial_ends_at")]
            public string? TrialEndsAt { get; set; }
        }
    }
}
