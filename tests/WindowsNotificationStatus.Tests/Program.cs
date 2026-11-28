using JoesScanner.Services;
using Microsoft.Windows.AppNotifications;

Check(AppNotificationSetting.Enabled, "");
Check(AppNotificationSetting.DisabledForApplication, "app’s notification settings");
Check(AppNotificationSetting.DisabledForUser, "app’s notification settings");
Check(AppNotificationSetting.DisabledByGroupPolicy, "group policy");
Check(AppNotificationSetting.DisabledByManifest, "disabled for this Windows package");
Check(AppNotificationSetting.Unsupported, "Relaunch it without administrator privileges", true);
Check(AppNotificationSetting.Unsupported, "unavailable in this environment", false);
Check((AppNotificationSetting)int.MaxValue, "could not be checked");

static void Check(AppNotificationSetting setting, string expected, bool? highIntegrity = null)
{
    var message = WindowsNotificationStatus.Describe(setting, () =>
        highIntegrity ?? throw new InvalidOperationException("Unexpected integrity check for " + setting));
    if (expected.Length == 0 ? message.Length != 0 : !message.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Unexpected message for {setting}: {message}");
    if (message.Contains("administrator", StringComparison.OrdinalIgnoreCase) != (highIntegrity == true))
        throw new InvalidOperationException("Elevation advice must require unsupported status and high integrity.");
    Console.WriteLine($"PASS {setting} (high integrity: {highIntegrity})");
}
