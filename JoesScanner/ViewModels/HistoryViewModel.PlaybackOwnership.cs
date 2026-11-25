using System.Collections.Specialized;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.Dispatching;

namespace JoesScanner.ViewModels
{
    public sealed partial class HistoryViewModel
    {
        private const string HistoryPlayingStatus = "Playing";
        private const string HistoryPreparingStatus = "Preparing recording…";
        private readonly PlaybackOwnership _playbackOwnership;
        private readonly LivePlaybackRequests _historyPlaybackRequests = new();
        private readonly LivePlaybackRequests _historySelectionRequests = new();
        private PlaybackOwnership.Lease? _historyOwnership;
        private IDispatcher? _historyDispatcher;
        private int _historyStopRevision;

        private enum HistoryNavigation { Play, Recording, Previous, Next }

        private sealed record HistoryPlayback(PlaybackOwnership.Lease Lease, HistorySearchSnapshot Snapshot,
            LivePlaybackRequests.Request Request);

        private sealed record HistorySelection(HistoryPlayback Playback, CallItem? Target, CallItem? PageAnchor,
            Task<bool>? PendingPage, IDisposable? Foreground);

        internal void UseHistoryDispatcher(IDispatcher dispatcher) => _historyDispatcher = dispatcher;

        private Task<T> ReadHistoryUiAsync<T>(Func<T> action)
        {
            if (_historyDispatcher is { } dispatcher)
                return dispatcher.IsDispatchRequired ? dispatcher.DispatchAsync(action) : Task.FromResult(action());
            return MainThread.IsMainThread ? Task.FromResult(action()) : MainThread.InvokeOnMainThreadAsync(action);
        }

        private Task RunOnMainThreadAsync(Action action)
        {
            if (_historyDispatcher is { } dispatcher)
            {
                if (dispatcher.IsDispatchRequired)
                    return dispatcher.DispatchAsync(action);
            }
            else if (!MainThread.IsMainThread)
                return MainThread.InvokeOnMainThreadAsync(action);

            action();
            return Task.CompletedTask;
        }

        private void TakeHistoryPlaybackOwnership() =>
            _historyOwnership ??= _playbackOwnership.EnterHistory();

        private bool OwnsHistoryPage(PlaybackOwnership.Lease? lease) =>
            lease != null && ReferenceEquals(_historyOwnership, lease) && _playbackOwnership.IsCurrent(lease);

        private bool OwnsHistoryPlayback(HistoryPlayback playback) =>
            playback.Request.CanContinue && OwnsHistoryPage(playback.Lease) &&
            ReferenceEquals(_historyResults?.Snapshot, playback.Snapshot) &&
            _historyRecorder == playback.Snapshot.Access.Recorder;

        private Task PublishHistoryPlaybackAsync(HistoryPlayback playback, Action action) => RunOnMainThreadAsync(() =>
        {
            if (OwnsHistoryPlayback(playback))
                action();
        });

        private void OnHistoryCallsChanged(object? sender, NotifyCollectionChangedEventArgs change)
        {
            var lease = Volatile.Read(ref _historyOwnership);
            _ = RunOnMainThreadAsync(() =>
            {
                if (!OwnsHistoryPage(lease))
                    return;
                RefreshCommandStates();
                if (change.NewItems == null)
                    return;
                foreach (var item in change.NewItems)
                    if (item is CallItem { HasDetectedAddress: true } call)
                        AddAddressAlertIfNew(call);
            });
        }

        public Task OnPageClosedAsync()
        {
            var closing = Volatile.Read(ref _historyOwnership);
            return RunOnMainThreadAsync(() =>
            {
                if (!OwnsHistoryPage(closing))
                    return;
                _historyOwnership = null;
                try
                {
                    RetireHistorySearchRequests();
                    StopHistoryPlayback();
                }
                finally
                {
                    _playbackOwnership.LeaveHistory(closing!);
                }
            });
        }

        private Task StopAsync()
        {
            var lease = Volatile.Read(ref _historyOwnership);
            return RunOnMainThreadAsync(() =>
            {
                if (OwnsHistoryPage(lease))
                    StopHistoryPlayback();
            });
        }

        private void StopHistoryPlayback()
        {
            Interlocked.Increment(ref _historyStopRevision);
            _historySelectionRequests.RetireCurrent();
            _historyPlaybackRequests.RetireCurrent();
            if (_isPaging)
                RetireHistoryPageRequest();
            SetHistoryPlaybackStopped();
        }

        private void SetHistoryPlaybackStopped()
        {
            if (_activeCall != null)
                _activeCall.IsPlaying = false;
            _isQueuePlaybackRunning = false;
            if (StatusText == HistoryPlayingStatus || StatusText == HistoryPreparingStatus)
                StatusText = _historySelectionRequests.HasManualSelection ? HistoryPreparingStatus :
                    _historyPageError ?? HistoryResultStatus();
            NotifyHistoryPlaybackState();
        }

        private void NotifyHistoryPlaybackState()
        {
            OnPropertyChanged(nameof(IsHistoryPlaybackRunning));
            OnPropertyChanged(nameof(ShowHistoryPlayButton));
            OnPropertyChanged(nameof(ShowHistoryStopButton));
            RefreshCommandStates();
        }

        private int ActiveHistoryIndex => _activeCall == null ? -1 : Calls.IndexOf(_activeCall);
        private bool CanNavigateHistory => !IsLoading && HasHistoryResults && OwnsHistoryPage(_historyOwnership);
        private bool CanGoHistoryPrevious => CanNavigateHistory &&
            (ActiveHistoryIndex < Calls.Count - 1 || _historyResults?.HasMore == true);
        private bool CanGoHistoryNext => CanNavigateHistory && ActiveHistoryIndex > 0;

        private CallItem? GetHistoryCard(int index) => index >= 0 && index < Calls.Count ? Calls[index] : null;

        private CallItem? AdjacentHistoryCard(CallItem anchor, int direction)
        {
            var index = Calls.IndexOf(anchor);
            return index < 0 ? null : GetHistoryCard(index + direction);
        }

        private HistorySelection? BeginHistorySelection(PlaybackOwnership.Lease lease, int stopRevision,
            HistoryNavigation navigation, CallItem? recording)
        {
            if (stopRevision != _historyStopRevision || !OwnsHistoryPage(lease) || !CanNavigateHistory ||
                _historyResults is not { } results)
                return null;

            var index = ActiveHistoryIndex;
            var target = navigation switch
            {
                HistoryNavigation.Recording => recording != null && Calls.Contains(recording) ? recording : null,
                HistoryNavigation.Play => !_isQueuePlaybackRunning ? GetHistoryCard(index >= 0 ? index : Calls.Count - 1) : null,
                HistoryNavigation.Previous => GetHistoryCard(index + 1),
                HistoryNavigation.Next => GetHistoryCard(index - 1),
                _ => null
            };
            var pageAnchor = navigation == HistoryNavigation.Previous && target == null && results.HasMore ? _activeCall : null;
            if (target == null && pageAnchor == null)
                return null;

            var request = _historySelectionRequests.Begin(manual: true, lease.Token);
            IDisposable? foreground = null;
            try
            {
#if ANDROID
                // Hold Android foreground eligibility from the user's action,
                // while audio preparation leaves the existing player alone.
                foreground = SystemMediaService.BeginAndroidStandalonePlayback();
#endif
                if (!_isQueuePlaybackRunning && !_isPaging)
                    StatusText = HistoryPreparingStatus;
                NotifyHistoryPlaybackState();
                return new(new(lease, results.Snapshot, request), target, pageAnchor,
                    pageAnchor == null ? null : ReadOlderHistoryPageAsync(), foreground);
            }
            catch
            {
                request.Dispose();
                foreground?.Dispose();
                throw;
            }
        }

        private async Task SelectHistoryRecordingAsync(HistoryNavigation navigation, CallItem? recording = null)
        {
            var stopRevision = Volatile.Read(ref _historyStopRevision);
            var lease = Volatile.Read(ref _historyOwnership);
            if (lease == null)
                return;
            var selection = await ReadHistoryUiAsync(() => BeginHistorySelection(lease, stopRevision, navigation, recording))
                .ConfigureAwait(false);
            if (selection == null)
                return;

            var preparation = selection.Playback;
            var token = preparation.Request.Token;
            PlaybackAudioLease? prepared = null;
            try
            {
                var target = selection.Target;
                if (selection.PendingPage is { } page)
                {
                    if (!await page.WaitAsync(token).ConfigureAwait(false))
                        return;
                    target = await ReadHistoryUiAsync(() =>
                    {
                        if (!OwnsHistoryPlayback(preparation))
                            return null;
                        var older = AdjacentHistoryCard(selection.PageAnchor!, 1);
                        if (older == null)
                            StatusText = "No older recording on this page.";
                        return older;
                    }).ConfigureAwait(false);
                }
                if (target == null)
                    return;

                prepared = await PrepareHistorySelectionAsync(preparation, target).ConfigureAwait(false);
                var traversal = await ReadHistoryUiAsync(() => CommitHistorySelection(preparation, target))
                    .ConfigureAwait(false);
                if (traversal != null)
                {
                    var first = prepared;
                    prepared = null;
                    await PlayHistoryQueueAsync(traversal, target, first).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await PublishHistoryPlaybackAsync(preparation, () => ReportHistorySelectionFailure(ex)).ConfigureAwait(false);
            }
            finally
            {
                prepared?.Dispose();
                await RunOnMainThreadAsync(() =>
                {
                    var ownsPreparation = OwnsHistoryPlayback(preparation);
                    preparation.Request.Dispose();
                    selection.Foreground?.Dispose();
                    if (ownsPreparation)
                    {
                        if (StatusText == HistoryPreparingStatus)
                            StatusText = _historyPageError ?? HistoryResultStatus();
                        NotifyHistoryPlaybackState();
                    }
                }).ConfigureAwait(false);
            }
        }

        private HistoryPlayback? CommitHistorySelection(HistoryPlayback selection, CallItem target)
        {
            if (!OwnsHistoryPlayback(selection) || !Calls.Contains(target))
                return null;

            // Preparation and authorization must succeed before retiring the
            // existing traversal, even when the target needs an older page.
            RequireHistoryRecordingAccess(selection.Snapshot, target);
#if ANDROID
            JoesScanner.Platforms.Android.Services.AndroidAudioEnvironment.RequestPlay();
#elif IOS
            JoesScanner.Platforms.iOS.Services.IosAudioEnvironment.RequestPlay();
#endif
            selection.Request.Dispose();
            var request = _historyPlaybackRequests.Begin(manual: true, selection.Lease.Token);
            if (_activeCall != null)
                _activeCall.IsPlaying = false;
            _isQueuePlaybackRunning = true;
            if (!_isPaging)
                StatusText = _historyPageError ?? HistoryPlayingStatus;
            NotifyHistoryPlaybackState();
            return new(selection.Lease, selection.Snapshot, request);
        }

        private async Task<PlaybackAudioLease> PrepareHistorySelectionAsync(HistoryPlayback playback, CallItem call)
        {
            var token = playback.Request.Token;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            if (!await ReadHistoryUiAsync(() => OwnsHistoryPlayback(playback)).ConfigureAwait(false))
                throw new OperationCanceledException(token);

            var prepared = await AcquireHistoryRecordingAsync(playback.Snapshot, call, deadline.Token).ConfigureAwait(false);
            if (deadline.IsCancellationRequested)
            {
                prepared.Dispose();
                deadline.Token.ThrowIfCancellationRequested();
            }
            return prepared;
        }

        private async Task PlayHistoryQueueAsync(HistoryPlayback playback, CallItem first, PlaybackAudioLease firstAudio)
        {
            var token = playback.Request.Token;
            PlaybackAudioLease? prepared = firstAudio;
            try
            {
                CallItem? next = first;
                while (next != null && !token.IsCancellationRequested)
                {
                    var call = next;
                    var audio = prepared;
                    prepared = null;
                    var outcome = await PlayHistoryRecordingAsync(playback, call, audio).ConfigureAwait(false);
                    if (outcome is PlaybackOutcome.Canceled or PlaybackOutcome.Withheld)
                        break;

                    // Every result is visited once per traversal. Failed recordings
                    // are skipped and chronological playback never needs an older page.
                    next = await ReadHistoryUiAsync(() => OwnsHistoryPlayback(playback)
                        ? AdjacentHistoryCard(call, -1) : null).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await PublishHistoryPlaybackAsync(playback, () => ReportHistorySelectionFailure(ex)).ConfigureAwait(false);
            }
            finally
            {
                prepared?.Dispose();
                await RunOnMainThreadAsync(() =>
                {
                    try
                    {
                        if (OwnsHistoryPlayback(playback))
                            SetHistoryPlaybackStopped();
                    }
                    finally
                    {
                        playback.Request.Dispose();
                    }
                }).ConfigureAwait(false);
            }
        }

        private async Task<PlaybackOutcome> PlayHistoryRecordingAsync(HistoryPlayback playback, CallItem call, PlaybackAudioLease? audio)
        {
            var token = playback.Request.Token;
            var completion = new PlaybackCompletion();
            Exception? failure = null;
            CallItem? previous = null;
            var published = false;
            try
            {
                var outcome = await PlaybackAttempt.RunAsync(async () =>
                {
                    audio ??= await PrepareHistorySelectionAsync(playback, call).ConfigureAwait(false);
                    var rate = await ReadHistoryUiAsync<double?>(() =>
                    {
                        if (!OwnsHistoryPlayback(playback) || !Calls.Contains(call))
                            return null;

                        RequireHistoryRecordingAccess(playback.Snapshot, call);
                        previous = _activeCall;
                        _activeCall = call;
                        published = true;
                        call.IsPlaying = true;
                        OnPropertyChanged(nameof(ActiveCall));
                        ScrollToIndex(Calls.IndexOf(call), ScrollToPosition.Start);
                        RefreshCommandStates();
                        return GetPlaybackRate();
                    }).ConfigureAwait(false);
                    if (rate == null)
                        throw new OperationCanceledException(token);

                    await RunAuthorizedHistoryAudioAsync(playback.Snapshot, call, token, authorizedToken =>
                        _playbackOwnership.RunNativeAsync(playback.Lease, authorizedToken, async nativeToken =>
                    {
                        RequireHistoryRecordingAccess(playback.Snapshot, call);
                        try
                        {
                            await _audioPlaybackService.PlayAsync(audio.Url, rate.Value, nativeToken, completion).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!nativeToken.IsCancellationRequested && !completion.Confirmed &&
                            ex is not (OperationCanceledException or RecordingAccessException))
                        {
                            audio.Invalidate();
                            throw;
                        }
                    })).ConfigureAwait(false);
                }, token, ex => failure = ex, completion).ConfigureAwait(false);

                if (failure != null)
                {
                    await PublishHistoryPlaybackAsync(playback, () =>
                    {
                        if (published && ReferenceEquals(_activeCall, call))
                        {
                            call.IsPlaying = false;
                            _activeCall = previous;
                            OnPropertyChanged(nameof(ActiveCall));
                            RefreshCommandStates();
                        }
                        if (failure is RecordingAccessException or FirewallAuthenticationException)
                            StatusText = failure.Message;
                        else if (!_isPaging && _historyPageError == null)
                            StatusText = "Skipped an unavailable recording.";
                        AppLog.DebugWriteLine(() => $"History playback failed ({failure.GetType().Name}).");
                    }).ConfigureAwait(false);
                }
                return failure is RecordingAccessException or FirewallAuthenticationException ? PlaybackOutcome.Withheld : outcome;
            }
            finally
            {
                audio?.Dispose();
                await PublishHistoryPlaybackAsync(playback, () => call.IsPlaying = false).ConfigureAwait(false);
            }
        }

        private void ReportHistorySelectionFailure(Exception failure)
        {
            AppLog.DebugWriteLine(() => $"History recording could not be prepared ({failure.GetType().Name}).");
            StatusText = failure switch
            {
                RecordingAccessException or FirewallAuthenticationException => failure.Message,
                OperationCanceledException => "Recording preparation timed out. Try again.",
                _ => _isQueuePlaybackRunning ? "Recording unavailable. Playback continues." : "Recording unavailable. Try another."
            };
        }
    }
}
