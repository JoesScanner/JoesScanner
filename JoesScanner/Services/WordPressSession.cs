namespace JoesScanner.Services;

internal sealed class WordPressSession
{
    private readonly ISettingsService _settings;
    internal string BaseUrl { get; }
    internal string Token { get; }
    internal long Version { get; }
    internal long AccountVersion { get; }

    private WordPressSession(ISettingsService settings, (string BaseUrl, string Token, long Version, long AccountVersion) snapshot)
    {
        _settings = settings;
        (BaseUrl, Token, Version, AccountVersion) = snapshot;
    }

    internal static WordPressSession Capture(ISettingsService settings) => new(settings, settings.ReadAccountSession());
    internal Uri Endpoint(string endpoint) => SavedConnectionUrl.AccountEndpoint(BaseUrl, endpoint);
    internal bool IsCurrent => _settings.ReadAccountSession().Version == Version;
    internal bool IsCurrentAccount => _settings.ReadAccountSession().AccountVersion == AccountVersion;
    internal bool TryApply(Action action) => _settings.TryApplyAccountSession(Version, action);
    internal bool TryApplyIdentity(Action action) => _settings.TryApplyAccountIdentity(AccountVersion, action);

    internal static HttpClient CreateClient(ISettingsService settings) => PrivacyNetworkPolicy.For(settings).CreateCloudClient();
}

internal sealed class ConnectionValidationRequests
{
    private long _latest;
    internal long Begin() => Interlocked.Increment(ref _latest);
    internal bool IsCurrent(long request) => Volatile.Read(ref _latest) == request;

    internal bool TryApply(long request, WordPressSession session, Func<bool> ownsInputs, Action apply)
    {
        var applied = false;
        session.TryApplyIdentity(() =>
        {
            if (!IsCurrent(request) || !ownsInputs())
                return;
            apply();
            applied = true;
        });
        return applied;
    }
}
