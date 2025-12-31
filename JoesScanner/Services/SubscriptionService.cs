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

                AuthResponse? authResponse = null;
                try
                {
                    authResponse = JsonSerializer.Deserialize<AuthResponse>(
                        responseBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                }

                var statusInt = (int)response.StatusCode;

                // Any HTTP response counts as "server contacted", so there is no grace window on a denial.
                if (!response.IsSuccessStatusCode)
                {
                    _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionLastLevel = authResponse?.SubscriptionLevel ?? string.Empty;
                    _settings.SubscriptionExpiresUtc = authResponse?.ExpiresUtc;
                    _settings.SubscriptionRenewalUtc = authResponse?.RenewalUtc;
                    _settings.SubscriptionLastMessage = authResponse?.Message ?? response.ReasonPhrase ?? "Denied";

                    return new SubscriptionCheckResult(false, "http_" + statusInt.ToString(CultureInfo.InvariantCulture), _settings.SubscriptionLastMessage);
                }

                if (authResponse == null)
                {
                    _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionLastLevel = string.Empty;
                    _settings.SubscriptionExpiresUtc = null;
                    _settings.SubscriptionRenewalUtc = null;
                    _settings.SubscriptionLastMessage = "Invalid server response";

                    return new SubscriptionCheckResult(false, "invalid_response", _settings.SubscriptionLastMessage);
                }

                if (!authResponse.Ok)
                {
                    _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionLastLevel = authResponse.SubscriptionLevel ?? string.Empty;
                    _settings.SubscriptionExpiresUtc = authResponse.ExpiresUtc;
                    _settings.SubscriptionRenewalUtc = authResponse.RenewalUtc;
                    _settings.SubscriptionLastMessage = authResponse.Message ?? "Denied";

                    return new SubscriptionCheckResult(false, authResponse.Error ?? "denied", _settings.SubscriptionLastMessage);
                }

                // Success
                _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                _settings.SubscriptionLastStatusOk = true;
                _settings.SubscriptionLastLevel = authResponse.SubscriptionLevel ?? string.Empty;
                _settings.SubscriptionExpiresUtc = authResponse.ExpiresUtc;
                _settings.SubscriptionRenewalUtc = authResponse.RenewalUtc;

                var planSummary = authResponse.Message ?? "OK";
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

            [JsonPropertyName("subscription_level")]
            public string? SubscriptionLevel { get; set; }

            [JsonPropertyName("expires_utc")]
            public DateTime? ExpiresUtc { get; set; }

            [JsonPropertyName("renewal_utc")]
            public DateTime? RenewalUtc { get; set; }
        }
    }
}
