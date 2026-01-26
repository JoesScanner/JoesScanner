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

namespace JoesScanner.Services
{
    // Cross-platform audio playback wrapper.
    // Critical behavior: PlayAsync must not return until playback is actually complete (or canceled),
    // otherwise the queue will advance and "skip" calls.
    public class AudioPlaybackService : IAudioPlaybackService
    {
#if WINDOWS
        private WinMediaPlayer? _windowsPlayer;
#endif

#if ANDROID
        private Android.Media.MediaPlayer? _androidPlayer;
        private AudioManager? _audioManager;
        private AudioFocusChangeListener? _focusListener;
#endif

#if IOS || MACCATALYST
        private AVPlayer? _iosPlayer;
        private NSObject? _iosEndObserver;
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

#if WINDOWS
            await PlayOnWindowsAsync(audioUrl, playbackRate, cancellationToken);
#elif ANDROID
            await PlayOnAndroidAsync(audioUrl, playbackRate, cancellationToken);
#elif IOS || MACCATALYST
            await PlayOnAppleAsync(audioUrl, playbackRate, cancellationToken);
#else
            await Task.CompletedTask;
#endif
        }

        public async Task StopAsync()
        {
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
        private Task PlayOnWindowsAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken)
        {
            _windowsPlayer ??= new WinMediaPlayer();

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
                _windowsPlayer.MediaEnded += OnEnded;
                _windowsPlayer.MediaFailed += OnFailed;

                _windowsPlayer.Source = MediaSource.CreateFromUri(new global::System.Uri(audioUrl));
                _windowsPlayer.PlaybackSession.PlaybackRate = playbackRate;
                _windowsPlayer.Play();
            }
            catch
            {
                try { tcs.TrySetResult(); } catch { }
            }

            return WaitWithCleanupAsync(tcs.Task, cancellationToken, () =>
            {
                try { _windowsPlayer.MediaEnded -= OnEnded; } catch { }
                try { _windowsPlayer.MediaFailed -= OnFailed; } catch { }
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
#endif

#if ANDROID
        private async Task PlayOnAndroidAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken)
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

                try
                {
                    var attrs = new AudioAttributes.Builder()
                        .SetUsage(AudioUsageKind.Media)
                        .SetContentType(AudioContentType.Speech)
                        .Build();
                    player.SetAudioAttributes(attrs);
                }
                catch
                {
                    try { player.SetAudioStreamType(AMediaStream.Music); } catch { }
                }

                player.Prepared += (_, __) =>
                {
                    try
                    {
                        try
                        {
                            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                            {
                                var p = new PlaybackParams();
                                p.SetSpeed((float)playbackRate);
                                player.PlaybackParams = p;
                            }
                        }
                        catch
                        {
                        }

                        player.Start();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { startedTcs.TrySetResult(); } catch { }
                    }
                };

                player.Completion += (_, __) =>
                {
                    try { finishedTcs.TrySetResult(); } catch { }
                };

                player.Error += (_, __) =>
                {
                    try { finishedTcs.TrySetResult(); } catch { }
                };

                var src = audioUrl.Trim();

                if (LooksLikeNetworkOrContentUri(src))
                {
                    var uri = AUri.Parse(src);
                    if (uri == null)
                        return;

                    player.SetDataSource(ctx, uri);
                }
                else
                {
                    player.SetDataSource(src);
                }

                player.PrepareAsync();

                using var reg = cancellationToken.Register(() =>
                {
                    try { finishedTcs.TrySetCanceled(cancellationToken); } catch { }
                });

                // Wait until the player is actually started (or canceled)
                await startedTcs.Task.WaitAsync(cancellationToken);

                // Primary completion path: Completion or Error event.
                // Secondary safety: poll IsPlaying so we do not "finish instantly" due to event quirks.
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (finishedTcs.Task.IsCompleted)
                        break;

                    try
                    {
                        if (player != null && !player.IsPlaying)
                        {
                            // If playback stopped and no completion fired, treat as finished.
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
                _audioManager.RequestAudioFocus(_focusListener, AMediaStream.Music, AudioFocus.Gain);
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
                if (_audioManager == null || _focusListener == null)
                    return;

#pragma warning disable CS0618
                _audioManager.AbandonAudioFocus(_focusListener);
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
#endif

#if IOS || MACCATALYST
        private async Task PlayOnAppleAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken)
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

                var nsUrl = NSUrl.FromString(audioUrl);
                if (nsUrl == null)
                    return;

                var item = new AVPlayerItem(nsUrl);
                var player = new AVPlayer(item);
                _iosPlayer = player;

                _iosEndObserver = NSNotificationCenter.DefaultCenter.AddObserver(
                    AVPlayerItem.DidPlayToEndTimeNotification,
                    _ => { try { tcs.TrySetResult(); } catch { } },
                    item);

                player.Play();

                try { player.Rate = (float)playbackRate; } catch { }

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
            catch
            {
                // Swallow playback failures
            }
            finally
            {
                try
                {
                    if (_iosEndObserver != null)
                        NSNotificationCenter.DefaultCenter.RemoveObserver(_iosEndObserver);
                }
                catch
                {
                }

                _iosEndObserver = null;
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

                try
                {
                    if (_iosEndObserver != null)
                        NSNotificationCenter.DefaultCenter.RemoveObserver(_iosEndObserver);
                }
                catch
                {
                }

                _iosEndObserver = null;

                try { AVAudioSession.SharedInstance().SetActive(false); } catch { }
            }

            return Task.CompletedTask;
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
