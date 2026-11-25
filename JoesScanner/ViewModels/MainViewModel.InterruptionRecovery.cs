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
    private (long Run, DateTimeOffset Since)? _androidBackgroundGap;

    private void OnAndroidAudioInterruption() =>
        _ = ObserveInterruption(LiveInterruptionSource.NativeAudio, AndroidAudioEnvironment.IsBlocked, AndroidAudioEnvironment.RoutePaused);

    private void OnAndroidFocusInterruption(PlaybackCompletion owner, bool blocked) =>
        _ = ObserveClipFocusInterruption(owner, blocked);

    private void OnAndroidForegroundChanged()
    {
        var foreground = SystemMediaService.AndroidForeground;
        var blocked = !foreground.ActivityVisible && !foreground.IsReady;
        if (_monitoring.Current is { } run && _monitoring.CanRecover(run))
        {
            if (blocked && _androidBackgroundGap?.Run != run.Id)
                _androidBackgroundGap = (run.Id, DateTimeOffset.UtcNow);
            else if (!blocked && _androidBackgroundGap is { } gap && gap.Run == run.Id)
            {
                _androidBackgroundGap = null;
                RequestRecorderRecovery(gap.Since);
            }
        }
        _ = ObserveInterruption(LiveInterruptionSource.Background, blocked);
    }
#elif IOS
    private void OnIosAudioInterruption() =>
        _ = ObserveInterruption(LiveInterruptionSource.NativeAudio,
            IosAudioEnvironment.IsRecoveryBlocked(waitForActivation: _nativeLiveRequest?.CanContinue == true),
            IosAudioEnvironment.RoutePaused);

    private void OnIosBackgroundCheckpoint(DateTimeOffset since)
    {
        if (_monitoring.CanRecover(_monitoring.Current))
            SaveInterruptionValue(PlaybackInterruptionKey + "_background", since.ToString("O", CultureInfo.InvariantCulture));
    }
#endif

    private bool IsLiveInterruptionBlockingPlayback =>
        _interruptionJump != null || _interruption?.IsInterrupted == true;

    private void StartInterruptionTracking(TimeProvider? clock = null)
    {
        _interruption = new LiveInterruptionRecovery(clock ?? TimeProvider.System, checkpoint => SaveInterruption(PlaybackInterruptionKey, checkpoint));
        SaveInterruption(PlaybackInterruptionKey, null);
#if IOS
        SaveInterruptionValue(PlaybackInterruptionKey + "_background", string.Empty);
#endif
        NotifyPlaybackResumeState();
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
#if ANDROID
        _androidBackgroundGap = null;
#endif
        SaveInterruption(PlaybackInterruptionKey, null);
#if IOS
        SaveInterruptionValue(PlaybackInterruptionKey + "_background", string.Empty);
#endif
    }

    private void ResetInterruptionRecovery()
    {
        _interruptionJump?.Cancel();
        _interruption?.Reset();
        NotifyPlaybackResumeState();
    }

    private void RefreshNativeInterruptionState()
    {
#if ANDROID
        OnAndroidAudioInterruption();
        OnAndroidForegroundChanged();
#elif IOS
        OnIosAudioInterruption();
#endif
    }

    private Task ObserveClipFocusInterruption(PlaybackCompletion owner, bool blocked) =>
        ReferenceEquals(_nativeLiveCompletion, owner) && (!blocked || _nativeLiveRequest?.CanContinue == true)
            ? ObserveInterruption(LiveInterruptionSource.AndroidFocus, blocked) : Task.CompletedTask;

    private Task ObserveInterruption(LiveInterruptionSource source, bool blocked, bool requiresPlay = false, DateTimeOffset? beganAt = null)
    {
        if (_interruption is not { } episode || !_monitoring.CanRecover(_monitoring.Current) || _playbackOwnership.Live == null)
            return Task.CompletedTask;
        var explicitPlay = episode.RequiresPlay;
        var manual = _selectingManualPlayback || _playbackRequests.HasManualSelection;
        episode.SetBlocked(source, blocked, requiresPlay, beganAt, trackRecovery: !manual);
        if (explicitPlay != episode.RequiresPlay)
            NotifyPlaybackResumeState();
        if (blocked)
        {
            _automaticPreloads.Retire();
            _interruptionJump?.Cancel();
        }
        else if (episode.Since.HasValue && !episode.IsInterrupted)
            return RecoverInterruptedPlaybackAsync();
        return Task.CompletedTask;
    }

    private LivePlaybackRequests.Request BeginManualPlayback(PlaybackOwnership.Lease live)
    {
        LivePlaybackRequests.Request? request = null;
        _selectingManualPlayback = true;
        try
        {
            request = BeginLiveManualRequest(live);
            if (!request.CanContinue)
                return request;
            ResetInterruptionRecovery();
            SetPlaybackRetry(null);
#if ANDROID
            AndroidAudioEnvironment.RequestPlay();
            if (IsRunning)
                SystemMediaService.PrepareAndroidPlaybackService();
#elif IOS
            IosAudioEnvironment.RequestPlay();
#endif
            RefreshNativeInterruptionState();
        }
        catch
        {
            request?.Dispose();
            throw;
        }
        finally { _selectingManualPlayback = false; }

        return request;
    }

    private Task RecoverInterruptedPlaybackAsync(bool userInitiated = false)
    {
        if (_interruption is not { } episode || _interruptionJump != null || _selectingManualPlayback ||
            _playbackRequests.HasManualSelection || _transportLookups.HasManualSelection ||
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
            var floor = await _durableQueue.SetPlaybackFloorAtLiveEdgeAsync(LiveQueueScopes(), jump.Token,
                skipOutstandingRecovery: true,
                prepareBoundary: () => FlushLiveCompletionAsync(jump.Token),
                commitPlayback: transaction => AppStateStore.Write(transaction, PlaybackInterruptionKey, string.Empty));
            if (!_monitoring.CanRecover(run) || !_playbackOwnership.IsCurrent(live) ||
                _interruption != episode || jump.IsCancellationRequested)
                return;
            _manualLiveTraversal = null;
            _livePlaybackFloor = floor;
            ResetScheduler();
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

        if (!_monitoring.CanRecover(run) || !_playbackOwnership.IsCurrent(live) || _interruption != episode)
            return;
        if (!episode.Since.HasValue)
            await RequestQueuePlaybackAsync("ReturnedToLiveAfterInterruption", userInitiated || _userRunShouldDriveQueue);
        else if (jump.IsCancellationRequested && !episode.IsInterrupted && !_playbackRequests.HasManualSelection)
            await RecoverInterruptedPlaybackAsync(userInitiated);
    }
}
