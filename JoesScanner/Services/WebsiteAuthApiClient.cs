using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JoesScanner.Services
{
    public sealed class WebsiteAuthApiClient : IWebsiteAuthApiClient, IDisposable
    {
        private readonly HttpClient _httpClient;

        public WebsiteAuthApiClient()
        {
#if IOS || MACCATALYST
            // Apple platforms: use managed handler to avoid ATS blocks for user-configured HTTP auth servers.
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
#else
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
#endif
        }

        public void Dispose()
        {
            try { _httpClient.Dispose(); } catch { }
        }

        public async Task<WebsiteAuthApiResult> AuthenticateAsync(WebsiteAuthApiRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                var payload = new
                {
                    username = request.Username,
                    password = request.Password,
                    device_platform = request.DevicePlatform,
                    device_type = request.DeviceType,
                    device_model = request.DeviceModel,
                    app_version = request.AppVersion,
                    app_build = request.AppBuild,
                    os_version = request.OsVersion,
                    device_id = request.DeviceId,
                    session_token = request.SessionToken
                };

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint)
                {
                    Content = content
                };

                // Apply Basic Auth header (even though the endpoint also accepts body creds).
                // This keeps the client compatible with alternate auth server configurations.
                var raw = $"{request.Username}:{request.Password}";
                var bytes = Encoding.ASCII.GetBytes(raw);
                var base64 = Convert.ToBase64String(bytes);
                message.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);

                using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                WebsiteAuthResponseDto? dto = null;
                try
                {
                    dto = JsonSerializer.Deserialize<WebsiteAuthResponseDto>(
                        body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                }

                if (!response.IsSuccessStatusCode)
                {
                    var statusInt = (int)response.StatusCode;
                    var msg = dto?.Message;
                    if (string.IsNullOrWhiteSpace(msg))
                        msg = $"Auth server responded with HTTP {statusInt} {response.ReasonPhrase}.";

                    return new WebsiteAuthApiResult(
                        WebsiteAuthApiResultKind.HttpError,
                        response.StatusCode,
                        msg.Trim(),
                        dto,
                        body);
                }

                if (dto == null)
                {
                    return new WebsiteAuthApiResult(
                        WebsiteAuthApiResultKind.InvalidResponse,
                        response.StatusCode,
                        "Invalid server response",
                        null,
                        body);
                }

                if (!dto.Ok)
                {
                    var code = (dto.Error ?? "denied").Trim();
                    var msg = (dto.Message ?? "Account validation failed.").Trim();
                    return new WebsiteAuthApiResult(
                        WebsiteAuthApiResultKind.ApiDenied,
                        response.StatusCode,
                        $"{code}: {msg}",
                        dto,
                        body);
                }

                return new WebsiteAuthApiResult(
                    WebsiteAuthApiResultKind.Success,
                    response.StatusCode,
                    (dto.Message ?? "OK").Trim(),
                    dto,
                    body);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new WebsiteAuthApiResult(
                    WebsiteAuthApiResultKind.Timeout,
                    null,
                    "Auth server timeout",
                    null,
                    null);
            }
            catch (OperationCanceledException)
            {
                return new WebsiteAuthApiResult(
                    WebsiteAuthApiResultKind.Canceled,
                    null,
                    "Canceled",
                    null,
                    null);
            }
            catch (Exception ex)
            {
                var msg = string.IsNullOrWhiteSpace(ex.Message) ? "Auth server unreachable" : ex.Message;
                return new WebsiteAuthApiResult(
                    WebsiteAuthApiResultKind.Unreachable,
                    null,
                    msg,
                    null,
                    null);
            }
        }

        public static DateTime? TryParseUtc(string? raw)
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

        public static string BuildPlanSummary(WebsiteAuthSubscriptionDto? sub)
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

            var dateLabel = statusText == "trialing" ? "Trial end date:" : "Renewal:";

            if (!string.IsNullOrEmpty(planLabel) && !string.IsNullOrEmpty(priceText) && !string.IsNullOrEmpty(formattedDate))
                return $"Plan: {planLabel} - {priceText} - {dateLabel} {formattedDate}";

            if (!string.IsNullOrEmpty(planLabel) && !string.IsNullOrEmpty(priceText))
                return $"Plan: {planLabel} - {priceText}";

            if (!string.IsNullOrEmpty(planLabel) && !string.IsNullOrEmpty(formattedDate))
                return $"Plan: {planLabel} - {dateLabel} {formattedDate}";

            if (!string.IsNullOrEmpty(planLabel))
                return $"Plan: {planLabel}";

            if (!string.IsNullOrEmpty(formattedDate))
                return $"{dateLabel} {formattedDate}";

            return string.Empty;
        }
    }
}
