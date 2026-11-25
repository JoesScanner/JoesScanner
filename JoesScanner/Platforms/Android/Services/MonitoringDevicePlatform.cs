using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.Maui.ApplicationModel;
using NativeWindow = Android.Views.Window;
using WindowManagerFlags = Android.Views.WindowManagerFlags;

namespace JoesScanner.Services;

internal sealed partial class MonitoringDevicePlatform
{
    private const int OptionalAlertId = 42015;
    private ChargingReceiver? _chargingReceiver;

    internal partial async Task<bool> IsAllowedAsync(bool request)
    {
        if (request && OperatingSystem.IsAndroidVersionAtLeast(33) &&
            await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            await Permissions.RequestAsync<Permissions.PostNotifications>();

        using var manager = NotificationManagerCompat.From(Android.App.Application.Context);
        return manager?.AreNotificationsEnabled() == true;
    }

    internal partial Task ShowAsync(MonitoringAlert alert)
    {
        var context = Android.App.Application.Context;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager == null)
            return Task.CompletedTask;

        var transmission = alert.Kind == MonitoringAlertKind.Transmission;
        var channelId = "joesscanner_" + (transmission ? "transmission" : "connection") + (alert.Audible ? "_sound" : "_silent");
        using var channel = OperatingSystem.IsAndroidVersionAtLeast(26) && manager.GetNotificationChannel(channelId) == null
            ? new NotificationChannel(channelId,
                (transmission ? "Scanner transmissions" : "Recorder connection failures") + (alert.Audible ? " (sound)" : " (silent)"),
                alert.Audible ? NotificationImportance.Default : NotificationImportance.Low)
            : null;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && channel != null)
            manager.CreateNotificationChannel(channel);

        using var intent = new Intent(context, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        using var open = PendingIntent.GetActivity(context, OptionalAlertId, intent, PendingIntentFlags.Immutable);
        using var builder = new NotificationCompat.Builder(context, channelId);
        builder.SetSmallIcon(Android.Resource.Drawable.IcDialogInfo);
        builder.SetContentTitle(alert.Title);
        builder.SetContentText(alert.Body);
        builder.SetContentIntent(open);
        builder.SetCategory(NotificationCompat.CategoryStatus);
        builder.SetVisibility(NotificationCompat.VisibilityPrivate);
        builder.SetDefaults(alert.Audible && !alert.Update ? (int)NotificationDefaults.Sound : 0);
        builder.SetSilent(!alert.Audible || alert.Update);
        builder.SetOnlyAlertOnce(true);
        builder.SetAutoCancel(true);
        using var notification = builder.Build();
        if (notification != null)
            manager.Notify(alert.Id, OptionalAlertId, notification);
        return Task.CompletedTask;
    }

    internal partial Task RemoveAsync(string id)
    {
        var manager = Android.App.Application.Context.GetSystemService(Context.NotificationService) as NotificationManager;
        manager?.Cancel(id, OptionalAlertId);
        return Task.CompletedTask;
    }

    internal partial IMonitoringScreenLease? AcquireScreen(Window window, Action<double> setWindowDim) =>
        Platform.CurrentActivity?.Window is { } native ? new ScreenLease(native) : null;

    internal partial bool ReadExternalPower()
    {
        // The public sticky battery broadcast includes the plug type without a privileged permission.
        using var filter = new IntentFilter(Intent.ActionBatteryChanged);
        using var battery = Android.App.Application.Context.RegisterReceiver(null, filter);
        return battery?.GetIntExtra(BatteryManager.ExtraPlugged, 0) > 0;
    }

    internal partial void SetPowerListener(Action? listener)
    {
        var context = Android.App.Application.Context;
        if (_chargingReceiver is { } previous)
        {
            _chargingReceiver = null;
            context.UnregisterReceiver(previous);
            previous.Dispose();
        }
        if (listener == null)
            return;

        var receiver = new ChargingReceiver(listener);
        using var filter = new IntentFilter(Intent.ActionBatteryChanged);
        try
        {
            using var initial = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported)
                : context.RegisterReceiver(receiver, filter);
            _chargingReceiver = receiver;
        }
        catch
        {
            receiver.Dispose();
            throw;
        }
    }

    private sealed class ChargingReceiver(Action changed) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (!IsInitialStickyBroadcast)
                changed();
        }
    }

    private sealed class ScreenLease : IMonitoringScreenLease
    {
        private readonly NativeWindow _window;
        private readonly bool _wasAwake;

        internal ScreenLease(NativeWindow window)
        {
            _window = window;
            _wasAwake = (window.Attributes?.Flags & WindowManagerFlags.KeepScreenOn) == WindowManagerFlags.KeepScreenOn;
            window.AddFlags(WindowManagerFlags.KeepScreenOn);
        }

        public double Brightness
        {
            get => _window.Attributes?.ScreenBrightness ?? -1;
            set
            {
                if (_window.Attributes is { } attributes)
                {
                    attributes.ScreenBrightness = (float)value;
                    _window.Attributes = attributes;
                }
            }
        }

        public void Dispose()
        {
            if (!_wasAwake)
                _window.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }
    }
}
