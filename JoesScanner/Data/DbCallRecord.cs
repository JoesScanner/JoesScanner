namespace JoesScanner.Data;

public sealed class DbCallRecord
{
    public required string ServerKey { get; init; }
    public required string BackendId { get; init; }

    public string? StartTimeUtc { get; init; }
    public string? TimeText { get; init; }
    public string? DateText { get; init; }

    public string? TargetId { get; init; }
    public string? TargetLabel { get; init; }
    public string? TargetTag { get; init; }

    public string? SourceId { get; init; }
    public string? SourceLabel { get; init; }
    public string? SourceTag { get; init; }

    public int? Lcn { get; init; }
    public double? Frequency { get; init; }
    public string? CallAudioType { get; init; }
    public string? CallType { get; init; }

    public string? SystemId { get; init; }
    public string? SystemLabel { get; init; }
    public string? SystemType { get; init; }

    public string? SiteId { get; init; }
    public string? SiteLabel { get; init; }
    public string? VoiceReceiver { get; init; }

    public string? AudioFilename { get; init; }
    public double? AudioStartPos { get; init; }
    public double? CallDurationSeconds { get; init; }

    public string? CallText { get; init; }
    public string? Transcription { get; init; }

    public DateTimeOffset ReceivedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}
