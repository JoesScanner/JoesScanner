using System.Text.Json.Serialization;

namespace JoesScanner.Models
{
    // Serializable snapshot of a single filter rule state.
    // Used for local filter profiles and future server backup.
    public sealed class FilterRuleStateRecord
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public FilterLevel Level { get; set; }

        public string Receiver { get; set; } = string.Empty;
        public string Site { get; set; } = string.Empty;
        public string Talkgroup { get; set; } = string.Empty;

        public bool IsMuted { get; set; }
        public bool IsDisabled { get; set; }

        public static string BuildKey(FilterLevel level, string receiver, string site, string talkgroup)
        {
            var r = (receiver ?? string.Empty).Trim();
            var s = (site ?? string.Empty).Trim();
            var t = (talkgroup ?? string.Empty).Trim();
            return $"{level}|{r}|{s}|{t}".ToUpperInvariant();
        }

        [JsonIgnore]
        public string Key => BuildKey(Level, Receiver, Site, Talkgroup);
    }
}
