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
    public class AudioPlaybackService : IAudioPlaybackService
    {
#if WINDOWS
        private WinMediaPlayer? _player;
#endif

#if ANDROID
        private MediaPlayer? _androidPlayer;
#endif

        // Old style calls use this: always normal speed
        public Task PlayAsync(string audioUrl, CancellationToken cancellationToken = default)
        {
            return PlayAsync(audioUrl, 1.0, cancellationToken);
        }

        // New overload with playbackRate
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
        private Task PlayOnWindowsAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    _player?.Pause();
                    _player?.Dispose();
                }
                catch
                {
                }

                var player = new WinMediaPlayer
                {
                    Source = MediaSource.CreateFromUri(new Uri(audioUrl))
                };

                _player = player;

                // Apply playback rate (1.0 = normal)
                try
                {
                    player.PlaybackSession.PlaybackRate = playbackRate;
                }
                catch
                {
                    // If rate cannot be set, ignore and play at normal speed
                }

                player.MediaEnded += (sender, args) =>
                {
                    try
                    {
                        player.Pause();
                        player.Dispose();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        if (_player == player)
                            _player = null;
                    }

                    tcs.TrySetResult();
                };

                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() =>
                    {
                        try
                        {
                            player.Pause();
                            player.Dispose();
                        }
                        catch
                        {
                        }
                        finally
                        {
                            if (_player == player)
                                _player = null;
                        }

                        tcs.TrySetCanceled(cancellationToken);
                    });
                }

                player.Play();
            });

            return tcs.Task;
        }
#endif

#if ANDROID
        private Task PlayOnAndroidAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();

            Task.Run(() =>
            {
                try
                {
                    // Stop any existing playback
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

                    // Apply playback speed on Android M and above using PlaybackParams
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
                        // If speed cannot be set, fall back to normal
                    }

                    player.Start();

                    _androidPlayer = player;

                    // When playback completes, clean up and signal completion
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
                    // Swallow playback errors for now
                    tcs.TrySetResult();
                }
            }, cancellationToken);

            return tcs.Task;
        }
#endif
    }
}
