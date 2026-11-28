using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels
{
    public partial class MainViewModel
    {
        private sealed class ManualLiveTraversal(long session, Guid? serverId, bool favoritesOnly, CallItem[] visibleReplays)
        {
            internal long Session { get; } = session;
            internal long Position { get; set; }
            internal Guid? ServerId { get; } = serverId;
            internal bool FavoritesOnly { get; } = favoritesOnly;
            internal CallItem[] VisibleReplays { get; } = visibleReplays;

            internal CallItem? FindReplay(long sequence) => VisibleReplays.FirstOrDefault(card => card.LiveArrivalSequence == sequence);
        }

        private ManualLiveTraversal? _manualLiveTraversal;
        private Func<CallItem, bool>? _visibleFavoriteFilter;
        private Task<long?>? _liveEdgeBoundary;
        private long _livePlaybackFloor;
        private readonly LiveAudioPreloader _automaticPreloads = new();
        private PlaybackCompletion? _nativeLiveCompletion;
        private PlaybackCompletion? _activeLiveAttempt;
        internal CallItem? ActiveLiveCard { get; private set; }

        private void PublishActiveLiveCard(CallItem item, PlaybackCompletion completion)
        {
            _activeLiveAttempt = completion;
            if (ReferenceEquals(ActiveLiveCard, item))
                return;
            if (ActiveLiveCard is { } previous)
                previous.IsPlaying = false;
            ActiveLiveCard = item;
            item.IsPlaying = true;
            OnPropertyChanged(nameof(ActiveLiveCard));
        }

        private void ClearActiveLiveCard(PlaybackCompletion? completion = null)
        {
            if (ActiveLiveCard is not { } active || (completion != null && !ReferenceEquals(_activeLiveAttempt, completion)))
                return;
            _activeLiveAttempt = null;
            ActiveLiveCard = null;
            active.IsPlaying = false;
            OnPropertyChanged(nameof(ActiveLiveCard));
        }

        private void ResetLiveTraversal()
        {
            _manualLiveTraversal = null;
            _liveEdgeBoundary = null;
            _livePlaybackFloor = 0;
            ResetAutomaticAudioSource();
            ResetScheduler();
        }

        private void BeginAudioAtLiveEdge(bool skipOutstandingRecovery = false)
        {
            ResetAutomaticAudioSource();
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
                ResetInterruptionRecovery();
                InvalidateLivePlayback();
                ResetAutomaticAudioSource();
                ResetScheduler();
                UpdateQueueDerivedState();
                return;
            }

            if (!_monitoring.CanRecover(_monitoring.Current))
                return;

            // Reserve the boundary synchronously with the lease change, ahead of later admissions.
            BeginAudioAtLiveEdge(skipOutstandingRecovery: true);
            RefreshNativeInterruptionState();
            SetPlaybackRetry(null);

            _lastPlayedCall = Calls.FirstOrDefault();
            _lastTransportCall = null;
            UpdateQueueDerivedState();
            if (AudioEnabled && (_settingsService.AutoPlay || _userRunShouldDriveQueue))
                _ = RequestQueuePlaybackAsync("HistoryReleasedAudio", userInitiated: _userRunShouldDriveQueue);
        }

        private void InvalidateLivePlayback()
        {
            _automaticPreloads.Retire();
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
            _automaticPreloads.Wake();
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
                    if (!await WaitForAutomaticAudioSourceAsync(token))
                        break;
                    var sourceRevision = _automaticAudioRevision;
                    var traversal = _manualLiveTraversal is { } selected && selected.Session == run!.Id ? selected : null;
                    var beforeRead = _queueWake.BeforeRead();
                    if (traversal == null)
                    {
                        await ApplyVerifiedPlaybackLagAsync(token);
                        if (_retryPlaybackCall != null)
                            break;
                        if (sourceRevision == _automaticAudioRevision && OwnsSelection() && AudioEnabled &&
                            !IsLiveInterruptionBlockingPlayback &&
                            await TryAutomaticBacklogJumpAsync(run!, live, version, token))
                            continue;
                    }
                    if (sourceRevision != _automaticAudioRevision)
                        continue;
                    if (!OwnsSelection() || !AudioEnabled || IsLiveInterruptionBlockingPlayback)
                        break;

                    var selectedCall = await GetNextDurableCallAsync(token, version, traversal);
                    if (sourceRevision != _automaticAudioRevision)
                        continue;
                    if (!OwnsSelection() || !AudioEnabled || IsLiveInterruptionBlockingPlayback)
                        break;
                    if (selectedCall is not { } selectedAtBoundary)
                    {
                        if (ShouldRecheck(beforeRead))
                            continue;
                        if (traversal != null)
                        {
                            if (traversal.FavoritesOnly)
                            {
                                // Keep bypassed calls in SQLite. Resume automatic playback
                                // beyond this pass instead of replaying intervening ordinary calls.
                                using var finish = _playbackRequests.Begin(manual: false, token);
                                try
                                {
                                    await _durableQueue.AdvancePlaybackFloorAsync(LiveQueueScopes(), traversal.Position + 1, finish.Token);
                                }
                                catch (OperationCanceledException) when (finish.Token.IsCancellationRequested) { }
                                if (!OwnsSelection())
                                    break;
                                if (!finish.CanContinue || sourceRevision != _automaticAudioRevision || ShouldRecheck(beforeRead))
                                    continue;
                            }
                            _manualLiveTraversal = null;
                            _scheduler.Reset();
                            continue;
                        }
                        break;
                    }

                    var (next, choice) = selectedAtBoundary;
                    UpdateQueueDerivedState();

                    using var request = _playbackRequests.Begin(manual: false, live.Token);
                    var outcome = await PlayAudioAsync(next, request, forecastFrom: traversal == null ? choice : null);
                    hadFailure = outcome == PlaybackOutcome.Failed;
                    if (sourceRevision != _automaticAudioRevision)
                    {
                        if (traversal != null && ReferenceEquals(_manualLiveTraversal, traversal) &&
                            (outcome.AdvancesQueue() || outcome == PlaybackOutcome.Withheld) &&
                            next.LiveArrivalSequence is { } position)
                            traversal.Position = position;
                        continue;
                    }
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
                AppLog.DebugWriteLine(() => $"Error in EnsureQueuePlaybackAsync: {DiagnosticRedaction.Describe(ex)}");
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
                    if (hadFailure && HasLagRecoveryForAutomaticRetry)
                        RequestQueueForLiveArrival("RecorderLagAfterAutomaticRetry");
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
            RetirePlaybackLag();
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
            var server = favoriteFilter == null && _visibleLiveServer.HasValue ? item.ServerId ?? _visibleLiveServer : null;
            var visibleReplays = Calls.Where(card => (server == null || card.ServerId == server) &&
                (favoriteFilter?.Invoke(card) ?? true) && card.LiveArrivalSequence.HasValue &&
                !string.IsNullOrWhiteSpace(card.AudioUrl) && !IsFilteredForPlayback(card)).ToArray();

            RetirePlaybackLag();
            _backlog.CancelPendingJump();
            _automaticPreloads.Retire();
            using var preparation = _transportLookups.Begin(manual: true, live.Token);
            var token = preparation.Token;
            PlaybackAudioLease? prepared = null;
            try
            {
                prepared = await PrepareManualAudioAsync(item, token);
                if (!preparation.CanContinue || !_playbackOwnership.IsCurrent(live) || _monitoring.Current != run ||
                    favoriteFilter != null && !favoriteFilter(item))
                    return;
                prepared.Owner?.Validate();
                RequireLiveRecordingEntitlement(item);

                // A failed or superseded preparation never cancels the current clip
                // or changes its traversal. The prepared recording can now take over.
                preparation.Dispose();
                using var request = BeginManualPlayback(live);
                if (!request.CanContinue)
                    return;
                _manualPlaybackTarget = item;
                var traversal = AudioEnabled && _monitoring.CanRecover(run)
                    ? new ManualLiveTraversal(run!.Id, server, favoriteFilter != null, visibleReplays) : null;
                // The first Favorite's catch-up rate can use its captured scope
                // while the durable seek is still being saved.
                _manualLiveTraversal = traversal?.FavoritesOnly == true ? traversal : null;
                _scheduler.Reset();
                ClearPlayingSelection();
#if ANDROID
                using var androidPlayback = run == null ? SystemMediaService.BeginAndroidStandalonePlayback() : null;
#endif
                try
                {
                    var seek = traversal != null
                        ? EstablishManualTraversalAsync(traversal, ScopeOf(item), item, request)
                        : Task.FromResult(false);

                    item.IsCompletedReplay = false;
                    PlaybackOutcome outcome;
                    try { outcome = await PlayAudioAsync(item, request, allowFilteredPlayback: true, preparedAudio: prepared); }
                    finally { await seek; }
                    if (!request.CanContinue || !outcome.AdvancesQueue() || !AudioEnabled || !_monitoring.CanRecover(run))
                        return;

                    if (traversal != null && ReferenceEquals(_manualLiveTraversal, traversal))
                    {
                        if (seek.Result && item.LiveArrivalSequence is { } sequence)
                            traversal.Position = sequence;
                        else
                            _manualLiveTraversal = null;
                    }
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
                    {
                        _manualPlaybackTarget = null;
                        if (traversal?.FavoritesOnly == true && ReferenceEquals(_manualLiveTraversal, traversal))
                            _manualLiveTraversal = null;
                    }
                }
            }
            catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token)) { }
            catch (Exception ex) { ReportPlaybackFailure(ex, preparation); }
            finally
            {
                prepared?.Dispose();
                if (preparation.IsCurrent)
                {
                    preparation.Dispose();
                    RequestQueueForLiveArrival("ManualPreparationReleased");
                }
            }
        }

        private async Task<bool> EstablishManualTraversalAsync(ManualLiveTraversal traversal,
            DurableLiveQueue.Run? durable, CallItem item, LivePlaybackRequests.Request request)
        {
            var token = request.Token;
            var sourceGeneration = _audioSourceGeneration;
            var position = item.LiveArrivalSequence;
            if (durable != null && TryGetDurableRecording(item, durable, out var id))
            {
                position ??= await _durableQueue.GetArrivalSequenceAsync(durable, id, token);
                var scopes = TraversalScopes(traversal);
                if (position == null || !await _durableQueue.SetPlaybackFloorAsync(scopes, durable,
                        LocalRecoveryFloor(durable), position.Value, token))
                    return false;
            }
            if (position is not { } sequence || !request.CanContinue || sourceGeneration != _audioSourceGeneration)
                return false;

            item.LiveArrivalSequence = sequence;
            _liveEdgeBoundary = null;
            if (traversal.ServerId == null)
                _livePlaybackFloor = sequence;
            _userRunShouldDriveQueue = true;
            traversal.Position = sequence - 1;
            _manualLiveTraversal = traversal;
            return true;
        }

        private DurableLiveQueue.Run[] TraversalScopes(ManualLiveTraversal traversal) =>
            LiveQueueScopes().Where(scope => traversal.ServerId == null || scope.ServerId == traversal.ServerId).ToArray();

        private Task<LivePlaybackScheduler.Scheduled?> ReadNextLiveRecordingAsync(CancellationToken token,
            ManualLiveTraversal? traversal, long? after = null)
        {
            if (traversal == null)
                return _scheduler.SelectAsync(_durableQueue, AutomaticQueueScopes(), LocalRecoveryFloor,
                    _livePlaybackPreferences, token, automatic: _automaticAudioFilter);

            var position = after ?? traversal.Position;
            var membership = traversal.FavoritesOnly ? ReadFavoriteAudioMembership() : null;
            var candidates = traversal.VisibleReplays.Where(card => card.LiveArrivalSequence > position);
            if (membership != null)
                candidates = candidates.Where(card => card.ServerId is { } id &&
                    FavoriteTalkgroupIdentity.From(card.CardMetadata) is { } identity && membership.Contains((id, identity)) &&
                    !IsFilteredForPlayback(card));
            var replays = candidates.Select(card => card.LiveArrivalSequence!.Value).Distinct().ToArray();
            // Completed payloads may already be compacted. The capture supplies
            // their identity, while the journal matches membership for pending rows.
            var filter = membership == null ? null : new LiveAutomaticAudioFilter(
                FavoriteEdges: membership.ToDictionary(identity => identity, _ => 0L));
            return _scheduler.SelectAsync(_durableQueue, TraversalScopes(traversal), LocalRecoveryFloor,
                _livePlaybackPreferences, token, position, replays, filter);
        }

        private CallItem ResolveLivePlaybackCard(LivePlaybackScheduler.Scheduled next, ManualLiveTraversal? traversal) =>
            ResolveDurableCard(next.Row.Scope, next.Row.Row, next.Row.Row.IsCompletedReplay && traversal?.FavoritesOnly == true
                ? traversal.FindReplay(next.Row.Row.ArrivalSequence) : null);

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
                // Return the reader to Live for this accepted user action, independent
                // of which automatic audio source has an eligible recording. Publish
                // synchronously before any await so a retired request cannot scroll
                // a subsequent listening session.
                try
                {
                    if (request.CanContinue)
                        RequestJumpToLiveScroll?.Invoke();
                }
                catch (Exception ex)
                {
                    AppLog.DebugWriteLine(() => $"Jump to Live could not publish visual navigation ({ex.GetType().Name}).");
                }
                if (!request.CanContinue)
                    return;
                if (!await WaitForAutomaticAudioSourceAsync(token))
                    return;
                var scopes = AutomaticQueueScopes();
                var run = _monitoring.Current;
                var playTarget = AudioEnabled;
                CallItem? newest;
                if (scopes.Length != 0 && _monitoring.CanRecover(run))
                {
                    var jump = await _durableQueue.JumpToLiveAsync(scopes, LocalRecoveryFloor, !playTarget, token,
                        // Returning to All retains a source edge for target selection.
                        // That edge does not narrow which recovery windows Jump retires.
                        skipOutstandingRecovery: _automaticAudioSource.Kind == LiveAudioSourceKind.All,
                        failed: row => failedRecording != null && TryGetDurableRecording(failedRecording, row.Scope, out var id) &&
                            id == row.Row.Id,
                        prepareBoundary: () => FlushLiveCompletionAsync(token),
                        automatic: AutomaticJumpFilter);
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
                    newest = SnapshotLiveCalls().Where(call => call.LiveArrivalSequence.HasValue &&
                        (!IsSelectedLiveAudioMode || AutomaticAudioIncludes(call)))
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
                if (traversal?.FavoritesOnly == true)
                    catchingUp = await ReadNextLiveRecordingAsync(token, traversal, sequence) != null;
                else
                {
                    var scopes = traversal == null ? AutomaticQueueScopes() : TraversalScopes(traversal);
                    catchingUp = traversal?.VisibleReplays.Any(card => card.LiveArrivalSequence > sequence) == true;
                    if (!catchingUp)
                        catchingUp = await _durableQueue.CountReadyAsync(scopes, LocalRecoveryFloor, sequence, token,
                            traversal == null ? _automaticAudioFilter : null) > 0;
                }
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
            Action? enteringNative = null, LivePlaybackScheduler.Choice? forecastFrom = null, Action? invalidateAudio = null,
            RecorderOperation? owner = null)
        {
            using var recorderLifetime = owner == null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Token);
            cancellationToken = recorderLifetime?.Token ?? cancellationToken;
            completion.Bind(owner);
            if (_playbackOwnership.Live is not { } live || !request.CanContinue || cancellationToken.IsCancellationRequested)
                return;

            var sourceRevision = _automaticAudioRevision;
            var preloadRevision = _automaticPreloads.Revision;
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
                    owner?.Validate();
                    RequireCurrentRecordingAccount(item);
                    if (!request.IsManual && _manualLiveTraversal == null && !AutomaticAudioIncludes(item))
                    {
                        request.Cancel();
                        token.ThrowIfCancellationRequested();
                    }

                    _nativeLiveRequest = request;
                    _nativeLiveCompletion = completion;
                    try
                    {
                        enteringNative?.Invoke();
                        completion.OnEnded(RetirePresentation);
                        completion.OnStarted(() => MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (!ReferenceEquals(_nativeLiveCompletion, completion) || !request.CanContinue ||
                                !_playbackOwnership.IsCurrent(live) || token.IsCancellationRequested || owner?.Token.IsCancellationRequested == true || completion.IsFinished)
                                return;
                            PublishActiveLiveCard(item, completion);
                            if (forecastFrom is { } choice && sourceRevision == _automaticAudioRevision &&
                                preloadRevision == _automaticPreloads.Revision)
                                StartAutomaticPreloads(choice, token, preloadRevision);
                        }));
                        await _audioPlaybackService.PlayAsync(url, rate, token, completion);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested && !completion.Confirmed &&
                        ex is not (OperationCanceledException or RecordingAccessException))
                    {
                        invalidateAudio?.Invoke();
                        throw;
                    }
                    finally
                    {
                        _automaticPreloads.Retire();
                        _nativeLiveCompletion = null;
                        _nativeLiveRequest = null;
                        if (!completion.IsFinished)
                            RetirePresentation();
#if IOS
                        OnIosAudioInterruption();
#endif
                    }
                }
                catch (Exception ex) when (completion.Confirmed)
                {
                    AppLog.DebugWriteLine(() => $"Audio teardown after confirmed playback ({ex.GetType().Name}).");
                }

                if (completion.Confirmed && !isCompletedReplay && scope != null &&
                    TryGetDurableRecording(item, scope, out var id))
                    saveCompletion = _playbackCommit.ConfirmAsync(scope, id);
            }, item.ServerId);

            if (saveCompletion != null)
                await saveCompletion;

            void RetirePresentation() => MainThread.BeginInvokeOnMainThread(() => ClearActiveLiveCard(completion));
        }

        private async Task FlushLiveCompletionAsync(CancellationToken token = default, Guid? recorderId = null)
        {
            await _playbackOwnership.WaitForLiveReleaseAsync(token, recorderId);
            await _playbackCommit.FlushAsync(token, recorderId);
        }

        private async Task<LiveStreamRecovery> StartRecorderRecoveryAsync(DurableLiveQueue.Run scope, DateTimeOffset since,
            CancellationToken token)
        {
            // A confirmed native end enters the completion queue before its player is released.
            // Finish that recorder's write while its previous journal owner can still accept it.
            await FlushLiveCompletionAsync(token, scope.ServerId).ConfigureAwait(false);
            return await LiveStreamRecovery.StartListeningAsync(_durableQueue, scope, since, token).ConfigureAwait(false);
        }
    }
}
