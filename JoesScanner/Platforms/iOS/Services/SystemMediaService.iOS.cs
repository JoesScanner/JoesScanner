#if IOS || MACCATALYST
using AVFoundation;
using Foundation;
using MediaPlayer;
using UIKit;

namespace JoesScanner.Services
{
    public partial class SystemMediaService
    {
        private bool _sessionStarted;
        private bool _remoteEventsStarted;
        private MPMediaItemArtwork? _cachedArtwork;
        private AVAudioPlayer? _silenceKeepAlivePlayer;

        // 1 second of silence (PCM 16-bit, 44.1kHz, mono) wrapped in a WAV header.
        // This is used as a keepalive so iOS does not suspend the process between discrete audio clips while locked.
        private static readonly byte[] SilenceWav = BuildSilenceWav1sPcm16Mono44100();

        // These are the documented Now Playing keys. Using string keys avoids binding differences.
        private static readonly NSString KeyTitle = new NSString("title");
        private static readonly NSString KeyArtist = new NSString("artist");
        private static readonly NSString KeyAlbumTitle = new NSString("albumTitle");
        private static readonly NSString KeyComposer = new NSString("composer");
        private static readonly NSString KeyGenre = new NSString("genre");
        private static readonly NSString KeyPlaybackRate = new NSString("playbackRate");
        private static readonly NSString KeyArtwork = new NSString("artwork");
        private static readonly NSString KeyIsLiveStream = new NSString("isLiveStream");
        private static readonly NSString KeyDefaultPlaybackRate = new NSString("defaultPlaybackRate");

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

            try
            {
                StartSilenceKeepAlive();
            }
            catch
            {
            }

            // Ensures iOS routes remote control events (lock screen and Control Center) to our handlers.
            try
            {
                if (!_remoteEventsStarted)
                {
                    UIApplication.SharedApplication.BeginReceivingRemoteControlEvents();
                    _remoteEventsStarted = true;
                }
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

            try
            {
                StopSilenceKeepAlive();
            }
            catch
            {
            }

            try
            {
                if (_remoteEventsStarted)
                {
                    UIApplication.SharedApplication.EndReceivingRemoteControlEvents();
                    _remoteEventsStarted = false;
                }
            }
            catch
            {
            }

            return Task.CompletedTask;
        }

        private partial void PlatformUpdateNowPlaying(NowPlayingMetadata metadata, bool audioEnabled)
        {
            try
            {
                var info = new NSMutableDictionary();
                var title = metadata?.Title ?? string.Empty;
                var artistRaw = metadata?.Artist ?? string.Empty;
                var album = metadata?.Album ?? string.Empty;
                var composer = metadata?.Composer ?? string.Empty;
                var genre = metadata?.Genre ?? string.Empty;

                info[KeyTitle] = new NSString(title);

                var artist = audioEnabled ? artistRaw : (artistRaw + " (Audio Off)");
                info[KeyArtist] = new NSString(artist);

                if (!string.IsNullOrWhiteSpace(album))
                    info[KeyAlbumTitle] = new NSString(album);

                if (!string.IsNullOrWhiteSpace(composer))
                    info[KeyComposer] = new NSString(composer);

                if (!string.IsNullOrWhiteSpace(genre))
                    info[KeyGenre] = new NSString(genre);

                // Make the lock screen Now Playing UI behave like audio playback.
                // Marking this as a non live stream helps iOS show expected controls for discrete clips.
                info[KeyIsLiveStream] = NSNumber.FromBoolean(false);
                info[KeyDefaultPlaybackRate] = NSNumber.FromDouble(1.0);
                info[KeyPlaybackRate] = NSNumber.FromDouble(_sessionStarted ? 1.0 : 0.0);

                // Optional artwork for the Now Playing card.
                var artwork = GetOrCreateArtwork();
                if (artwork != null)
                    info[KeyArtwork] = artwork;

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

        private MPMediaItemArtwork? GetOrCreateArtwork()
        {
            try
            {
                if (_cachedArtwork != null)
                    return _cachedArtwork;

                // Prefer the standard logo asset.
                // MAUI images in Resources/Images are bundled and can be loaded by name without extension.
                var image = UIImage.FromBundle("logo_button") ?? UIImage.FromBundle("logo_button.png");
                if (image == null)
                    return null;

                _cachedArtwork = new MPMediaItemArtwork(image);
                return _cachedArtwork;
            }
            catch
            {
                return null;
            }
        }

        private void StartSilenceKeepAlive()
        {
            if (_silenceKeepAlivePlayer != null)
                return;

            NSError? error;
            using var data = NSData.FromArray(SilenceWav);
            _silenceKeepAlivePlayer = new AVAudioPlayer(data, "wav", out error);
            if (_silenceKeepAlivePlayer == null)
                return;

            try
            {
                _silenceKeepAlivePlayer.NumberOfLoops = -1;
                _silenceKeepAlivePlayer.Volume = 0f;
                _silenceKeepAlivePlayer.PrepareToPlay();
                _silenceKeepAlivePlayer.Play();
            }
            catch
            {
            }
        }

        private void StopSilenceKeepAlive()
        {
            try
            {
                _silenceKeepAlivePlayer?.Stop();
                _silenceKeepAlivePlayer?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _silenceKeepAlivePlayer = null;
            }
        }

        private static byte[] BuildSilenceWav1sPcm16Mono44100()
        {
            const int sampleRate = 44100;
            const short channels = 1;
            const short bitsPerSample = 16;
            const short blockAlign = (short)(channels * (bitsPerSample / 8));
            const int byteRate = sampleRate * blockAlign;
            const int sampleCount = sampleRate; // 1 second
            const int dataSize = sampleCount * blockAlign;

            // WAV header is 44 bytes.
            var buffer = new byte[44 + dataSize];

            void WriteAscii(int offset, string text)
            {
                for (var i = 0; i < text.Length; i++)
                    buffer[offset + i] = (byte)text[i];
            }

            void WriteInt32(int offset, int value)
            {
                buffer[offset + 0] = (byte)(value & 0xFF);
                buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
                buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
                buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
            }

            void WriteInt16(int offset, short value)
            {
                buffer[offset + 0] = (byte)(value & 0xFF);
                buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            }

            // RIFF chunk
            WriteAscii(0, "RIFF");
            WriteInt32(4, 36 + dataSize);
            WriteAscii(8, "WAVE");

            // fmt subchunk
            WriteAscii(12, "fmt ");
            WriteInt32(16, 16); // PCM
            WriteInt16(20, 1); // audio format = PCM
            WriteInt16(22, channels);
            WriteInt32(24, sampleRate);
            WriteInt32(28, byteRate);
            WriteInt16(32, blockAlign);
            WriteInt16(34, bitsPerSample);

            // data subchunk
            WriteAscii(36, "data");
            WriteInt32(40, dataSize);

            // The data portion is already zeroed (silence).
            return buffer;
        }
    }
}
#endif
