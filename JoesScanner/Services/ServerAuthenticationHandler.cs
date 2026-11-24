using System.Net;
using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal readonly record struct RecorderRequestScope(Uri Endpoint, Guid? ServerId = null);

internal sealed class ServerAuthenticationHandler(ServerAuthentication authentication, HttpMessageHandler transport,
    RecorderRequestScope? recorder = null)
    : DelegatingHandler(transport)
{
    // Settings can probe an unsaved custom server with or without its proposed credentials.
    internal static readonly HttpRequestOptionsKey<bool> CustomProbe = new("JoesScanner.CustomServerProbe");

    internal static readonly HttpRequestOptionsKey<RecorderRequestScope> Recorder = new("JoesScanner.RecorderOrigin");

    internal static HttpClient CreateClient(ISettingsService settings, HttpMessageHandler? transport = null,
        RecorderRequestScope? recorder = null) =>
        new(new ServerAuthenticationHandler(ServerAuthentication.For(settings),
            transport ?? new HttpClientHandler { AllowAutoRedirect = false }, recorder));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var destination = request.RequestUri ?? throw new InvalidOperationException("A scanner server URL is required.");
        if (destination.UserInfo.Length != 0)
            throw new HttpRequestException("Remove embedded credentials from the scanner server URL.");

        request.Headers.Host = null;
        var configured = recorder ?? (request.Options.TryGetValue(Recorder, out var origin) ? origin : null);
        var belongsToRecorder = configured == null || HostedServerRules.SameOrigin(destination, configured.Value.Endpoint);
        var firewall = belongsToRecorder && authentication.RequiresFirewall(destination);
        if (!belongsToRecorder)
            request.Headers.Authorization = null;
        else if (firewall)
            request.Headers.Authorization = await authentication.GetFirewallAuthorizationAsync(destination, cancellationToken).ConfigureAwait(false);
        else if (!request.Options.TryGetValue(CustomProbe, out var isProbe) || !isProbe)
            request.Headers.Authorization = configured == null
                ? authentication.GetCustomAuthorization(destination)
                : authentication.GetCustomAuthorization(destination, configured.Value);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (firewall && IsAuthenticationDenied(response.StatusCode))
        {
            response.Dispose();
            var updated = await authentication.GetFirewallAuthorizationAsync(
                destination, cancellationToken, request.Headers.Authorization).ConfigureAwait(false);
            using var retry = CopyRequest(request, destination);
            retry.Headers.Authorization = updated;
            response = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
            if (IsAuthenticationDenied(response.StatusCode))
            {
                var status = response.StatusCode;
                response.Dispose();
                await authentication.MarkRejectedAsync(destination, updated, cancellationToken).ConfigureAwait(false);
                throw new FirewallAuthenticationException($"The scanner server rejected its refreshed firewall credentials (HTTP {(int)status}).", status);
            }
        }

        // Follow ordinary read redirects, stripping credentials permanently once the origin changes.
        // WebSocket handshakes and requests with a body must use the configured endpoint directly.
        if ((request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) || request.Headers.Upgrade.Count != 0)
            return response;

        var redirected = destination;
        var sameOrigin = true;
        for (var count = 0; count < 5 && IsRedirect(response.StatusCode) && response.Headers.Location is { } location; count++)
        {
            if (!Uri.TryCreate(redirected, location, out var target) ||
                target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 ||
                (redirected.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps))
            {
                break;
            }

            sameOrigin &= HostedServerRules.SameOrigin(destination, target);
            if (!sameOrigin && authentication.RequiresFirewall(target))
                break;

            using var redirect = CopyRequest(request, target);
            // Never forward Basic credentials via a redirect, even on the same origin.
            redirect.Headers.Authorization = null;
            response.Dispose();
            response = await base.SendAsync(redirect, cancellationToken).ConfigureAwait(false);
            redirected = target;
        }

        return response;
    }

    private static bool IsAuthenticationDenied(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private static HttpRequestMessage CopyRequest(HttpRequestMessage request, Uri destination)
    {
        // Scanner POST bodies use replayable string/form content. The original request owns it.
        var copy = new RequestCopy(request.Method, destination)
        {
            Content = request.Content,
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options)
            copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        return copy;
    }

    private sealed class RequestCopy(HttpMethod method, Uri destination) : HttpRequestMessage(method, destination)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Content = null;
            base.Dispose(disposing);
        }
    }
}
