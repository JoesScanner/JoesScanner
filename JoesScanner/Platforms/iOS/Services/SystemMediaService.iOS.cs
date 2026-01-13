#if IOS || MACCATALYST
using AVFoundation;
using Foundation;
using MediaPlayer;

namespace JoesScanner.Services
{
    public partial class SystemMediaService
    {
        private bool _sessionStarted;

        // These are the documented Now Playing keys. Using string keys avoids binding differences.
        private static readonly NSString KeyTitle = new NSString("title");
        private static readonly NSString KeyArtist = new NSString("artist");
        private static readonly NSString KeyPlaybackRate = new NSString("playbackRate");

        private partial void PlatformSetHandlers(Func<Task> onPlay, Func<Task> onStop, Func<Task>? onNext, Func<Task>? onPrevious)
        {
            try
            {
                var center = MPRemoteCommandCenter.Shared;

                center.PlayCommand.Enabled = true;
                center.PauseCommand.Enabled = true;
                center.StopCommand.Enabled = true;

                center.NextTrackCommand.Enabled = onNext != null;
                center.PreviousTrackCommand.Enabled = onPrevious != null;

                // Play -> Connect
                center.PlayCommand.AddTarget((MPRemoteCommandEvent evt) =>
                {
                    _ = Task.Run(onPlay);
                    return MPRemoteCommandHandlerStatus.Success;
                });

                // Pause and Stop -> Disconnect
                center.PauseCommand.AddTarget((MPRemoteCommandEvent evt) =>
                {
                    _ = Task.Run(onStop);
                    return MPRemoteCommandHandlerStatus.Success;
                });

                center.StopCommand.AddTarget((MPRemoteCommandEvent evt) =>
                {
                    _ = Task.Run(onStop);
                    return MPRemoteCommandHandlerStatus.Success;
                });

                if (onNext != null)
                {
                    center.NextTrackCommand.AddTarget((MPRemoteCommandEvent evt) =>
                    {
                        _ = Task.Run(onNext);
                        return MPRemoteCommandHandlerStatus.Success;
                    });
                }

                if (onPrevious != null)
                {
                    center.PreviousTrackCommand.AddTarget((MPRemoteCommandEvent evt) =>
                    {
                        _ = Task.Run(onPrevious);
                        return MPRemoteCommandHandlerStatus.Success;
                    });
                }
            }
            catch
            {
            }
        }

        private partial Task PlatformStartSessionAsync(bool audioEnabled)
        {
            if (_sessionStarted)
                return Task.CompletedTask;

            _sessionStarted = true;

            try
            {
                var session = AVAudioSession.SharedInstance();
                var options = AVAudioSessionCategoryOptions.AllowBluetooth | AVAudioSessionCategoryOptions.AllowBluetoothA2DP;
                session.SetCategory(AVAudioSessionCategory.Playback, options);
                session.SetActive(true);
            }
            catch
            {
            }

            UpdateNowPlaying("Connected", "Joes Scanner", audioEnabled);
            return Task.CompletedTask;
        }

        private partial Task PlatformStopSessionAsync()
        {
            if (!_sessionStarted)
                return Task.CompletedTask;

            _sessionStarted = false;

            try
            {
                SetNowPlaying(null);
            }
            catch
            {
            }

            try
            {
                var session = AVAudioSession.SharedInstance();
                session.SetActive(false);
            }
            catch
            {
            }

            return Task.CompletedTask;
        }

        private partial void PlatformUpdateNowPlaying(string title, string subtitle, bool audioEnabled)
        {
            try
            {
                var info = new NSMutableDictionary();
                info[KeyTitle] = new NSString(title ?? string.Empty);

                var artist = audioEnabled ? (subtitle ?? string.Empty) : ((subtitle ?? string.Empty) + " (Audio Off)");
                info[KeyArtist] = new NSString(artist);
                info[KeyPlaybackRate] = NSNumber.FromDouble(_sessionStarted ? 1.0 : 0.0);

                SetNowPlaying(info);
            }
            catch
            {
            }
        }

        private partial void PlatformClear()
        {
            try
            {
                SetNowPlaying(null);
            }
            catch
            {
            }
        }

        private static void SetNowPlaying(NSDictionary? value)
        {
            var center = MPNowPlayingInfoCenter.DefaultCenter;

            // Different bindings expose different property names/types.
            // Use reflection so this compiles across Microsoft.iOS binding differences.
            var t = center.GetType();

            var prop = t.GetProperty("NowPlayingInfo") ?? t.GetProperty("NowPlaying") ?? t.GetProperty("NowPlayingItem");
            if (prop == null)
                return;

            prop.SetValue(center, value, null);
        }
    }
}
#endif
