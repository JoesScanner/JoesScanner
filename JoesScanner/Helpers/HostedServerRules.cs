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

            return string.Equals(uri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);
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
