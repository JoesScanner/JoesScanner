using System;

namespace JoesScanner.Models
{
    public sealed class ServerDirectoryEntry : IEquatable<ServerDirectoryEntry>
    {
        public int DirectoryId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string InfoUrl { get; set; } = string.Empty;

        public string AreaLabel { get; set; } = string.Empty;

        public string MapAnchor { get; set; } = string.Empty;

        public bool IsOfficial { get; set; }

        public bool IsCustom { get; set; }


        public string Badge { get; set; } = string.Empty;

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
