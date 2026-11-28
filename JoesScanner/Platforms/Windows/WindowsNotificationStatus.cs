using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Windows.AppNotifications;

namespace JoesScanner.Services;

internal static class WindowsNotificationStatus
{
    internal static string Describe(AppNotificationSetting setting, Func<bool> hasHighIntegrity) => setting switch
    {
        AppNotificationSetting.Enabled => "",
        AppNotificationSetting.Unsupported when hasHighIntegrity() =>
            "Windows notifications are unavailable while Joe’s Scanner is running with administrator privileges. Relaunch it without administrator privileges to receive notifications.",
        AppNotificationSetting.Unsupported => "Windows notifications are unavailable in this environment.",
        AppNotificationSetting.DisabledByGroupPolicy => "Windows notifications are disabled by group policy.",
        AppNotificationSetting.DisabledByManifest => "Notifications are disabled for this Windows package.",
        AppNotificationSetting.DisabledForApplication or AppNotificationSetting.DisabledForUser =>
            "Windows notifications are disabled. Check the app’s notification settings in Windows.",
        _ => "Notification access could not be checked."
    };

    internal static bool HasHighProcessIntegrity()
    {
        const int tokenIntegrityLevel = 25;
        // TOKEN_MANDATORY_LABEL followed by a SID (SECURITY_MAX_SID_SIZE is 68 bytes).
        var size = 2 * IntPtr.Size + 68;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // GetCurrentProcessToken pseudo-handle identifies the process even during impersonation.
            if (!GetTokenInformation(new IntPtr(-4), tokenIntegrityLevel, buffer, size, out _))
                return false;
            var label = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            return label.IsWellKnown(WellKnownSidType.WinHighLabelSid) ||
                label.IsWellKnown(WellKnownSidType.WinSystemLabelSid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr buffer,
        int bufferSize, out int returnedSize);
}
