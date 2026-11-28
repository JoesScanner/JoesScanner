using System.Net;

namespace JoesScanner.Services;

// Persist a location only when it can be replayed without copying URL credentials.
// Signed or ambiguous references stay transient and are looked up again by ID.
internal static class RecordingStorageReference
{
    internal static string? ForCache(string? value) => Storable(value, allowRelative: true);
    internal static string? ForJournal(string? value) => Storable(value, allowRelative: false);

    private static string? Storable(string? value, bool allowRelative)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl))
            return null;
        value = value.Trim();

        // Inspect encoded delimiters, too. Keep the original escaping of a safe path.
        var inspected = value;
        for (var i = 0; i < 4; i++)
        {
            var decoded = Uri.UnescapeDataString(WebUtility.HtmlDecode(inspected));
            if (decoded == inspected)
                break;
            if (i == 3)
                return null;
            inspected = decoded;
        }
        if (inspected.IndexOfAny(['?', '#', '\\', '<', '>', '"']) >= 0 || inspected.Any(char.IsControl))
            return null;

        var networkPath = value.StartsWith("//", StringComparison.Ordinal);
        if (networkPath || IsHttpUrl(value))
        {
            if (networkPath && !allowRelative)
                return null;
            var raw = networkPath ? "https:" + value : value;
            var decoded = networkPath ? "https:" + inspected : inspected;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Host.Length == 0 ||
                !Uri.TryCreate(decoded, UriKind.Absolute, out var decodedUri) ||
                HasUserInfo(raw, uri) || HasUserInfo(decoded, decodedUri) ||
                Uri.UnescapeDataString(decodedUri.AbsolutePath).Split('/').Any(ContainsAuthorizationText))
                return null;
            return networkPath ? uri.AbsoluteUri["https:".Length..] : uri.AbsoluteUri;
        }

        if (!allowRelative || inspected.StartsWith("//", StringComparison.Ordinal) || IsHttpUrl(inspected))
            return null;
        var segments = inspected.Split('/');
        return segments[0].Contains(':') || segments.Any(ContainsAuthorizationText) ? null : value;
    }

    private static bool IsHttpUrl(string value) =>
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAuthorizationText(string segment)
    {
        var value = segment.TrimStart();
        var separator = value.AsSpan().IndexOfAny(':', '=');
        var name = separator < 0 ? string.Empty : value[..separator].TrimEnd();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
            (value.Contains(':') && value.Contains('@'));
    }

    private static bool HasUserInfo(string text, Uri uri)
    {
        var authority = text.AsSpan(text.IndexOf("://", StringComparison.Ordinal) + 3);
        var slash = authority.IndexOf('/');
        return uri.UserInfo.Length != 0 || (slash < 0 ? authority : authority[..slash]).Contains('@');
    }

    internal static string? ServerKey(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            return null;
        var server = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
        return ForJournal(server.AbsoluteUri)?.TrimEnd('/');
    }

    // A saved listening profile can be edited to point to another archive.
    // The path is case-sensitive. Scheme, host, default port and trailing slash
    // follow the identical normalization used for saved recorder endpoints.
    internal static string? ArchiveKey(Guid? recorderId, string? endpoint)
    {
        var archive = ServerKey(endpoint);
        if (archive == null)
            return null;
        return recorderId is { } id ? RecorderPrefix(id) + archive : archive;
    }

    internal static string RecorderPrefix(Guid id) => $"recorder:{id:N}:";
}
