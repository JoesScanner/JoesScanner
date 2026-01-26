using System.Text.Json.Serialization;

namespace JoesScanner.Models
{
    public sealed class SettingsFilterProfile
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        // UTC timestamp for last update.
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        // Only rules with non default state are stored.
        public List<FilterRuleStateRecord> Rules { get; set; } = new();
    }

    public sealed class SettingsFilterProfileStoreEnvelope
    {
        public int SchemaVersion { get; set; } = 1;

        // Identifies the intended scope when exported to or imported from a server.
        public string Context { get; set; } = "settings";

        public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;

        public List<SettingsFilterProfile> Profiles { get; set; } = new();
    }
}
