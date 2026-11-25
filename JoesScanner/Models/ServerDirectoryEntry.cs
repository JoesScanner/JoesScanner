using System;
using System.Text.Json.Serialization;

namespace JoesScanner.Models
{
    public sealed class ServerDirectoryEntry : IEquatable<ServerDirectoryEntry>
    {
        [JsonPropertyName("id")]
        public int DirectoryId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("info_url")]
        public string InfoUrl { get; set; } = string.Empty;

        [JsonPropertyName("area_label")]
        public string AreaLabel { get; set; } = string.Empty;

        [JsonPropertyName("map_anchor")]
        public string MapAnchor { get; set; } = string.Empty;

        [JsonPropertyName("is_official")]
        public bool IsOfficial { get; set; }

        [JsonIgnore]
        public bool IsCustom { get; set; }

        [JsonIgnore]
        internal string? Source { get; set; }

        [JsonPropertyName("uses_api_firewall_credentials")]
        public bool UsesApiFirewallCredentials { get; set; }

        [JsonPropertyName("requires_scanner_login")]
        public bool RequiresScannerLogin { get; set; }

        [JsonPropertyName("requires_firewall_auth")]
        public bool RequiresFirewallAuth { get; set; }

        public string Badge { get; set; } = string.Empty;

        [JsonPropertyName("badge_label")]
        public string BadgeLabel { get; set; } = string.Empty;



        private static string NormalizeUrl(string? value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('/');
        }

        public bool Equals(ServerDirectoryEntry? other)
        {
            if (ReferenceEquals(this, other))
                return true;

            if (other is null)
                return false;

            if (IsCustom || other.IsCustom)
                return IsCustom == other.IsCustom;
            if (DirectoryId > 0 || other.DirectoryId > 0)
                return DirectoryId == other.DirectoryId;

            return string.Equals(
                NormalizeUrl(Url),
                NormalizeUrl(other.Url),
                StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as ServerDirectoryEntry);
        }

        public override int GetHashCode()
        {
            if (IsCustom)
                return HashCode.Combine(true, "custom");
            if (DirectoryId > 0)
                return DirectoryId.GetHashCode();

            return StringComparer.OrdinalIgnoreCase.GetHashCode(NormalizeUrl(Url));
        }

        public string DisplayName
        {
            get
            {
                var n = (Name ?? string.Empty).Trim();
                if (n.Length == 0)
                    n = (Url ?? string.Empty).Trim();

                if (IsCustom)
                    return "Custom server";

                if (IsOfficial)
                    return "🛡️ " + n;

                return n;
            }
        }
    }
}
