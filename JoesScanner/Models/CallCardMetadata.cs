namespace JoesScanner.Models;

public sealed record CallCardMetadata
{
    public DateTime? Timestamp { get; init; }
    public string? System { get; init; }
    public string? Talkgroup { get; init; }
    public string? Site { get; init; }
    public string? Radio { get; init; }
    public string? Receiver { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Frequency { get; init; }
    public string? TalkgroupTag { get; init; }
    public string? RadioTag { get; init; }
    public string? SystemType { get; init; }
    public string? CallType { get; init; }
    public string? VoiceType { get; init; }
    public string? Lcn { get; init; }
    public string? HistoryDate => Timestamp?.ToString("M/d/yyyy");

    public string? GetValue(CallCardField field) => field switch
    {
        CallCardField.Time => Timestamp?.ToString("HH:mm:ss"),
        CallCardField.System => System,
        CallCardField.Talkgroup => Talkgroup,
        CallCardField.Site => Site,
        CallCardField.Radio => Radio,
        CallCardField.Receiver => Receiver,
        CallCardField.Duration => FormatDuration(DurationSeconds, compact: false),
        CallCardField.Frequency => Frequency,
        CallCardField.TalkgroupTag => TalkgroupTag,
        CallCardField.RadioTag => RadioTag,
        CallCardField.SystemType => SystemType,
        CallCardField.CallType => CallType,
        CallCardField.VoiceType => VoiceType,
        CallCardField.Lcn => Lcn,
        _ => null
    };

    internal static bool IsValidDuration(double? seconds) =>
        seconds is double value && value > 0 && value < TimeSpan.MaxValue.TotalSeconds;

    internal static string? FormatDuration(double? seconds, bool compact)
    {
        if (seconds is not double value || !IsValidDuration(value))
            return null;

        if (!compact)
            return $"{value:F1}s";

        var time = TimeSpan.FromSeconds(value);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

}
