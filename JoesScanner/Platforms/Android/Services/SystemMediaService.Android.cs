#if ANDROID
using Android.Content;
using JoesScanner.Platforms.Android.Services;

namespace JoesScanner.Services;

public partial class SystemMediaService
{
    internal static AndroidForegroundOwnership AndroidForeground { get; } = new();
    private static long _androidMonitoringRunId;
    private static long _androidStandaloneRunId;
    private static long _nextStandaloneRunId;
    private static int _standaloneLeases;
    private IDisposable? _monitoringRouteWatch;
    private long _androidSessionRunId;
    private NowPlayingMetadata? _nextClipMetadata;
    private bool _clipPlaying;
    private bool _monitoringAudioEnabled;

    internal static void BeginAndroidMonitoringRun(long runId)
    {
        _androidMonitoringRunId = runId;
        UpdateAndroidForegroundOwner();
    }

    // A History queue or a disconnected Live selection claims eligibility at
    // the user action, before downloading or draining the previous player.
    internal static IDisposable BeginAndroidStandalonePlayback()
    {
        if (_standaloneLeases++ == 0)
            _androidStandaloneRunId = --_nextStandaloneRunId;
        if (AndroidForeground.RunId == _androidStandaloneRunId)
            PrepareAndroidPlaybackService();
        else
            UpdateAndroidForegroundOwner();
        return new StandalonePlaybackLease();
    }

    internal static void PrepareAndroidPlaybackService()
    {
        if (!AndroidForeground.TryRequest())
            return;

        var runId = AndroidForeground.RunId;
        var context = Platform.AppContext;
        using var intent = new Intent(context, typeof(AudioForegroundService));
        intent.PutExtra(AudioForegroundService.ExtraMonitoringRun, runId);
        try
        {
            var service = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? context.StartForegroundService(intent)
                : context.StartService(intent);
            if (service == null)
            {
                AndroidForeground.RequestFailed(runId);
                AppLog.Add("Android playback service did not accept the start request.");
            }
        }
        catch (Exception ex)
        {
            AndroidForeground.RequestFailed(runId);
            AppLog.Add(() => $"Android playback service could not start ({ex.GetType().Name}).");
        }
    }

    internal static void EndAndroidMonitoringRun(long runId)
    {
        if (_androidMonitoringRunId != runId)
            return;
        _androidMonitoringRunId = 0;
        UpdateAndroidForegroundOwner();
    }

    private static void UpdateAndroidForegroundOwner()
    {
        var nextOwner = _androidMonitoringRunId != 0 ? _androidMonitoringRunId : _androidStandaloneRunId;
        var previousOwner = AndroidForeground.RunId;
        if (previousOwner == nextOwner)
            return;
        var context = Platform.AppContext;
        if (AndroidForeground.End(previousOwner))
        {
            using var intent = new Intent(context, typeof(AudioForegroundService));
            try { context.StopService(intent); }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine(() => $"Could not stop the Android audio service ({ex.GetType().Name}).");
            }
            if (previousOwner > 0)
                AndroidMediaCenter.EndMonitoringRun(context, previousOwner);
        }
        if (nextOwner == 0)
            return;
        AndroidForeground.Begin(nextOwner);
        if (nextOwner > 0)
            AndroidMediaCenter.BeginMonitoringRun(context, nextOwner);
        PrepareAndroidPlaybackService();
    }

    private sealed class StandalonePlaybackLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (--_standaloneLeases != 0)
                    return;
                _androidStandaloneRunId = 0;
                UpdateAndroidForegroundOwner();
            });
        }
    }

    private partial void PlatformSetHandlers(Func<Task> onPlay, Func<Task> onStop, Func<Task>? onNext, Func<Task>? onPrevious) =>
        AndroidMediaCenter.SetHandlers(onPlay, onStop, onNext, onPrevious);

    private partial Task PlatformStartSessionAsync(bool audioEnabled, long monitoringRunId)
    {
        if (!AndroidForeground.IsCurrent(monitoringRunId))
            return Task.CompletedTask;

        _androidSessionRunId = monitoringRunId;
        _monitoringAudioEnabled = audioEnabled;
        var context = Platform.AppContext;
        PrepareAndroidPlaybackService();
        if (_monitoringRouteWatch == null)
        {
            _ = _settings.MobileAudio;
            _monitoringRouteWatch = AndroidAudioEnvironment.Watch(context, _settings);
            AndroidAudioEnvironment.ResumeStateChanged += OnResumeStateChanged;
            AndroidAudioEnvironment.InterruptionChanged += OnResumeStateChanged;
            AndroidAudioEnvironment.ClipFocusInterrupted += OnClipFocusInterrupted;
            AndroidForeground.StateChanged += OnResumeStateChanged;
        }
        AndroidMediaCenter.SetConnected(context, true);
        UpdateIdleNotification();
        return Task.CompletedTask;
    }

    private partial Task PlatformStopSessionAsync(long monitoringRunId)
    {
        if (_androidSessionRunId != monitoringRunId)
            return Task.CompletedTask;

        _androidSessionRunId = 0;
        AndroidAudioEnvironment.ResumeStateChanged -= OnResumeStateChanged;
        AndroidAudioEnvironment.InterruptionChanged -= OnResumeStateChanged;
        AndroidAudioEnvironment.ClipFocusInterrupted -= OnClipFocusInterrupted;
        AndroidForeground.StateChanged -= OnResumeStateChanged;
        _monitoringRouteWatch?.Dispose();
        _monitoringRouteWatch = null;
        _nextClipMetadata = null;
        _clipPlaying = false;
        return Task.CompletedTask;
    }

    private partial void PlatformUpdateNowPlaying(NowPlayingMetadata metadata, bool audioEnabled)
    {
        _nextClipMetadata = metadata;
        if (_monitoringAudioEnabled != audioEnabled)
            _ = PlatformRefreshAudioSessionAsync(audioEnabled, "Audio state changed");
    }

    private partial Task PlatformRefreshAudioSessionAsync(bool audioEnabled, string reason)
    {
        _monitoringAudioEnabled = audioEnabled;
        OnResumeStateChanged();
        return Task.CompletedTask;
    }

    private partial Task PlatformSetClipPlaybackStateAsync(bool isPlaying, bool audioEnabled, string reason)
    {
        // An old clip can finish while the replacement run's foreground service
        // is already being promoted. Keep that run's notification intact.
        if (_androidSessionRunId != AndroidForeground.RunId)
            return Task.CompletedTask;

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
        if (_clipPlaying || !AndroidForeground.IsCurrent(_androidSessionRunId))
            return;
        try { UpdateIdleNotification(); }
        catch (Java.Lang.RuntimeException ex)
        {
            AppLog.DebugWriteLine(() => $"Android audio status update failed ({ex.GetType().Name}).");
        }
    }

    private void OnClipFocusInterrupted(PlaybackCompletion owner, bool blocked) => OnResumeStateChanged();

    private void UpdateIdleNotification()
    {
        var context = Platform.AppContext;
        AndroidMediaCenter.SetPlaybackState(context, false);
        AndroidMediaCenter.UpdateNowPlaying(context, "Joe's Scanner",
            !_monitoringAudioEnabled ? "Audio off. Monitoring." :
            AndroidAudioEnvironment.RequiresPlay ? "Playback paused. Select Play to resume." :
            AndroidAudioEnvironment.IsBlocked || AndroidAudioEnvironment.HasInterruptedClip ? "Audio interrupted. Monitoring." :
            AndroidForeground.StartFailed ? "Monitoring. Background playback unavailable." : "Monitoring");
    }

    private partial void PlatformClear() => AndroidMediaCenter.Clear(Platform.AppContext);
}
#endif
