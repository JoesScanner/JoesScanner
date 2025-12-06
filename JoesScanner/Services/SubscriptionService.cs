using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JoesScanner.Services
{
    public sealed class SubscriptionService : ISubscriptionService
    {
        private const int SubscriptionGraceDays = 3; // change this number when you want a different window
        private static readonly Uri AuthEndpoint =
            new("https://joesscanner.com/wp-json/joes-scanner/v1/auth");

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

        public async Task<SubscriptionCheckResult> EnsureSubscriptionAsync(CancellationToken cancellationToken)
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
                var payload = new
                {
                    username,
                    password,
                    client = "JoesScannerApp",
                    version = "1.0.0"
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

                // --------------------------------------------------------------------
                // 1) HTTP response received (server was contacted)
                // --------------------------------------------------------------------

                // Any non-success HTTP status means: server responded, but not OK.
                // Per your rules, there is NO grace for this case.
                if (!response.IsSuccessStatusCode)
                {
                    _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                    _settings.SubscriptionLastStatusOk = false;
                    _settings.SubscriptionLastLevel = authResponse?.Subscription?.Level ?? string.Empty;
                    _settings.SubscriptionLastMessage =
                        authResponse?.Message ?? response.ReasonPhrase ?? "Denied";

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

                    return new SubscriptionCheckResult(
                        isAllowed: false,
                        errorCode: authResponse?.Error ?? "subscription_denied",
                        message: authResponse?.Message ?? "Subscription not active."
                    );
                }

                // At this point HTTP is success and the payload says Ok=true.
                var active = authResponse.Subscription?.Active == true;

                _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;
                _settings.SubscriptionLastStatusOk = active;
                _settings.SubscriptionLastLevel = authResponse.Subscription?.Level ?? string.Empty;
                _settings.SubscriptionLastMessage = authResponse.Subscription?.Status ?? string.Empty;

                if (!active)
                {
                    return new SubscriptionCheckResult(
                        isAllowed: false,
                        errorCode: "inactive_subscription",
                        message: "Subscription is not active."
                    );
                }

                // Everything checks out: active subscription.
                return new SubscriptionCheckResult(isAllowed: true);
            }
            catch (OperationCanceledException)
            {
                // --------------------------------------------------------------------
                // 2) Auth server could NOT be contacted (timeout)
                // --------------------------------------------------------------------
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
                // --------------------------------------------------------------------
                // 3) Auth server could NOT be contacted (network error, DNS, etc.)
                // --------------------------------------------------------------------
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

        private sealed class AuthResponse
        {
            public bool Ok { get; set; }
            public string? Error { get; set; }
            public string? Message { get; set; }
            public SubscriptionDto? Subscription { get; set; }
        }

        private sealed class SubscriptionDto
        {
            public bool Active { get; set; }
            public string? Status { get; set; }
            public string? Level { get; set; }
            public DateTime? Expires_At { get; set; }
        }
    }
}
