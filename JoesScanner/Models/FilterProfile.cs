using System.Text.Json.Serialization;

namespace JoesScanner.Models
{
    public static class FilterProfileContexts
    {
        public const string History = "History";
        public const string Archive = "Archive";
    }

    public sealed class FilterProfile
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Context { get; init; } = string.Empty;

        public FilterProfileFilters Filters { get; init; } = new FilterProfileFilters();

        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed class FilterProfileFilters
    {
        // Lookup values
        public string? ReceiverValue { get; init; }
        public string? ReceiverLabel { get; init; }
        public string? SiteValue { get; init; }
        public string? SiteLabel { get; init; }
        public string? TalkgroupValue { get; init; }
        public string? TalkgroupLabel { get; init; }

        // Optional date and time selection used by Archive.
        // History uses time only.
        public DateTime? SelectedDateLocal { get; init; }
        public TimeSpan? SelectedTime { get; init; }
    }

    // Wrapper for persistence and future API sync.
    public sealed class FilterProfileStoreEnvelope
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("profiles")]
        public List<FilterProfile> Profiles { get; init; } = new List<FilterProfile>();
    }
}
