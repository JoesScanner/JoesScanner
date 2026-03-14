#if WINDOWS
using Windows.Media.Core;
using WinMediaPlayer = Windows.Media.Playback.MediaPlayer;
#endif

#if ANDROID
using Android.Content;
using Android.Media;
using Android.OS;
using Microsoft.Maui.ApplicationModel;
using AMediaStream = Android.Media.Stream;
using AUri = Android.Net.Uri;
#endif

#if IOS || MACCATALYST
using AVFoundation;
using Foundation;
#endif

using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

namespace JoesScanner.Services
{
    // Cross-platform audio playback wrapper.
    // Critical behavior: PlayAsync must not return until playback is actually complete (or canceled),
    // otherwise the queue will advance and "skip" calls.
    public class AudioPlaybackService : IAudioPlaybackService
    {
        private readonly IAudioFilterService _audioFilterService;
        private readonly IToneAlertService _toneAlertService;

        // Global interrupt token: ensures Stop/Skip actions cancel *all* in-flight work immediately,
        // including: filter preparation, downloads, and platform playback.
        // This is intentionally independent from the caller-provided CancellationToken.
        private readonly object _interruptLock = new object();
        private CancellationTokenSource _interruptCts = new CancellationTokenSource();

#if IOS || MACCATALYST
        private string? _iosTempDownloadedFile;
#endif

        public AudioPlaybackService(IAudioFilterService audioFilterService, IToneAlertService toneAlertService)
        {
            _audioFilterService = audioFilterService;
            _toneAlertService = toneAlertService;
        }

#if WINDOWS
        private WinMediaPlayer? _windowsPlayer;
#endif

#if ANDROID
        private Android.Media.MediaPlayer? _androidPlayer;
        private AudioManager? _audioManager;
        private AudioFocusChangeListener? _focusListener;
	        // Android audio focus request object is intentionally not used here.
	        // The .NET Android bindings for AudioFocusRequest vary across TFMs/SDKs.
#endif

#if IOS || MACCATALYST
        private AVPlayer? _iosPlayer;
        private NSObject? _iosEndObserver;
        private NSObject? _iosFailObserver;
        private NSObject? _iosErrorLogObserver;
#endif

        public Task PlayAsync(string audioUrl, CancellationToken cancellationToken = default)
        {
            return PlayAsync(audioUrl, 1.0, cancellationToken);
        }

        public async Task PlayAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
                return;

            if (playbackRate <= 0)
                playbackRate = 1.0;

            // Link the caller token with the global interrupt token so Stop/Skip actions take effect
            // immediately even if the caller token isn't canceled yet.
            CancellationToken linkedToken;
            CancellationTokenSource? linkedCts = null;
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

            try
            {
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
                AppLog.Add(() => $"Audio: Play requested. url={preparedUrl}, rate={playbackRate:0.###}");
            }
            catch
            {
            }

#if WINDOWS
                await PlayOnWindowsAsync(prepared, playbackRate, linkedToken);
#elif ANDROID
                await PlayOnAndroidAsync(prepared, playbackRate, linkedToken);
#elif IOS || MACCATALYST
                await PlayOnAppleAsync(prepared, playbackRate, linkedToken);
#else
                await Task.CompletedTask;
#endif
            }
            finally
            {
                try { linkedCts?.Dispose(); } catch { }
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
            await StopOnAndroidAsync();
#elif IOS || MACCATALYST
            await StopOnAppleAsync();
#else
            await Task.CompletedTask;
#endif
        }

#if WINDOWS
        private Task PlayOnWindowsAsync(PreparedAudio prepared, double playbackRate, CancellationToken cancellationToken)
        {
            _windowsPlayer ??= new WinMediaPlayer();

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = _windowsPlayer;

            void OnEnded(object? s, object e)
            {
                try { tcs.TrySetResult(); } catch { }
            }

            void OnFailed(WinMediaPlayer s, Windows.Media.Playback.MediaPlayerFailedEventArgs e)
            {
                try { tcs.TrySetResult(); } catch { }
            }

            try
            {
                if (player is null)
                {
                    try { tcs.TrySetResult(); } catch { }
                    return tcs.Task;
                }

                player.MediaEnded += OnEnded;
                player.MediaFailed += OnFailed;

                player.Source = MediaSource.CreateFromUri(new global::System.Uri(prepared.Url));
                player.PlaybackSession.PlaybackRate = playbackRate;

                if (prepared.StaticFilterEnabled && 0 > 0)
                {
                    try { player.Volume = Clamp01(1.0); } catch { }
                }
                player.Play();

                if (prepared.StaticFilterEnabled || (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0))
                    _ = ApplyDynamicVolumeWindowsAsync(prepared, cancellationToken);
            }
            catch
            {
                try { tcs.TrySetResult(); } catch { }
            }

            return WaitWithCleanupAsync(tcs.Task, cancellationToken, () =>
            {
                if (player is null)
                    return;

                try { player.MediaEnded -= OnEnded; } catch { }
                try { player.MediaFailed -= OnFailed; } catch { }
            });
        }

        private Task StopOnWindowsAsync()
        {
            try
            {
                _windowsPlayer?.Pause();
                _windowsPlayer?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _windowsPlayer = null;
            }

            return Task.CompletedTask;
        }

        private async Task ApplyDynamicVolumeWindowsAsync(PreparedAudio prepared, CancellationToken cancellationToken)
        {
            var player = _windowsPlayer;
            if (player == null)
                return;

            var durationMs = TryGetWindowsDurationMs(player);
            if (durationMs <= 0)
                durationMs = 0;

            // Unified volume scheduler (static fade plus tone ducking).
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_windowsPlayer == null)
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
        private async Task PlayOnAndroidAsync(PreparedAudio prepared, double playbackRate, CancellationToken cancellationToken)
        {
            await StopOnAndroidAsync();

            var ctx = Platform.CurrentActivity ?? Platform.AppContext;
            if (ctx == null)
                return;

            RequestAudioFocus(ctx);

            var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Android.Media.MediaPlayer? player = null;

            try
            {
                player = new Android.Media.MediaPlayer();
                _androidPlayer = player;

                var p0 = player;
                if (p0 == null)
                    return;

                try
                {
                    var attrsBuilder = new AudioAttributes.Builder();
                    attrsBuilder.SetUsage(AudioUsageKind.Media);
                    attrsBuilder.SetContentType(AudioContentType.Speech);
                    var attrs = attrsBuilder.Build();
                    p0.SetAudioAttributes(attrs);
                }
                catch
                {
                    #pragma warning disable CS0618
#pragma warning disable CA1422
                    try { p0.SetAudioStreamType(AMediaStream.Music); } catch { }
#pragma warning restore CA1422
#pragma warning restore CS0618
                }

                p0.Prepared += (_, __) =>
                {
                    try
                    {
                        try
                        {
                            if (OperatingSystem.IsAndroidVersionAtLeast(23))
                            {
                                var p = new PlaybackParams();
                                p.SetSpeed((float)playbackRate);
                                p0.PlaybackParams = p;
                            }
                        }
                        catch
                        {
                        }

                        if (prepared.StaticFilterEnabled && 0 > 0)
                        {
                            try
                            {
                                var v = (float)Clamp01(1.0);
                                p0.SetVolume(v, v);
                            }
                            catch
                            {
                            }
                        }

                        p0.Start();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { startedTcs.TrySetResult(); } catch { }
                    }
                };

                p0.Completion += (_, __) =>
                {
                    try { finishedTcs.TrySetResult(); } catch { }
                };

                p0.Error += (_, __) =>
                {
                    try { finishedTcs.TrySetResult(); } catch { }
                };

                var src = prepared.Url.Trim();

                if (LooksLikeNetworkOrContentUri(src))
                {
                    var uri = AUri.Parse(src);
                    if (uri == null)
                        return;

                    p0.SetDataSource(ctx, uri);
                }
                else
                {
                    p0.SetDataSource(src);
                }

                p0.PrepareAsync();

                using var reg = cancellationToken.Register(() =>
                {
                    try { finishedTcs.TrySetCanceled(cancellationToken); } catch { }
                });

                // Wait until the player is actually started (or canceled)
                await startedTcs.Task.WaitAsync(cancellationToken);

                if (prepared.StaticFilterEnabled || (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0))
                    _ = ApplyDynamicVolumeAndroidAsync(prepared, p0, cancellationToken);

                // Primary completion path: Completion or Error event.
                // Secondary safety: poll IsPlaying, but do not treat a brief not-yet-playing window
                // immediately after Start() as the end of playback.
                var observedActivePlayback = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (finishedTcs.Task.IsCompleted)
                        break;

                    try
                    {
                        if (player == null)
                            break;

                        if (player.IsPlaying)
                        {
                            observedActivePlayback = true;
                        }
                        else if (observedActivePlayback)
                        {
                            // Playback was active and then stopped without a completion callback.
                            break;
                        }
                    }
                    catch
                    {
                        break;
                    }

                    await Task.Delay(200, cancellationToken);
                }

                // If event completed, observe it (propagates cancellation cleanly).
                if (finishedTcs.Task.IsCompleted)
                    await finishedTcs.Task;
            }
            catch (global::System.OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Swallow playback failures; queue logic handles skipping.
            }
        }

        private static bool LooksLikeNetworkOrContentUri(string src)
        {
            if (string.IsNullOrWhiteSpace(src))
                return false;

            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return true;
            if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return true;
            if (src.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
                return true;
            if (src.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private Task StopOnAndroidAsync()
        {
            try
            {
                if (_androidPlayer != null)
                {
                    try { _androidPlayer.Stop(); } catch { }
                    try { _androidPlayer.Reset(); } catch { }
                    try { _androidPlayer.Release(); } catch { }
                    try { _androidPlayer.Dispose(); } catch { }
                }
            }
            catch
            {
            }
            finally
            {
                _androidPlayer = null;
                AbandonAudioFocus();
            }

            return Task.CompletedTask;
        }

	        private void RequestAudioFocus(Context context)
	        {
	            try
	            {
	                _audioManager ??= (AudioManager?)context.GetSystemService(Context.AudioService);
	                if (_audioManager == null)
	                    return;

	                _focusListener ??= new AudioFocusChangeListener(this);

	#pragma warning disable CS0618
#pragma warning disable CA1422
                _audioManager.RequestAudioFocus(_focusListener, AMediaStream.Music, AudioFocus.Gain);
#pragma warning restore CA1422
#pragma warning restore CS0618
	            }
	            catch
	            {
	            }
	        }

	        private void AbandonAudioFocus()
	        {
	            try
	            {
	                if (_audioManager == null)
	                    return;

	                if (_focusListener == null)
	                    return;

	#pragma warning disable CS0618
#pragma warning disable CA1422
                _audioManager.AbandonAudioFocus(_focusListener);
#pragma warning restore CA1422
#pragma warning restore CS0618
	            }
	            catch
	            {
	            }
	        }

        private sealed class AudioFocusChangeListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
        {
            private readonly AudioPlaybackService _owner;

            public AudioFocusChangeListener(AudioPlaybackService owner)
            {
                _owner = owner;
            }

            public void OnAudioFocusChange(AudioFocus focusChange)
            {
                // App semantics: no pause, stop on focus loss.
                try
                {
                    if (focusChange == AudioFocus.Loss || focusChange == AudioFocus.LossTransient)
                        _ = _owner.StopAsync();
                }
                catch
                {
                }
            }
        }

        private async Task ApplyDynamicVolumeAndroidAsync(PreparedAudio prepared, Android.Media.MediaPlayer player, CancellationToken cancellationToken)
        {
            var durationMs = 0;
            try { durationMs = player.Duration; } catch { durationMs = 0; }
	            var currentVol = 1.0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (_androidPlayer == null)
                    return;

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

#if IOS || MACCATALYST
        private async Task PlayOnAppleAsync(PreparedAudio prepared, double playbackRate, CancellationToken cancellationToken)
        {
            await StopOnAppleAsync();

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var session = AVAudioSession.SharedInstance();
                session.SetCategory(
                    AVAudioSessionCategory.Playback,
                    AVAudioSessionCategoryOptions.AllowBluetooth | AVAudioSessionCategoryOptions.AllowBluetoothA2DP);
                session.SetActive(true);

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
	                        AppLog.Add(() => $"Audio(iOS): downloaded http audio to {localPath}");
	                    }
	                    catch (Exception ex)
	                    {
	                        try { AppLog.Add(() => $"Audio(iOS): http download failed: {ex.Message}"); } catch { }
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
                    return;

                try
                {
                    var scheme = nsUrl.Scheme ?? string.Empty;
                    var abs = nsUrl.AbsoluteString ?? string.Empty;
                    AppLog.Add(() => $"Audio(iOS): AVPlayerItem url={abs} scheme={scheme}");
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
                    _ => { try { tcs.TrySetResult(); } catch { } },
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
                                    errText = $"{nsErr.Domain} ({(int)nsErr.Code}): {nsErr.LocalizedDescription}";
                            }

                            if (string.IsNullOrWhiteSpace(errText) && item.Error != null)
                                errText = $"{item.Error.Domain} ({(int)item.Error.Code}): {item.Error.LocalizedDescription}";

                            if (string.IsNullOrWhiteSpace(errText))
                                errText = "unknown_error";

                            AppLog.Add(() => $"Audio(iOS): FailedToPlayToEndTime: {errText}");
                        }
                        catch
                        {
                        }

                        try { tcs.TrySetResult(); } catch { }
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
                                AppLog.Add(() => $"Audio(iOS): ErrorLog: domain={last.ErrorDomain}, code={last.ErrorStatusCode}, comment={last.ErrorComment}");
                            }
                        }
                        catch
                        {
                        }
                    },
                    item);

                player.Play();

                try { player.Rate = (float)playbackRate; } catch { }

                if (prepared.StaticFilterEnabled || (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0))
                    _ = ApplyDynamicVolumeAppleAsync(prepared, player, cancellationToken);

                using var reg = cancellationToken.Register(() =>
                {
                    try { tcs.TrySetCanceled(cancellationToken); } catch { }
                });

                await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.Add(() => $"Audio(iOS): playback exception: {ex.Message}");
                    AppLog.Add(() => ex.ToString());
                }
                catch
                {
                }
            }
            finally
            {
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

	        private static async Task<string> DownloadToTempFileAsync(string url, CancellationToken cancellationToken)
	        {
	            // Use the managed HTTP stack so http:// sources work even when the native stack is constrained.
	            var handler = new SocketsHttpHandler
	            {
	                AllowAutoRedirect = true
	            };

	            using var http = new HttpClient(handler)
	            {
	                Timeout = TimeSpan.FromSeconds(15)
	            };

	            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
	            response.EnsureSuccessStatusCode();

	            var tmpRoot = Path.Combine(Path.GetTempPath(), "joesscanner-audio");
	            Directory.CreateDirectory(tmpRoot);

	            // Stable filename per URL to avoid re-downloading when replaying the same call.
	            using var sha = SHA256.Create();
	            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
	            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

	            var ext = ".bin";
	            try
	            {
	                var uri = new Uri(url);
	                var path = uri.AbsolutePath;
	                var candidate = Path.GetExtension(path);
	                if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 8)
	                    ext = candidate;
	            }
	            catch
	            {
	            }

	            var tmpPath = Path.Combine(tmpRoot, $"{hash}{ext}");

	            await using (var fs = File.Open(tmpPath, FileMode.Create, FileAccess.Write, FileShare.Read))
	            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
	            {
	                await stream.CopyToAsync(fs, cancellationToken);
	            }

	            return tmpPath;
	        }
#endif

        
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
                if (_iosPlayer == null)
                    return;

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
