using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    private sealed record PendingLag(DurableLiveQueue.Run Scope, LiveStreamRecovery Recovery,
        LiveStreamRecovery.LagObservation Observation, CancellationToken Connection, Func<Func<bool>, bool> CommitConnection);

    private readonly PlaybackLagGeneration _lagGeneration = new();
    private readonly Dictionary<long, PendingLag> _pendingPlaybackLag = [];

    private bool HasLagRecoveryForAutomaticRetry => _retryIsAutomatic &&
        _retryPlaybackCall is { } retry && _pendingPlaybackLag.ContainsKey(retry.LivePlaybackOwner);

    private void RetirePlaybackLag()
    {
        _lagGeneration.Retire();
        foreach (var lag in _pendingPlaybackLag.Values)
            LogPlaybackLag(lag.Scope, "canceled or superseded");
        _pendingPlaybackLag.Clear();
    }

    private void RetireRecorderPlaybackLag(long owner)
    {
        if (_pendingPlaybackLag.Remove(owner, out var lag))
            LogPlaybackLag(lag.Scope, "canceled or superseded");
    }

    private void OnPlaybackLag(DurableLiveQueue.Run scope, LiveStreamRecovery recovery,
        LiveStreamRecovery.LagObservation observation, CancellationToken connection, Func<Func<bool>, bool> commitConnection)
    {
        if (observation.Gap.Audio.Token.IsCancellationRequested || connection.IsCancellationRequested || !IsCurrentLiveScope(scope))
        {
            LogPlaybackLag(scope, "canceled or superseded");
            return;
        }
        if (observation.Evidence == null)
        {
            LogPlaybackLag(scope, "indeterminate");
            return;
        }
        if (_livePlaybackPreferences.VerifiedLagRecovery.Minutes == PlaybackLagRecoveryPreference.Never)
            return;
        if (!AudioEnabled || ManualBacklogExemption || _playbackOwnership.Live == null ||
            IsLiveInterruptionBlockingPlayback || !AutomaticQueueScopes().Contains(scope))
        {
            LogPlaybackLag(scope, "canceled or superseded");
            return;
        }

        RetireRecorderPlaybackLag(scope.Owner);
        _pendingPlaybackLag.Add(scope.Owner, new(scope, recovery, observation, connection, commitConnection));
        RequestQueueForLiveArrival("RecorderLagReconciled");
    }

    // The shared scheduler only calls this between recordings. An HTTP gap
    // never holds another recorder's audio while the evidence is collected.
    private async Task ApplyVerifiedPlaybackLagAsync(CancellationToken token)
    {
        if (_pendingPlaybackLag.Count == 0 || ManualBacklogExemption)
            return;

        var options = _livePlaybackPreferences.VerifiedLagRecovery;
        foreach (var pending in _pendingPlaybackLag.Values.ToArray())
        {
            var (scope, recovery, observation, connection, commitConnection) = pending;
            if (!_pendingPlaybackLag.TryGetValue(scope.Owner, out var current) || !ReferenceEquals(pending, current))
                continue;
            _pendingPlaybackLag.Remove(scope.Owner);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, connection, observation.Gap.Audio.Token);
            try
            {
                if (lifetime.IsCancellationRequested || ManualBacklogExemption || IsLiveInterruptionBlockingPlayback ||
                    !IsCurrentLiveScope(scope) || !AutomaticQueueScopes().Contains(scope))
                {
                    LogPlaybackLag(scope, "canceled or superseded");
                    continue;
                }

                var result = await _durableQueue.TryAdvanceVerifiedLagAsync(scope, LocalRecoveryFloor(scope), _automaticAudioFilter,
                    observation.Evidence!, options, commit => IsCurrentLiveScope(scope) &&
                        commitConnection(() => recovery.CommitLagIfCurrent(observation.Gap, commit)), lifetime.Token);
                if (result.Lag is not { } lag)
                {
                    LogPlaybackLag(scope, "indeterminate");
                    continue;
                }
                LogPlaybackLag(scope, FormattableString.Invariant($"verified ({lag.TotalSeconds:F0} seconds)"));
                if (!options.Exceeds(lag))
                    continue;
                LogPlaybackLag(scope, "threshold exceeded");
                if (result.Superseded)
                {
                    LogPlaybackLag(scope, "canceled or superseded");
                    continue;
                }

                if (result.Floor is { } floor)
                {
                    LogPlaybackLag(scope, FormattableString.Invariant($"playback floor advanced to {floor}"));
                    if (lifetime.IsCancellationRequested)
                        continue;
                    if (_retryIsAutomatic && _retryPlaybackCall?.LivePlaybackOwner == scope.Owner)
                        SetPlaybackRetry(null);
                    LastQueueEvent = "Recorder playback returned to Live";
                    RequestReadyCount();
                    UpdateQueueDerivedState();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                LogPlaybackLag(scope, "canceled or superseded");
            }
            catch (Exception error)
            {
                LogPlaybackLag(scope, $"indeterminate ({error.GetType().Name})");
            }
        }
    }

    private static void LogPlaybackLag(DurableLiveQueue.Run scope, string result) =>
        AppLog.Add(() => $"Verified playback lag for recorder {scope.ServerId:N}: {result}.");
}
