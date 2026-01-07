// Services/JoesScannerApiClient.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace JoesScanner.Services
{
    public sealed class JoesScannerApiClient : IJoesScannerApiClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;

        public JoesScannerApiClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task<ApiPingResult> PingAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // BaseAddress is expected to already be set by the caller via SetBaseUrl.
                using var response = await _httpClient.GetAsync("wp-json/joes-scanner/v1/ping", cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return new ApiPingResult { Ok = false, Message = $"Ping failed: {(int)response.StatusCode} {response.ReasonPhrase}" };

                // If the endpoint returns JSON, keep it simple and treat success status as Ok.
                return new ApiPingResult { Ok = true, Message = string.IsNullOrWhiteSpace(body) ? "OK" : body };
            }
            catch (Exception ex)
            {
                return new ApiPingResult { Ok = false, Message = $"Ping exception: {ex.Message}" };
            }
        }

        public async Task<ApiAuthResult> AuthenticateAsync(
            string serverUrl,
            string username,
            string password,
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                return new ApiAuthResult { Ok = false, Message = "Server URL is required." };

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return new ApiAuthResult { Ok = false, Message = "Username and password are required." };

            if (string.IsNullOrWhiteSpace(deviceId))
                return new ApiAuthResult { Ok = false, Message = "Device ID is required." };

            try
            {
                SetBaseUrl(serverUrl);

                ApplyBasicAuth(username, password);

                var payload = new
                {
                    device_id = deviceId
                };

                using var response = await _httpClient.PostAsJsonAsync(
                    "wp-json/joes-scanner/v1/auth",
                    payload,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var text = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                    return new ApiAuthResult
                    {
                        Ok = false,
                        Message = $"Auth failed: {(int)response.StatusCode} {response.ReasonPhrase} {text}".Trim()
                    };
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken).ConfigureAwait(false);

                // Expected shape based on your earlier output:
                // { ok: true, session_token: "...", user: { ... }, ... }
                var ok = TryGetBool(json, "ok");
                var sessionToken = TryGetString(json, "session_token");
                var status = TryGetString(json, "status");
                var planLabel = TryGetString(json, "plan_label");
                var message = TryGetString(json, "message");

                return new ApiAuthResult
                {
                    Ok = ok,
                    SessionToken = sessionToken,
                    Status = status,
                    PlanLabel = planLabel,
                    Message = string.IsNullOrWhiteSpace(message) ? (ok ? "OK" : "Auth failed.") : message
                };
            }
            catch (Exception ex)
            {
                return new ApiAuthResult { Ok = false, Message = $"Auth exception: {ex.Message}" };
            }
        }

        private void SetBaseUrl(string serverUrl)
        {
            var trimmed = serverUrl.Trim();

            if (!trimmed.EndsWith("/", StringComparison.Ordinal))
                trimmed += "/";

            _httpClient.BaseAddress = new Uri(trimmed, UriKind.Absolute);
        }

        private void ApplyBasicAuth(string username, string password)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        private static bool TryGetBool(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty(name, out var el))
                return false;

            return el.ValueKind == JsonValueKind.True || (el.ValueKind == JsonValueKind.False ? false : false);
        }

        private static string TryGetString(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return string.Empty;

            if (!root.TryGetProperty(name, out var el))
                return string.Empty;

            return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.ToString();
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
