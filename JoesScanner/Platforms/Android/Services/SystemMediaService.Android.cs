#if ANDROID
using Android.Content;
using JoesScanner.Platforms.Android.Services;

namespace JoesScanner.Services;

public partial class SystemMediaService
{
    private static bool _foregroundStartRequested;
    private IDisposable? _monitoringRouteWatch;
    private NowPlayingMetadata? _nextClipMetadata;
    private bool _clipPlaying;

    internal static void PrepareAndroidMonitoringService()
    {
        if (!MainActivity.IsAudioActivityVisible || _foregroundStartRequested || AudioForegroundService.IsForegroundReady)
            return;

        var context = Platform.AppContext;
        using var intent = new Intent(context, typeof(AudioForegroundService));
        try
        {
            _foregroundStartRequested = true;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch (Exception ex)
        {
            _foregroundStartRequested = false;
            AppLog.Add(() => $"Android playback service could not start ({ex.GetType().Name}).");
        }
    }

    internal static void AndroidForegroundServiceStopped() => _foregroundStartRequested = false;

    private partial void PlatformSetHandlers(Func<Task> onPlay, Func<Task> onStop, Func<Task>? onNext, Func<Task>? onPrevious) =>
        AndroidMediaCenter.SetHandlers(onPlay, onStop, onNext, onPrevious);

    private partial Task PlatformStartSessionAsync(bool audioEnabled)
    {
        var context = Platform.AppContext;
        PrepareAndroidMonitoringService();
        if (_monitoringRouteWatch == null)
        {
            _ = _settings.MobileAudio;
            _monitoringRouteWatch = AndroidAudioEnvironment.Watch(context, _settings);
            AndroidAudioEnvironment.ResumeStateChanged += OnResumeStateChanged;
        }
        AndroidMediaCenter.SetConnected(context, true);
        UpdateIdleNotification();
        return Task.CompletedTask;
    }

    private partial Task PlatformStopSessionAsync()
    {
        var context = Platform.AppContext;
        AndroidAudioEnvironment.ResumeStateChanged -= OnResumeStateChanged;
        _monitoringRouteWatch?.Dispose();
        _monitoringRouteWatch = null;
        _nextClipMetadata = null;
        _clipPlaying = false;
        AndroidMediaCenter.Clear(context);
        using var intent = new Intent(context, typeof(AudioForegroundService));
        context.StopService(intent);
        _foregroundStartRequested = false;
        return Task.CompletedTask;
    }

    private partial void PlatformUpdateNowPlaying(NowPlayingMetadata metadata, bool audioEnabled) =>
        _nextClipMetadata = metadata;

    private partial Task PlatformRefreshAudioSessionAsync(bool audioEnabled, string reason) => Task.CompletedTask;

    private partial Task PlatformSetClipPlaybackStateAsync(bool isPlaying, bool audioEnabled, string reason)
    {
        var context = Platform.AppContext;
        _clipPlaying = isPlaying;
        if (isPlaying)
        {
            AndroidMediaCenter.UpdateNowPlaying(context, _nextClipMetadata ?? new NowPlayingMetadata { Title = "Joe's Scanner", Artist = "Recording" });
            AndroidMediaCenter.SetPlaybackState(context, true);
        }
        else if (_monitoringRouteWatch != null)
            UpdateIdleNotification();
        else
            AndroidMediaCenter.Clear(context);
        return Task.CompletedTask;
    }

    private void OnResumeStateChanged()
    {
        if (!_clipPlaying)
            UpdateIdleNotification();
    }

    private static void UpdateIdleNotification()
    {
        var context = Platform.AppContext;
        AndroidMediaCenter.SetPlaybackState(context, false);
        AndroidMediaCenter.UpdateNowPlaying(context, "Joe's Scanner",
            AndroidAudioEnvironment.RequiresPlay ? "Playback paused. Select Play to resume." : "Monitoring");
    }

    private partial void PlatformClear() => AndroidMediaCenter.Clear(Platform.AppContext);
}
#endif
