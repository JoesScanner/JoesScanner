namespace JoesScanner.Services;

internal static class SavedConnectionUrl
{
    internal const string DefaultAccountBase = "https://joesscanner.com";

    internal static bool TryParse(string? value, out Uri? uri, out string safe, out ProtectedCredentials.Pair? embedded)
    {
        safe = string.Empty;
        embedded = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host))
            return false;

        // Split the escaped component before decoding so a percent-encoded colon
        // in the password never becomes a credential delimiter.
        var userInfo = uri.GetComponents(UriComponents.UserInfo, UriFormat.UriEscaped);
        if (userInfo.Length != 0)
        {
            var separator = userInfo.IndexOf(':');
            embedded = separator >= 0
                ? new(Uri.UnescapeDataString(userInfo[..separator]), Uri.UnescapeDataString(userInfo[(separator + 1)..]))
                : new(Uri.UnescapeDataString(userInfo), string.Empty);
        }
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Fragment = "" };
        // Recorder and account base addresses are used to build API paths. Query
        // strings are neither part of those paths nor supported authentication.
        builder.Query = "";
        safe = builder.Uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    private static bool IsSecretParameter(string entry)
    {
        var key = Uri.UnescapeDataString(entry.Split('=', 2)[0]).ToLowerInvariant().Replace('-', '_');
        return key is "user" or "username" or "pass" or "password" or "auth" or "authorization" or "key" or
            "sig" or "signature" or "credential" or "credentials" or "code" or "apikey" or "session" or "jwt" or "bearer" ||
            key.EndsWith("token", StringComparison.Ordinal) || key.EndsWith("api_key", StringComparison.Ordinal) ||
            key.StartsWith("x_amz_", StringComparison.Ordinal) || key.StartsWith("x_goog_", StringComparison.Ordinal) ||
            key.StartsWith("x_ms_", StringComparison.Ordinal);
    }

    internal static string SafeDisplay(string? value) => TryParse(value, out _, out var safe, out _) ? safe : "";

    internal static string SafeInfoUrl(string? value)
    {
        if (!TryParse(value, out var uri, out var safe, out _))
            return "";
        if (uri!.Query.Length == 0)
            return safe;
        var builder = new UriBuilder(safe)
        {
            Query = string.Join("&", uri.Query[1..].Split('&').Where(entry => !IsSecretParameter(entry)))
        };
        return builder.Uri.AbsoluteUri;
    }

    internal static string? Scope(string? value)
    {
        if (!TryParse(value, out var uri, out _, out _))
            return null;
        return uri!.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    internal static Uri AccountEndpoint(string? value, string endpoint = "auth")
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? DefaultAccountBase : value;
        if (!TryParse(candidate, out var uri, out var safe, out var embedded) ||
            uri!.Scheme != "https" || embedded != null || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new CredentialStorageException();
        return new Uri(safe + "/wp-json/joes-scanner/v1/" + endpoint);
    }
}
