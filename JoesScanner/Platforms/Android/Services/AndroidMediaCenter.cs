#if ANDROID
using System;
using System.Threading.Tasks;
using global::Android.App;
using global::Android.Content;
using global::Android.Media;
using global::Android.Media.Session;
using global::Android.OS;
using global::Android.Graphics.Drawables;

namespace JoesScanner.Platforms.Android.Services
{
    public static class AndroidMediaCenter
    {
        public const string NotificationChannelId = "joesscanner_playback";
        public const int NotificationId = 41001;

        private static MediaSession? _session;
        private static string _title = "Joe's Scanner";
        private static string _subtitle = "Disconnected";
        private static bool _isConnected;
        private static bool _isPlaying;

        private static Func<Task>? _onPlay;
        private static Func<Task>? _onStop;
        private static Func<Task>? _onNext;
        private static Func<Task>? _onPrevious;

        public static void SetHandlers(
            Func<Task> onPlay,
            Func<Task> onStop,
            Func<Task>? onNext = null,
            Func<Task>? onPrevious = null)
        {
            _onPlay = onPlay;
            _onStop = onStop;
            _onNext = onNext;
            _onPrevious = onPrevious;
        }

        public static void EnsureInitialized(Context context)
        {
            if (_session != null)
                return;

            _session = new MediaSession(context, "JoesScannerMediaSession");
            _session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
            _session.SetCallback(new SessionCallback());
            _session.Active = true;

            EnsureChannel(context);
            SetPlaybackState(context, false);
        }

        public static void SetConnected(Context context, bool isConnected)
        {
            EnsureInitialized(context);

            _isConnected = isConnected;

            if (!_isConnected)
            {
                _isPlaying = false;
                _title = "Joe's Scanner";
                _subtitle = "Disconnected";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_subtitle) || _subtitle == "Disconnected")
                    _subtitle = "Connected";
            }

            UpdateNotification(context);
        }

        public static void UpdateNowPlaying(Context context, string title, string subtitle)
        {
            EnsureInitialized(context);

            _title = string.IsNullOrWhiteSpace(title) ? "Joe's Scanner" : title;
            _subtitle = string.IsNullOrWhiteSpace(subtitle)
                ? (_isConnected ? "Connected" : "Disconnected")
                : subtitle;

            try
            {
                var meta = new MediaMetadata.Builder()
                    .PutString(MediaMetadata.MetadataKeyTitle, _title)
                    .PutString(MediaMetadata.MetadataKeyArtist, _subtitle)
                    .Build();

                _session?.SetMetadata(meta);
            }
            catch
            {
            }

            UpdateNotification(context);
        }

        public static void SetPlaybackState(Context context, bool isPlaying)
        {
            EnsureInitialized(context);

            _isPlaying = isPlaying;

            var state = isPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Stopped;

            var actions =
                PlaybackState.ActionPlay |
                PlaybackState.ActionStop |
                PlaybackState.ActionPlayPause;

            if (_isConnected)
                actions |= PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious;

            var ps = new PlaybackState.Builder()
                .SetState(state, PlaybackState.PlaybackPositionUnknown, 1.0f)
                .SetActions(actions)
                .Build();

            _session?.SetPlaybackState(ps);

            UpdateNotification(context);
        }

        public static void HandleAction(Context context, string? action)
        {
            if (string.IsNullOrWhiteSpace(action))
                return;

            EnsureInitialized(context);

            try
            {
                switch (action)
                {
                    case MediaActionReceiver.ActionPlay:
                        _ = _onPlay?.Invoke();
                        break;

                    case MediaActionReceiver.ActionStop:
                        _ = _onStop?.Invoke();
                        break;

                    case MediaActionReceiver.ActionNext:
                        _ = _onNext?.Invoke();
                        break;

                    case MediaActionReceiver.ActionPrevious:
                        _ = _onPrevious?.Invoke();
                        break;
                }
            }
            catch
            {
            }
        }

        public static Notification BuildNotification(Context context)
        {
            EnsureInitialized(context);

            var openIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName);
            PendingIntent? contentPending = null;

            if (openIntent != null)
            {
                openIntent.AddFlags(ActivityFlags.SingleTop);
                contentPending = PendingIntent.GetActivity(
                    context,
                    0,
                    openIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            }

            var playPending = BuildActionPendingIntent(context, MediaActionReceiver.ActionPlay, 1);
            var stopPending = BuildActionPendingIntent(context, MediaActionReceiver.ActionStop, 2);
            var nextPending = BuildActionPendingIntent(context, MediaActionReceiver.ActionNext, 3);
            var prevPending = BuildActionPendingIntent(context, MediaActionReceiver.ActionPrevious, 4);

            var builder = new Notification.Builder(context, NotificationChannelId)
                .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                .SetContentTitle(_title)
                .SetContentText(_subtitle)
                .SetOnlyAlertOnce(true)
                .SetOngoing(_isConnected);

            if (contentPending != null)
                builder.SetContentIntent(contentPending);

            var playLabel = new global::Java.Lang.String("Play");
            var stopLabel = new global::Java.Lang.String("Stop");
            var prevLabel = new global::Java.Lang.String("Prev");
            var nextLabel = new global::Java.Lang.String("Next");

            var playIcon = Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcMediaPlay);
            var stopIcon = Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcDelete);
            var prevIcon = Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcMediaPrevious);
            var nextIcon = Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcMediaNext);

            builder.AddAction(new Notification.Action.Builder(playIcon, playLabel, playPending).Build());
            builder.AddAction(new Notification.Action.Builder(stopIcon, stopLabel, stopPending).Build());

            if (_isConnected)
            {
                builder.AddAction(new Notification.Action.Builder(prevIcon, prevLabel, prevPending).Build());
                builder.AddAction(new Notification.Action.Builder(nextIcon, nextLabel, nextPending).Build());
            }

            var style = new Notification.MediaStyle();
            if (_session != null)
                style.SetMediaSession(_session.SessionToken);

            style.SetShowActionsInCompactView(0, 1);
            builder.SetStyle(style);

            return builder.Build();
        }

        public static void UpdateNotification(Context context)
        {
            EnsureInitialized(context);

            var nm = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (nm == null)
                return;

            nm.Notify(NotificationId, BuildNotification(context));
        }

        public static void Clear(Context context)
        {
            var nm = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            nm?.Cancel(NotificationId);

            if (_session != null)
            {
                try
                {
                    _session.Active = false;
                    _session.Release();
                }
                catch
                {
                }
                _session = null;
            }
        }

        private static void EnsureChannel(Context context)
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O)
                return;

            var nm = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (nm == null)
                return;

            var existing = nm.GetNotificationChannel(NotificationChannelId);
            if (existing != null)
                return;

            var channel = new NotificationChannel(
                NotificationChannelId,
                "Joe's Scanner Playback",
                NotificationImportance.Low);

            channel.Description = "Playback controls and now playing information";
            nm.CreateNotificationChannel(channel);
        }

        private static PendingIntent BuildActionPendingIntent(Context context, string action, int requestCode)
        {
            var intent = new Intent(context, typeof(MediaActionReceiver));
            intent.SetAction(action);

            return PendingIntent.GetBroadcast(
                context,
                requestCode,
                intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        }

        private sealed class SessionCallback : MediaSession.Callback
        {
            public override void OnPlay()
            {
                try { _ = _onPlay?.Invoke(); } catch { }
            }

            public override void OnStop()
            {
                try { _ = _onStop?.Invoke(); } catch { }
            }

            public override void OnPause()
            {
                // App semantics: Pause maps to Stop and Disconnect.
                try { _ = _onStop?.Invoke(); } catch { }
            }

            public override void OnSkipToNext()
            {
                try { _ = _onNext?.Invoke(); } catch { }
            }

            public override void OnSkipToPrevious()
            {
                try { _ = _onPrevious?.Invoke(); } catch { }
            }
        }
    }
}
#endif
