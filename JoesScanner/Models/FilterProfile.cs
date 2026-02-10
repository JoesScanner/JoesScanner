using System.Text.Json.Serialization;

namespace JoesScanner.Models
{
    // Unified filter profile shared by History, Archive, and Settings.
    // One profile file is used everywhere.
    public sealed class FilterProfile
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        // History and Archive selection state.
        public FilterProfileFilters Filters { get; init; } = new FilterProfileFilters();

        // Settings filter rule snapshot (only rules with non default state should be stored).
        public List<FilterRuleStateRecord> Rules { get; init; } = new List<FilterRuleStateRecord>();

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
        public int SchemaVersion { get; init; } = 2;

        [JsonPropertyName("profiles")]
        public List<FilterProfile> Profiles { get; init; } = new List<FilterProfile>();
    }
}
