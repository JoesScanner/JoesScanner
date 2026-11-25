using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.System.Display;

namespace JoesScanner.Services;

internal sealed partial class MonitoringDevicePlatform
{
    private const string AlertGroup = "ScannerMonitoring";
    private bool _registered;

    internal partial Task<bool> IsAllowedAsync(bool request) =>
        Task.FromResult(AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled);

    internal partial Task ShowAsync(MonitoringAlert alert)
    {
        var manager = AppNotificationManager.Default;
        if (!_registered)
        {
            manager.Register();
            _registered = true;
        }

        var builder = new AppNotificationBuilder().AddText(alert.Title).AddText(alert.Body)
            .SetTag(alert.Id).SetGroup(AlertGroup);
        if (!alert.Audible || alert.Update)
            builder.MuteAudio();
        var notification = builder.BuildNotification();
        notification.SuppressDisplay = alert.Update;
        notification.ExpiresOnReboot = true;
        manager.Show(notification);
        return Task.CompletedTask;
    }

    internal partial async Task RemoveAsync(string id)
    {
        if (_registered)
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(id, AlertGroup);
    }

    internal partial IMonitoringScreenLease? AcquireScreen(Window window, Action<double> setWindowDim) =>
        new ScreenLease(setWindowDim);

    private sealed class ScreenLease : IMonitoringScreenLease
    {
        private readonly DisplayRequest _request = new();
        private readonly Action<double> _setWindowDim;
        private double _brightness = 1;

        internal ScreenLease(Action<double> setWindowDim)
        {
            _setWindowDim = setWindowDim;
            _request.RequestActive();
        }

        public double Brightness
        {
            get => _brightness;
            set
            {
                _brightness = value;
                _setWindowDim(1 - value);
            }
        }

        public void Dispose() => _request.RequestRelease();
    }
}
