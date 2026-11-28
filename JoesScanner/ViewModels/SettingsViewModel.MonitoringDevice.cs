using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class SettingsViewModel
{
    private MonitoringDeviceService DeviceMonitoring => _mainViewModel.MonitoringDevice;
    private MonitoringDeviceOptions DeviceOptions => DeviceMonitoring.Options;
    private NotificationTalkgroup[] _notificationChoices = [];
    private int _notificationTalkgroupIndex = -1;

    public string[] TransmissionScopeChoices { get; } = ["All Calls", "Selected Talkgroups", "Favorites"];
    public string[] NotificationSoundChoices { get; } = ["Silent", "Audible"];

    public bool TransmissionNotifications
    {
        get => DeviceOptions.TransmissionNotifications;
        set
        {
            if (SaveDeviceOptions(DeviceOptions with { TransmissionNotifications = value }) && value)
                _ = RefreshNotificationAccessAsync(request: true);
        }
    }

    public bool ConnectionFailureNotifications
    {
        get => DeviceOptions.ConnectionNotifications;
        set
        {
            if (SaveDeviceOptions(DeviceOptions with { ConnectionNotifications = value }) && value)
                _ = RefreshNotificationAccessAsync(request: true);
        }
    }

    public int TransmissionScopeIndex
    {
        get => (int)DeviceOptions.Scope;
        set
        {
            if (value is >= 0 and <= 2 && SaveDeviceOptions(DeviceOptions with { Scope = (TransmissionScope)value }))
                RefreshNotificationTalkgroups();
        }
    }

    public int NotificationSoundIndex
    {
        get => DeviceOptions.Audible ? 1 : 0;
        set
        {
            if (value is 0 or 1)
                SaveDeviceOptions(DeviceOptions with { Audible = value == 1 });
        }
    }

    public bool GroupNotifications
    {
        get => DeviceOptions.GroupByTalkgroup;
        set => SaveDeviceOptions(DeviceOptions with { GroupByTalkgroup = value });
    }

    public bool ShowsNotificationTalkgroups => TransmissionNotifications && DeviceOptions.Scope == TransmissionScope.SelectedTalkgroups;
    public bool HasRecentNotificationChoices => _notificationChoices.Length > 0;
    public bool CanToggleNotificationTalkgroup => _notificationTalkgroupIndex >= 0 && _notificationTalkgroupIndex < _notificationChoices.Length;
    public string SelectedNotificationTalkgroupCount => $"{DeviceOptions.Talkgroups.Length} selected";
    public string[] NotificationTalkgroupLabels => _notificationChoices.Select(item =>
        DeviceOptions.Talkgroups.Any(saved => saved.Key == item.Key) ? $"✓ {item.Label}" : item.Label).ToArray();

    public int NotificationTalkgroupIndex
    {
        get => _notificationTalkgroupIndex;
        set
        {
            if (_notificationTalkgroupIndex == value)
                return;
            _notificationTalkgroupIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanToggleNotificationTalkgroup));
            OnPropertyChanged(nameof(NotificationTalkgroupAction));
        }
    }

    public string NotificationTalkgroupAction => CanToggleNotificationTalkgroup && DeviceOptions.Talkgroups.Any(
        item => item.Key == _notificationChoices[_notificationTalkgroupIndex].Key) ? "Remove talkgroup" : "Add talkgroup";

    internal void RefreshNotificationTalkgroups()
    {
        var current = CanToggleNotificationTalkgroup ? _notificationChoices[_notificationTalkgroupIndex].Key : (TalkgroupKey?)null;
        var recent = _mainViewModel.RecentNotificationTalkgroups();
        _notificationChoices = recent.Concat(DeviceOptions.Talkgroups).DistinctBy(item => item.Key)
            .OrderBy(item => item.Label).ToArray();
        OnPropertyChanged(nameof(NotificationTalkgroupLabels));
        NotificationTalkgroupIndex = Array.FindIndex(_notificationChoices, item => item.Key == current);
        OnPropertyChanged(nameof(NotificationTalkgroupAction));
        OnPropertyChanged(nameof(HasRecentNotificationChoices));
    }

    internal void ToggleNotificationTalkgroup()
    {
        if (!CanToggleNotificationTalkgroup)
            return;
        var item = _notificationChoices[_notificationTalkgroupIndex];
        var changed = DeviceOptions.ToggleTalkgroup(item);
        if (changed.Talkgroups == DeviceOptions.Talkgroups)
            SetDeviceOptionsNote("Remove a talkgroup before adding another selection.");
        else if (SaveDeviceOptions(changed))
            RefreshNotificationTalkgroups();
    }

    public string NotificationAccessText { get; private set; } = "";
    public bool ShowsNotificationAccessText => (TransmissionNotifications || ConnectionFailureNotifications) &&
        NotificationAccessText.Length != 0;
    public bool ShowsNotificationAccessButton => ShowsNotificationAccessText &&
        (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst());

    internal async Task RefreshNotificationAccessAsync(bool request = false)
    {
        if (!TransmissionNotifications && !ConnectionFailureNotifications)
            return;
        try
        {
#if WINDOWS
            NotificationAccessText = MonitoringDevicePlatform.NotificationAccessText();
#else
            var allowed = request ? await DeviceMonitoring.RequestNotificationAccessAsync() : await DeviceMonitoring.GetNotificationAccessAsync();
            NotificationAccessText = allowed ? "" : "Joe’s Scanner needs notification access. Check the device’s app notification settings.";
#endif
        }
        catch (Exception ex)
        {
            NotificationAccessText = "Notification access could not be checked.";
            AppLog.Basic(() => $"Could not check local notification access ({DiagnosticRedaction.Describe(ex)}).");
        }

        OnPropertyChanged(nameof(NotificationAccessText));
        OnPropertyChanged(nameof(ShowsNotificationAccessText));
        OnPropertyChanged(nameof(ShowsNotificationAccessButton));
    }

    public bool ScreenAwakeSupported => !OperatingSystem.IsMacCatalyst();

    public bool KeepScreenAwake
    {
        get => DeviceOptions.ScreenAwake;
        set => SaveDeviceOptions(DeviceOptions with { ScreenAwake = value });
    }

    public bool OnlyWhileCharging
    {
        get => DeviceOptions.ChargingOnly;
        set => SaveDeviceOptions(DeviceOptions with { ChargingOnly = value });
    }

    public bool LowLightMonitoring
    {
        get => DeviceOptions.LowLight;
        set => SaveDeviceOptions(DeviceOptions with { LowLight = value });
    }

    public bool ShowsMonitoringBrightness => KeepScreenAwake && LowLightMonitoring;
    public bool WindowsWindowDimming => OperatingSystem.IsWindows();

    public double MonitoringBrightness
    {
        get => DeviceOptions.Brightness;
        set => SaveDeviceOptions(DeviceOptions with { Brightness = Math.Clamp((int)Math.Round(value), 5, 100) });
    }

    public string MonitoringBrightnessText => $"{DeviceOptions.Brightness}%";
    public string DeviceOptionsNote { get; private set; } = "";
    public bool HasDeviceOptionsNote => DeviceOptionsNote.Length != 0;

    private bool SaveDeviceOptions(MonitoringDeviceOptions next, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (next == DeviceOptions)
            return false;
        var saved = false;
        try
        {
            DeviceMonitoring.SetOptions(next);
            SetDeviceOptionsNote("");
            saved = true;
        }
        catch (Exception ex)
        {
            AppLog.Basic(() => $"Could not save monitoring device settings ({DiagnosticRedaction.Describe(ex)}).");
            SetDeviceOptionsNote("This change could not be saved. Try again.");
        }
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(ShowsNotificationTalkgroups));
        OnPropertyChanged(nameof(ShowsNotificationAccessText));
        OnPropertyChanged(nameof(ShowsNotificationAccessButton));
        OnPropertyChanged(nameof(SelectedNotificationTalkgroupCount));
        OnPropertyChanged(nameof(ShowsMonitoringBrightness));
        OnPropertyChanged(nameof(MonitoringBrightnessText));
        return saved;
    }

    private void SetDeviceOptionsNote(string message)
    {
        DeviceOptionsNote = message;
        OnPropertyChanged(nameof(DeviceOptionsNote));
        OnPropertyChanged(nameof(HasDeviceOptionsNote));
    }
}
