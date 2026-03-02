namespace JoesScanner.Models
{
    public sealed class ServerDirectoryEntry
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
                    return "🐟 " + n;

                return n;
            }
        }
    }
}
