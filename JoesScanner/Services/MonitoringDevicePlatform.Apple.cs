#if IOS || MACCATALYST
using UIKit;
using UserNotifications;

namespace JoesScanner.Services;

internal sealed partial class MonitoringDevicePlatform
{
    private static readonly ForegroundNotifications Presentation = new();

    internal partial async Task<bool> IsAllowedAsync(bool request)
    {
        var center = UNUserNotificationCenter.Current;
        var settings = await center.GetNotificationSettingsAsync();
        if (request && settings.AuthorizationStatus == UNAuthorizationStatus.NotDetermined)
        {
            var (granted, _) = await center.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound);
            return granted;
        }

        return settings.AuthorizationStatus is UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional;
    }

    internal partial async Task ShowAsync(MonitoringAlert alert)
    {
        var center = UNUserNotificationCenter.Current;
        center.Delegate = Presentation;
        using var content = new UNMutableNotificationContent
        {
            Title = alert.Title,
            Body = alert.Body,
            ThreadIdentifier = alert.Id,
            InterruptionLevel = alert.Update ? UNNotificationInterruptionLevel.Passive2 : UNNotificationInterruptionLevel.Active2,
            Sound = alert.Audible && !alert.Update ? UNNotificationSound.Default : null
        };
        using var request = UNNotificationRequest.FromIdentifier(alert.Id, content, trigger: null);
        await center.AddNotificationRequestAsync(request);
    }

    internal partial Task RemoveAsync(string id)
    {
        var center = UNUserNotificationCenter.Current;
        center.RemovePendingNotificationRequests([id]);
        center.RemoveDeliveredNotifications([id]);
        return Task.CompletedTask;
    }

    internal partial IMonitoringScreenLease? AcquireScreen(Window window, Action<double> setWindowDim) =>
#if IOS
        (window.Handler?.PlatformView as UIWindow)?.WindowScene?.Screen is { } screen ? new ScreenLease(screen) : null;
#else
        null;
#endif

    private sealed class ForegroundNotifications : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification,
            Action<UNNotificationPresentationOptions> completionHandler)
        {
            var content = notification.Request.Content;
            var options = UNNotificationPresentationOptions.List;
            if (content.InterruptionLevel != UNNotificationInterruptionLevel.Passive2)
                options |= UNNotificationPresentationOptions.Banner;
            if (content.Sound != null)
                options |= UNNotificationPresentationOptions.Sound;
            completionHandler(options);
        }
    }

#if IOS
    private sealed class ScreenLease : IMonitoringScreenLease
    {
        private readonly UIScreen _screen;
        private readonly bool _wasAwake;

        internal ScreenLease(UIScreen screen)
        {
            _screen = screen;
            _wasAwake = UIApplication.SharedApplication.IdleTimerDisabled;
            UIApplication.SharedApplication.IdleTimerDisabled = true;
        }

        public double Brightness
        {
            get => _screen.Brightness;
            set => _screen.Brightness = (nfloat)value;
        }

        public void Dispose() => UIApplication.SharedApplication.IdleTimerDisabled = _wasAwake;
    }
#endif
}
#endif
