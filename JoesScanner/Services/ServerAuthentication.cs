using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal sealed class ServerAuthentication(ISettingsService settings, HttpClient credentialClient)
{
    private static readonly HttpClient CredentialClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private static readonly ConditionalWeakTable<ISettingsService, ServerAuthentication> Instances = new();
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal static ServerAuthentication For(ISettingsService settings) =>
        Instances.GetValue(settings, source => new ServerAuthentication(source, CredentialClient));

    internal bool RequiresFirewall(Uri uri) => HostedServerRules.RequiresApiFirewallCredentials(settings, uri);

    internal AuthenticationHeaderValue? GetCustomAuthorization(Uri destination)
    {
        if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var configured) ||
            !HostedServerRules.SameOrigin(destination, configured) ||
            RequiresFirewall(configured))
        {
            return null;
        }

        if (settings.TryGetServerCredentials(configured.AbsoluteUri, out var username, out var password))
            return BuildBasicAuthorization(username, password);

        username = settings.BasicAuthUsername.Trim();
        password = settings.BasicAuthPassword.Trim();
        if (string.IsNullOrWhiteSpace(username) ||
            !Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var current) ||
            !HostedServerRules.SameOrigin(configured, current))
        {
            return null;
        }

        return BuildBasicAuthorization(username, TextNormalizationHelper.NormalizeSmartQuotes(password));
    }

    internal async Task<AuthenticationHeaderValue> GetFirewallAuthorizationAsync(
        Uri destination, CancellationToken cancellationToken, AuthenticationHeaderValue? rejected = null)
    {
        var isDefault = HostedServerRules.IsHostedServerUri(destination);
        var directoryId = isDefault ? null : HostedServerRules.FindFirewallDirectoryId(settings, destination);
        if ((!isDefault && !directoryId.HasValue) ||
            HostedServerRules.HttpScheme(destination) != Uri.UriSchemeHttps ||
            (isDefault && !HostedServerRules.SameOrigin(destination, new Uri(HostedServerRules.DefaultServerUrl))))
        {
            throw new FirewallAuthenticationException("Firewall credentials require the server's configured HTTPS origin.");
        }

        var session = ReadSession(directoryId);
        var serverUrl = new UriBuilder(Uri.UriSchemeHttps, destination.IdnHost, destination.Port)
            .Uri.GetLeftPart(UriPartial.Authority);
        var entry = _cache.GetOrAdd(serverUrl, static _ => new CacheEntry());

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session != ReadSession(directoryId))
                throw new FirewallAuthenticationException("The Joe's Scanner session changed. Try the connection again.");

            if (entry.Session != session)
            {
                entry.Session = session;
                entry.Authorization = null;
                entry.RetryAfter = default;
            }

            // Concurrent requests rejected with the same cached credential share one refresh.
            if (entry.Authorization is { } cached && !ReferenceEquals(cached, rejected))
                return cached;

            if (entry.RetryAfter > DateTimeOffset.UtcNow)
            {
                var message = rejected == null
                    ? "Firewall credentials are temporarily unavailable. Try again shortly or verify your Joe's Scanner account."
                    : "The scanner server rejected its API-issued firewall credentials. A refresh was attempted recently. Try again shortly.";
                throw new FirewallAuthenticationException(message);
            }

            entry.Authorization = null;
            try
            {
                entry.Authorization = await RequestFirewallAuthorizationAsync(session, serverUrl, cancellationToken).ConfigureAwait(false);
                if (session != ReadSession(directoryId))
                {
                    entry.Authorization = null;
                    throw new FirewallAuthenticationException("The Joe's Scanner session changed. Try the connection again.");
                }

                return entry.Authorization;
            }
            catch (FirewallAuthenticationException)
            {
                entry.RetryAfter = DateTimeOffset.UtcNow + FailureCooldown;
                throw;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    internal async Task MarkRejectedAsync(Uri destination, AuthenticationHeaderValue authorization, CancellationToken cancellationToken)
    {
        var serverUrl = new UriBuilder(Uri.UriSchemeHttps, destination.IdnHost, destination.Port)
            .Uri.GetLeftPart(UriPartial.Authority);
        if (!_cache.TryGetValue(serverUrl, out var entry))
            return;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(entry.Authorization, authorization))
            {
                // A single endpoint can deny access while the same firewall credential still works elsewhere.
                entry.RetryAfter = DateTimeOffset.UtcNow + FailureCooldown;
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private Session ReadSession(int? directoryId)
    {
        var token = settings.AuthSessionToken.Trim();
        if (string.IsNullOrEmpty(token) || !settings.HasActiveHostedSubscription())
            throw new FirewallAuthenticationException("Firewall credentials require a verified Joe's Scanner account with an active subscription. Verify your account in Settings.");

        var baseUrl = settings.AuthServerBaseUrl.Trim();
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = "https://joesscanner.com";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || baseUri.UserInfo.Length != 0 ||
            baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0)
        {
            throw new FirewallAuthenticationException("The WordPress firewall credential endpoint requires a valid HTTPS address.");
        }

        var endpoint = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/wp-json/joes-scanner/v1/server-firewall-credentials");
        return new Session(endpoint, token, directoryId > 0 ? directoryId : null, settings.DeviceInstallId);
    }

    private async Task<AuthenticationHeaderValue> RequestFirewallAuthorizationAsync(
        Session session, string serverUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await credentialClient.PostAsJsonAsync(session.Endpoint, new
            {
                session_token = session.Token,
                directory_id = session.DirectoryId,
                server_url = serverUrl,
                device_id = session.DeviceId
            }, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new FirewallAuthenticationException($"WordPress could not issue firewall credentials (HTTP {(int)response.StatusCode}). Verify your Joe's Scanner account in Settings.");

            var credentials = await response.Content.ReadFromJsonAsync<FirewallCredentialsResponse>(cancellationToken)
                .ConfigureAwait(false);
            if (credentials is not { Ok: true } ||
                string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password) ||
                credentials.Username.Contains(':'))
            {
                throw new FirewallAuthenticationException("WordPress did not return usable firewall credentials.");
            }

            return BuildBasicAuthorization(credentials.Username, credentials.Password);
        }
        catch (Exception ex) when (ex is not FirewallAuthenticationException &&
                                   (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException ||
                                    ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new FirewallAuthenticationException("Could not retrieve firewall credentials from WordPress. Try again or verify your Joe's Scanner account.");
        }
    }

    private static AuthenticationHeaderValue BuildBasicAuthorization(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    private sealed record Session(Uri Endpoint, string Token, int? DirectoryId, string DeviceId);
    private sealed record FirewallCredentialsResponse(bool Ok, string? Username, string? Password);

    private sealed class CacheEntry
    {
        internal SemaphoreSlim Gate { get; } = new(1);
        internal Session? Session { get; set; }
        internal AuthenticationHeaderValue? Authorization { get; set; }
        internal DateTimeOffset RetryAfter { get; set; }
    }
}

internal sealed class FirewallAuthenticationException(string message) : HttpRequestException(message);
