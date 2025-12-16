using System;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace JoesScanner.Platforms.Android.Services
{
    // Foreground service used to keep the app alive for continuous audio playback.
    // This does not play audio itself, it just keeps an ongoing notification active.
    [Service(Exported = false)]
    public class AudioForegroundService : Service
    {
        private const int NotificationId = 1001;
        private const string ChannelId = "joesscanner_audio";
        private const string ChannelName = "Joe's Scanner Audio";

        public override void OnCreate()
        {
            base.OnCreate();

            try
            {
                CreateNotificationChannelIfNeeded();
                StartForeground(NotificationId, BuildNotificationNonNull());
            }
            catch
            {
            }
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            try
            {
                CreateNotificationChannelIfNeeded();
                StartForeground(NotificationId, BuildNotificationNonNull());
            }
            catch
            {
            }

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

            base.OnDestroy();
        }

        public override IBinder? OnBind(Intent? intent) => null;

        private void CreateNotificationChannelIfNeeded()
        {
            // All NotificationChannel APIs are Android 26+.
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

        private Notification BuildNotificationNonNull()
        {
            var builder = new NotificationCompat.Builder(this, ChannelId);

            builder.SetContentTitle("Joe's Scanner");
            builder.SetContentText("Audio playback active");
            builder.SetOngoing(true);
            builder.SetOnlyAlertOnce(true);
            builder.SetPriority((int)NotificationPriority.Low);
            builder.SetSmallIcon(Resource.Mipmap.appicon);

            try
            {
                var pm = PackageManager;
                if (pm == null)
                    return ForceNonNull(builder.Build());

                var packageName = PackageName;
                if (string.IsNullOrWhiteSpace(packageName))
                    return ForceNonNull(builder.Build());

                var launchIntent = pm.GetLaunchIntentForPackage(packageName);
                if (launchIntent == null)
                    return ForceNonNull(builder.Build());

                launchIntent.AddFlags(ActivityFlags.SingleTop);

                var flags = PendingIntentFlags.UpdateCurrent;

                if (OperatingSystem.IsAndroidVersionAtLeast(23))
                    flags |= PendingIntentFlags.Immutable;

                var pending = PendingIntent.GetActivity(this, 0, launchIntent, flags);
                if (pending != null)
                {
                    builder.SetContentIntent(pending);
                }
            }
            catch
            {
            }

            return ForceNonNull(builder.Build());
        }

        private static Notification ForceNonNull(Notification? notification)
        {
            // Android bindings sometimes annotate Build() as nullable.
            // This guarantees our service never returns a null Notification.
            return notification ?? new Notification();
        }
    }
}
