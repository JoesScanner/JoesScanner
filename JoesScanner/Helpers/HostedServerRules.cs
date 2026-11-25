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
        => FindFirewallDirectory(settings, uri)?.Id;

    internal static (int Id, string Endpoint)? FindFirewallDirectory(ISettingsService settings, Uri uri)
    {
        var profile = DirectoryRecorderPolicy.For(settings).Find(uri);
        if (profile is { DirectoryId: null } or { DirectoryFirewall: false } || profile?.DirectoryBlocked == true)
            return null;
        if (profile is { DirectoryId: int id, DirectoryFirewall: true })
            return (id, profile.Endpoint);

        (int Id, string Endpoint)? best = null;
        var bestMatch = -1;
        foreach (var entry in settings.GetCachedDirectoryServers())
        {
            if (entry.DirectoryId <= 0 || !entry.UsesApiFirewallCredentials ||
                !Uri.TryCreate(entry.Url, UriKind.Absolute, out var endpoint) || !SameOrigin(endpoint, uri) ||
                profile != null && (profile.DirectoryId != entry.DirectoryId || profile.Endpoint != ScannerConnectionStore.Normalize(entry.Url)))
                continue;
            var match = DirectoryRecorderPolicy.Contains(entry.Url, uri) ? entry.Url.Length : 0;
            if (match <= bestMatch)
                continue;
            bestMatch = match;
            best = (entry.DirectoryId, entry.Url);
        }

        return best;
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
