using System;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace JoesScanner.Platforms.Android.Services
{
    [Service(
        Exported = false,
        ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
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
                AndroidMediaCenter.EnsureInitialized(this);
                StartForeground(NotificationId, AndroidMediaCenter.BuildNotification(this));
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
                AndroidMediaCenter.EnsureInitialized(this);
                StartForeground(NotificationId, AndroidMediaCenter.BuildNotification(this));
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
