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
        private const int NotificationId = AndroidMediaCenter.NotificationId;
        private const string ChannelId = AndroidMediaCenter.NotificationChannelId;
        private const string ChannelName = "Joe's Scanner Audio";

        private static int _isForegroundReady;

        public static bool IsForegroundReady => System.Threading.Volatile.Read(ref _isForegroundReady) == 1;

        public override void OnCreate()
        {
            base.OnCreate();
            EnsureForegroundStartedImmediate("OnCreate");
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            EnsureForegroundStartedImmediate($"OnStartCommand startId={startId}");
            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                {
                    StopForeground(StopForegroundFlags.Remove);
                }
                else
                {
                    StopForeground(true);
                }
            }
            catch
            {
            }

            System.Threading.Interlocked.Exchange(ref _isForegroundReady, 0);

            base.OnDestroy();
        }

        public override IBinder? OnBind(Intent? intent) => null;

        private void EnsureForegroundStartedImmediate(string source)
        {
            try
            {
                CreateNotificationChannelIfNeeded();

                // Always promote this specific service instance immediately.
                // Do not rely on static one-time guards here, because Android can recreate the
                // service during the same app session and each instance must call StartForeground.
                StartForeground(NotificationId, BuildBootstrapNotification());
                System.Threading.Interlocked.Exchange(ref _isForegroundReady, 1);

                try { AppLog.Add(() => $"AndroidSvc: foreground bootstrap started from {source}."); } catch { }
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Exchange(ref _isForegroundReady, 0);
                try { AppLog.Add(() => $"AndroidSvc: bootstrap StartForeground failed from {source}. {ex.GetType().Name}: {ex.Message}"); } catch { }
                return;
            }

            try
            {
                AndroidMediaCenter.EnsureInitialized(this);
                var notification = AndroidMediaCenter.BuildNotification(this);
                StartForeground(NotificationId, notification);
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AndroidSvc: media notification update failed. {ex.GetType().Name}: {ex.Message}"); } catch { }
            }
        }

        private Notification BuildBootstrapNotification()
        {
            Notification.Builder builder;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                builder = new Notification.Builder(this, ChannelId);
            else
                builder = new Notification.Builder(this);

            builder
                .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                .SetContentTitle("Joe's Scanner")
                .SetContentText("Starting audio service")
                .SetOnlyAlertOnce(true)
                .SetOngoing(true);

            return builder.Build();
        }

        private void CreateNotificationChannelIfNeeded()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
                return;

            try
            {
                var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
                {
                    Description = "Keeps Joe's Scanner running for continuous audio playback"
                };

                channel.SetSound(null, null);
                channel.EnableVibration(false);
                channel.EnableLights(false);

                var manager = GetSystemService(NotificationService) as NotificationManager;
                if (manager == null)
                    return;

                manager.CreateNotificationChannel(channel);
            }
            catch
            {
            }
        }

        // Notification content is built and updated by AndroidMediaCenter.
    }
}
