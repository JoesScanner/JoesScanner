using JoesScanner.Models;
using JoesScanner.Services;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoesScanner.Helpers
{
    public static class HostedServerRules
    {
        public const string DefaultServerUrl = "https://app.joesscanner.com";
        private static readonly string[] ProvidedDefaultServerUrls =
        {
            DefaultServerUrl
        };

        public static bool IsHostedStreamServerUrl(string? streamServerUrl)
        {
            var url = (streamServerUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            return IsHostedServerUri(uri);
        }

        public static bool IsHostedServerUri(Uri? uri)
        {
            return uri != null && string.Equals(uri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanUseHostedServiceCredentials(ISettingsService? settings)
        {
            if (settings == null)
                return false;

            var ok = settings.SubscriptionLastStatusOk;
            var expires = settings.SubscriptionExpiresUtc;
            var tier = settings.SubscriptionTierLevel;

            return ok && tier >= 1 && expires.HasValue && expires.Value.ToUniversalTime() > DateTime.UtcNow;
        }

        public static string? GetHostedBasicAuthParameterIfAllowed(ISettingsService? settings)
        {
            return GetApiFirewallBasicAuthParameterIfAllowed(settings, DefaultServerUrl);
        }


        public static async Task<string?> GetApiFirewallBasicAuthParameterEnsuredAsync(ISettingsService? settings, string? serverUrl, CancellationToken cancellationToken = default)
        {
            if (!CanUseHostedServiceCredentials(settings) || string.IsNullOrWhiteSpace(serverUrl) || settings == null)
                return null;

            if (!await EnsureApiFirewallCredentialsAvailableAsync(settings, serverUrl, cancellationToken).ConfigureAwait(false))
                return null;

            return GetApiFirewallBasicAuthParameterIfAllowed(settings, serverUrl);
        }

        public static async Task<bool> EnsureApiFirewallCredentialsAvailableAsync(ISettingsService? settings, string? serverUrl, CancellationToken cancellationToken = default)
        {
            if (!CanUseHostedServiceCredentials(settings) || string.IsNullOrWhiteSpace(serverUrl) || settings == null)
                return false;

            var normalizedServerUrl = NormalizeServerAuthority(serverUrl);
            if (string.IsNullOrWhiteSpace(normalizedServerUrl))
                return false;

            if (settings.TryGetServerFirewallCredentials(normalizedServerUrl, out _, out _))
                return true;

            var entry = ResolveDirectoryServerEntry(settings, normalizedServerUrl);
            if (entry == null || !entry.UsesApiFirewallCredentials)
                return false;

            var sessionToken = (settings.AuthSessionToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sessionToken))
                return false;

            var payload = new
            {
                session_token = sessionToken,
                directory_id = entry.DirectoryId > 0 ? entry.DirectoryId : (int?)null,
                server_url = normalizedServerUrl,
                device_id = settings.DeviceInstallId ?? string.Empty
            };

            try
            {
                var endpointUrl = BuildAuthServerUrl(settings, "/wp-json/joes-scanner/v1/server-firewall-credentials");
                AppLog.Add(() => $"Firewall creds: request start. server={normalizedServerUrl}, endpoint={endpointUrl}");

                using var httpClient = new HttpClient();
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync(endpointUrl, content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Add(() => $"Firewall creds: request failed. server={normalizedServerUrl}, endpoint={endpointUrl}, status={(int)response.StatusCode} {response.ReasonPhrase}");
                    return false;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<ServerFirewallCredentialsResponseDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed == null || !parsed.Ok)
                {
                    AppLog.Add(() => $"Firewall creds: response invalid. server={normalizedServerUrl}, endpoint={endpointUrl}");
                    return false;
                }

                var username = (parsed.Username ?? string.Empty).Trim();
                var password = (parsed.Password ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    AppLog.Add(() => $"Firewall creds: response empty. server={normalizedServerUrl}, endpoint={endpointUrl}");
                    return false;
                }

                settings.SetServerFirewallCredentials(normalizedServerUrl, username, password);
                AppLog.Add(() => $"Firewall creds: request succeeded. server={normalizedServerUrl}, endpoint={endpointUrl}");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Firewall creds: request exception. server={normalizedServerUrl}, ex={ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static ServerDirectoryEntry? ResolveDirectoryServerEntry(ISettingsService settings, string normalizedServerUrl)
        {
            if (IsProvidedDefaultServerUrl(normalizedServerUrl))
            {
                return new ServerDirectoryEntry
                {
                    DirectoryId = 0,
                    Url = DefaultServerUrl,
                    Name = "Joe's Scanner Default",
                    IsOfficial = true,
                    UsesApiFirewallCredentials = true
                };
            }

            foreach (var entry in settings.GetCachedDirectoryServers())
            {
                var candidate = NormalizeServerAuthority(entry?.Url);
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                if (string.Equals(candidate, normalizedServerUrl, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }

            return null;
        }


        public static bool RequiresApiFirewallCredentials(ISettingsService? settings, string? serverUrl)
        {
            if (settings == null || string.IsNullOrWhiteSpace(serverUrl))
                return false;

            var normalizedServerUrl = NormalizeServerAuthority(serverUrl);
            if (string.IsNullOrWhiteSpace(normalizedServerUrl))
                return false;

            var entry = ResolveDirectoryServerEntry(settings, normalizedServerUrl);
            return entry != null && entry.UsesApiFirewallCredentials;
        }

        private static string NormalizeServerAuthority(string? serverUrl)
        {
            var value = (serverUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return string.Empty;

            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static string BuildAuthServerUrl(ISettingsService settings, string path)
        {
            var baseUrl = (settings.AuthServerBaseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "https://joesscanner.com";

            baseUrl = baseUrl.TrimEnd('/');

            var cleanPath = (path ?? string.Empty).Trim();
            if (!cleanPath.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                cleanPath = "/" + cleanPath;

            return baseUrl + cleanPath;
        }

        private sealed class ServerFirewallCredentialsResponseDto
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("password")]
            public string? Password { get; set; }
        }

        public static string? GetApiFirewallBasicAuthParameterIfAllowed(ISettingsService? settings, string? serverUrl)
        {
            if (!CanUseHostedServiceCredentials(settings) || string.IsNullOrWhiteSpace(serverUrl))
                return null;

            if (!settings.TryGetServerFirewallCredentials(serverUrl, out var username, out var password))
                return null;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;

            var raw = $"{username}:{password}";
            return Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(raw));
        }

        public static bool IsProvidedDefaultServerUrl(string? streamServerUrl)
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
    }
}
