namespace JoesScanner.Services;

public sealed class ObservedTriple
{
    public string ReceiverValue { get; set; } = string.Empty;
    public string ReceiverLabel { get; set; } = string.Empty;

    public string SiteValue { get; set; } = string.Empty;
    public string SiteLabel { get; set; } = string.Empty;

    public string TalkgroupValue { get; set; } = string.Empty;
    public string TalkgroupLabel { get; set; } = string.Empty;

    public int SeenCount { get; set; }
    public DateTime LastSeenUtc { get; set; }
}
