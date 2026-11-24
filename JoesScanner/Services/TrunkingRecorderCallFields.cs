using System.Globalization;
using System.Text.Json;
using JoesScanner.Models;

namespace JoesScanner.Services;

// These fields are present in both live and history calljson responses.
internal class TrunkingRecorderCallFields
{
    public string? TargetID { get; private set; }
    public string? TargetLabel { get; private set; }
    public string? TargetTag { get; private set; }
    public string? SourceID { get; private set; }
    public string? SourceLabel { get; private set; }
    public string? SourceTag { get; private set; }
    public string? LCN { get; private set; }
    public string? Frequency { get; private set; }
    public string? CallAudioType { get; private set; }
    public string? SystemID { get; private set; }
    public string? SystemLabel { get; private set; }
    public string? SystemType { get; private set; }
    public string? SiteID { get; private set; }
    public string? SiteLabel { get; private set; }
    public string? VoiceReceiver { get; private set; }
    public string? CallType { get; private set; }
    public string? CallDuration { get; private set; }

    public void ReadCardFields(JsonElement row, Func<JsonElement, string, string?> read)
    {
        TargetID = read(row, "TargetID");
        TargetLabel = read(row, "TargetLabel");
        TargetTag = read(row, "TargetTag");
        SourceID = read(row, "SourceID");
        SourceLabel = read(row, "SourceLabel");
        SourceTag = read(row, "SourceTag");
        LCN = read(row, "LCN");
        Frequency = read(row, "Frequency");
        CallAudioType = read(row, "CallAudioType");
        if (string.IsNullOrWhiteSpace(CallAudioType))
            CallAudioType = read(row, "VoiceType");
        SystemID = read(row, "SystemID");
        SystemLabel = read(row, "SystemLabel");
        SystemType = read(row, "SystemType");
        SiteID = read(row, "SiteID");
        SiteLabel = read(row, "SiteLabel");
        VoiceReceiver = read(row, "VoiceReceiver");
        CallType = read(row, "CallType");
        CallDuration = read(row, "CallDuration");
    }

    public CallCardMetadata ToCardMetadata(DateTime? timestamp, bool omitUnknownSite = false)
    {
        var site = LabelOrId(SiteLabel, SiteID);
        if (omitUnknownSite && site?.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) == true)
            site = null;

        return new()
        {
            Timestamp = timestamp,
            System = LabelOrId(SystemLabel, SystemID),
            SystemId = SystemID?.Trim(),
            TalkgroupId = TargetID?.Trim(),
            SiteId = SiteID?.Trim(),
            Talkgroup = LabelOrId(TargetLabel, TargetID),
            Site = site,
            Radio = LabelOrId(SourceLabel, SourceID),
            Receiver = VoiceReceiver?.Trim(),
            DurationSeconds = GetDurationSeconds(),
            Frequency = Frequency,
            TalkgroupTag = TargetTag,
            RadioTag = SourceTag,
            SystemType = SystemType,
            CallType = CallType,
            VoiceType = CallAudioType,
            Lcn = LCN
        };
    }

    public double? GetDurationSeconds() =>
        double.TryParse(CallDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && CallCardMetadata.IsValidDuration(seconds) ? seconds : null;

    public LiveCallSnapshot ToLiveSnapshot(DateTime? timestamp, string? transcript) => new(
        ToCardMetadata(timestamp), transcript,
        Fallback(SystemLabel, CallCardIdFallback.System) |
        Fallback(TargetLabel, CallCardIdFallback.Talkgroup) |
        Fallback(SiteLabel, CallCardIdFallback.Site) |
        Fallback(SourceLabel, CallCardIdFallback.Radio));

    private static CallCardIdFallback Fallback(string? label, CallCardIdFallback flag) =>
        string.IsNullOrWhiteSpace(label) ? flag : CallCardIdFallback.None;

    private static string? LabelOrId(string? label, string? id) =>
        string.IsNullOrWhiteSpace(label) ? id : label;
}
