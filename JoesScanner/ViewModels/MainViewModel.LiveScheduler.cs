using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    private readonly LivePlaybackScheduler _scheduler;
    private readonly LiveBacklogGuard _backlog = new();
    private LivePlaybackPreferences _livePlaybackPreferences = LivePlaybackPreferences.Read();
    private Guid? _visibleLiveServer;
    private int _readyWaiting;
    private int _readyCountRevision;
    private bool _refreshingReadyCount;
    private bool _readyCountDirty;
    private Task _pendingFilterProcessing = Task.CompletedTask;

    internal LivePlaybackPreferences LivePlaybackOptions => _livePlaybackPreferences;
    public int CallsWaiting => _readyWaiting;
    public bool HasBacklogWarning => _backlog.WarningActive;
    public string BacklogWarningText => $"Playback backlog · {_readyWaiting} ready";

    internal void SetVisibleLiveView(Guid? serverId, Func<CallItem, bool>? favorites = null)
    {
        _visibleLiveServer = serverId;
        _visibleFavoriteFilter = favorites;
    }

    internal void ApplyLivePlaybackOptions(LivePlaybackPreferences options)
    {
        options.Save();
        var previous = _livePlaybackPreferences;
        _livePlaybackPreferences = options;
        if (previous.GroupCalls != options.GroupCalls || previous.PrioritizeFavorites != options.PrioritizeFavorites)
            _scheduler.Reset();
        if (previous.JumpThreshold != options.JumpThreshold)
            _backlog.ResetJump();
        PublishReadyCount(_readyWaiting);
        RequestQueueForLiveArrival("PlaybackOptionsChanged");
    }

    private async Task<(CallItem Call, LivePlaybackScheduler.Choice Choice)?> GetNextDurableCallAsync(
        CancellationToken token, int version, ManualLiveTraversal? traversal)
    {
        await _pendingFilterProcessing.WaitAsync(token);
        var scopes = LiveQueueScopes();
        if (traversal?.ServerId is { } selected)
            scopes = scopes.Where(scope => scope.ServerId == selected).ToArray();
        var next = await _scheduler.SelectAsync(_durableQueue, scopes, LocalRecoveryFloor, _livePlaybackPreferences,
            token, traversal?.Position, traversal?.ReplaysAfter(traversal.Position));
        if (version != _queuePlaybackVersion || token.IsCancellationRequested || _playbackRequests.HasManualSelection ||
            next == null || !IsCurrentLiveScope(next.Row.Scope))
            return null;

        var card = ResolveDurableCard(next.Row.Scope, next.Row.Row);
        card.IsCompletedReplay = next.Row.Row.IsCompletedReplay;
        // Keep filtered and stale candidates out of the native owner even when
        // a rule changes after the journal snapshot has been read.
        ShowDurableCard(card);
        return (card, next.Choice);
    }

    private bool ManualBacklogExemption => _manualLiveTraversal != null || _playbackRequests.HasManualSelection ||
        _transportLookups.HasManualSelection;

    private void RequestReadyCount()
    {
        _readyCountDirty = true;
        if (!_refreshingReadyCount)
            _ = RefreshReadyCountAsync();
    }

    private async Task RefreshReadyCountAsync()
    {
        _refreshingReadyCount = true;
        try
        {
            while (_readyCountDirty)
            {
                _readyCountDirty = false;
                var run = _monitoring.Current;
                if (!AudioEnabled || !_monitoring.CanRecover(run) || _playbackOwnership.Live == null)
                {
                    PublishReadyCount(0);
                    continue;
                }

                var revision = ++_readyCountRevision;
                var activeSequence = _currentPlayingCall?.LiveArrivalSequence;
                await _pendingFilterProcessing.WaitAsync(run!.Token);
                var count = await _durableQueue.CountReadyAsync(LiveQueueScopes(), LocalRecoveryFloor, activeSequence, run.Token);
                if (revision == _readyCountRevision && AudioEnabled && _monitoring.CanRecover(run) &&
                    _playbackOwnership.Live != null && activeSequence == _currentPlayingCall?.LiveArrivalSequence)
                    PublishReadyCount(count);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Could not count ready Live recordings ({ex.GetType().Name}).");
        }
        finally
        {
            _refreshingReadyCount = false;
            if (_readyCountDirty)
                RequestReadyCount();
        }
    }

    private void PublishReadyCount(int count)
    {
        _readyWaiting = count;
        if (_backlog.Observe(count, _livePlaybackPreferences, ManualBacklogExemption))
            AppLog.Add(() => $"Live playback backlog reached {count} ready recordings.");
        OnPropertyChanged(nameof(CallsWaiting));
        OnPropertyChanged(nameof(HasBacklogWarning));
        OnPropertyChanged(nameof(BacklogWarningText));
    }

    private void ResetScheduler()
    {
        ++_readyCountRevision;
        _scheduler.Reset();
        _backlog.Reset();
        PublishReadyCount(0);
    }

    private async Task<bool> TryAutomaticBacklogJumpAsync(MonitoringLifecycle.Run run, PlaybackOwnership.Lease live,
        int version, CancellationToken token)
    {
        if (ManualBacklogExemption)
            return false;
        await _pendingFilterProcessing.WaitAsync(token);
        var revision = ++_readyCountRevision;
        var scopes = LiveQueueScopes();
        var count = await _durableQueue.CountReadyAsync(scopes, LocalRecoveryFloor, null, token);
        if (version != _queuePlaybackVersion || !_monitoring.CanRecover(run) || !_playbackOwnership.IsCurrent(live))
            return false;
        if (revision == _readyCountRevision)
            PublishReadyCount(count);
        if (!_backlog.JumpPending || ManualBacklogExemption)
            return false;

        // The journal reserves the edge in arrival order after the current
        // clip has completed, including arrivals since the threshold was hit.
        using var jump = _transportLookups.Begin(manual: false, token);
        var floor = await _durableQueue.SetPlaybackFloorAtLiveEdgeAsync(scopes, jump.Token, skipOutstandingRecovery: true);
        if (version != _queuePlaybackVersion || !jump.CanContinue)
            return false;
        _livePlaybackFloor = floor;
        _lastTransportCall = null;
        _lastPlayedCall = Calls.FirstOrDefault();
        ResetScheduler();
        UpdateQueueDerivedState();
        LastQueueEvent = "Playback backlog skipped to Live";
        RequestReadyCount();
        return true;
    }

    private bool IsFilteredForPlayback(Guid? serverId, CallCardMetadata metadata) =>
        _filterService.ShouldMute(serverId, string.IsNullOrWhiteSpace(metadata.Receiver) ? metadata.Radio : metadata.Receiver,
            metadata.Site, metadata.Talkgroup);

    private async Task ApplyAutomaticAudioFiltersAsync()
    {
        UpdateQueueDerivedState();
        var run = _monitoring.Current;
        if (!_monitoring.CanRecover(run))
            return;

        var scopes = LiveQueueScopes();
        var processing = _durableQueue.ProcessFilteredAsync(scopes, LocalRecoveryFloor,
            candidate => candidate.Row.Snapshot is { } snapshot && IsFilteredForPlayback(candidate.Scope.ServerId, snapshot.Metadata),
            run!.Token);
        _pendingFilterProcessing = processing;
        if (_currentPlayingCall is { } playing && !_playbackRequests.HasManualSelection && IsFilteredForPlayback(playing))
        {
            _queueWake.Signal();
            _playbackRequests.CancelCurrent();
        }
        try { await processing; }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Add(() => $"Could not save processed Live filter results ({ex.GetType().Name})."); }
        finally
        {
            if (ReferenceEquals(_pendingFilterProcessing, processing))
                _pendingFilterProcessing = Task.CompletedTask;
        }
        if (_monitoring.CanRecover(run))
            RequestQueueForLiveArrival("AutomaticAudioFiltersChanged");
    }
}
