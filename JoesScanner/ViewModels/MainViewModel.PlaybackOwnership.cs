using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels
{
    public partial class MainViewModel
    {
        private sealed class ManualLiveTraversal(long session, long position, Guid? serverId, long[] visibleReplays)
        {
            internal long Session { get; } = session;
            internal long Position { get; set; } = position;
            internal Guid? ServerId { get; } = serverId;
            internal bool HasReplayAfter(long position) => visibleReplays.Any(sequence => sequence > position);
            internal long[] ReplaysAfter(long position) => visibleReplays.Where(sequence => sequence > position).ToArray();
        }

        private ManualLiveTraversal? _manualLiveTraversal;
        private Func<CallItem, bool>? _visibleFavoriteFilter;
        private Task<long?>? _liveEdgeBoundary;
        private long _livePlaybackFloor;

        private void ResetLiveTraversal()
        {
            _manualLiveTraversal = null;
            _liveEdgeBoundary = null;
            _livePlaybackFloor = 0;
            ResetScheduler();
        }

        private void BeginAudioAtLiveEdge(bool skipOutstandingRecovery = false)
        {
            _manualLiveTraversal = null;
            ResetScheduler();
            _liveEdgeBoundary = _monitoring.Current is { } run && _monitoring.CanRecover(run)
                ? ReserveLiveEdgeAsync(run, LiveQueueScopes(), skipOutstandingRecovery) : null;
        }

        private async Task<long?> ReserveLiveEdgeAsync(MonitoringLifecycle.Run run, IReadOnlyList<DurableLiveQueue.Run> scopes,
            bool skipOutstandingRecovery)
        {
            try
            {
                var floor = await _durableQueue.SetPlaybackFloorAtLiveEdgeAsync(scopes, run.Token, skipOutstandingRecovery);
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

        private void OnPlaybackOwnershipChanged()
        {
            if (_playbackOwnership.Live == null)
            {
                InvalidateLivePlayback();
                ResetScheduler();
                UpdateQueueDerivedState();
                return;
            }

            if (!_monitoring.CanRecover(_monitoring.Current))
                return;

            // Reserve the boundary synchronously with the lease change, ahead of later admissions.
            BeginAudioAtLiveEdge(skipOutstandingRecovery: true);
            SetPlaybackRetry(null);

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

            ClearPlayingSelection();
        }

        private void RequestQueueForLiveArrival(string reason)
        {
            RequestReadyCount();
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
            var traversal = _manualLiveTraversal is { } selected && selected.Session == run!.Id ? selected : null;
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
                    if (traversal == null && await TryAutomaticBacklogJumpAsync(run!, live, version, token))
                        continue;
                    if (!OwnsSelection() || !AudioEnabled || IsLiveInterruptionBlockingPlayback)
                        break;

                    var selectedCall = await GetNextDurableCallAsync(token, version, traversal);
                    if (!OwnsSelection() || !AudioEnabled || IsLiveInterruptionBlockingPlayback)
                        break;
                    if (selectedCall is not { } selectedAtBoundary)
                    {
                        if (ShouldRecheck(beforeRead))
                            continue;
                        if (traversal != null)
                        {
                            _manualLiveTraversal = traversal = null;
                            _scheduler.Reset();
                            continue;
                        }
                        break;
                    }

                    var (next, choice) = selectedAtBoundary;
                    UpdateQueueDerivedState();

                    using var request = _playbackRequests.Begin(manual: false, live.Token);
                    var outcome = await PlayAudioAsync(next, request);
                    hadFailure = outcome == PlaybackOutcome.Failed;
                    if (request.CanContinue)
                    {
                        if (traversal == null && outcome == PlaybackOutcome.Completed)
                            _scheduler.Complete(choice);
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
                    RequestReadyCount();
                    UpdateQueueDerivedState();
                    if (!hadFailure)
                        LastQueueEvent = aborted ? "Queue playback aborted" : "Queue playback finished";
                }
            }

            bool OwnsSelection() =>
                version == _queuePlaybackVersion && _playbackOwnership.IsCurrent(live) &&
                _monitoring.CanRecover(run) && !_playbackRequests.HasManualSelection;

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
            _backlog.CancelPendingJump();
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

        private bool IsFilteredForPlayback(CallItem item, bool allowFilteredPlayback = false) =>
            !allowFilteredPlayback && _filterService.ShouldMute(item);

        private async Task OnCallTappedAsync(CallItem? item)
        {
            if (item == null || _monitoring.Current is { IsActive: false } ||
                _playbackOwnership.Live is not { } live)
                return;

            var run = _monitoring.Current;
            var favoriteFilter = _visibleFavoriteFilter;
            if (favoriteFilter != null && !favoriteFilter(item))
                return;
            var server = _visibleLiveServer.HasValue ? item.ServerId ?? _visibleLiveServer : null;
            var visibleReplays = favoriteFilter == null
                ? Calls.Where(card => (server == null || card.ServerId == server) && card.LiveArrivalSequence.HasValue &&
                        !string.IsNullOrWhiteSpace(card.AudioUrl) && !IsFilteredForPlayback(card))
                    .Select(card => card.LiveArrivalSequence!.Value).Distinct().ToArray()
                : [];

            _backlog.CancelPendingJump();
            using var preparation = _transportLookups.Begin(manual: true, live.Token);
            var token = preparation.Token;
            string? prepared = null;
            try
            {
                prepared = await PrepareManualAudioAsync(item, token);
                if (!preparation.CanContinue || !_playbackOwnership.IsCurrent(live) || _monitoring.Current != run ||
                    favoriteFilter != null && !favoriteFilter(item))
                    return;

                // A failed or superseded preparation never cancels the current clip
                // or changes its traversal. The prepared recording can now take over.
                preparation.Dispose();
                using var request = BeginManualPlayback(live);
                if (!request.CanContinue)
                    return;
                _manualPlaybackTarget = item;
                _scheduler.Reset();
                ClearPlayingSelection();
#if ANDROID
                using var androidPlayback = run == null ? SystemMediaService.BeginAndroidStandalonePlayback() : null;
#endif
                try
                {
                    var durable = ScopeOf(item);
                    // A Favorite selection is scoped to the card. The regular
                    // automatic queue resumes without changing any recorder's floor.
                    if (favoriteFilter != null)
                        _manualLiveTraversal = null;
                    var seek = favoriteFilter == null && AudioEnabled && _monitoring.CanRecover(run)
                        ? EstablishManualTraversalAsync(run!.Id, server, durable, item, visibleReplays, request)
                        : Task.FromResult<ManualLiveTraversal?>(null);

                    item.IsCompletedReplay = false;
                    PlaybackOutcome outcome;
                    try { outcome = await PlayAudioAsync(item, request, allowFilteredPlayback: true, preparedAudio: prepared); }
                    finally { await seek; }
                    if (!request.CanContinue || !outcome.AdvancesQueue() || !AudioEnabled || !_monitoring.CanRecover(run))
                        return;

                    if (seek.Result is { } traversal && item.LiveArrivalSequence is { } sequence)
                        traversal.Position = sequence;
                    _userRunShouldDriveQueue = true;
                    _lastPlayedCall = item;
                    UpdateQueueDerivedState();
                    LastQueueEvent = "Selected call finished; continuing toward Live";
                    _manualPlaybackTarget = null;
                    request.Dispose();
                    _ = RequestQueuePlaybackAsync("TappedCallFinished", true);
                }
                catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, request.Token)) { }
                catch (Exception ex) { ReportPlaybackFailure(ex, request); }
                finally
                {
                    if (request.IsCurrent)
                        _manualPlaybackTarget = null;
                }
            }
            catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token)) { }
            catch (Exception ex) { ReportPlaybackFailure(ex, preparation); }
            finally
            {
                if (prepared != null)
                    ReleasePreparedManualAudio(prepared);
                if (preparation.IsCurrent)
                {
                    preparation.Dispose();
                    RequestQueueForLiveArrival("ManualPreparationReleased");
                }
            }
        }

        private async Task<ManualLiveTraversal?> EstablishManualTraversalAsync(long session, Guid? server,
            DurableLiveQueue.Run? durable, CallItem item, long[] visibleReplays, LivePlaybackRequests.Request request)
        {
            var token = request.Token;
            var position = item.LiveArrivalSequence;
            if (durable != null && TryGetDurableRecording(item, durable, out var id))
            {
                position ??= await _durableQueue.GetArrivalSequenceAsync(durable, id, token);
                var scopes = LiveQueueScopes().Where(scope => server == null || scope.ServerId == server).ToArray();
                if (position == null || !await _durableQueue.SetPlaybackFloorAsync(scopes, durable,
                        LocalRecoveryFloor(durable), position.Value, token))
                    return null;
            }
            if (position is not { } sequence || !request.CanContinue)
                return null;

            item.LiveArrivalSequence = sequence;
            _liveEdgeBoundary = null;
            if (server == null)
                _livePlaybackFloor = sequence;
            _userRunShouldDriveQueue = true;
            return _manualLiveTraversal = new(session, sequence - 1, server, visibleReplays);
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
                ResetScheduler();
                var scopes = LiveQueueScopes();
                var run = _monitoring.Current;
                var playTarget = AudioEnabled;
                CallItem? newest;
                if (scopes.Length != 0 && _monitoring.CanRecover(run))
                {
                    var jump = await _durableQueue.JumpToLiveAsync(scopes, LocalRecoveryFloor, !playTarget, token,
                        failed: row => failedRecording != null && TryGetDurableRecording(failedRecording, row.Scope, out var id) &&
                            id == row.Row.Id,
                        prepareBoundary: () => FlushLiveCompletionAsync(token));
                    if (!request.CanContinue || !_monitoring.CanRecover(run))
                        return;
                    if (jump == null)
                    {
                        resumeOnExit = true;
                        LastQueueEvent = "No live call available";
                        return;
                    }

                    var (durable, row) = jump.Target;
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
                    (scopes.Length != 0 || CallsWaiting > 0))
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

        private Task NavigateToAdjacentCallAsync(int direction)
        {
            if (_playbackOwnership.Live == null || direction is not (1 or -1))
                return Task.CompletedTask;

            var visible = Calls.Where(call => (_visibleLiveServer == null || call.ServerId == _visibleLiveServer) &&
                (_visibleFavoriteFilter?.Invoke(call) ?? true)).ToArray();
            var anchor = _manualPlaybackTarget ?? _currentPlayingCall ?? _lastTransportCall;
            var index = Array.FindIndex(visible, call => ReferenceEquals(call, anchor) ||
                call.LiveArrivalSequence.HasValue && call.LiveArrivalSequence == anchor?.LiveArrivalSequence);
            // The first card is the navigation anchor when the current recording
            // belongs to a different view or has aged out of the display.
            var target = Math.Max(index, 0) + direction;
            if (target >= 0 && target < visible.Length)
                return OnCallTappedAsync(visible[target]);

            LastQueueEvent = direction == 1 ? "No previous call available" : "No next call available";
            return Task.CompletedTask;
        }

        private async Task<double> GetEffectivePlaybackRateAsync(CallItem item, CancellationToken token)
        {
            var step = PlaybackSpeedStep;
            if (step == 0 || !AudioEnabled)
                return 1.0;

            var catchingUp = false;
            if (item.LiveArrivalSequence is { } sequence && ScopeOf(item) != null)
            {
                var traversal = _manualLiveTraversal;
                var scopes = LiveQueueScopes().Where(scope => traversal?.ServerId == null || scope.ServerId == traversal.ServerId).ToArray();
                catchingUp = traversal?.HasReplayAfter(sequence) == true;
                if (!catchingUp)
                    catchingUp = await _durableQueue.CountReadyAsync(scopes, LocalRecoveryFloor, sequence, token) > 0;
            }

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
            LivePlaybackRequests.Request request, CancellationToken cancellationToken, PlaybackCompletion completion,
            Action? enteringNative = null)
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

                var scope = ScopeOf(item);
                try
                {
                    if (!request.IsManual && scope == null)
                        return;
                    RequireCurrentRecordingAccount(item);
                    enteringNative?.Invoke();
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
