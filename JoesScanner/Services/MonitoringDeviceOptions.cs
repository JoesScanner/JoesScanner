using System.Text.Json;

namespace JoesScanner.Services;

internal enum TransmissionScope { AllCalls, SelectedTalkgroups, Favorites }

internal sealed record NotificationTalkgroup(TalkgroupKey Key, string Label)
{
    public override string ToString() => Label;
}

internal sealed record MonitoringDeviceOptions
{
    private const string SettingsKey = "monitoring_device_options";
    private const int SelectionLimit = 128;

    public bool TransmissionNotifications { get; init; }
    public TransmissionScope Scope { get; init; }
    public bool Audible { get; init; }
    public bool GroupByTalkgroup { get; init; } = true;
    public bool ConnectionNotifications { get; init; }
    public bool ScreenAwake { get; init; }
    public bool ChargingOnly { get; init; } = true;
    public bool LowLight { get; init; }
    public int Brightness { get; init; } = 25;
    public NotificationTalkgroup[] Talkgroups { get; init; } = [];

    internal static MonitoringDeviceOptions Read() =>
        Decode(AppStateStore.GetString(SettingsKey));

    internal static MonitoringDeviceOptions Decode(string json)
    {
        try { return JsonSerializer.Deserialize<MonitoringDeviceOptions>(json)?.Normalize() ?? new(); }
        catch (JsonException) { return new(); }
    }

    internal void Save() => AppStateStore.SetString(SettingsKey, JsonSerializer.Serialize(this));

    internal MonitoringDeviceOptions Normalize() => this with
    {
        Scope = Enum.IsDefined(Scope) ? Scope : TransmissionScope.AllCalls,
        Brightness = Math.Clamp(Brightness, 5, 100),
        Talkgroups = (Talkgroups ?? []).Where(item => item != null && item.Key.ServerId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(item.Key.Talkgroup) && !string.IsNullOrWhiteSpace(item.Label))
            .DistinctBy(item => item.Key).Take(SelectionLimit).ToArray()
    };

    internal MonitoringDeviceOptions ToggleTalkgroup(NotificationTalkgroup item) => this with
    {
        Talkgroups = Talkgroups.Any(saved => saved.Key == item.Key)
            ? Talkgroups.Where(saved => saved.Key != item.Key).ToArray()
            : Talkgroups.Length < SelectionLimit ? [.. Talkgroups, item] : Talkgroups
    };
}
