using JoesScanner.Services;

namespace JoesScanner.Helpers;

public static class HostedServerRules
{
    public const string DefaultServerUrl = "https://app.joesscanner.com";

    public static bool IsHostedStreamServerUrl(string? serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && IsHostedServerUri(uri);

    public static bool IsHostedServerUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        string.Equals(uri.IdnHost.TrimEnd('.'), "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsProvidedDefaultServerUrl(string? serverUrl) => IsHostedStreamServerUrl(serverUrl);

    public static bool RequiresApiFirewallCredentials(ISettingsService settings, string? serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && RequiresApiFirewallCredentials(settings, uri);

    internal static bool RequiresApiFirewallCredentials(ISettingsService settings, Uri uri) =>
        IsHostedServerUri(uri) || FindFirewallDirectoryId(settings, uri).HasValue;

    internal static int? FindFirewallDirectoryId(ISettingsService settings, Uri uri)
    {
        foreach (var entry in settings.GetCachedDirectoryServers())
        {
            if (entry.UsesApiFirewallCredentials && Uri.TryCreate(entry.Url, UriKind.Absolute, out var candidate) &&
                SameOrigin(uri, candidate))
            {
                return entry.DirectoryId;
            }
        }

        return null;
    }

    internal static bool SameOrigin(Uri first, Uri second) =>
        first.IsAbsoluteUri && second.IsAbsoluteUri &&
        HttpScheme(first) == HttpScheme(second) && first.Port == second.Port &&
        string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase);

    internal static string HttpScheme(Uri uri) => uri.Scheme switch
    {
        "ws" => Uri.UriSchemeHttp,
        "wss" => Uri.UriSchemeHttps,
        _ => uri.Scheme
    };
}
