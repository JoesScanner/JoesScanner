namespace JoesScanner.Helpers
{
    public static class ServerKeyHelper
    {
        public static string Normalize(string? serverUrl)
        {
            var raw = (serverUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            raw = raw.TrimEnd('/');

            try
            {
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                    return raw;

                var builder = new UriBuilder(uri)
                {
                    Path = string.Empty,
                    Query = string.Empty,
                    Fragment = string.Empty,
                    Port = uri.IsDefaultPort ? -1 : uri.Port
                };

                return builder.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return raw;
            }
        }

        public static string NormalizeLegacy(string? serverUrl)
        {
            return (serverUrl ?? string.Empty).Trim().TrimEnd('/');
        }

        public static (string Normalized, string Legacy) GetKeys(string? serverUrl)
        {
            var normalized = Normalize(serverUrl);
            if (string.IsNullOrWhiteSpace(normalized))
                return (string.Empty, string.Empty);

            var legacy = NormalizeLegacy(serverUrl);
            return (normalized, legacy);
        }
    }
}
