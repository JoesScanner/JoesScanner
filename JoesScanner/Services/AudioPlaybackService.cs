#if WINDOWS
using Windows.Media.Core;
using WinMediaPlayer = Windows.Media.Playback.MediaPlayer;
#endif

#if ANDROID
using Android.Content;
using Android.Media;
using JoesScanner.Models;
using Microsoft.Maui.ApplicationModel;
using JoesScanner.Platforms.Android.Services;
using AUri = Android.Net.Uri;
#endif

#if IOS || MACCATALYST
using AVFoundation;
using Foundation;
using UIKit;
#endif

#if IOS
using JoesScanner.Platforms.iOS.Services;
#endif

using JoesScanner.Helpers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;

namespace JoesScanner.Services
{
    // Cross-platform audio playback wrapper.
    // Critical behavior: PlayAsync must not return until playback is actually complete (or canceled),
    // otherwise the queue will advance and "skip" calls.
    public partial class AudioPlaybackService : IAudioPlaybackService
    {
        private readonly IAudioFilterService _audioFilterService;
        private readonly IToneAlertService _toneAlertService;
        private readonly ISettingsService _settings;
        private readonly ISystemMediaService _systemMediaService;

        // Global interrupt token: ensures Stop/Skip actions cancel *all* in-flight work immediately,
        // including: filter preparation, downloads, and platform playback.
        // This is intentionally independent from the caller-provided CancellationToken.
        private readonly object _interruptLock = new object();
        private CancellationTokenSource _interruptCts = new CancellationTokenSource();

#if MACCATALYST
        private string? _iosTempDownloadedFile;
#endif

        public AudioPlaybackService(IAudioFilterService audioFilterService, IToneAlertService toneAlertService, ISettingsService settings, ISystemMediaService systemMediaService)
        {
            _audioFilterService = audioFilterService;
            _toneAlertService = toneAlertService;
            _settings = settings;
            _systemMediaService = systemMediaService;
        }

#if MACCATALYST
        private bool MobileMixAudioWithOtherAppsEnabled => _settings.MobileMixAudioWithOtherApps;
#endif

#if WINDOWS
        private WinMediaPlayer? _windowsPlayer;
#endif

#if ANDROID
        private readonly SemaphoreSlim _androidClipGate = new(1);
#endif

#if MACCATALYST
        private AVPlayer? _iosPlayer;
        private NSObject? _iosEndObserver;
        private NSObject? _iosFailObserver;
        private NSObject? _iosErrorLogObserver;
#endif

        public Task PlayAsync(string audioUrl, CancellationToken cancellationToken = default)
        {
            return PlayAsync(audioUrl, 1.0, cancellationToken);
        }

        public Task PlayAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken = default) =>
            PlayAsync(audioUrl, playbackRate, cancellationToken, new PlaybackCompletion());

        public async Task PlayAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken, PlaybackCompletion completion)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
                throw new InvalidOperationException("The recording has no audio source.");

            if (playbackRate <= 0)
                playbackRate = 1.0;

            // Link the caller token with the global interrupt token so Stop/Skip actions take effect
            // immediately even if the caller token isn't canceled yet.
            CancellationToken linkedToken;
            CancellationTokenSource? linkedCts = null;
            string? authenticatedDownload = null;
#if IOS
            IDisposable? routeWatch = null;
#endif
            try
            {
                CancellationToken interruptToken;
                lock (_interruptLock)
                {
                    interruptToken = _interruptCts.Token;
                }

                if (cancellationToken.CanBeCanceled)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interruptToken);
                    linkedToken = linkedCts.Token;
                }
                else
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(interruptToken);
                    linkedToken = linkedCts.Token;
                }
            }
            catch
            {
                // If linking fails for any reason, fall back to the caller token.
                linkedToken = cancellationToken;
            }

            using var endRegistration = linkedToken.Register(() => completion.Reject());
            try
            {
#if IOS
                routeWatch = await MainThread.InvokeOnMainThreadAsync(() => IosAudioEnvironment.Watch(_settings));
#endif
                if (Uri.TryCreate(audioUrl, UriKind.Absolute, out var source) && source.Scheme is "http" or "https")
                {
                    if (!string.IsNullOrEmpty(source.UserInfo))
                        source = new UriBuilder(source) { UserName = string.Empty, Password = string.Empty }.Uri;

                    var authentication = ServerAuthentication.For(_settings);
                    if (authentication.RequiresFirewall(source) || authentication.GetCustomAuthorization(source) != null)
                    {
                        authenticatedDownload = await DownloadToTempFileAsync(source.AbsoluteUri, linkedToken);
                        source = new Uri(authenticatedDownload);
                    }

                    audioUrl = source.AbsoluteUri;
                }

                // Phase 2+: when audio filters are enabled, ensure we have a local file URL.
                // Phase 3: carry static filter fade hints without altering queue behavior.
                PreparedAudio prepared;
                try
                {
                    prepared = await _audioFilterService.PrepareForPlaybackAsync(audioUrl, linkedToken);
                }
                catch (global::System.OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    prepared = new PreparedAudio(audioUrl);
                }


            try
            {
                if (prepared.StaticFilterEnabled || prepared.ToneFilterEnabled)
                {
                    var duckSummary = prepared.ToneDuckSegments != null && prepared.ToneDuckSegments.Count > 0
                        ? string.Join(",", prepared.ToneDuckSegments.Select(s => $"{s.StartMs}-{s.EndMs}"))
                        : "(none)";

                    AppLog.Add(() => $"AudioPrep: cache={prepared.PreparedFromCache} prepMs={prepared.PreparationElapsedMs} dlMs={prepared.DownloadElapsedMs} dlBytes={prepared.DownloadBytes} toneMs={prepared.ToneDetectionElapsedMs} toneScanMs={prepared.ToneScanWindowMs} toneDetected={prepared.ToneDetected} freq={prepared.ToneDetectedFrequencyHz} duck={duckSummary}");
                }
            }
            catch
            {
            }

            linkedToken.ThrowIfCancellationRequested();

            var preparedUrl = prepared.Url;

            if (prepared.ToneDetected)
            {
                try
                {
                    AppLog.Add(() => $"AudioTone: tone flag set for this call (freq={prepared.ToneDetectedFrequencyHz}Hz).");
                }
                catch { }

                try
                {
                    _toneAlertService.NotifyToneDetected(audioUrl);
                }
                catch
                {
                }
            }

            if (!string.Equals(preparedUrl, audioUrl, StringComparison.Ordinal))
            {
                try { AppLog.Add(() => "Audio: using locally prepared audio (Audio filters enabled)." ); } catch { }
            }

            if (prepared.StaticFilterEnabled && (0 > 0 || 0 > 0))
            {
                try
                {
                    AppLog.Add(() => $"AudioStatic: attenuator vol={prepared.StaticAttenuatorVolume} segments={prepared.StaticSegments.Count}");
                }
                catch { }
            }

            try
            {
                AppLog.Add(() => $"Audio: Play requested. rate={playbackRate:0.###}");
            }
            catch
            {
            }

#if WINDOWS
                await PlayOnWindowsAsync(prepared, playbackRate, linkedToken, completion);
#elif ANDROID
                await PlayOnAndroidAsync(prepared, playbackRate, linkedToken, completion);
#elif IOS
                await PlayOnIosAsync(prepared, playbackRate, linkedToken, completion);
#elif MACCATALYST
                await PlayOnAppleAsync(prepared, playbackRate, linkedToken, completion);
#else
                throw new PlatformNotSupportedException("Audio playback is not available on this platform.");
#endif
            }
            finally
            {
                if (authenticatedDownload != null)
                    DeleteTemporaryAudio(authenticatedDownload);

                try { linkedCts?.Dispose(); } catch { }
#if IOS
                if (routeWatch != null)
                    await MainThread.InvokeOnMainThreadAsync(routeWatch.Dispose);
#endif
            }
        }

        public async Task StopAsync()
        {
            // Cancel any in-flight PlayAsync (including preparation/download). This is the piece that
            // makes Stop/Skip feel instant under slow I/O.
            CancellationTokenSource? toCancel;
            lock (_interruptLock)
            {
                toCancel = _interruptCts;
                _interruptCts = new CancellationTokenSource();
            }

            try
            {
                try { toCancel.Cancel(); } catch { }
                try { toCancel.Dispose(); } catch { }
            }
            catch
            {
            }

#if WINDOWS
            await StopOnWindowsAsync();
#elif ANDROID
            // Wait for the canceled clip to release its player and focus before a
            // Stop tears down the media session or a Skip starts a replacement.
            await _androidClipGate.WaitAsync();
            _androidClipGate.Release();
#elif IOS
            await _iosClipGate.WaitAsync();
            _iosClipGate.Release();
#elif MACCATALYST
            await StopOnAppleAsync();
#else
            await Task.CompletedTask;
#endif
        }

#if WINDOWS
        private Task PlayOnWindowsAsync(PreparedAudio prepared, double playbackRate, CancellationToken cancellationToken,
            PlaybackCompletion completion)
        {
            var player = new WinMediaPlayer();
            var previous = Interlocked.Exchange(ref _windowsPlayer, player);
            try { previous?.Pause(); } catch { }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnEnded(object? s, object e)
            {
                if (completion.Confirm(cancellationToken))
                    tcs.TrySetResult();
            }

            void OnFailed(WinMediaPlayer s, Windows.Media.Playback.MediaPlayerFailedEventArgs e)
            {
                if (completion.Reject())
                    tcs.TrySetException(new InvalidOperationException($"Windows audio playback failed ({e.Error})."));
            }

            try
            {
                player.MediaEnded += OnEnded;
                player.MediaFailed += OnFailed;

                player.Source = MediaSource.CreateFromUri(new global::System.Uri(prepared.Url));
                player.PlaybackSession.PlaybackRate = playbackRate;

                if (prepared.StaticFilterEnabled && 0 > 0)
                {
                    try { player.Volume = Clamp01(1.0); } catch { }
                }
                cancellationToken.ThrowIfCancellationRequested();
                player.Play();

                if (prepared.StaticFilterEnabled || (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0))
                    _ = ApplyDynamicVolumeWindowsAsync(prepared, player, cancellationToken);
            }
            catch (Exception ex)
            {
                if (completion.Reject())
                    tcs.TrySetException(ex);
            }

            return WaitWithCleanupAsync(tcs.Task, cancellationToken, () =>
            {
                try { player.MediaEnded -= OnEnded; } catch { }
                try { player.MediaFailed -= OnFailed; } catch { }
                Interlocked.CompareExchange(ref _windowsPlayer, null, player);
                player.Dispose();
            });
        }

        private Task StopOnWindowsAsync()
        {
            var player = Interlocked.Exchange(ref _windowsPlayer, null);
            try
            {
                player?.Pause();
            }
            catch
            {
            }
            return Task.CompletedTask;
        }

        private async Task ApplyDynamicVolumeWindowsAsync(PreparedAudio prepared, WinMediaPlayer player, CancellationToken cancellationToken)
        {
            var durationMs = TryGetWindowsDurationMs(player);
            if (durationMs <= 0)
                durationMs = 0;

            // Unified volume scheduler (static fade plus tone ducking).
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!ReferenceEquals(Volatile.Read(ref _windowsPlayer), player))
                    return;

                var posMs = 0;
                try
                {
                    posMs = (int)Math.Round(player.PlaybackSession.Position.TotalMilliseconds);
                }
                catch
                {
                    posMs = 0;
                }

                if (durationMs <= 0)
                {
                    var d2 = TryGetWindowsDurationMs(player);
                    if (d2 > 0)
                        durationMs = d2;
                }

                var vol = ComputeDynamicVolume(prepared, posMs, durationMs);
                try { player.Volume = vol; } catch { }

                if (durationMs > 0 && posMs >= durationMs)
                    return;

                try { await Task.Delay(50, cancellationToken); } catch { return; }
            }
        }

        private static int TryGetWindowsDurationMs(WinMediaPlayer player)
        {
            try
            {
                var d = player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
                if (d <= TimeSpan.Zero)
                    return 0;
                var ms = (long)d.TotalMilliseconds;
                if (ms <= 0 || ms > int.MaxValue)
                    return 0;
                return (int)ms;
            }
            catch
            {
                return 0;
            }
        }
#endif

#if ANDROID
        private async Task PlayOnAndroidAsync(PreparedAudio prepared, double playbackRate, CancellationToken cancellationToken,
            PlaybackCompletion completion)
        {
            await _androidClipGate.WaitAsync(cancellationToken);
            try
            {
                // MediaPlayer and the interruption state have one owner, including when
                // playback originated from a Bluetooth command or a background continuation.
                await MainThread.InvokeOnMainThreadAsync(
                    () => PlayAndroidClipOnMainThreadAsync(prepared, playbackRate, cancellationToken, completion));
            }
            finally
            {
                _androidClipGate.Release();
            }
        }

        private async Task PlayAndroidClipOnMainThreadAsync(PreparedAudio prepared, double rate, CancellationToken token,
            PlaybackCompletion completion)
        {
            token.ThrowIfCancellationRequested();
            var context = Platform.AppContext;
            var manager = (AudioManager?)context.GetSystemService(Context.AudioService)
                ?? throw new InvalidOperationException("Android audio manager is unavailable.");
            using var routeWatch = AndroidAudioEnvironment.Watch(context, _settings);
            using var callModeWatch = AndroidAudioEnvironment.WatchCallMode(context, manager);
            using var player = new Android.Media.MediaPlayer();
            try
            {
                player.SetWakeMode(context, global::Android.OS.WakeLockFlags.Partial);
                await PlayAndroidPlayerAsync(context, manager, player, prepared, rate, token, completion);
            }
            finally
            {
                player.Release();
            }
        }

        private async Task PlayAndroidPlayerAsync(Context context, AudioManager manager, Android.Media.MediaPlayer player,
            PreparedAudio prepared, double rate, CancellationToken token, PlaybackCompletion completion)
        {
            using var builder = new AudioAttributes.Builder();
            builder.SetUsage(AudioUsageKind.Media);
            builder.SetContentType(AudioContentType.Speech);
            using var attributes = builder.Build() ?? throw new InvalidOperationException("Android audio attributes could not be created.");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            player.SetAudioAttributes(attributes);
            var released = false;
            void OnPrepared(object? sender, EventArgs args)
            {
                if (!released && !token.IsCancellationRequested)
                    ready.TrySetResult();
            }
            void OnCompletion(object? sender, EventArgs args)
            {
                if (!released && completion.Confirm(token))
                    finished.TrySetResult();
            }
            void OnError(object? sender, Android.Media.MediaPlayer.ErrorEventArgs args)
            {
                args.Handled = true;
                if (released || !completion.Reject())
                    return;
                var error = new InvalidOperationException($"Android audio playback failed ({args.What}, {args.Extra}).");
                if (!ready.TrySetException(error))
                    finished.TrySetException(error);
            }

            player.Prepared += OnPrepared;
            player.Completion += OnCompletion;
            player.Error += OnError;
            try
            {
                await PlayPreparedAndroidPlayerAsync();
            }
            finally
            {
                released = true;
                player.Prepared -= OnPrepared;
                player.Completion -= OnCompletion;
                player.Error -= OnError;
            }

            async Task PlayPreparedAndroidPlayerAsync()
            {
                var src = prepared.Url.Trim();
                if (Uri.TryCreate(src, UriKind.Absolute, out var source) && source.Scheme is "http" or "https" or "content" or "file")
                    player.SetDataSource(context, AUri.Parse(src)!);
                else
                    player.SetDataSource(src);
                player.PrepareAsync();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(90), token);

                // Snapshot the setting for this recording, including any resumed segments.
                var mode = _settings.MobileAudio.Mode;
                Task? duckingDeadline = null;
                using var playerRoute = AndroidAudioEnvironment.WatchPlayer(player);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (finished.Task.IsCompleted)
                    {
                        await finished.Task;
                        return;
                    }

                    var focusMode = duckingDeadline?.IsCompleted == true ? MobileAudioMode.MixNormally : mode;
                    if (!CanPlayAndroidAudio(focusMode))
                    {
                        await WaitForAndroidAudioChangeAsync(manager, token, finished.Task);
                        continue;
                    }
                    AndroidAudioEnvironment.RefreshExpectedRoute();
                    if (!CanPlayAndroidAudio(focusMode))
                        continue;

                    var requestCallRevision = AndroidAudioEnvironment.CallRevision;
                    using var focus = new AndroidClipFocus(manager);
                    focus.Request(focusMode, attributes);
                    while (!finished.Task.IsCompleted && CanKeepAndroidFocus(focusMode))
                    {
                        token.ThrowIfCancellationRequested();
                        var action = AndroidAudioEnvironment.GetFocusAction(focus.Status, requestCallRevision);
                        if (action == AndroidFocusAttemptAction.Block)
                        {
                            AndroidAudioEnvironment.ReadCallMode(manager);
                            AndroidAudioEnvironment.BlockForFocus(requestCallRevision);
                            AppLog.DebugWriteLine(() => $"Android audio focus: {focus.Status}. Explicit resume required={AndroidAudioEnvironment.RequiresPlay}.");
                            break;
                        }

                        if (action == AndroidFocusAttemptAction.Renew)
                            break;

                        // Retain the listener across transient and duckable loss. Releasing
                        // here would discard the gain callback and could strand the queue.
                        if (action == AndroidFocusAttemptAction.Wait)
                        {
                            await WaitForAndroidAudioChangeAsync(manager, token, finished.Task, focus.Changed);
                            continue;
                        }

                        if (focusMode == MobileAudioMode.MixAndDuck)
                        {
                            duckingDeadline ??= Task.Delay(TimeSpan.FromSeconds(3), token);
                            if (duckingDeadline.IsCompleted)
                                break;
                        }
                        await PlayAndroidSegmentAsync(manager, player, playerRoute, prepared, focusMode, rate, focus, finished.Task,
                            focusMode == MobileAudioMode.MixAndDuck ? duckingDeadline : null, token);
                    }
                }
            }
        }

        private async Task PlayAndroidSegmentAsync(AudioManager manager, Android.Media.MediaPlayer player,
            AndroidAudioEnvironment.PlayerRouteWatch playerRoute, PreparedAudio prepared, MobileAudioMode mode, double rate,
            AndroidClipFocus focus, Task finished, Task? duckingDeadline, CancellationToken token)
        {
            var playing = false;
            var showPlayingState = false;
            var normalExit = false;
            Exception? pauseFailure = null;
            void Pause()
            {
                if (!playing)
                    return;
                playing = false;
                try
                {
                    if (!finished.IsCompleted)
                    {
                        if (token.IsCancellationRequested)
                            player.Stop();
                        else
                            player.Pause();
                    }
                }
                catch (Java.Lang.IllegalStateException ex)
                {
                    pauseFailure = ex;
                }
                finally
                {
                    playerRoute.SetAudible(false);
                }
            }

            bool MayContinue() => !token.IsCancellationRequested && duckingDeadline?.IsCompleted != true &&
                CanPlayAndroidAudio(mode) && focus.Status == AndroidClipFocusStatus.Granted;
            void OnEligibilityChanged()
            {
                if (!MayContinue())
                    Pause();
            }

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var progress = new AndroidPlaybackProgress(player.CurrentPosition);
            void OnInfo(object? sender, Android.Media.MediaPlayer.InfoEventArgs args)
            {
                if (args.What is MediaInfo.BufferingStart or MediaInfo.BufferingEnd)
                    progress.BufferingChanged(args.What == MediaInfo.BufferingStart, elapsed.Elapsed);
            }

            using var audibleSegment = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task? volume = null;
            player.Info += OnInfo;
            focus.LostAudibility += Pause;
            AndroidAudioEnvironment.PlaybackBlocked += Pause;
            SystemMediaService.AndroidForeground.StateChanged += OnEligibilityChanged;
            try
            {
                token.ThrowIfCancellationRequested();
                AndroidAudioEnvironment.RefreshExpectedRoute();
                if (finished.IsCompleted || !MayContinue())
                {
                    normalExit = true;
                    return;
                }

                // Keep the same prepared player and resume at its paused position.
                var playbackLimit = TimeSpan.FromSeconds(Math.Clamp((player.Duration / 1000d / rate * 2) + 120, 180, 10800));
                player.Start();
                playing = true;
                try
                {
                    using var speed = new PlaybackParams();
                    speed.SetSpeed((float)rate);
                    player.PlaybackParams = speed;
                }
                catch (Exception ex)
                {
                    AppLog.DebugWriteLine(() => $"Android playback speed could not be set ({ex.GetType().Name}).");
                }

                playerRoute.SetAudible(true);
                if (playing)
                {
                    showPlayingState = true;
                    await _systemMediaService.SetClipPlaybackStateAsync(true, true, "AndroidClipStarted");
                    if (prepared.StaticFilterEnabled || (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0))
                        volume = ApplyDynamicVolumeAndroidAsync(prepared, player, audibleSegment.Token);
                }

                while (playing && !finished.IsCompleted && MayContinue())
                {
                    playerRoute.ReadInitialRoute();
                    if (!playing)
                        break;
                    if (progress.IsStalled(player.CurrentPosition, elapsed.Elapsed))
                        throw new TimeoutException("Android audio stalled. Press Play to retry this recording.");
                    if (elapsed.Elapsed >= playbackLimit)
                        throw new TimeoutException("Android audio playback timed out.");
                    await WaitForAndroidAudioChangeAsync(manager, token, finished, focus.Changed,
                        observeProgress: true, duckingDeadline: duckingDeadline);
                }
                normalExit = true;
            }
            finally
            {
                player.Info -= OnInfo;
                focus.LostAudibility -= Pause;
                AndroidAudioEnvironment.PlaybackBlocked -= Pause;
                SystemMediaService.AndroidForeground.StateChanged -= OnEligibilityChanged;
                Pause();
                if (!normalExit || pauseFailure != null || finished.IsCompleted ||
                    token.IsCancellationRequested || duckingDeadline?.IsCompleted == true || !CanKeepAndroidFocus(mode) ||
                    (AndroidAudioEnvironment.CallActive && focus.Status == AndroidClipFocusStatus.Granted) ||
                    focus.Status == AndroidClipFocusStatus.PermanentLoss)
                    focus.Release();
                audibleSegment.Cancel();
                if (showPlayingState)
                    await _systemMediaService.SetClipPlaybackStateAsync(false, true, "AndroidClipIdle");
                if (volume != null)
                    await volume;
                if (pauseFailure != null)
                {
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("An interrupted Android recording could not be paused.", pauseFailure);
                }
            }
        }

        private static bool HasAndroidAudioEligibility(MobileAudioMode mode)
        {
            var needsFocus = OperatingSystem.IsAndroidVersionAtLeast(35) && mode != MobileAudioMode.MixNormally;
            return SystemMediaService.AndroidForeground.CanPlay(
                requiresForegroundService: OperatingSystem.IsAndroidVersionAtLeast(37) || needsFocus,
                requiresTopActivity: needsFocus);
        }

        private static bool CanPlayAndroidAudio(MobileAudioMode mode) =>
            HasAndroidAudioEligibility(mode) && !AndroidAudioEnvironment.IsBlocked;

        private static bool CanKeepAndroidFocus(MobileAudioMode mode) =>
            HasAndroidAudioEligibility(mode) && !AndroidAudioEnvironment.RoutePaused &&
            !AndroidAudioEnvironment.RequiresNewFocusRequest;

        private static async Task WaitForAndroidAudioChangeAsync(AudioManager manager, CancellationToken token,
            Task finished, Task? focusChanged = null, bool observeProgress = false, Task? duckingDeadline = null)
        {
            // Audio-mode listeners aren't available on every device. Reconcile
            // mode at the same wakeups used to check for actual player progress.
            var interval = OperatingSystem.IsAndroidVersionAtLeast(31) ? (observeProgress ? 250 : 1000) : 150;
            await Task.WhenAny(finished, focusChanged ?? finished, duckingDeadline ?? finished,
                AndroidAudioEnvironment.Changed, SystemMediaService.AndroidForeground.Changed, Task.Delay(interval, token));
            token.ThrowIfCancellationRequested();
            AndroidAudioEnvironment.ReadCallMode(manager);
        }

        private async Task ApplyDynamicVolumeAndroidAsync(PreparedAudio prepared, Android.Media.MediaPlayer player, CancellationToken cancellationToken)
        {
            var durationMs = 0;
            try { durationMs = player.Duration; } catch { durationMs = 0; }
	            var currentVol = 1.0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var posMs = 0;
                try { posMs = player.CurrentPosition; } catch { posMs = 0; }

	                if (durationMs <= 0)
	                {
	                    try { durationMs = player.Duration; } catch { durationMs = 0; }
	                }

                var target = ComputeDynamicVolume(prepared, posMs, durationMs);
                currentVol = SmoothVolume(currentVol, target, 50);
                try { player.SetVolume((float)currentVol, (float)currentVol); } catch { }

                if (durationMs > 0 && posMs >= durationMs)
                    return;

                try { await Task.Delay(50, cancellationToken); } catch { return; }
            }
        }
#endif

#if MACCATALYST
        private async Task PlayOnAppleAsync(PreparedAudio prepared, double playbackRate, CancellationToken cancellationToken,
            PlaybackCompletion completion)
        {
            await StopOnAppleAsync();

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clipPlaybackStateNotified = false;

            try
            {
                var mixEnabled = MobileMixAudioWithOtherAppsEnabled;

                try
                {
                    var session = AVAudioSession.SharedInstance();
                    var appState = UIApplication.SharedApplication.ApplicationState;
                    AppLog.Add(() => $"AudioSession(iOS): preparing clip playback. mixEnabled={mixEnabled} appState={appState} otherAudioPlaying={session.OtherAudioPlaying}");
                }
                catch
                {
                }

	                // When audio is downloaded to a local temp file, iOS needs a file URL.
	                // Passing a raw filesystem path into NSUrl.FromString often fails.
	                NSUrl? nsUrl = null;
	                var audioUrl = prepared.Url;
	                var requestedUrl = audioUrl;

	                // IMPORTANT (Apple + HTTP): even with ATS allowances, AVPlayer streaming to http:// URLs
	                // can still fail on some iOS/macOS builds. For http:// we download via managed HttpClient
	                // and then play from a local file URL.
	                if (Uri.TryCreate(audioUrl, UriKind.Absolute, out var requestedUri)
	                    && string.Equals(requestedUri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
	                {
	                    try
	                    {
	                        var localPath = await DownloadToTempFileAsync(audioUrl, cancellationToken);
	                        _iosTempDownloadedFile = localPath;
	                        requestedUrl = new Uri(localPath).AbsoluteUri;
	                        nsUrl = NSUrl.FromFilename(localPath);
	                        AppLog.Add("Audio(iOS): downloaded recording for local playback.");
	                    }
	                    catch (Exception ex)
	                    {
	                        try { AppLog.Add(() => $"Audio(iOS): http download failed ({ex.GetType().Name})."); } catch { }
	                        // Fall back to streaming attempt.
	                        nsUrl = NSUrl.FromString(audioUrl);
	                    }
	                }
	                else
	                {
	                    try
	                    {
	                        if (requestedUri != null && requestedUri.IsFile)
	                        {
	                            nsUrl = NSUrl.FromFilename(requestedUri.LocalPath);
	                        }
	                        else
	                        {
	                            // Ensure the URL is properly escaped (spaces, etc.)
	                            if (Uri.TryCreate(audioUrl, UriKind.Absolute, out var u))
	                                requestedUrl = u.AbsoluteUri;
	                            nsUrl = NSUrl.FromString(requestedUrl);
	                        }
	                    }
	                    catch
	                    {
	                        nsUrl = NSUrl.FromString(audioUrl);
	                    }
	                }

                if (nsUrl == null)
                    throw new InvalidOperationException("The recording source could not be opened.");

                try
                {
                    var scheme = nsUrl.Scheme ?? string.Empty;
                    AppLog.Add(() => $"Audio(iOS): AVPlayerItem scheme={scheme}");
                }
                catch
                {
                }

                var item = new AVPlayerItem(nsUrl);
                var player = new AVPlayer(item);
                _iosPlayer = player;

                if (prepared.StaticFilterEnabled && 0 > 0)
                {
                    try { player.Volume = (float)Clamp01(1.0); } catch { }
                }

	                _iosEndObserver = NSNotificationCenter.DefaultCenter.AddObserver(
	                    AVPlayerItem.DidPlayToEndTimeNotification,
	                    _ => { if (completion.Confirm(cancellationToken)) tcs.TrySetResult(); },
                    item);

	                // Some notification constants are missing in certain SDK bindings; use raw names.
	                var failedToPlayName = new NSString("AVPlayerItemFailedToPlayToEndTimeNotification");
	                var failedErrorKey = new NSString("AVPlayerItemFailedToPlayToEndTimeErrorKey");
	                var newErrorLogName = new NSString("AVPlayerItemNewErrorLogEntryNotification");

	                _iosFailObserver = NSNotificationCenter.DefaultCenter.AddObserver(
	                    failedToPlayName,
                    n =>
                    {
                        try
                        {
                            string errText = string.Empty;

	                            if (n?.UserInfo != null && n.UserInfo.ContainsKey(failedErrorKey))
                            {
	                                var obj = n.UserInfo[failedErrorKey];
                                if (obj is NSError nsErr)
                                    errText = $"{nsErr.Domain} ({(int)nsErr.Code})";
                            }

                            if (string.IsNullOrWhiteSpace(errText) && item.Error != null)
                                errText = $"{item.Error.Domain} ({(int)item.Error.Code})";

                            if (string.IsNullOrWhiteSpace(errText))
                                errText = "unknown_error";

                            AppLog.Add(() => $"Audio(iOS): FailedToPlayToEndTime: {errText}");
                        }
                        catch
                        {
                        }

                        if (completion.Reject())
                            tcs.TrySetException(new InvalidOperationException("The recording could not be played to completion."));
                    },
                    item);

	                _iosErrorLogObserver = NSNotificationCenter.DefaultCenter.AddObserver(
	                    newErrorLogName,
                    _ =>
                    {
                        try
                        {
	                            var log = item.ErrorLog;
                            var events = log?.Events;
                            AVPlayerItemErrorLogEvent? last = null;
                            if (events != null && events.Length > 0)
                                last = events[events.Length - 1];
                            if (last != null)
                            {
                                AppLog.Add(() => $"Audio(iOS): ErrorLog: domain={last.ErrorDomain}, code={last.ErrorStatusCode}");
                            }
                        }
                        catch
                        {
                        }
                    },
                    item);

                await _systemMediaService.SetClipPlaybackStateAsync(true, true, "ClipPlaybackStart");
                clipPlaybackStateNotified = true;

                cancellationToken.ThrowIfCancellationRequested();

                player.Play();
                try { AppLog.Add(() => $"Audio(iOS): player.Play invoked. mixEnabled={mixEnabled} rate={playbackRate}"); } catch { }

                try { player.Rate = (float)playbackRate; } catch { }
                try { AppLog.Add(() => $"Audio(iOS): player.Rate applied={player.Rate}"); } catch { }

                if (prepared.StaticFilterEnabled || (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0))
                    _ = ApplyDynamicVolumeAppleAsync(prepared, player, cancellationToken);

                using var reg = cancellationToken.Register(() =>
                {
                    try { tcs.TrySetCanceled(cancellationToken); } catch { }
                });

                await tcs.Task;
            }
            catch (global::System.OperationCanceledException)
            {
                throw;
            }
            finally
            {
                if (clipPlaybackStateNotified)
                {
                    try
                    {
                        await _systemMediaService.SetClipPlaybackStateAsync(false, true, "ClipPlaybackStop");
                    }
                    catch
                    {
                    }
                }

                try
                {
                    if (_iosEndObserver != null)
                        NSNotificationCenter.DefaultCenter.RemoveObserver(_iosEndObserver);
                    if (_iosFailObserver != null)
                        NSNotificationCenter.DefaultCenter.RemoveObserver(_iosFailObserver);
                    if (_iosErrorLogObserver != null)
                        NSNotificationCenter.DefaultCenter.RemoveObserver(_iosErrorLogObserver);
                }
                catch
                {
                }

                _iosEndObserver = null;
                _iosFailObserver = null;
                _iosErrorLogObserver = null;

                if (cancellationToken.IsCancellationRequested)
                    await StopOnAppleAsync();
            }
        }


        private Task StopOnAppleAsync()
        {
            try
            {
                if (_iosPlayer != null)
                {
                    try { _iosPlayer.Pause(); } catch { }
                    try { _iosPlayer.ReplaceCurrentItemWithPlayerItem(null); } catch { }
                    try { _iosPlayer.Dispose(); } catch { }
                    try { AppLog.Add("Audio(iOS): player stopped and disposed."); } catch { }
                }
            }
            catch
            {
            }
            finally
            {
                _iosPlayer = null;

	                // Cleanup any temp file we downloaded for http:// playback.
	                try
	                {
	                    if (!string.IsNullOrWhiteSpace(_iosTempDownloadedFile) && File.Exists(_iosTempDownloadedFile))
	                        File.Delete(_iosTempDownloadedFile);
	                }
	                catch
	                {
	                }
	                finally
	                {
	                    _iosTempDownloadedFile = null;
	                }

                try
                {
                    if (_iosEndObserver != null)
                        NSNotificationCenter.DefaultCenter.RemoveObserver(_iosEndObserver);
                    if (_iosFailObserver != null)
                        NSNotificationCenter.DefaultCenter.RemoveObserver(_iosFailObserver);
                    if (_iosErrorLogObserver != null)
                        NSNotificationCenter.DefaultCenter.RemoveObserver(_iosErrorLogObserver);
                }
                catch
                {
                }

                _iosEndObserver = null;
                _iosFailObserver = null;
                _iosErrorLogObserver = null;

                // Do not deactivate the shared AVAudioSession here.
                // The SystemMediaService owns the lifetime of the playback session while monitoring is running.
                // Deactivating it on every Stop can cause iOS to suspend background execution,
                // which makes the call stream appear to stall when audio is toggled off.
            }

            return Task.CompletedTask;
        }

#endif

        private async Task<string> DownloadToTempFileAsync(string url, CancellationToken cancellationToken)
        {
            // Keep HTTP support when the Apple native networking stack is constrained.
            using var http = ServerAuthenticationHandler.CreateClient(_settings,
                new SocketsHttpHandler { AllowAutoRedirect = false });
            http.Timeout = TimeSpan.FromSeconds(15);

            var source = new Uri(url);
            using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var tempRoot = Path.Combine(Path.GetTempPath(), "joesscanner-audio");
            Directory.CreateDirectory(tempRoot);
            var extension = Path.GetExtension(source.AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
                extension = ".bin";

            var path = Path.Combine(tempRoot, $"{Guid.NewGuid():N}{extension}");
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await stream.CopyToAsync(file, cancellationToken);
                return path;
            }
            catch
            {
                DeleteTemporaryAudio(path);
                throw;
            }
        }

        private static void DeleteTemporaryAudio(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static double SmoothVolume(double current, double target, int tickMs)
        {
            // Fast drop (attack), slower rise (release) to avoid chattering.
            var attackMs = 80.0;
            var releaseMs = 160.0;

            var tau = (target < current) ? attackMs : releaseMs;
            var alpha = tickMs / (tau + tickMs);

            return Clamp01(current + ((target - current) * alpha));
        }

private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private static double ComputeDynamicVolume(PreparedAudio prepared, int positionMs, int durationMs)
        {
            var baseVol = 1.0;

            if (prepared.StaticFilterEnabled && prepared.StaticAttenuatorVolume > 0 && prepared.StaticSegments.Count > 0)
            {
                var inStatic = false;
                foreach (var seg in prepared.StaticSegments)
                {
                    if (positionMs >= seg.StartMs && positionMs <= seg.EndMs)
                    {
                        inStatic = true;
                        break;
                    }
                }

                if (inStatic)
                {
                    // Volume 0..100 maps to 1.0..0.05
                    var v = prepared.StaticAttenuatorVolume / 100.0;
                    var target = 1.0 - (0.95 * v);
                    baseVol = Clamp01(target);
                }
            }

            var toneFactor = 1.0;
            if (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0)
            {
                var strength = prepared.ToneStrength;
                if (strength < 0) strength = 0;
                if (strength > 100) strength = 100;

                // Strength 0..100 maps to a volume multiplier of 1.0..0.15
                var target = 1.0 - ((1.0 - 0.15) * (strength / 100.0));

                foreach (var seg in prepared.ToneDuckSegments)
                {
                    if (positionMs >= seg.StartMs && positionMs <= seg.EndMs)
                    {
                        toneFactor = Math.Min(toneFactor, target);
                        break;
                    }
                }
            }

            return Clamp01(baseVol * toneFactor);
        }

        private static async Task FadeVolumeAsync(Action<double> setVolume, double from, double to, int durationMs, CancellationToken cancellationToken)
        {
            if (durationMs <= 0)
            {
                try { setVolume(Clamp01(to)); } catch { }
                return;
            }

            from = Clamp01(from);
            to = Clamp01(to);

            const int stepMs = 50;
            var steps = Math.Max(1, durationMs / stepMs);
            for (var i = 0; i <= steps; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var t = (double)i / steps;
                var v = from + ((to - from) * t);
                try { setVolume(Clamp01(v)); } catch { }

                if (i < steps)
                {
                    try { await Task.Delay(stepMs, cancellationToken); } catch { return; }
                }
            }
        }

#if IOS || MACCATALYST
        private async Task ApplyDynamicVolumeAppleAsync(PreparedAudio prepared, AVPlayer player, CancellationToken cancellationToken)
        {
            var durationMs = TryGetAppleDurationMs(player);

            while (!cancellationToken.IsCancellationRequested)
            {
#if MACCATALYST
                if (_iosPlayer == null)
                    return;
#endif

                var posMs = 0;
                try
                {
                    var time = player.CurrentTime;
                    posMs = (int)Math.Round(time.Seconds * 1000.0);
                }
                catch
                {
                    posMs = 0;
                }

                if (durationMs <= 0)
                {
                    var d2 = TryGetAppleDurationMs(player);
                    if (d2 > 0)
                        durationMs = d2;
                }

                var vol = ComputeDynamicVolume(prepared, posMs, durationMs);
                try { player.Volume = (float)vol; } catch { }

                if (durationMs > 0 && posMs >= durationMs)
                    return;

                try { await Task.Delay(50, cancellationToken); } catch { return; }
            }
        }

        private static int TryGetAppleDurationMs(AVPlayer player)
        {
            try
            {
                var item = player.CurrentItem;
                if (item == null)
                    return 0;

                var dur = item.Duration;
                if (dur.IsIndefinite)
                    return 0;

                var seconds = dur.Seconds;
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                    return 0;

                var ms = (long)(seconds * 1000.0);
                if (ms <= 0 || ms > int.MaxValue)
                    return 0;
                return (int)ms;
            }
            catch
            {
                return 0;
            }
        }
#endif

        private static async Task WaitWithCleanupAsync(Task task, CancellationToken token, Action cleanup)
        {
            try
            {
                using var reg = token.Register(() => { });
                await task.WaitAsync(token);
            }
            catch (global::System.OperationCanceledException)
            {
                throw;
            }
            finally
            {
                try { cleanup(); } catch { }
            }
        }
    }
}
