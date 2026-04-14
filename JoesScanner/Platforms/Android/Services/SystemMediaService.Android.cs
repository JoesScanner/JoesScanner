#if ANDROID
using System;
using global::Android.Content;
using global::Android.OS;
using JoesScanner.Platforms.Android.Services;

namespace JoesScanner.Services
{
    public partial class SystemMediaService
    {
        private static int _androidForegroundServiceRunning;
        private static int _androidForegroundServiceStartPending;
        private const string AndroidAppInForegroundKey = "android_app_in_foreground";


        private static bool IsAndroidAppInForeground()
        {
            try
            {
                return AppStateStore.GetBool(AndroidAppInForegroundKey, true);
            }
            catch
            {
                return true;
            }
        }

        private static void EnsureForegroundServiceStartedIfNeeded(Context ctx)
        {
            if (ctx == null)
                return;

            if (IsAndroidAppInForeground())
                return;

            var shouldStartService = false;

            if (!AudioForegroundService.IsForegroundReady &&
                System.Threading.Volatile.Read(ref _androidForegroundServiceRunning) == 0 &&
                System.Threading.Interlocked.CompareExchange(ref _androidForegroundServiceStartPending, 1, 0) == 0)
            {
                shouldStartService = true;
            }

            if (!shouldStartService)
                return;

            try
            {
                var intent = new Intent(ctx, typeof(AudioForegroundService));
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    ctx.StartForegroundService(intent);
                else
                    ctx.StartService(intent);

                System.Threading.Interlocked.Exchange(ref _androidForegroundServiceRunning, 1);
                try { AppLog.Add(() => "AndroidSvc: requested foreground service start while app backgrounded."); } catch { }
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref _androidForegroundServiceRunning, 0);
                System.Threading.Interlocked.Exchange(ref _androidForegroundServiceStartPending, 0);
                throw;
            }
        }
        private partial void PlatformSetHandlers(Func<Task> onPlay, Func<Task> onStop, Func<Task>? onNext, Func<Task>? onPrevious)
        {
            try
            {
                // AndroidMediaCenter will invoke these delegates when the user uses Bluetooth controls,
                // lock-screen controls, or notification action buttons.
                AndroidMediaCenter.SetHandlers(onPlay, onStop, onNext, onPrevious);
            }
            catch
            {
            }
        }

        private partial Task PlatformStartSessionAsync(bool audioEnabled)
        {
            try
            {
                var ctx = Platform.AppContext;
                if (ctx != null)
                {
                    AndroidMediaCenter.EnsureInitialized(ctx);
                    AndroidMediaCenter.SetConnected(ctx, true);

                    // Keep notification present while connected, even if audio is off.
                    AndroidMediaCenter.UpdateNowPlaying(
                        ctx,
                        "Joe's Scanner",
                        audioEnabled ? "Connected" : "Connected (Audio Off)");

                    AndroidMediaCenter.SetPlaybackState(ctx, audioEnabled);

                    // Do not launch the foreground service while the app is in the foreground.
                    // This avoids the launch-time foreground-service race the user can hit by tapping
                    // play again while the app is still opening. Only promote to a foreground service
                    // once the app is backgrounded and the playback/session still needs to stay alive.
                    EnsureForegroundServiceStartedIfNeeded(ctx);

                    if (AudioForegroundService.IsForegroundReady)
                        System.Threading.Interlocked.Exchange(ref _androidForegroundServiceStartPending, 0);
                }
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AndroidSvc: start session failed. {ex.GetType().Name}: {ex.Message}"); } catch { }
                System.Threading.Interlocked.Exchange(ref _androidForegroundServiceRunning, 0);
                System.Threading.Interlocked.Exchange(ref _androidForegroundServiceStartPending, 0);
            }

            return Task.CompletedTask;
        }

        private partial Task PlatformStopSessionAsync()
        {
            try
            {
                var ctx = Platform.AppContext;
                if (ctx != null)
                {
                    try
                    {
                        AndroidMediaCenter.SetConnected(ctx, false);
                        AndroidMediaCenter.SetPlaybackState(ctx, false);
                        AndroidMediaCenter.Clear(ctx);
                    }
                    catch
                    {
                    }

                    var intent = new Intent(ctx, typeof(AudioForegroundService));
                    ctx.StopService(intent);
                }
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"AndroidSvc: stop session failed. {ex.GetType().Name}: {ex.Message}"); } catch { }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _androidForegroundServiceRunning, 0);
                System.Threading.Interlocked.Exchange(ref _androidForegroundServiceStartPending, 0);
            }

            return Task.CompletedTask;
        }

        private partial void PlatformUpdateNowPlaying(NowPlayingMetadata metadata, bool audioEnabled)
        {
            try
            {
                var ctx = Platform.AppContext;
                if (ctx != null)
                {
                    AndroidMediaCenter.UpdateNowPlaying(ctx, metadata);
                    EnsureForegroundServiceStartedIfNeeded(ctx);

                    // For your app semantics:
                    // If audio is enabled, treat as "playing"; otherwise treat as "stopped" but still connected.
                    AndroidMediaCenter.SetPlaybackState(ctx, audioEnabled);
                }
            }
            catch
            {
            }
        }

        private partial Task PlatformRefreshAudioSessionAsync(bool audioEnabled, string reason)
        {
            return Task.CompletedTask;
        }

        private partial Task PlatformSetClipPlaybackStateAsync(bool isPlaying, bool audioEnabled, string reason)
        {
            return Task.CompletedTask;
        }

        private partial void PlatformClear()
        {
            try
            {
                var ctx = Platform.AppContext;
                if (ctx != null)
                {
                    AndroidMediaCenter.Clear(ctx);
                }
            }
            catch
            {
            }
        }
    }
}
#endif
