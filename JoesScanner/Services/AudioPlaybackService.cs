#if WINDOWS
using Windows.Media.Core;
using WinMediaPlayer = Windows.Media.Playback.MediaPlayer;
#endif

#if ANDROID
using Android.Media;
using Android.OS;
using AndroidUri = Android.Net.Uri;
using AStream = Android.Media.Stream;
#endif

namespace JoesScanner.Services
{
    // Cross-platform audio playback service used by the app to play call audio.
    // Uses platform-specific media players on Windows and Android, with optional playback speed control.
    public class AudioPlaybackService : IAudioPlaybackService
    {
#if WINDOWS
        // Windows media player instance used for playback on WinUI.
        private WinMediaPlayer? _player;
#endif

#if ANDROID
        // Android media player instance used for playback on Android devices.
        private MediaPlayer? _androidPlayer;
#endif

        // Legacy overload that plays at normal speed (1.0x).
        public Task PlayAsync(string audioUrl, CancellationToken cancellationToken = default)
        {
            return PlayAsync(audioUrl, 1.0, cancellationToken);
        }

        // Plays the given audio URL at the specified playbackRate.
        // playbackRate is clamped to a minimum of 1.0 if a non-positive value is provided.
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
#else
            await Task.CompletedTask;
#endif
        }

        // Stops any active playback and releases platform-specific media player resources.
        public Task StopAsync()
        {
#if WINDOWS
            try
            {
                _player?.Pause();
                _player?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _player = null;
            }
#endif

#if ANDROID
            try
            {
                _androidPlayer?.Stop();
                _androidPlayer?.Reset();
                _androidPlayer?.Release();
            }
            catch
            {
            }
            finally
            {
                _androidPlayer = null;
            }
#endif

            return Task.CompletedTask;
        }

#if WINDOWS
// Plays the audio URL on Windows using WinUI's MediaPlayer with the specified playbackRate.
// The returned task completes when playback ends or is canceled.
private Task PlayOnWindowsAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken)
{
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    MainThread.BeginInvokeOnMainThread(() =>
    {
        try
        {
            try
            {
                _player?.Pause();
                _player?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _player = null;
            }

            var player = new WinMediaPlayer
            {
                Source = MediaSource.CreateFromUri(new Uri(audioUrl))
            };

            _player = player;

            try
            {
                player.PlaybackSession.PlaybackRate = playbackRate;
            }
            catch
            {
            }

            void CleanupPlayer()
            {
                try
                {
                    player.Pause();
                }
                catch
                {
                }

                try
                {
                    player.Dispose();
                }
                catch
                {
                }

                if (_player == player)
                    _player = null;
            }

            player.MediaEnded += (sender, args) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    CleanupPlayer();
                    tcs.TrySetResult();
                });
            };

            if (cancellationToken.CanBeCanceled)
            {
                try
                {
                    cancellationToken.Register(() =>
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            CleanupPlayer();
                            tcs.TrySetCanceled(cancellationToken);
                        });
                    });
                }
                catch (Exception ex)
                {
                    // If registration fails for any reason, do not hang the awaiter.
                    System.Diagnostics.Debug.WriteLine($"PlayOnWindowsAsync cancellation registration failed: {ex}");
                }
            }

            player.Play();
        }
        catch (Exception ex)
        {
            // Ensure callers do not hang waiting for a completion that never comes.
            tcs.TrySetException(ex);
        }
    });

    return tcs.Task;
}
#endif


#if ANDROID
        // Plays the audio URL on Android using MediaPlayer with the specified playbackRate.
        // The returned task completes when playback ends, errors, or is canceled.
        private Task PlayOnAndroidAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();

            Task.Run(() =>
            {
                try
                {
                    // Stop any existing playback and dispose prior player instance.
                    try
                    {
                        _androidPlayer?.Stop();
                        _androidPlayer?.Reset();
                        _androidPlayer?.Release();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        _androidPlayer = null;
                    }

                    var ctx = Platform.CurrentActivity ?? Platform.AppContext;
                    if (ctx == null)
                    {
                        tcs.TrySetResult();
                        return;
                    }

                    var uri = AndroidUri.Parse(audioUrl);

                    var player = new MediaPlayer();
#pragma warning disable CA1422
                    player.SetAudioStreamType(AStream.Music);
#pragma warning restore CA1422
#pragma warning disable CS8604
                    player.SetDataSource(ctx, uri);
#pragma warning restore CS8604
                    player.Prepare();

                    // Apply playback speed on Android M and above using PlaybackParams.
                    try
                    {
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                        {
#pragma warning disable CA1416
                            var p = player.PlaybackParams ?? new PlaybackParams();
                            p.SetSpeed((float)playbackRate);
                            player.PlaybackParams = p;
#pragma warning restore CA1416
                        }
                    }
                    catch
                    {
                        // If speed cannot be set, fall back to normal.
                    }

                    player.Start();

                    _androidPlayer = player;

                    // When playback completes, clean up and signal completion.
                    player.Completion += (sender, args) =>
                    {
                        try
                        {
                            player.Stop();
                            player.Reset();
                            player.Release();
                        }
                        catch
                        {
                        }
                        finally
                        {
                            if (_androidPlayer == player)
                                _androidPlayer = null;
                        }

                        tcs.TrySetResult();
                    };

                    if (cancellationToken.CanBeCanceled)
                    {
                        cancellationToken.Register(() =>
                        {
                            try
                            {
                                player.Stop();
                                player.Reset();
                                player.Release();
                            }
                            catch
                            {
                            }
                            finally
                            {
                                if (_androidPlayer == player)
                                    _androidPlayer = null;
                            }

                            tcs.TrySetCanceled(cancellationToken);
                        });
                    }
                }
                catch
                {
                    // Swallow playback errors for now and report completion.
                    tcs.TrySetResult();
                }
            }, cancellationToken);

            return tcs.Task;
        }
#endif
    }
}
