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
        private static readonly NSString UIApplicationWillResignActiveNotificationName = new NSString("UIApplicationWillResignActiveNotification");
        private static readonly NSString UIApplicationDidEnterBackgroundNotificationName = new NSString("UIApplicationDidEnterBackgroundNotification");
        private static readonly NSString AVAudioSessionInterruptionTypeKeyName = new NSString("AVAudioSessionInterruptionTypeKey");
        private static readonly NSString AVAudioSessionInterruptionOptionKeyName = new NSString("AVAudioSessionInterruptionOptionKey");
        private static readonly NSString AVAudioSessionRouteChangeReasonKeyName = new NSString("AVAudioSessionRouteChangeReasonKey");

        private readonly object _audioSessionGate = new();
        private bool _sessionStarted;
        private bool _transientClipSessionActive;
        private bool _clipPlaybackActive;
        private bool _sessionConfigured;
        private bool _sessionActivated;
        private bool _remoteEventsStarted;
        private bool _remoteCommandsEnabled;
        private bool _lastAudioEnabled = true;
        private bool _observersRegistered;
        private bool _isAppForegroundActive = true;
        private bool _isInterrupted;
        private AVAudioSessionCategoryOptions _lastConfiguredOptions;
        private NowPlayingMetadata _lastNowPlayingMetadata = new();
        private MPMediaItemArtwork? _cachedArtwork;
        private AVAudioPlayer? _silenceKeepAlivePlayer;
        private NSObject? _interruptionObserver;
        private NSObject? _routeChangeObserver;
        private NSObject? _mediaServicesResetObserver;
        private NSObject? _didBecomeActiveObserver;
        private NSObject? _willEnterForegroundObserver;
        private NSObject? _willResignActiveObserver;
        private NSObject? _didEnterBackgroundObserver;

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

            UpdateRemoteTransportState("SetHandlers");
        }

        private partial Task PlatformStartSessionAsync(bool audioEnabled)
        {
            _sessionStarted = true;
            _lastAudioEnabled = audioEnabled;
            _isAppForegroundActive = IsForegroundActive();
            _lastNowPlayingMetadata = new NowPlayingMetadata
            {
                Title = "Connected",
                Artist = "Joes Scanner"
            };

            EnsureAudioSessionObserversRegistered();
            EnsurePlaybackSessionConfigured("StartSession", audioEnabled, forceCategoryUpdate: true);
            UpdateKeepAliveState("StartSession");
            UpdateRemoteTransportState("StartSession");
            RefreshNowPlayingState("StartSession");
            return Task.CompletedTask;
        }

        private partial Task PlatformStopSessionAsync()
        {
            _sessionStarted = false;
            _clipPlaybackActive = false;

            ClearPublishedNowPlaying("StopSession");

            try
            {
                if (!_transientClipSessionActive)
                    DeactivatePlaybackSession("StopSession", removeObserversWhenIdle: true);
                else
                    UpdateKeepAliveState("StopSession");
            }
            catch
            {
            }

            UpdateRemoteTransportState("StopSession");
            return Task.CompletedTask;
        }

        private partial Task PlatformRefreshAudioSessionAsync(bool audioEnabled, string reason)
        {
            _lastAudioEnabled = audioEnabled;
            _isAppForegroundActive = IsForegroundActive();

            if (IsPlaybackSessionOwned())
            {
                EnsureAudioSessionObserversRegistered();
                EnsurePlaybackSessionConfigured(reason, audioEnabled, forceCategoryUpdate: true);
                UpdateKeepAliveState(reason);
            }

            UpdateRemoteTransportState(reason);
            RefreshNowPlayingState(reason);
            return Task.CompletedTask;
        }

        private partial Task PlatformSetClipPlaybackStateAsync(bool isPlaying, bool audioEnabled, string reason)
        {
            _lastAudioEnabled = audioEnabled;
            _isAppForegroundActive = IsForegroundActive();

            if (isPlaying)
            {
                _clipPlaybackActive = true;
                EnsureAudioSessionObserversRegistered();

                if (!_sessionStarted)
                    _transientClipSessionActive = true;

                EnsurePlaybackSessionConfigured(reason, audioEnabled, forceCategoryUpdate: !_sessionStarted);
                UpdateKeepAliveState(reason);
            }
            else
            {
                _clipPlaybackActive = false;

                if (_sessionStarted)
                {
                    UpdateKeepAliveState(reason);
                }
                else if (_transientClipSessionActive)
                {
                    _transientClipSessionActive = false;
                    DeactivatePlaybackSession(reason, removeObserversWhenIdle: true);
                }
            }

            UpdateRemoteTransportState(reason);
            RefreshNowPlayingState(reason);
            return Task.CompletedTask;
        }

        private partial void PlatformUpdateNowPlaying(NowPlayingMetadata metadata, bool audioEnabled)
        {
            try
            {
                _lastAudioEnabled = audioEnabled;
                _lastNowPlayingMetadata = CloneMetadata(metadata);
                RefreshNowPlayingState("MetadataUpdate");
            }
            catch
            {
            }
        }

        private partial void PlatformClear()
        {
            try
            {
                _lastNowPlayingMetadata = new NowPlayingMetadata();
                ClearPublishedNowPlaying("Clear");
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
                _didBecomeActiveObserver = center.AddObserver(UIApplicationDidBecomeActiveNotificationName, _ => OnApplicationDidBecomeActive());
                _willEnterForegroundObserver = center.AddObserver(UIApplicationWillEnterForegroundNotificationName, _ => OnApplicationWillEnterForeground());
                _willResignActiveObserver = center.AddObserver(UIApplicationWillResignActiveNotificationName, _ => OnApplicationWillResignActive());
                _didEnterBackgroundObserver = center.AddObserver(UIApplicationDidEnterBackgroundNotificationName, _ => OnApplicationDidEnterBackground());
                _observersRegistered = true;
                _isAppForegroundActive = IsForegroundActive();
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
                RemoveObserver(center, ref _willResignActiveObserver);
                RemoveObserver(center, ref _didEnterBackgroundObserver);
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

        private void OnApplicationWillResignActive()
        {
            _isAppForegroundActive = false;
            ReevaluatePlaybackStateAsync("AppWillResignActive", forceCategoryUpdate: false);
        }

        private void OnApplicationDidEnterBackground()
        {
            _isAppForegroundActive = false;
            ReevaluatePlaybackStateAsync("AppDidEnterBackground", forceCategoryUpdate: false);
        }

        private void OnApplicationWillEnterForeground()
        {
            _isAppForegroundActive = true;
            ReevaluatePlaybackStateAsync("AppWillEnterForeground", forceCategoryUpdate: false);
        }

        private void OnApplicationDidBecomeActive()
        {
            _isAppForegroundActive = true;
            ReevaluatePlaybackStateAsync("AppDidBecomeActive", forceCategoryUpdate: false);
        }

        private void OnAudioSessionInterrupted(NSNotification notification)
        {
            try
            {
                AVAudioSessionInterruptionType? interruptionType = null;
                var interruptionOptions = (AVAudioSessionInterruptionOptions)0;

                if (notification.UserInfo != null)
                {
                    if (notification.UserInfo[AVAudioSessionInterruptionTypeKeyName] is NSNumber typeNumber)
                        interruptionType = (AVAudioSessionInterruptionType)(nuint)typeNumber.UInt64Value;

                    if (notification.UserInfo[AVAudioSessionInterruptionOptionKeyName] is NSNumber optionNumber)
                        interruptionOptions = (AVAudioSessionInterruptionOptions)(nuint)optionNumber.UInt64Value;
                }

                var shouldResume = interruptionOptions.HasFlag(AVAudioSessionInterruptionOptions.ShouldResume);
                AppLog.Add(() => $"AudioSession(iOS): interruption observed. type={interruptionType?.ToString() ?? "unknown"} shouldResume={shouldResume} sessionStarted={_sessionStarted} transientSession={_transientClipSessionActive} clipPlaybackActive={_clipPlaybackActive}");

                if (interruptionType == AVAudioSessionInterruptionType.Began)
                {
                    _isInterrupted = true;

                    lock (_audioSessionGate)
                    {
                        _sessionActivated = false;
                    }

                    UpdateKeepAliveState("InterruptionBegan");
                    UpdateRemoteTransportState("InterruptionBegan");
                    RefreshNowPlayingState("InterruptionBegan");
                    return;
                }

                if (interruptionType == AVAudioSessionInterruptionType.Ended)
                {
                    _isInterrupted = false;

                    if (shouldResume)
                    {
                        ReevaluatePlaybackStateAsync("InterruptionEnded", forceCategoryUpdate: false);
                    }
                    else
                    {
                        UpdateRemoteTransportState("InterruptionEndedNoResume");
                        RefreshNowPlayingState("InterruptionEndedNoResume");
                    }
                }
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
                var routeChangeReason = AVAudioSessionRouteChangeReason.Unknown;
                if (notification.UserInfo != null && notification.UserInfo[AVAudioSessionRouteChangeReasonKeyName] is NSNumber reasonNumber)
                    routeChangeReason = (AVAudioSessionRouteChangeReason)(nuint)reasonNumber.UInt64Value;

                AppLog.Add(() => $"AudioSession(iOS): route change observed. reason={routeChangeReason} sessionStarted={_sessionStarted} transientSession={_transientClipSessionActive} interrupted={_isInterrupted}");

                if (routeChangeReason == AVAudioSessionRouteChangeReason.CategoryChange)
                    return;

                UpdateKeepAliveState($"RouteChange:{routeChangeReason}");
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

            lock (_audioSessionGate)
            {
                _sessionConfigured = false;
                _sessionActivated = false;
                _lastConfiguredOptions = 0;
            }

            _isInterrupted = false;
            ReevaluatePlaybackStateAsync("MediaServicesReset", forceCategoryUpdate: true);
        }

        private void ReevaluatePlaybackStateAsync(string reason, bool forceCategoryUpdate)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!IsPlaybackSessionOwned())
                    {
                        UpdateRemoteTransportState(reason);
                        RefreshNowPlayingState(reason);
                        return;
                    }

                    EnsurePlaybackSessionConfigured(reason, _lastAudioEnabled, forceCategoryUpdate);
                    UpdateKeepAliveState(reason);
                    UpdateRemoteTransportState(reason);
                    RefreshNowPlayingState(reason);
                }
                catch (Exception ex)
                {
                    try { AppLog.Add(() => $"AudioSession(iOS): async reevaluate failed. reason={reason} error={ex.Message}"); } catch { }
                }
            });
        }

        private bool IsPlaybackSessionOwned()
        {
            return _sessionStarted || _transientClipSessionActive;
        }

        private bool ShouldRegisterAsNowPlayingCandidate()
        {
            if (!IsPlaybackSessionOwned())
                return false;

            if (_isInterrupted)
                return false;

            if (!_sessionActivated)
                return false;

            return !IsMobileMixAudioWithOtherAppsEnabled();
        }

        private void UpdateRemoteTransportState(string reason)
        {
            try
            {
                var shouldEnable = ShouldRegisterAsNowPlayingCandidate() && _onPlay != null && _onStop != null;
                var center = MPRemoteCommandCenter.Shared;

                center.PlayCommand.Enabled = shouldEnable;
                center.PauseCommand.Enabled = shouldEnable;
                center.StopCommand.Enabled = shouldEnable;
                center.NextTrackCommand.Enabled = shouldEnable && _onNext != null;
                center.PreviousTrackCommand.Enabled = shouldEnable && _onPrevious != null;

                if (shouldEnable && !_remoteEventsStarted)
                {
                    UIApplication.SharedApplication.BeginReceivingRemoteControlEvents();
                    _remoteEventsStarted = true;
                }
                else if (!shouldEnable && _remoteEventsStarted)
                {
                    UIApplication.SharedApplication.EndReceivingRemoteControlEvents();
                    _remoteEventsStarted = false;
                }

                if (_remoteCommandsEnabled != shouldEnable)
                {
                    _remoteCommandsEnabled = shouldEnable;
                    AppLog.Add(() => $"AudioSession(iOS): remote transport {(shouldEnable ? "enabled" : "disabled")}. reason={reason} mixEnabled={IsMobileMixAudioWithOtherAppsEnabled()} sessionStarted={_sessionStarted} transientSession={_transientClipSessionActive} interrupted={_isInterrupted}");
                }
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AudioSession(iOS): remote transport update failed. reason={reason} error={ex.Message}"); } catch { }
            }
        }

        private void RefreshNowPlayingState(string reason)
        {
            try
            {
                if (ShouldRegisterAsNowPlayingCandidate())
                    PublishNowPlaying(_lastNowPlayingMetadata, _lastAudioEnabled, reason);
                else
                    ClearPublishedNowPlaying(reason);
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AudioSession(iOS): now playing refresh failed. reason={reason} error={ex.Message}"); } catch { }
            }
        }

        private void PublishNowPlaying(NowPlayingMetadata metadata, bool audioEnabled, string reason)
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

            info[KeyIsLiveStream] = NSNumber.FromBoolean(false);
            info[KeyDefaultPlaybackRate] = NSNumber.FromDouble(1.0);
            info[KeyPlaybackRate] = NSNumber.FromDouble(_clipPlaybackActive ? 1.0 : 0.0);

            var artwork = GetOrCreateArtwork();
            if (artwork != null)
                info[KeyArtwork] = artwork;

            SetNowPlaying(info);
            try { AppLog.Add(() => $"AudioSession(iOS): NowPlaying published. reason={reason} title='{title}' artist='{artist}' audioEnabled={audioEnabled} clipPlaybackActive={_clipPlaybackActive}"); } catch { }
        }

        private void ClearPublishedNowPlaying(string reason)
        {
            try
            {
                SetNowPlaying(null);
                AppLog.Add(() => $"AudioSession(iOS): NowPlaying cleared. reason={reason}");
            }
            catch
            {
            }
        }

        private static NowPlayingMetadata CloneMetadata(NowPlayingMetadata? metadata)
        {
            return new NowPlayingMetadata
            {
                Title = metadata?.Title ?? string.Empty,
                Artist = metadata?.Artist ?? string.Empty,
                Album = metadata?.Album ?? string.Empty,
                Composer = metadata?.Composer ?? string.Empty,
                Genre = metadata?.Genre ?? string.Empty
            };
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

                AppLog.Add(() => $"AudioSession(iOS): reason={reason} mixEnabled={mixEnabled} requestedOptions={requestedOptions} category={category} mode={mode} otherAudioPlaying={otherAudioPlaying} appState={(_isAppForegroundActive ? "Active" : "BackgroundLike")} clipPlaybackActive={_clipPlaybackActive} sessionStarted={_sessionStarted} transientSession={_transientClipSessionActive} interrupted={_isInterrupted} keepAliveDesired={keepAliveDesired} keepAliveRunning={_silenceKeepAlivePlayer != null} route={currentRoute}");
            }
            catch
            {
            }
        }

        private AVAudioSessionCategoryOptions BuildRequestedOptions(bool mixEnabled)
        {
            var options = AVAudioSessionCategoryOptions.AllowBluetooth | AVAudioSessionCategoryOptions.AllowBluetoothA2DP;
            if (mixEnabled)
                options |= AVAudioSessionCategoryOptions.MixWithOthers;

            return options;
        }

        private bool ShouldKeepAlive(bool mixEnabled)
        {
            if (!IsPlaybackSessionOwned())
                return false;

            if (_isInterrupted)
                return false;

            if (!_sessionActivated)
                return false;

            if (_clipPlaybackActive)
                return false;

            return !mixEnabled || !_isAppForegroundActive;
        }

        private void EnsurePlaybackSessionConfigured(string reason, bool audioEnabled, bool forceCategoryUpdate)
        {
            lock (_audioSessionGate)
            {
                if (!IsPlaybackSessionOwned())
                    return;

                if (_isInterrupted)
                {
                    try { AppLog.Add(() => $"AudioSession(iOS): configure skipped during interruption. reason={reason}"); } catch { }
                    return;
                }

                var mixEnabled = IsMobileMixAudioWithOtherAppsEnabled();
                var keepAliveDesired = ShouldKeepAlive(mixEnabled);

                try
                {
                    var session = AVAudioSession.SharedInstance();
                    var requestedOptions = BuildRequestedOptions(mixEnabled);
                    var shouldSetCategory = forceCategoryUpdate || !_sessionConfigured || _lastConfiguredOptions != requestedOptions;

                    if (shouldSetCategory)
                    {
                        session.SetCategory(AVAudioSessionCategory.Playback, requestedOptions);
                        _lastConfiguredOptions = requestedOptions;
                        _sessionConfigured = true;
                        AppLog.Add(() => $"AudioSession(iOS): SetCategory success. reason={reason} category=Playback options={requestedOptions} audioEnabled={audioEnabled}");
                    }

                    if (!_sessionActivated || forceCategoryUpdate)
                    {
                        session.SetActive(true);
                        _sessionActivated = true;
                        AppLog.Add(() => $"AudioSession(iOS): SetActive(true) success. reason={reason}");
                    }

                    LogAudioSessionState(reason, session, requestedOptions, mixEnabled, keepAliveDesired);
                }
                catch (Exception ex)
                {
                    try { AppLog.Add(() => $"AudioSession(iOS): configure failed. reason={reason} error={ex.Message}"); } catch { }
                }
            }
        }

        private void UpdateKeepAliveState(string reason)
        {
            lock (_audioSessionGate)
            {
                var mixEnabled = IsMobileMixAudioWithOtherAppsEnabled();
                var keepAliveDesired = ShouldKeepAlive(mixEnabled);

                try
                {
                    if (keepAliveDesired)
                        StartSilenceKeepAlive();
                    else
                        StopSilenceKeepAlive();

                    AppLog.Add(() => $"AudioSession(iOS): keepalive evaluated. reason={reason} desired={keepAliveDesired} mixEnabled={mixEnabled} clipPlaybackActive={_clipPlaybackActive} appForeground={_isAppForegroundActive} sessionStarted={_sessionStarted} transientSession={_transientClipSessionActive} interrupted={_isInterrupted}");
                }
                catch (Exception ex)
                {
                    try { AppLog.Add(() => $"AudioSession(iOS): keepalive transition failed. reason={reason} error={ex.Message}"); } catch { }
                }
            }
        }

        private void DeactivatePlaybackSession(string reason, bool removeObserversWhenIdle)
        {
            lock (_audioSessionGate)
            {
                try
                {
                    StopSilenceKeepAlive();
                }
                catch
                {
                }

                try
                {
                    if (_sessionActivated || _sessionConfigured)
                    {
                        var session = AVAudioSession.SharedInstance();
                        session.SetActive(false);
                        AppLog.Add(() => $"AudioSession(iOS): SetActive(false) success. reason={reason}");
                    }
                }
                catch (Exception ex)
                {
                    try { AppLog.Add(() => $"AudioSession(iOS): SetActive(false) failed. reason={reason} error={ex.Message}"); } catch { }
                }
                finally
                {
                    _sessionActivated = false;
                    _sessionConfigured = false;
                    _lastConfiguredOptions = 0;
                }
            }

            if (removeObserversWhenIdle && !IsPlaybackSessionOwned())
                RemoveAudioSessionObservers();
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
