using System.Net;
using System.Runtime.CompilerServices;
using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal sealed class PrivacyNetworkPolicy
{
    private static readonly ConditionalWeakTable<ISettingsService, PrivacyNetworkPolicy> Instances = new();
    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private CancellationTokenSource _normalMode = new();
    private bool _localOnly;

    private PrivacyNetworkPolicy(ISettingsService settings)
    {
        _settings = settings;
        _settings.LocalOnlyModeChanged += (_, _) => ApplyMode();
        ApplyMode();
    }

    internal static PrivacyNetworkPolicy For(ISettingsService settings) =>
        Instances.GetValue(settings, static source => new PrivacyNetworkPolicy(source));

    private void ApplyMode()
    {
        CancellationTokenSource? retired = null;
        var local = _settings.LocalOnlyMode;
        lock (_gate)
        {
            if (local == _localOnly)
                return;
            _localOnly = local;
            if (local)
                retired = _normalMode;
            else
                _normalMode = new();
        }
        if (retired == null)
            return;

        try { retired.Cancel(); }
        catch (AggregateException ex) { AppLog.Add(() => $"A privacy cancellation callback failed ({ex.GetType().Name})."); }
        finally { retired.Dispose(); }
    }

    internal bool AllowsCloud
    {
        get { lock (_gate) return !_localOnly; }
    }

    internal CancellationToken Generation
    {
        get { lock (_gate) return _localOnly ? new CancellationToken(canceled: true) : _normalMode.Token; }
    }

    internal CancellationToken CloudToken()
    {
        lock (_gate)
        {
            if (_localOnly)
                throw new HttpRequestException("This network service is unavailable in Local Only Mode.");
            return _normalMode.Token;
        }
    }

    internal bool Allows(Uri uri) => AllowsCloud || !IsJoeManaged(uri);

    internal bool IsJoeManaged(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.');
        return host.Equals("joesscanner.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".joesscanner.com", StringComparison.OrdinalIgnoreCase)
            || HostedServerRules.SameOrigin(uri, SavedConnectionUrl.AccountEndpoint(_settings.AuthServerBaseUrl))
            || HostedServerRules.RequiresApiFirewallCredentials(_settings, uri);
    }

    internal HttpClient CreateCloudClient(HttpMessageHandler? transport = null) =>
        new(Wrap(transport ?? new HttpClientHandler { AllowAutoRedirect = false }, cloud: true));

    internal HttpMessageHandler Wrap(HttpMessageHandler transport, bool cloud = false) => new Handler(this, transport, cloud);

    private sealed class Handler(PrivacyNetworkPolicy policy, HttpMessageHandler transport, bool cloud)
        : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!cloud && (request.RequestUri == null || !policy.IsJoeManaged(request.RequestUri)))
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var generation = policy.CloudToken();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
            linked.Token.ThrowIfCancellationRequested();
            var response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                // Keep the policy in effect while a caller is reading a response
                // body. WebSockets use the recorder run's lifetime after upgrade.
                if (response.StatusCode != HttpStatusCode.SwitchingProtocols)
                    response.Content = new GuardedContent(response.Content, generation);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private sealed class GuardedContent : HttpContent
    {
        private readonly HttpContent _content;
        private readonly CancellationTokenRegistration _retirement;

        internal GuardedContent(HttpContent content, CancellationToken generation)
        {
            _content = content;
            foreach (var header in content.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            _retirement = generation.Register(static state => ((HttpContent)state!).Dispose(), content);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Headers.ContentLength ?? -1;
            return length >= 0;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _content.CopyToAsync(stream, context);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _content.CopyToAsync(stream, context, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync() => _content.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            _content.ReadAsStreamAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _retirement.Dispose();
                _content.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
