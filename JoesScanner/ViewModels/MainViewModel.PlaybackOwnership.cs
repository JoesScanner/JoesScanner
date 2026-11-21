using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels
{
    public partial class MainViewModel
    {
        private void OnPlaybackOwnershipChanged()
        {
            if (_playbackOwnership.Live == null)
            {
                _resumeAfterHistory = IsRunning && AudioEnabled &&
                    (_isQueuePlaybackRunning || _currentPlayingCall != null);
                InvalidateLivePlayback();
                UpdateQueueDerivedState();
                return;
            }

            var resume = _resumeAfterHistory;
            _resumeAfterHistory = false;
            if (IsRunning && AudioEnabled && (resume || _settingsService.AutoPlay || _userRunShouldDriveQueue))
                _ = RequestQueuePlaybackAsync("HistoryReleasedAudio", userInitiated: resume || _userRunShouldDriveQueue);
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
            using var selection = CancellationTokenSource.CreateLinkedTokenSource(run!.Token, live.Token);
            var token = selection.Token;
            var aborted = false;
            var hadFailure = false;

            LastQueueEvent = "Queue playback started";

            try
            {
                // A rapidly reopened Live queue must read after a completed
                // clip from the previous lease has saved its disposition.
                await FlushLiveCompletionAsync(token);
                while (OwnsSelection() && AudioEnabled && !IsLiveInterruptionBlockingPlayback)
                {
                    var beforeRead = _queueWake.BeforeRead();
                    var next = durable == null ? GetNextQueuedCall() :
                        await GetNextDurableCallAsync(durable, token, version);
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
                    if (outcome == PlaybackOutcome.Withheld && request.CanContinue)
                        continue;

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

        private async Task PlayOnDeviceAsync(CallItem item, string url, double rate,
            LivePlaybackRequests.Request request, CancellationToken cancellationToken, PlaybackCompletion completion)
        {
            if (_playbackOwnership.Live is not { } live || !request.CanContinue || cancellationToken.IsCancellationRequested)
                return;

            await _liveCompletionGate.WaitAsync(cancellationToken);
            try
            {
                await _playbackCommit.FlushAsync();
                if (!_playbackOwnership.IsCurrent(live) || !request.CanContinue || cancellationToken.IsCancellationRequested)
                    return;

                var scope = _durableRun;
                try
                {
                    await _playbackOwnership.RunNativeAsync(live, cancellationToken,
                        token =>
                        {
                            RequireCurrentRecordingAccount(item);
                            return _audioPlaybackService.PlayAsync(url, rate, token, completion);
                        });
                }
                catch (Exception ex) when (completion.Confirmed)
                {
                    AppLog.DebugWriteLine(() => $"Audio teardown after confirmed playback ({ex.GetType().Name}).");
                }

                if (completion.Confirmed && scope != null && TryGetDurableRecording(item, scope, out var id))
                    await _playbackCommit.ConfirmAsync(scope, id);
            }
            finally
            {
                _liveCompletionGate.Release();
            }
        }

        private async Task FlushLiveCompletionAsync(CancellationToken token = default)
        {
            await _liveCompletionGate.WaitAsync(token);
            try { await _playbackCommit.FlushAsync(); }
            finally { _liveCompletionGate.Release(); }
        }
    }
}
