#if ANDROID
using System;
using System.Threading.Tasks;
using global::Android.Content;
using global::Android.OS;
using global::Microsoft.Maui.ApplicationModel;
using JoesScanner.Platforms.Android.Services;

namespace JoesScanner.Services
{
    public partial class SystemMediaService
    {
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
                var ctx = Platform.CurrentActivity ?? Platform.AppContext;
                if (ctx != null)
                {
                    var intent = new Intent(ctx, typeof(AudioForegroundService));
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                        ctx.StartForegroundService(intent);
                    else
                        ctx.StartService(intent);

                    AndroidMediaCenter.EnsureInitialized(ctx);
                    AndroidMediaCenter.SetConnected(ctx, true);

                    // Keep notification present while connected, even if audio is off.
                    AndroidMediaCenter.UpdateNowPlaying(
                        ctx,
                        "Joe's Scanner",
                        audioEnabled ? "Connected" : "Connected (Audio Off)");

                    AndroidMediaCenter.SetPlaybackState(ctx, audioEnabled);
                }
            }
            catch
            {
            }

            return Task.CompletedTask;
        }

        private partial Task PlatformStopSessionAsync()
        {
            try
            {
                var ctx = Platform.CurrentActivity ?? Platform.AppContext;
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
            catch
            {
            }

            return Task.CompletedTask;
        }

        private partial void PlatformUpdateNowPlaying(string title, string subtitle, bool audioEnabled)
        {
            try
            {
                var ctx = Platform.CurrentActivity ?? Platform.AppContext;
                if (ctx != null)
                {
                    AndroidMediaCenter.UpdateNowPlaying(ctx, title, subtitle);

                    // For your app semantics:
                    // If audio is enabled, treat as "playing"; otherwise treat as "stopped" but still connected.
                    AndroidMediaCenter.SetPlaybackState(ctx, audioEnabled);
                }
            }
            catch
            {
            }
        }

        private partial void PlatformClear()
        {
            try
            {
                var ctx = Platform.CurrentActivity ?? Platform.AppContext;
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
