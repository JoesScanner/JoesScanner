using System.Globalization;
using System.Text.Json;
using JoesScanner.Services;
#if ANDROID
using JoesScanner.Platforms.Android.Services;
#elif IOS
using JoesScanner.Platforms.iOS.Services;
#endif

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    private const string PlaybackInterruptionKey = "live_playback_interruption";
    private LiveInterruptionRecovery? _interruption;
    private CancellationTokenSource? _interruptionJump;
    private bool _selectingManualPlayback;

#if ANDROID
    private void OnAndroidAudioInterruption() =>
        ObserveInterruption(LiveInterruptionSource.NativeAudio, AndroidAudioEnvironment.CallActive || AndroidAudioEnvironment.RoutePaused,
            AndroidAudioEnvironment.RoutePaused);

    private void OnAndroidFocusInterruption(bool blocked, bool released)
    {
        if (released && _playbackRequests.HasManualSelection && _interruption is { } episode &&
            episode.IsBlockedBy(LiveInterruptionSource.AndroidFocus))
        {
            // Releasing the previous clip's focus is part of a manual handoff.
            episode.SetBlocked(LiveInterruptionSource.AndroidFocus, false);
            episode.Complete();
            return;
        }

        if (!blocked || _playbackOwnership.Live != null)
            ObserveInterruption(LiveInterruptionSource.AndroidFocus, blocked);
    }

    private void OnAndroidForegroundChanged()
    {
        var foreground = SystemMediaService.AndroidForeground;
        var blocked = !foreground.ActivityVisible && !foreground.IsReady;
        if (!blocked && _interruption?.IsBlockedBy(LiveInterruptionSource.Background) == true && _interruption.Since is { } since)
            RequestRecorderRecovery(since);
        ObserveInterruption(LiveInterruptionSource.Background, blocked);
    }
#elif IOS
    private void OnIosAudioInterruption() =>
        ObserveInterruption(LiveInterruptionSource.NativeAudio, IosAudioEnvironment.HasRecoveryInterruption, IosAudioEnvironment.RoutePaused);

    private void OnIosBackgroundCheckpoint(DateTimeOffset since)
    {
        if (_monitoring.CanRecover(_monitoring.Current))
            SaveInterruptionValue(PlaybackInterruptionKey + "_background", since.ToString("O", CultureInfo.InvariantCulture));
    }
#endif

    private bool IsLiveInterruptionBlockingPlayback =>
        _interruptionJump != null || _interruption?.IsInterrupted == true;

    private void StartInterruptionTracking()
    {
        _interruption = new LiveInterruptionRecovery(TimeProvider.System, checkpoint => SaveInterruption(PlaybackInterruptionKey, checkpoint));
        SaveInterruption(PlaybackInterruptionKey, null);
#if IOS
        SaveInterruptionValue(PlaybackInterruptionKey + "_background", string.Empty);
#endif
        OnPropertyChanged(nameof(CanResumePlayback));
    }

    private static void SaveInterruption(string key, LiveInterruptionRecovery.Checkpoint? checkpoint) =>
        SaveInterruptionValue(key, checkpoint == null ? string.Empty : JsonSerializer.Serialize(checkpoint));

    private static void SaveInterruptionValue(string key, string value, string? onlyIf = null)
    {
        try
        {
            AppStateStore.InTransaction(transaction =>
            {
                if (onlyIf == null || AppStateStore.Read(transaction, key) == onlyIf)
                    AppStateStore.Write(transaction, key, value);
                return true;
            }, durable: true);
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Live interruption state could not be saved ({ex.GetType().Name}).");
        }
    }

    private void StopInterruptionTracking()
    {
        _interruptionJump?.Cancel();
        _interruptionJump = null;
        _interruption = null;
        SaveInterruption(PlaybackInterruptionKey, null);
#if IOS
        SaveInterruptionValue(PlaybackInterruptionKey + "_background", string.Empty);
#endif
    }

    private void ObserveInterruption(LiveInterruptionSource source, bool blocked, bool requiresPlay = false, DateTimeOffset? beganAt = null)
    {
        if (_interruption is not { } episode || !_monitoring.CanRecover(_monitoring.Current))
            return;
        var explicitPlay = episode.RequiresPlay;
        episode.SetBlocked(source, blocked, requiresPlay, beganAt);
        if (explicitPlay != episode.RequiresPlay)
            OnPropertyChanged(nameof(CanResumePlayback));
        if (blocked)
            _interruptionJump?.Cancel();
        else if (episode.Since.HasValue && !episode.IsInterrupted)
            _ = RecoverInterruptedPlaybackAsync();
    }

    private Task RecoverInterruptedPlaybackAsync(bool userInitiated = false)
    {
        if (_interruption is not { } episode || _interruptionJump != null || _selectingManualPlayback ||
            _monitoring.Current is not { } run || !_monitoring.CanRecover(run) ||
            _playbackOwnership.Live is not { } live || !AudioEnabled ||
            !(userInitiated || _settingsService.AutoPlay || _userRunShouldDriveQueue || _currentPlayingCall != null))
            return Task.CompletedTask;

        return episode.ShouldJump(_settingsService.MobileAudio.InterruptionRecovery) switch
        {
            true => JumpAfterInterruptionAsync(episode, run, live, userInitiated || _currentPlayingCall != null),
            false => ResumeInterruptedQueueAsync(episode, userInitiated),
            null => Task.CompletedTask
        };
    }

    private Task ResumeInterruptedQueueAsync(LiveInterruptionRecovery episode, bool userInitiated)
    {
        episode.Complete();
        return RequestQueuePlaybackAsync("InterruptionEnded", userInitiated || _userRunShouldDriveQueue);
    }

    private void CancelSourceRecoveryJump() => _interruptionJump?.Cancel();

    private async Task JumpAfterInterruptionAsync(LiveInterruptionRecovery episode, MonitoringLifecycle.Run run,
        PlaybackOwnership.Lease live, bool userInitiated)
    {
        using var jump = CancellationTokenSource.CreateLinkedTokenSource(run.Token, live.Token);
        _interruptionJump = jump;
        try
        {
            InvalidateLivePlayback();
            if (!await WaitForLiveEdgeAsync(jump.Token) || !await WaitForAutomaticAudioSourceAsync(jump.Token))
                return;
            var result = await _durableQueue.JumpToLiveAsync(AutomaticQueueScopes(), LocalRecoveryFloor, skipSelected: false, jump.Token,
                prepareBoundary: () => FlushLiveCompletionAsync(jump.Token),
                commitPlayback: transaction => AppStateStore.Write(transaction, PlaybackInterruptionKey, string.Empty),
                automatic: AutomaticJumpFilter);
            if (!_monitoring.CanRecover(run) || jump.IsCancellationRequested)
                return;
            _manualLiveTraversal = null;
            if (result != null)
                _livePlaybackFloor = result.Target.Row.ArrivalSequence;
            episode.Complete(persisted: true);
            SetPlaybackRetry(null);
            _lastTransportCall = null;
            LastQueueEvent = "Returned to Live after interruption";
            UpdateQueueDerivedState();
        }
        catch (OperationCanceledException) when (jump.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Could not return to Live after an interruption ({ex.GetType().Name}).");
        }
        finally
        {
            if (_interruptionJump == jump)
                _interruptionJump = null;
        }

        if (!_monitoring.CanRecover(run) || !_playbackOwnership.IsCurrent(live))
            return;
        if (!episode.Since.HasValue)
            await RequestQueuePlaybackAsync("ReturnedToLiveAfterInterruption", userInitiated || _userRunShouldDriveQueue);
        else if (jump.IsCancellationRequested && !episode.IsInterrupted && !_playbackRequests.HasManualSelection)
            await RecoverInterruptedPlaybackAsync(userInitiated);
    }
}
