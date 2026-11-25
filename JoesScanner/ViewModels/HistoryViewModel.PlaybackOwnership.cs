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
                    RetireHistorySearchRequests();
                }
                finally
                {
                    try { StopHistoryPlayback(); }
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

        private void StopHistoryPlayback()
        {
            Interlocked.Increment(ref _historyStopRevision);
            _historyPlaybackRequests.RetireCurrent();
            SetHistoryPlaybackStopped();
        }

        private sealed record HistoryPlaybackStart(LivePlaybackRequests.Request Request, CallItem First,
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
#if ANDROID
                platformPlayback = SystemMediaService.BeginAndroidStandalonePlayback();
                JoesScanner.Platforms.Android.Services.AndroidAudioEnvironment.RequestPlay();
#elif IOS
                JoesScanner.Platforms.iOS.Services.IosAudioEnvironment.RequestPlay();
#endif
                var first = Calls[Math.Clamp(index, 0, Calls.Count - 1)];
                ClearHistoryPlayingCards();
                _isQueuePlaybackRunning = true;
                OnPropertyChanged(nameof(IsHistoryPlaybackRunning));
                OnPropertyChanged(nameof(IsPlayEnabled));
                OnPropertyChanged(nameof(IsStopEnabled));
                OnPropertyChanged(nameof(ShowHistoryPlayButton));
                OnPropertyChanged(nameof(ShowHistoryStopButton));
                StatusText = "Playing";
                RefreshCommandStates();
                return new(request, first, platformPlayback);
            }
            catch
            {
                request.Dispose();
                platformPlayback?.Dispose();
                throw;
            }
        }

        private (CallItem Call, double Rate)? SelectHistoryRecording(CallItem anchor, bool advance,
            PlaybackOwnership.Lease lease, LivePlaybackRequests.Request request)
        {
            if (!CanPublishHistoryPlayback(lease, request))
                return null;

            // Keep traversal anchored to the actual card when a manually loaded
            // page inserts older recordings into the newest-first display.
            var index = Calls.IndexOf(anchor) - (advance ? 1 : 0);
            if (index < 0)
                return null;

            var item = _activeCall = Calls[index];
            OnPropertyChanged(nameof(ActiveCall));
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
                var anchor = start.First;
                var advance = false;
                while (!token.IsCancellationRequested)
                {
                    var selection = await ReadHistoryUiAsync(() => SelectHistoryRecording(anchor, advance, lease, request))
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
                    anchor = selected.Call;
                    advance = true;
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
    }
}
