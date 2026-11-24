using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly TimeSpan PlaybackCatchUpWait = TimeSpan.FromSeconds(10);
    private LiveInterruptionRecovery? _interruption;
    private LiveStreamRecovery? _liveStreamRecovery;
    private CancellationTokenSource? _interruptionJump;
    private bool _connectedThisRun;
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
            _liveStreamRecovery?.Request(since);
        ObserveInterruption(LiveInterruptionSource.Background, blocked);
    }
#elif IOS
    private void OnIosAudioInterruption() =>
        ObserveInterruption(LiveInterruptionSource.NativeAudio, IosAudioEnvironment.HasRecoveryInterruption, IosAudioEnvironment.RoutePaused);

    private void OnIosBackgroundCheckpoint(DateTimeOffset since)
    {
        if (_durableRun is { } scope && _monitoring.CanRecover(_monitoring.Current) && IsCurrentLiveScope(scope))
            SaveInterruptionValue(PlaybackInterruptionKey(scope) + "_background", since.ToString("O", CultureInfo.InvariantCulture));
    }
#endif

    private bool IsLiveInterruptionBlockingPlayback =>
        _interruptionJump != null || _interruption?.IsInterrupted == true;

    private static string PlaybackInterruptionKey(DurableLiveQueue.Run scope)
    {
        var scopeBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { scope.Server, scope.AccountScope }));
        return "live_interruption_" + Convert.ToHexString(SHA256.HashData(scopeBytes));
    }

    private void StartInterruptionTracking(DurableLiveQueue.Run scope)
    {
        var key = PlaybackInterruptionKey(scope);
        _connectedThisRun = false;
        _interruption = new LiveInterruptionRecovery(TimeProvider.System, checkpoint => SaveInterruption(key, checkpoint));
        // Interruption policy belongs to the active listening session. The
        // independent HTTP checkpoint has already been reattached to this run.
        SaveInterruption(key, null);
#if IOS
        SaveInterruptionValue(key + "_background", string.Empty);
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

    private void StopInterruptionTracking(DurableLiveQueue.Run? scope)
    {
        _interruptionJump?.Cancel();
        _interruptionJump = null;
        _interruption = null;
        _connectedThisRun = false;
        if (scope != null)
        {
            SaveInterruption(PlaybackInterruptionKey(scope), null);
#if IOS
            SaveInterruptionValue(PlaybackInterruptionKey(scope) + "_background", string.Empty);
#endif
        }
    }

    private void ObserveConnectionInterruption(ConnectionStatus status)
    {
        if (status == ConnectionStatus.Connected)
        {
            _connectedThisRun = true;
            ObserveInterruption(LiveInterruptionSource.Connection, false);
        }
        else if (_connectedThisRun && status == ConnectionStatus.ServerUnreachable &&
            _interruption?.IsBlockedBy(LiveInterruptionSource.Connection) == false)
        {
            ObserveInterruption(LiveInterruptionSource.Connection, true);
            if (_interruption.Since is { } since)
                _liveStreamRecovery?.Request(since);
        }
    }

    private void ObserveInterruption(LiveInterruptionSource source, bool blocked, bool requiresPlay = false, DateTimeOffset? beganAt = null)
    {
        if (_interruption is not { } episode || !_monitoring.CanRecover(_monitoring.Current) ||
            _durableRun is not { } scope || !IsCurrentLiveScope(scope))
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
            _durableRun is not { } scope || !IsCurrentLiveScope(scope) ||
            _playbackOwnership.Live is not { } live || !AudioEnabled ||
            !(userInitiated || _settingsService.AutoPlay || _userRunShouldDriveQueue || _currentPlayingCall != null))
            return Task.CompletedTask;

        return episode.ShouldJump(_settingsService.MobileAudio.InterruptionRecovery) switch
        {
            true => JumpAfterInterruptionAsync(episode, run, scope, live, userInitiated || _currentPlayingCall != null),
            false => ResumeInterruptedQueueAsync(episode, userInitiated),
            null => Task.CompletedTask
        };
    }

    private Task ResumeInterruptedQueueAsync(LiveInterruptionRecovery episode, bool userInitiated)
    {
        episode.Complete();
        return RequestQueuePlaybackAsync("InterruptionEnded", userInitiated || _userRunShouldDriveQueue);
    }

    private async Task JumpAfterInterruptionAsync(LiveInterruptionRecovery episode, MonitoringLifecycle.Run run,
        DurableLiveQueue.Run scope, PlaybackOwnership.Lease live, bool userInitiated)
    {
        using var jump = CancellationTokenSource.CreateLinkedTokenSource(run.Token, live.Token);
        _interruptionJump = jump;
        try
        {
            InvalidateLivePlayback();
            if (!await WaitForLiveEdgeAsync(jump.Token))
                return;
            try
            {
                if (_liveStreamRecovery?.ReconciledForPlayback is { IsCompleted: false } catchUp)
                    await catchUp.WaitAsync(PlaybackCatchUpWait, jump.Token);
            }
            catch (TimeoutException)
            {
                // History recovery may still be retrying while Live is available.
                // Keep its window open and apply the boundary to the rows we have.
                AppLog.Add("Live playback resumed while HTTP recovery was still pending.");
            }

            var result = await _durableQueue.JumpToLiveAsync(scope, LocalRecoveryFloor(scope), skipSelected: false, jump.Token,
                prepareBoundary: async () =>
                {
                    await FlushLiveCompletionAsync(jump.Token);
                    if (!_monitoring.CanRecover(run) || !IsCurrentLiveScope(scope))
                        jump.Cancel();
                    return LocalRecoveryFloor(scope);
                },
                commitPlayback: transaction => AppStateStore.Write(transaction, PlaybackInterruptionKey(scope), string.Empty));
            if (!_monitoring.CanRecover(run) || !IsCurrentLiveScope(scope) || jump.IsCancellationRequested)
                return;
            _manualLiveTraversal = null;
            if (result != null)
                _livePlaybackFloor = result.Target.ArrivalSequence;
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

        if (!_monitoring.CanRecover(run) || !IsCurrentLiveScope(scope) || !_playbackOwnership.IsCurrent(live))
            return;
        if (!episode.Since.HasValue)
            await RequestQueuePlaybackAsync("ReturnedToLiveAfterInterruption", userInitiated || _userRunShouldDriveQueue);
        else if (jump.IsCancellationRequested && !episode.IsInterrupted && !_playbackRequests.HasManualSelection)
            await RecoverInterruptedPlaybackAsync(userInitiated);
    }
}
