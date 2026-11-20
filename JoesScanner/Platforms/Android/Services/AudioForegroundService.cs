using Android.App;
using Android.Content;
using Android.OS;
using JoesScanner.Services;

namespace JoesScanner.Platforms.Android.Services
{
    [Service(
        Exported = false,
        ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
    public class AudioForegroundService : Service
    {
        private const string ChannelId = AndroidMediaCenter.NotificationChannelId;
        private const string ChannelName = "Joe's Scanner Audio";

        internal const string ExtraMonitoringRun = "app.joesscanner.monitoring_run";
        private long _runId;
        private bool _promoted;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            var runId = intent?.GetLongExtra(ExtraMonitoringRun, 0) ?? 0;
            var notificationId = AndroidMediaCenter.NotificationIdForRun(runId);
            var ownership = SystemMediaService.AndroidForeground;
            if (!ownership.IsCurrent(runId))
            {
                // A queued foreground start can arrive after Stop. Satisfy
                // Android's promotion contract without publishing its readiness.
                if (!_promoted && runId != 0)
                    PromoteBootstrap(notificationId, runId < 0);
                StopSelfResult(startId);
                return StartCommandResult.NotSticky;
            }

            if (PromoteBootstrap(notificationId, runId < 0))
            {
                _runId = runId;
                ownership.Promote(runId, this);
                try
                {
                    if (runId > 0)
                        StartForeground(notificationId, AndroidMediaCenter.BuildNotification(this));
                }
                catch (Exception ex)
                {
                    AppLog.Add(() => $"Android audio notification update failed ({ex.GetType().Name}).");
                }
            }
            else
            {
                ownership.Destroy(_runId, this);
                ownership.RequestFailed(runId);
                StopSelfResult(startId);
            }
            return StartCommandResult.NotSticky;
        }

        public override void OnDestroy()
        {
            // Android removes this instance's foreground notification. Readiness
            // belongs to whichever instance promoted the current run.
            SystemMediaService.AndroidForeground.Destroy(_runId, this);
            base.OnDestroy();
        }

        public override IBinder? OnBind(Intent? intent) => null;

        private bool PromoteBootstrap(int notificationId, bool standalone)
        {
            try
            {
                CreateNotificationChannelIfNeeded();

                StartForeground(notificationId, BuildBootstrapNotification(standalone));
                _promoted = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Android audio service promotion failed ({ex.GetType().Name}).");
                return false;
            }
        }

        private Notification BuildBootstrapNotification(bool standalone)
        {
            Notification.Builder builder;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                builder = new Notification.Builder(this, ChannelId);
            else
                builder = new Notification.Builder(this);

            builder
                .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                .SetContentTitle("Joe's Scanner")
                .SetContentText(standalone ? "Playback session" : "Starting audio service")
                .SetOnlyAlertOnce(true)
                .SetOngoing(true);

            if (standalone && PackageName is { } packageName && PackageManager?.GetLaunchIntentForPackage(packageName) is { } launch)
            {
                using (launch)
                {
                    launch.AddFlags(ActivityFlags.SingleTop);
                    builder.SetContentIntent(PendingIntent.GetActivity(this, 0, launch,
                        PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
                }
            }
            return builder.Build();
        }

        private void CreateNotificationChannelIfNeeded()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
                return;

            var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                Description = "Keeps Joe's Scanner running for continuous audio playback"
            };
            channel.SetSound(null, null);
            channel.EnableVibration(false);
            channel.EnableLights(false);
            var manager = GetSystemService(NotificationService) as NotificationManager
                ?? throw new InvalidOperationException("Android notification manager is unavailable.");
            manager.CreateNotificationChannel(channel);
        }

        // Notification content is built and updated by AndroidMediaCenter.
    }
}
