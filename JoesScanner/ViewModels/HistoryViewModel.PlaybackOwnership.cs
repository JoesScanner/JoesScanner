using System.Collections.Specialized;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.Dispatching;

namespace JoesScanner.ViewModels
{
    public sealed partial class HistoryViewModel
    {
        private readonly PlaybackOwnership _playbackOwnership;
        private readonly LivePlaybackRequests _historyPlaybackRequests = new();
        private PlaybackOwnership.Lease? _historyOwnership;
        private IDispatcher? _historyDispatcher;
        private int _historyStopRevision;

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

        private void OnHistoryCallsChanged(object? sender, NotifyCollectionChangedEventArgs change)
        {
            var lease = Volatile.Read(ref _historyOwnership);
            _ = RunOnMainThreadAsync(() =>
            {
                if (!OwnsHistoryPage(lease))
                    return;
                OnPropertyChanged(nameof(IsHistoryMediaButtonsEnabled));
                OnPropertyChanged(nameof(IsStopEnabled));
                RefreshCommandStates();
                if (change.NewItems == null)
                    return;
                foreach (var item in change.NewItems)
                    if (item is CallItem { HasDetectedAddress: true } call)
                        AddAddressAlertIfNew(call);
            });
        }

        private bool CanPublishHistoryPlayback(PlaybackOwnership.Lease lease, LivePlaybackRequests.Request request) =>
            OwnsHistoryPage(lease) && request.CanContinue;

        private Task PublishHistoryPlaybackAsync(PlaybackOwnership.Lease lease, LivePlaybackRequests.Request request, Action action) =>
            RunOnMainThreadAsync(() =>
            {
                if (CanPublishHistoryPlayback(lease, request))
                    action();
            });

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
                    _searchCts?.Cancel();
                }
                finally
                {
                    try { StopHistoryPlayback(closingPage: true); }
                    finally { _playbackOwnership.LeaveHistory(closing!); }
                }
            });
        }

        private Task PlayHistoryOnDeviceAsync(PlaybackOwnership.Lease lease, string url, double rate,
            CancellationToken token, PlaybackCompletion completion) =>
            _playbackOwnership.RunNativeAsync(lease, token, nativeToken =>
                _audioPlaybackService.PlayAsync(url, rate, nativeToken, completion));

        private Task StopAsync()
        {
            var lease = Volatile.Read(ref _historyOwnership);
            return RunOnMainThreadAsync(() =>
            {
                if (OwnsHistoryPage(lease))
                    StopHistoryPlayback();
            });
        }

        private void StopHistoryPlayback(bool closingPage = false)
        {
            Interlocked.Increment(ref _historyStopRevision);
            _historyPlaybackRequests.RetireCurrent();
            RetireNewerPage(closingPage);
            SetHistoryPlaybackStopped();
        }

        private sealed record HistoryPlaybackStart(LivePlaybackRequests.Request Request, int Index, bool IsSnapshot,
            IDisposable? PlatformPlayback);

        private HistoryPlaybackStart? StartHistoryPlayback(PlaybackOwnership.Lease lease, Func<int?> chooseIndex)
        {
            if (!OwnsHistoryPage(lease) || Calls.Count == 0 || chooseIndex() is not { } index)
                return null;
            if (!OwnsHistoryPage(lease) || Calls.Count == 0)
                return null;

            var request = _historyPlaybackRequests.Begin(manual: true, lease.Token);
            IDisposable? platformPlayback = null;
            try
            {
                RetireNewerPage();
#if ANDROID
                platformPlayback = SystemMediaService.BeginAndroidStandalonePlayback();
                JoesScanner.Platforms.Android.Services.AndroidAudioEnvironment.RequestPlay();
#elif IOS
                JoesScanner.Platforms.iOS.Services.IosAudioEnvironment.RequestPlay();
#endif
                _currentIndex = Math.Clamp(index, 0, Calls.Count - 1);
                ClearHistoryPlayingCards();
                _isQueuePlaybackRunning = true;
                OnPropertyChanged(nameof(IsHistoryPlaybackRunning));
                OnPropertyChanged(nameof(IsPlayEnabled));
                OnPropertyChanged(nameof(IsStopEnabled));
                OnPropertyChanged(nameof(ShowHistoryPlayButton));
                OnPropertyChanged(nameof(ShowHistoryStopButton));
                StatusText = "Playing";
                RefreshCommandStates();
                return new(request, _currentIndex, _isSnapshotSearchActive, platformPlayback);
            }
            catch
            {
                request.Dispose();
                platformPlayback?.Dispose();
                throw;
            }
        }

        private (CallItem Call, double Rate)? SelectHistoryRecording(int index, bool isSnapshot,
            PlaybackOwnership.Lease lease, LivePlaybackRequests.Request request)
        {
            if (!CanPublishHistoryPlayback(lease, request) || index >= Calls.Count)
                return null;

            _currentIndex = index;
            var item = _activeCall = Calls[index];
            OnPropertyChanged(nameof(ActiveCall));
            if (!isSnapshot)
                ScrollToIndex(index, ScrollToPosition.Start);
            if (!CanPublishHistoryPlayback(lease, request))
                return null;
            item.IsPlaying = true;
            return (item, GetPlaybackRate());
        }

        private async Task PlayHistoryQueueAsync(Func<int?> chooseIndex)
        {
            var stopRevision = Volatile.Read(ref _historyStopRevision);
            var lease = Volatile.Read(ref _historyOwnership);
            if (lease == null)
                return;
            var start = await ReadHistoryUiAsync(() => stopRevision == _historyStopRevision
                ? StartHistoryPlayback(lease, chooseIndex)
                : null).ConfigureAwait(false);
            if (start == null)
                return;

            var request = start.Request;
            var token = request.Token;
            try
            {
                for (var index = start.Index; !token.IsCancellationRequested; index++)
                {
                    var selection = await ReadHistoryUiAsync(() => SelectHistoryRecording(index, start.IsSnapshot, lease, request))
                        .ConfigureAwait(false);
                    if (selection is not { } selected)
                        break;

                    PlaybackOutcome outcome;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        outcome = await PlayAudioAsync(selected.Call, selected.Rate, lease, request).ConfigureAwait(false);
                    }
                    finally
                    {
                        await PublishHistoryPlaybackAsync(lease, request, () => selected.Call.IsPlaying = false)
                            .ConfigureAwait(false);
                    }

                    if (!outcome.AdvancesQueue() || token.IsCancellationRequested)
                        break;
                    if (!start.IsSnapshot && await ReadHistoryUiAsync(() =>
                        CanPublishHistoryPlayback(lease, request) && index >= Calls.Count - 1).ConfigureAwait(false))
                    {
                        if (await AppendNewerPageAsync(lease, request).ConfigureAwait(false) == 0)
                            break;
                        await PublishHistoryPlaybackAsync(lease, request, () => StatusText = "Playing").ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                await RunOnMainThreadAsync(() =>
                {
                    try
                    {
                        if (CanPublishHistoryPlayback(lease, request))
                            SetHistoryPlaybackStopped();
                    }
                    finally
                    {
                        request.Dispose();
                        start.PlatformPlayback?.Dispose();
                    }
                }).ConfigureAwait(false);
            }
        }

        private sealed record NewerPageLoad(PlaybackOwnership.Lease Lease, LivePlaybackRequests.Request? Playback,
            int Generation, HistorySearchFilters Filters, DateTime? From, DateTime? To, int Start, int Length,
            List<CallItem>? Prefetched);

        private NewerPageLoad? _newerPageLoad;

        private bool OwnsNewerPage(NewerPageLoad load) =>
            OwnsHistoryPage(load.Lease) && load.Generation == _searchGeneration &&
            (load.Playback == null || load.Playback.CanContinue);

        private void RetireNewerPage(bool closingPage = false)
        {
            if (_newerPageLoad == null || (!closingPage && _newerPageLoad.Playback == null))
                return;
            _newerPageLoad = null;
            _isPaging = false;
        }

        private async Task<int> AppendNewerPageAsync(PlaybackOwnership.Lease lease,
            LivePlaybackRequests.Request? playback = null)
        {
            var token = playback?.Token ?? lease.Token;
            var generation = await ReadHistoryUiAsync(() => _searchGeneration).ConfigureAwait(false);
            NewerPageLoad? load = null;
            var acquired = false;
            var appended = false;
            try
            {
                await _prefetchSemaphore.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                await RunOnMainThreadAsync(() =>
                {
                    if (!OwnsHistoryPage(lease) || generation != _searchGeneration || token.IsCancellationRequested ||
                        playback is { CanContinue: false } || !CanLoadMoreNewer || Calls.Count == 0 ||
                        _activeSearchFilters is not { } filters)
                        return;

                    var prefetched = _prefetchedNewerCalls;
                    var start = prefetched is { Count: > 0 }
                        ? _prefetchedNewerStartIndex : Math.Max(0, _activeStartIndex - _activeWindowSize);
                    if (_activeStartIndex <= start && prefetched is not { Count: > 0 })
                        return;

                    load = new(lease, playback, generation, filters, _activeDateFromLocal, _activeDateToLocal,
                        start, _activeStartIndex - start, prefetched);
                    _newerPageLoad = load;
                    _isPaging = true;
                    RefreshCommandStates();
                }).ConfigureAwait(false);
                if (load == null)
                    return 0;

                // The prefetch worker uses this same semaphore. A buffer still
                // queued behind us cannot be awaited while we own the lock.
                token.ThrowIfCancellationRequested();
                var page = load.Prefetched is { Count: > 0 } ? null :
                    await _callHistoryService.GetCallsPageAsync(load.Start, load.Length, load.Filters, load.From, load.To, token)
                        .ConfigureAwait(false);
                var calls = page?.Calls ?? load.Prefetched;
                if (calls is not { Count: > 0 })
                    return 0;

                return await ReadHistoryUiAsync(() =>
                {
                    if (!ReferenceEquals(_newerPageLoad, load) || !OwnsNewerPage(load) || token.IsCancellationRequested)
                        return 0;

                    _prefetchedNewerCalls = null;
                    if (page is { TotalMatches: > 0 })
                        _activeTotalMatches = page.TotalMatches;

                    var inWindow = calls.Where(IsWithinHistoryWindow).OrderBy(call => call.Timestamp).ToList();
                    if (inWindow.Count == 0)
                    {
                        _historyUpperLimitReached = true;
                        StatusText = "Reached the end of the selected date range.";
                        return 0;
                    }

                    var last = Calls.LastOrDefault()?.Timestamp;
                    var added = inWindow.Where(call => last == null || call.Timestamp > last.Value).ToList();
                    foreach (var call in added)
                        Calls.Add(call);
                    if (added.Count == 0)
                        return 0;

                    _activeStartIndex = load.Start;
                    StartAddressDetectionForHistoryCalls(added);
                    StatusText = $"{_activeTotalMatches} match(es), showing {Calls.Count}";
                    appended = true;
                    return added.Count;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine(() => $"Appending History during playback failed ({ex.GetType().Name}).");
                return 0;
            }
            finally
            {
                try
                {
                    if (load != null)
                        await RunOnMainThreadAsync(() =>
                        {
                            if (!ReferenceEquals(_newerPageLoad, load))
                                return;
                            _newerPageLoad = null;
                            _isPaging = false;
                            if (OwnsNewerPage(load))
                            {
                                RefreshCommandStates();
                                if (appended)
                                    StartPrefetchNewerIfNeeded();
                            }
                        }).ConfigureAwait(false);
                }
                finally
                {
                    if (acquired)
                        _prefetchSemaphore.Release();
                }
            }
        }
    }
}
