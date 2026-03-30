#if IOS
using AVFoundation;
using Foundation;
using MediaPlayer;
using UIKit;

namespace JoesScanner.Services
{
    public partial class SystemMediaService
    {
        private static readonly NSString AudioSessionInterruptionNotificationName = new NSString("AVAudioSessionInterruptionNotification");
        private static readonly NSString AudioSessionRouteChangeNotificationName = new NSString("AVAudioSessionRouteChangeNotification");
        private static readonly NSString AudioSessionMediaServicesWereResetNotificationName = new NSString("AVAudioSessionMediaServicesWereResetNotification");
        private static readonly NSString UIApplicationDidBecomeActiveNotificationName = new NSString("UIApplicationDidBecomeActiveNotification");
        private static readonly NSString UIApplicationWillEnterForegroundNotificationName = new NSString("UIApplicationWillEnterForegroundNotification");
        private static readonly NSString AVAudioSessionInterruptionTypeKeyName = new NSString("AVAudioSessionInterruptionTypeKey");
        private static readonly NSString AVAudioSessionInterruptionOptionKeyName = new NSString("AVAudioSessionInterruptionOptionKey");
        private static readonly NSString AVAudioSessionRouteChangeReasonKeyName = new NSString("AVAudioSessionRouteChangeReasonKey");

        private readonly object _audioSessionGate = new();
        private bool _sessionStarted;
        private bool _remoteEventsStarted;
        private bool _lastAudioEnabled = true;
        private bool _observersRegistered;
        private MPMediaItemArtwork? _cachedArtwork;
        private AVAudioPlayer? _silenceKeepAlivePlayer;
        private NSObject? _interruptionObserver;
        private NSObject? _routeChangeObserver;
        private NSObject? _mediaServicesResetObserver;
        private NSObject? _didBecomeActiveObserver;
        private NSObject? _willEnterForegroundObserver;

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

                center.PlayCommand.AddTarget((MPRemoteCommandEvent evt) =>
                {
                    _ = Task.Run(onPlay);
                    return MPRemoteCommandHandlerStatus.Success;
                });

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
            _sessionStarted = true;
            _lastAudioEnabled = audioEnabled;

            EnsureAudioSessionObserversRegistered();
            ConfigurePlaybackSession("StartSession", audioEnabled, deactivateFirst: true);

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
                try { AppLog.Add("AudioSession(iOS): NowPlaying cleared."); } catch { }
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
                var session = AVAudioSession.SharedInstance();
                session.SetActive(false);
                AppLog.Add("AudioSession(iOS): SetActive(false) success. reason=StopSession");
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AudioSession(iOS): SetActive(false) failed. reason=StopSession error={ex.Message}"); } catch { }
            }

            RemoveAudioSessionObservers();

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

        private partial Task PlatformRefreshAudioSessionAsync(bool audioEnabled, string reason)
        {
            _lastAudioEnabled = audioEnabled;

            if (_sessionStarted)
                EnsureAudioSessionObserversRegistered();

            ConfigurePlaybackSession(reason, audioEnabled, deactivateFirst: true);
            return Task.CompletedTask;
        }

        private partial void PlatformUpdateNowPlaying(NowPlayingMetadata metadata, bool audioEnabled)
        {
            try
            {
                _lastAudioEnabled = audioEnabled;

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

                info[KeyIsLiveStream] = NSNumber.FromBoolean(false);
                info[KeyDefaultPlaybackRate] = NSNumber.FromDouble(1.0);
                info[KeyPlaybackRate] = NSNumber.FromDouble(_sessionStarted ? 1.0 : 0.0);

                var artwork = GetOrCreateArtwork();
                if (artwork != null)
                    info[KeyArtwork] = artwork;

                SetNowPlaying(info);
                try { AppLog.Add(() => $"AudioSession(iOS): NowPlaying updated. title='{title}' artist='{artist}' audioEnabled={audioEnabled} sessionStarted={_sessionStarted}"); } catch { }
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
                try { AppLog.Add("AudioSession(iOS): NowPlaying cleared via Clear."); } catch { }
            }
            catch
            {
            }
        }

        private void EnsureAudioSessionObserversRegistered()
        {
            if (_observersRegistered)
                return;

            try
            {
                var center = NSNotificationCenter.DefaultCenter;

                _interruptionObserver = center.AddObserver(AudioSessionInterruptionNotificationName, OnAudioSessionInterrupted);
                _routeChangeObserver = center.AddObserver(AudioSessionRouteChangeNotificationName, OnAudioSessionRouteChanged);
                _mediaServicesResetObserver = center.AddObserver(AudioSessionMediaServicesWereResetNotificationName, OnAudioSessionMediaServicesWereReset);
                _didBecomeActiveObserver = center.AddObserver(UIApplicationDidBecomeActiveNotificationName, _ => ReapplyAudioSessionAsync("AppDidBecomeActive"));
                _willEnterForegroundObserver = center.AddObserver(UIApplicationWillEnterForegroundNotificationName, _ => ReapplyAudioSessionAsync("AppWillEnterForeground"));
                _observersRegistered = true;
                AppLog.Add("AudioSession(iOS): observers registered.");
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AudioSession(iOS): observer registration failed. error={ex.Message}"); } catch { }
            }
        }

        private void RemoveAudioSessionObservers()
        {
            if (!_observersRegistered)
                return;

            try
            {
                var center = NSNotificationCenter.DefaultCenter;
                RemoveObserver(center, ref _interruptionObserver);
                RemoveObserver(center, ref _routeChangeObserver);
                RemoveObserver(center, ref _mediaServicesResetObserver);
                RemoveObserver(center, ref _didBecomeActiveObserver);
                RemoveObserver(center, ref _willEnterForegroundObserver);
                _observersRegistered = false;
                AppLog.Add("AudioSession(iOS): observers removed.");
            }
            catch
            {
            }
        }

        private static void RemoveObserver(NSNotificationCenter center, ref NSObject? observer)
        {
            try
            {
                if (observer != null)
                    center.RemoveObserver(observer);
            }
            catch
            {
            }
            finally
            {
                observer = null;
            }
        }

        private void OnAudioSessionInterrupted(NSNotification notification)
        {
            try
            {
                ulong typeValue = 0;
                ulong optionValue = 0;

                if (notification.UserInfo != null)
                {
                    if (notification.UserInfo[AVAudioSessionInterruptionTypeKeyName] is NSNumber typeNumber)
                        typeValue = typeNumber.UInt64Value;

                    if (notification.UserInfo[AVAudioSessionInterruptionOptionKeyName] is NSNumber optionNumber)
                        optionValue = optionNumber.UInt64Value;
                }

                AppLog.Add(() => $"AudioSession(iOS): interruption observed. type={typeValue} options={optionValue} sessionStarted={_sessionStarted}");

                if (typeValue == 1)
                    ReapplyAudioSessionAsync("InterruptionEnded");
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AudioSession(iOS): interruption handling failed. error={ex.Message}"); } catch { }
            }
        }

        private void OnAudioSessionRouteChanged(NSNotification notification)
        {
            try
            {
                ulong reasonValue = 0;
                if (notification.UserInfo != null && notification.UserInfo[AVAudioSessionRouteChangeReasonKeyName] is NSNumber reasonNumber)
                    reasonValue = reasonNumber.UInt64Value;

                AppLog.Add(() => $"AudioSession(iOS): route change observed. reason={reasonValue} sessionStarted={_sessionStarted}");
                ReapplyAudioSessionAsync($"RouteChange:{reasonValue}");
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AudioSession(iOS): route change handling failed. error={ex.Message}"); } catch { }
            }
        }

        private void OnAudioSessionMediaServicesWereReset(NSNotification notification)
        {
            try
            {
                AppLog.Add("AudioSession(iOS): media services reset observed.");
            }
            catch
            {
            }

            ReapplyAudioSessionAsync("MediaServicesReset");
        }

        private void ReapplyAudioSessionAsync(string reason)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await PlatformRefreshAudioSessionAsync(_lastAudioEnabled, reason);
                }
                catch (Exception ex)
                {
                    try { AppLog.Add(() => $"AudioSession(iOS): async reapply failed. reason={reason} error={ex.Message}"); } catch { }
                }
            });
        }

        private static void SetNowPlaying(NSDictionary? value)
        {
            var center = MPNowPlayingInfoCenter.DefaultCenter;
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

                var image = UIImage.FromBundle("logo_button") ?? UIImage.FromBundle("logo_button.png");
                if (image == null)
                    return null;

                var boundsSize = image.Size;
                _cachedArtwork = new MPMediaItemArtwork(boundsSize, _ => image);
                return _cachedArtwork;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsForegroundActive()
        {
            try
            {
                return UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active;
            }
            catch
            {
                return false;
            }
        }

        private void LogAudioSessionState(string reason, AVAudioSession session, AVAudioSessionCategoryOptions requestedOptions, bool mixEnabled, bool keepAliveDesired)
        {
            try
            {
                var category = string.Empty;
                var mode = string.Empty;
                var otherAudioPlaying = false;
                var currentRoute = string.Empty;
                try { category = session.Category ?? string.Empty; } catch { }
                try { mode = session.Mode ?? string.Empty; } catch { }
                try { otherAudioPlaying = session.OtherAudioPlaying; } catch { }
                try
                {
                    var outputs = session.CurrentRoute?.Outputs;
                    if (outputs != null && outputs.Length > 0)
                        currentRoute = string.Join(",", outputs.Select(o => $"{o.PortType}:{o.PortName}"));
                }
                catch { }

                AppLog.Add(() => $"AudioSession(iOS): reason={reason} mixEnabled={mixEnabled} requestedOptions={requestedOptions} category={category} mode={mode} otherAudioPlaying={otherAudioPlaying} appState={(IsForegroundActive() ? "Active" : "BackgroundLike")} keepAliveDesired={keepAliveDesired} keepAliveRunning={_silenceKeepAlivePlayer != null} route={currentRoute}");
            }
            catch
            {
            }
        }

        private void ConfigurePlaybackSession(string reason, bool audioEnabled, bool deactivateFirst)
        {
            lock (_audioSessionGate)
            {
                var mixEnabled = IsMobileMixAudioWithOtherAppsEnabled();
                var keepAliveDesired = !mixEnabled || !IsForegroundActive();

                try
                {
                    var session = AVAudioSession.SharedInstance();
                    var options = AVAudioSessionCategoryOptions.AllowBluetooth | AVAudioSessionCategoryOptions.AllowBluetoothA2DP;
                    if (mixEnabled)
                        options |= AVAudioSessionCategoryOptions.MixWithOthers;

                    if (deactivateFirst)
                    {
                        try
                        {
                            session.SetActive(false);
                            AppLog.Add(() => $"AudioSession(iOS): SetActive(false) success. reason={reason} step=preconfigure");
                        }
                        catch (Exception ex)
                        {
                            try { AppLog.Add(() => $"AudioSession(iOS): SetActive(false) failed. reason={reason} step=preconfigure error={ex.Message}"); } catch { }
                        }
                    }

                    session.SetCategory(AVAudioSessionCategory.Playback, options);
                    AppLog.Add(() => $"AudioSession(iOS): SetCategory success. reason={reason} category=Playback options={options} audioEnabled={audioEnabled}");

                    session.SetActive(true);
                    AppLog.Add(() => $"AudioSession(iOS): SetActive(true) success. reason={reason}");

                    LogAudioSessionState(reason, session, options, mixEnabled, keepAliveDesired);
                }
                catch (Exception ex)
                {
                    try { AppLog.Add(() => $"AudioSession(iOS): configure failed. reason={reason} error={ex.Message}"); } catch { }
                }

                try
                {
                    if (keepAliveDesired)
                        StartSilenceKeepAlive();
                    else
                        StopSilenceKeepAlive();
                }
                catch (Exception ex)
                {
                    try { AppLog.Add(() => $"AudioSession(iOS): keepalive transition failed. reason={reason} error={ex.Message}"); } catch { }
                }
            }
        }

        private void StartSilenceKeepAlive()
        {
            if (_silenceKeepAlivePlayer != null)
                return;

            NSError? error;
            using var data = NSData.FromArray(SilenceWav);
            _silenceKeepAlivePlayer = AVAudioPlayer.FromData(data, out error);
            if (_silenceKeepAlivePlayer == null)
            {
                try { AppLog.Add(() => $"AudioSession(iOS): keepalive creation failed. error={(error?.LocalizedDescription ?? "unknown")}"); } catch { }
                return;
            }

            try
            {
                _silenceKeepAlivePlayer.NumberOfLoops = -1;
                _silenceKeepAlivePlayer.Volume = 0f;
                _silenceKeepAlivePlayer.PrepareToPlay();
                _silenceKeepAlivePlayer.Play();
                try { AppLog.Add("AudioSession(iOS): keepalive started."); } catch { }
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
                try { AppLog.Add("AudioSession(iOS): keepalive stopped."); } catch { }
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
            const int sampleCount = sampleRate;
            const int dataSize = sampleCount * blockAlign;
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

            WriteAscii(0, "RIFF");
            WriteInt32(4, 36 + dataSize);
            WriteAscii(8, "WAVE");
            WriteAscii(12, "fmt ");
            WriteInt32(16, 16);
            WriteInt16(20, 1);
            WriteInt16(22, channels);
            WriteInt32(24, sampleRate);
            WriteInt32(28, byteRate);
            WriteInt16(32, blockAlign);
            WriteInt16(34, bitsPerSample);
            WriteAscii(36, "data");
            WriteInt32(40, dataSize);
            return buffer;
        }
    }
}
#endif
