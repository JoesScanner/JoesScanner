using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels
{
    public partial class MainViewModel
    {
        private sealed class ManualLiveTraversal(DurableLiveQueue.Run scope, long position, long[] visibleReplays)
        {
            internal DurableLiveQueue.Run Scope { get; } = scope;
            internal long Position { get; set; } = position;
            internal bool HasReplayAfter(long position) => visibleReplays.Any(sequence => sequence > position);
            internal long[] ReplaysAfter(long position) => visibleReplays.Where(sequence => sequence > position).ToArray();
        }

        private ManualLiveTraversal? _manualLiveTraversal;
        private Task<long?>? _liveEdgeBoundary;
        private long _livePlaybackFloor;

        private void ResetLiveTraversal()
        {
            _manualLiveTraversal = null;
            _liveEdgeBoundary = null;
            _livePlaybackFloor = 0;
        }

        private void BeginAudioAtLiveEdge(bool skipOutstandingRecovery = false)
        {
            _manualLiveTraversal = null;
            _liveEdgeBoundary = _monitoring.Current is { } run && _monitoring.CanRecover(run) &&
                _durableRun is { } durable ? ReserveLiveEdgeAsync(run, durable, skipOutstandingRecovery) : null;
        }

        private async Task<long?> ReserveLiveEdgeAsync(MonitoringLifecycle.Run run, DurableLiveQueue.Run durable,
            bool skipOutstandingRecovery)
        {
            try
            {
                var floor = await _durableQueue.SetPlaybackFloorAtLiveEdgeAsync(durable, run.Token, skipOutstandingRecovery);
                return floor > 0 ? floor : null;
            }
            catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Could not reserve the Live playback edge ({ex.GetType().Name}).");
            }
            return null;
        }

        private async Task<bool> WaitForLiveEdgeAsync(CancellationToken token)
        {
            if (_liveEdgeBoundary is not { } boundary)
                return true;

            var floor = await boundary.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!floor.HasValue)
                return false;
            if (ReferenceEquals(_liveEdgeBoundary, boundary))
            {
                _livePlaybackFloor = floor.Value;
                _liveEdgeBoundary = null;
                UpdateQueueDerivedState();
            }
            return true;
        }

        private int CountRepresentedWaitingCalls(DurableLiveQueue.Run durable)
        {
            var manualPosition = _manualPlaybackTarget?.LiveArrivalSequence;
            var after = manualPosition ?? _currentPlayingCall?.LiveArrivalSequence ??
                _manualLiveTraversal?.Position ?? _lastPlayedCall?.LiveArrivalSequence ?? 0;
            var floor = manualPosition ?? _livePlaybackFloor;
            return SnapshotLiveCalls().Count(call => call.LivePlaybackOwner == durable.Owner &&
                call.LiveArrivalSequence is { } sequence && sequence >= floor &&
                sequence > after && IsPlayableForQueue(call));
        }

        private void OnPlaybackOwnershipChanged()
        {
            if (_playbackOwnership.Live == null)
            {
                InvalidateLivePlayback();
                UpdateQueueDerivedState();
                return;
            }

            if (!_monitoring.CanRecover(_monitoring.Current))
                return;

            // Reserve the boundary synchronously with the lease change, ahead of later admissions.
            BeginAudioAtLiveEdge(skipOutstandingRecovery: true);
            SetPlaybackRetry(null);
            DrainPendingCallsToVisible();
            if (_durableRun == null)
            {
                foreach (var call in Calls)
                    call.ConsumeLateAudio();
            }
            _lastPlayedCall = Calls.FirstOrDefault();
            _lastTransportCall = null;
            UpdateQueueDerivedState();
            if (AudioEnabled && (_settingsService.AutoPlay || _userRunShouldDriveQueue))
                _ = RequestQueuePlaybackAsync("HistoryReleasedAudio", userInitiated: _userRunShouldDriveQueue);
        }

        private void InvalidateLivePlayback()
        {
            ++_queuePlaybackVersion;
            _isQueuePlaybackRunning = false;
            _playbackRequests.RetireCurrent();
            _transportLookups.RetireCurrent();
            _manualPlaybackTarget = null;
            ClearPrefetchedPlayableUrl(deleteCompletedTempFile: true);
            ClearPlayingSelection();
        }

        private void RequestQueueForLiveArrival(string reason)
        {
            if (!AudioEnabled || _playbackOwnership.Live == null)
                return;
            if (_isQueuePlaybackRunning)
            {
                _queueWake.Signal();
                return;
            }
            if (_settingsService.AutoPlay || _userRunShouldDriveQueue)
                _ = RequestQueuePlaybackAsync(reason, _userRunShouldDriveQueue);
        }

        private async Task EnsureQueuePlaybackAsync(PlaybackOwnership.Lease live)
        {
            if (_isQueuePlaybackRunning || _playbackRequests.HasManualSelection)
                return;

            var run = _monitoring.Current;
            if (!_playbackOwnership.IsCurrent(live) || !_monitoring.CanRecover(run) || !AudioEnabled || !IsRunning)
                return;

            _isQueuePlaybackRunning = true;
            var version = ++_queuePlaybackVersion;
            var durable = _durableRun;
            var traversal = _manualLiveTraversal is { } selected && selected.Scope == durable ? selected : null;
            using var selection = CancellationTokenSource.CreateLinkedTokenSource(run!.Token, live.Token);
            var token = selection.Token;
            var aborted = false;
            var hadFailure = false;

            LastQueueEvent = "Queue playback started";

            try
            {
                // A rapidly reopened Live queue must read after a completed
                // clip from the previous lease has saved its disposition.
                if (!await WaitForLiveEdgeAsync(token))
                    return;
                await FlushLiveCompletionAsync(token);
                while (OwnsSelection() && AudioEnabled && !IsLiveInterruptionBlockingPlayback)
                {
                    var beforeRead = _queueWake.BeforeRead();
                    var next = durable == null ? GetNextQueuedCall() :
                        await GetNextDurableCallAsync(durable, token, version, traversal?.Position,
                            traversal?.ReplaysAfter(traversal.Position));
                    if (!OwnsSelection() || !AudioEnabled || IsLiveInterruptionBlockingPlayback)
                        break;
                    if (next == null)
                    {
                        if (ShouldRecheck(beforeRead))
                            continue;
                        break;
                    }

                    UpdateQueueDerivedState();

                    using var request = _playbackRequests.Begin(manual: false, live.Token);
                    var outcome = await PlayAudioAsync(next, request);
                    hadFailure = outcome == PlaybackOutcome.Failed;
                    if (request.CanContinue)
                    {
                        if (traversal != null && next.LiveArrivalSequence is { } sequence &&
                            (outcome.AdvancesQueue() || outcome == PlaybackOutcome.Withheld))
                            traversal.Position = sequence;
                        if (outcome == PlaybackOutcome.Withheld)
                            continue;
                    }

                    if (!outcome.AdvancesQueue() || !AudioEnabled || !IsRunning || !request.CanContinue)
                    {
                        // A late audio update can clear the retry while this attempt is unwinding.
                        if (ShouldRecheck(beforeRead))
                            continue;
                        aborted = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                aborted = true;
                AppLog.DebugWriteLine(() => $"Error in EnsureQueuePlaybackAsync: {ex}");
            }
            finally
            {
                if (version == _queuePlaybackVersion)
                {
                    _isQueuePlaybackRunning = false;
                    UpdateQueueDerivedState();
                    if (!hadFailure)
                        LastQueueEvent = aborted ? "Queue playback aborted" : "Queue playback finished";
                }
            }

            bool OwnsSelection() =>
                version == _queuePlaybackVersion && _playbackOwnership.IsCurrent(live) &&
                _monitoring.CanRecover(run) && !_playbackRequests.HasManualSelection &&
                (durable == null || IsCurrentLiveScope(durable));

            bool ShouldRecheck(long beforeRead) =>
                OwnsSelection() && AudioEnabled && _retryPlaybackCall == null && _queueWake.ChangedSince(beforeRead);
        }

        private LivePlaybackRequests.Request BeginLiveManualRequest(PlaybackOwnership.Lease live)
        {
            // Retire an automatic loop immediately. Its canceled I/O may still be unwinding
            // when the newer manual selection finishes and requests queue playback again.
            ++_queuePlaybackVersion;
            _isQueuePlaybackRunning = false;
            _manualPlaybackTarget = null;
            _transportLookups.RetireCurrent();
            return _playbackRequests.Begin(manual: true, live.Token);
        }

        private static async Task RunLivePlaybackInputAsync(Func<Task> input)
        {
            try { await input(); }
            catch (Exception error)
            {
                // The dispatcher command is async void. Failures during input
                // setup or teardown must be logged inside the Task boundary.
                AppLog.Add(() => $"Live playback command failed ({error.GetType().Name}).");
            }
        }

        private bool IsFilteredForPlayback(CallItem item, bool allowMutedPlayback = false) =>
            _filterService.ShouldHide(item) || (!allowMutedPlayback && _filterService.ShouldMute(item));

        private async Task OnCallTappedAsync(CallItem? item)
        {
            if (item == null || _filterService.ShouldHide(item) ||
                _monitoring.Current is { IsActive: false } || _playbackOwnership.Live is not { } live)
                return;

            using var request = BeginManualPlayback(live);
            var token = request.Token;

            try
            {
                if (!request.CanContinue)
                    return;
                _manualPlaybackTarget = item;
#if ANDROID
                using var androidPlayback = _monitoring.Current == null ? SystemMediaService.BeginAndroidStandalonePlayback() : null;
#endif
                if (!request.CanContinue)
                    return;

                LastQueueEvent = $"Call tapped at {DateTime.Now:T}";
                ClearPlayingSelection();
                if (!AudioEnabled)
                {
                    await PlaySingleCallWithoutQueueAsync(item, request);
                    return;
                }

                var run = _monitoring.Current;
                var durable = _durableRun;
                // Capture the card set at selection. New admissions are picked up
                // by FIFO, and terminal replay is limited to cards the user could see.
                var visibleReplays = durable == null ? [] : Calls
                    .Where(card => card.LiveArrivalSequence.HasValue && TryGetDurableRecording(card, durable, out _) &&
                        !IsFilteredForPlayback(card))
                    .Select(card => card.LiveArrivalSequence!.Value).Distinct().ToArray();

                var seek = durable != null && _monitoring.CanRecover(run) && IsCurrentLiveScope(durable) &&
                    TryGetDurableRecording(item, durable, out var id)
                    ? EstablishManualTraversalAsync(durable, item, id, visibleReplays, request)
                    : Task.FromResult<ManualLiveTraversal?>(null);

                item.IsCompletedReplay = false;
                PlaybackOutcome outcome;
                try { outcome = await PlayAudioAsync(item, request, allowMutedPlayback: true); }
                finally { await seek; }
                if (!request.CanContinue || outcome == PlaybackOutcome.Canceled || !_monitoring.CanRecover(run))
                    return;

                if (seek.Result is { } traversal)
                    traversal.Position = item.LiveArrivalSequence!.Value;
                if (!outcome.AdvancesQueue())
                    return;
                _userRunShouldDriveQueue = true;
                _lastPlayedCall = item;
                UpdateQueueDerivedState();
                LastQueueEvent = "Tapped call finished; continuing toward Live";
                _manualPlaybackTarget = null;
                request.Dispose();
                _ = RequestQueuePlaybackAsync("TappedCallFinished", true);
            }
            catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token))
            {
                // A newer selection or explicit stop owns the playback state now.
            }
            catch (Exception ex)
            {
                ReportPlaybackFailure(ex, request);
                if (request.CanContinue)
                    SetPlaybackRetry(item);
            }
            finally
            {
                if (request.IsCurrent)
                    _manualPlaybackTarget = null;
            }
        }

        private async Task<ManualLiveTraversal?> EstablishManualTraversalAsync(DurableLiveQueue.Run durable,
            CallItem item, string id, long[] visibleReplays, LivePlaybackRequests.Request request)
        {
            // Reserve the seek while audio is being prepared. Returning from History
            // can then pick up an interrupted, still-pending manual selection.
            var token = request.Token;
            var position = item.LiveArrivalSequence ?? await _durableQueue.GetArrivalSequenceAsync(durable, id, token);
            if (position is not { } sequence || !await _durableQueue.SetPlaybackFloorAsync(durable,
                    LocalRecoveryFloor(durable), sequence, token) || !request.CanContinue)
                return null;

            item.LiveArrivalSequence = sequence;
            _liveEdgeBoundary = null;
            _livePlaybackFloor = sequence;
            _userRunShouldDriveQueue = true;
            return _manualLiveTraversal = new(durable, sequence - 1, visibleReplays);
        }

        private async Task JumpToLiveAsync()
        {
            if (_monitoring.Current is { IsActive: false } || _playbackOwnership.Live is not { } live)
                return;
#if ANDROID
            using var androidPlayback = _monitoring.Current == null ? SystemMediaService.BeginAndroidStandalonePlayback() : null;
#endif
            var failedRecording = _retryPlaybackCall;
            using var request = BeginManualPlayback(live);
            var token = request.Token;
            var resumeOnExit = false;
            try
            {
                if (!request.CanContinue)
                    return;

                ClearPlayingSelection();
                _manualLiveTraversal = null;
                var durable = _durableRun;
                var run = _monitoring.Current;
                var playTarget = AudioEnabled;
                CallItem? newest;
                if (durable != null && _monitoring.CanRecover(run))
                {
                    var failedId = failedRecording != null && TryGetDurableRecording(failedRecording, durable, out var id)
                        ? id : null;
                    var jump = await _durableQueue.JumpToLiveAsync(durable, LocalRecoveryFloor(durable), !playTarget, token, failedId,
                        prepareBoundary: async () =>
                        {
                            await FlushLiveCompletionAsync(token);
                            return LocalRecoveryFloor(durable);
                        });
                    if (!request.CanContinue || !_monitoring.CanRecover(run))
                        return;
                    if (jump == null)
                    {
                        resumeOnExit = true;
                        LastQueueEvent = "No live call available";
                        return;
                    }

                    var row = jump.Target;
                    _liveEdgeBoundary = null;
                    _livePlaybackFloor = row.ArrivalSequence;
                    playTarget = jump.PlayTarget;
                    // A skipped or already-terminal edge should not hold up
                    // future Live arrivals, even if its explicit replay fails.
                    resumeOnExit = !playTarget || row.Snapshot == null;
                    newest = ResolveDurableCard(durable, row);
                    newest.IsCompletedReplay = false;
                    _manualPlaybackTarget = newest;
                    if (playTarget && row.Snapshot == null)
                        await RefreshTransportCardAsync(newest, token);
                    if (!request.CanContinue || !_monitoring.CanRecover(run))
                        return;
                    ShowDurableCard(newest);
                    // A Live arrival can commit immediately after the jump transaction.
                    // Preserve those cards when moving the display to the live edge.
                    DrainPendingCallsToVisible();
                }
                else
                {
                    newest = SnapshotLiveCalls().Where(call => call.LiveArrivalSequence.HasValue)
                        .MaxBy(call => call.LiveArrivalSequence);
                    if (newest == null)
                    {
                        _lastPlayedCall = null;
                        UpdateQueueDerivedState();
                        LastQueueEvent = "Jump to live ignored (no calls)";
                        return;
                    }
                    if (!Calls.Contains(newest))
                    {
                        ClearPendingCalls();
                        InsertLiveCardInDisplayOrder(newest);
                    }
                    _manualPlaybackTarget = newest;
                }

                // Cached cards can otherwise request automatic late-audio playback
                // for a recording the user just chose to skip.
                foreach (var card in Calls)
                    card.ConsumeLateAudio();

                // Force the UI list to snap back to the newest call even if the user was
                // scrolled into history (which intentionally disables auto-follow).
                try
                {
                    RequestJumpToLiveScroll?.Invoke();
                }
                catch
                {
                }

                if (!playTarget || !AudioEnabled)
                {
                    _lastPlayedCall = newest;
                    _lastTransportCall = newest;
                    UpdateQueueDerivedState();
                    LastQueueEvent = AudioEnabled ? "Jumped to live; waiting for new calls" : "Jumped to live (audio off)";
                    return;
                }

                LastQueueEvent = "Jump to live (playing newest call)";

                _currentPlayingCall = newest;
                UpdateQueueDerivedState();

                var outcome = await PlayAudioAsync(newest, request);
                if (!request.CanContinue || !outcome.AdvancesQueue())
                    return;

                if (AudioEnabled && IsRunning && (_settingsService.AutoPlay || _userRunShouldDriveQueue) &&
                    (durable != null || CallsWaiting > 0))
                {
                    LastQueueEvent = "Jump to live finished; restarting queue";
                    _manualPlaybackTarget = null;
                    request.Dispose();
					_ = RequestQueuePlaybackAsync("JumpToLiveFinished", true);
                }
                else
                {
                    LastQueueEvent = "Jump to live finished";
                }
            }
            catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token))
            {
            }
            catch (Exception ex)
            {
                ReportPlaybackFailure(ex, request);
            }
            finally
            {
                if (request.IsCurrent)
                {
                    _manualPlaybackTarget = null;
                    if (resumeOnExit && request.CanContinue && IsRunning && AudioEnabled &&
                        (_settingsService.AutoPlay || _userRunShouldDriveQueue))
                    {
                        SetPlaybackRetry(null);
                        request.Dispose();
                        _ = RequestQueuePlaybackAsync("JumpToLiveResume", true);
                    }
                }
            }
        }

        private async Task NavigateToAdjacentCallAsync(int direction)
        {
            if (_playbackOwnership.Live is not { } live || (direction != 1 && direction != -1))
                return;

            var anchor = (_playbackRequests.HasManualSelection ? _manualPlaybackTarget : null) ??
                _currentPlayingCall ?? _retryPlaybackCall ?? _lastTransportCall;
            var run = _monitoring.Current;
            var older = direction == 1;
            using var lookup = _transportLookups.Begin(manual: true, live.Token);
            if (_durableRun is { } durable && _monitoring.CanRecover(run))
            {
                var token = lookup.Token;
                try
                {
                    long? position = null;
                    if (anchor != null && TryGetDurableRecording(anchor, durable, out var id))
                        position = anchor.LiveArrivalSequence ?? await _durableQueue.GetArrivalSequenceAsync(durable, id, token);
                    if (position == null && older)
                        position = (await _durableQueue.FindTransportTargetAsync(durable, LocalRecoveryFloor(durable),
                            anchor: null, older: true, token))?.ArrivalSequence;

                    while (position != null && lookup.CanContinue && _monitoring.CanRecover(run))
                    {
                        var row = await _durableQueue.FindTransportTargetAsync(durable, LocalRecoveryFloor(durable),
                            position, older, token);
                        if (row == null || !lookup.CanContinue || !_monitoring.CanRecover(run))
                            break;
                        position = row.ArrivalSequence;
                        var candidate = ResolveDurableCard(durable, row);
                        if (_filterService.ShouldHide(candidate) || _filterService.ShouldMute(candidate))
                            continue;
                        if (string.IsNullOrWhiteSpace(candidate.AudioUrl))
                        {
                            try { await RefreshTransportCardAsync(candidate, token); }
                            catch (RecordingAccessException) when (lookup.CanContinue) { continue; }
                            catch (HttpRequestException) when (lookup.CanContinue) { continue; }
                        }
                        if (!lookup.CanContinue || !_monitoring.CanRecover(run))
                            return;
                        if (!IsPlayableForQueue(candidate))
                            continue;
                        // Autoplay can reach the requested item while SQLite
                        // resolves the command. Let that attempt finish.
                        if (!_playbackRequests.HasManualSelection && ReferenceEquals(candidate, _currentPlayingCall))
                            return;
                        ShowDurableCard(candidate);
                        await OnCallTappedAsync(candidate);
                        return;
                    }
                }
                catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token)) { }
                catch (Exception ex)
                {
                    ReportPlaybackFailure(ex, lookup);
                    return;
                }
            }
            else
            {
                var calls = SnapshotLiveCalls().Where(call => call.LiveArrivalSequence.HasValue)
                    .OrderByDescending(call => call.LiveArrivalSequence).ToArray();
                var index = anchor?.LiveArrivalSequence is { } sequence
                    ? Array.FindIndex(calls, call => call.LiveArrivalSequence == sequence) : 0;
                for (var i = index + direction; index >= 0 && i >= 0 && i < calls.Length; i += direction)
                {
                    if (!IsPlayableForQueue(calls[i]))
                        continue;
                    await OnCallTappedAsync(calls[i]);
                    return;
                }
            }

            if (lookup.CanContinue)
                LastQueueEvent = older ? "No previous call available" : "No next call available";
        }

        private async Task<double> GetEffectivePlaybackRateAsync(CallItem item, CancellationToken token)
        {
            var step = PlaybackSpeedStep;
            if (step == 0)
                return 1.0;

            var catchingUp = _durableRun is { } durable
                ? item.LiveArrivalSequence is { } sequence && TryGetDurableRecording(item, durable, out _) &&
                    ((_manualLiveTraversal is { } traversal && traversal.Scope == durable &&
                        !_playbackRequests.HasManualSelection && traversal.HasReplayAfter(sequence)) ||
                        await _durableQueue.HasNewerPendingAsync(durable, LocalRecoveryFloor(durable), sequence, token))
                : GetBacklogCountForStartingCall(item) > 0;
            if (!catchingUp)
                return 1.0;

            return step switch
            {
                0 => 1.0,
                1 => 1.25,
                2 => 1.5,
                3 => 1.75,
                4 => 2.0,
                _ => 1.0
            };
        }

        private async Task PlayOnDeviceAsync(CallItem item, string url, double rate,
            LivePlaybackRequests.Request request, CancellationToken cancellationToken, PlaybackCompletion completion)
        {
            if (_playbackOwnership.Live is not { } live || !request.CanContinue || cancellationToken.IsCancellationRequested)
                return;

            if (!request.IsManual)
                await FlushLiveCompletionAsync(cancellationToken);

            var isCompletedReplay = item.IsCompletedReplay;
            Task? saveCompletion = null;
            await _playbackOwnership.RunNativeAsync(live, cancellationToken, async token =>
            {
                token.ThrowIfCancellationRequested();

                var scope = _durableRun;
                try
                {
                    RequireCurrentRecordingAccount(item);
                    await _audioPlaybackService.PlayAsync(url, rate, token, completion);
                }
                catch (Exception ex) when (completion.Confirmed)
                {
                    AppLog.DebugWriteLine(() => $"Audio teardown after confirmed playback ({ex.GetType().Name}).");
                }

                if (completion.Confirmed && !isCompletedReplay && scope != null &&
                    TryGetDurableRecording(item, scope, out var id))
                    saveCompletion = _playbackCommit.ConfirmAsync(scope, id);
            });

            if (saveCompletion != null)
                await saveCompletion;
        }

        private async Task FlushLiveCompletionAsync(CancellationToken token = default)
        {
            await _playbackOwnership.WaitForLiveReleaseAsync(token);
            await _playbackCommit.FlushAsync(token);
        }
    }
}
