using System.Collections.ObjectModel;
using JoesScanner.Models;
using JoesScanner.Services;

// These partial fixtures provide the UI state and the test-controlled I/O
// around the linked production ViewModel playback and page-ownership code.
namespace JoesScanner.ViewModels
{
    public partial class MainViewModel : BindableObject
    {
        private const string Server = "https://scanner.example";
        private static readonly DateTimeOffset Recorded = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
        private readonly PlaybackOwnership _playbackOwnership;
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly MonitoringLifecycle _monitoring = new();
        private readonly LivePlaybackRequests _playbackRequests = new();
        private readonly LivePlaybackRequests _transportLookups = new();
        private readonly LiveQueueWake _queueWake = new();
        private readonly SemaphoreSlim _liveCompletionGate = new(1);
        private readonly DurableLiveQueue _durableQueue;
        private readonly DurablePlaybackCommit _playbackCommit;
        private readonly TestSettings _settingsService = new();
        private DurableLiveQueue.Run? _durableRun;
        private CallItem? _currentPlayingCall;
        private CallItem? _manualPlaybackTarget;
        private readonly CallItem? _retryPlaybackCall = null;
        private readonly bool _userRunShouldDriveQueue = false;
        private bool _resumeAfterHistory;
        private bool _isQueuePlaybackRunning;
        private int _queuePlaybackVersion;
        private DelayedSelection? _nextSelection;

        internal int ResumeRequests { get; private set; }
        internal Task ResumedQueue { get; private set; } = Task.CompletedTask;
        internal Func<Task>? BeforeCommitForTest { get; set; }

        private bool IsRunning => _monitoring.CanRecover(_monitoring.Current);
        private bool AudioEnabled => true;
        private string LastQueueEvent { get; set; } = string.Empty;

        internal MainViewModel(PlaybackOwnership ownership, HandoffAudio player, string path)
        {
            _playbackOwnership = ownership;
            _audioPlaybackService = player;
            _durableQueue = new DurableLiveQueue(new TestPaths(path));
            _playbackCommit = new DurablePlaybackCommit(async (run, id) =>
            {
                if (BeforeCommitForTest is { } delay)
                    await delay();
                await _durableQueue.CommitCompletionAsync(run, id);
            });
            _playbackOwnership.Changed += OnPlaybackOwnershipChanged;
        }

        internal async Task StartForTestAsync(string? account = null)
        {
            var run = _monitoring.TryBeginStart()!;
            _durableRun = new DurableLiveQueue.Run(Server, run.Id, null, Protected: account != null, Account: account);
            await _durableQueue.OpenAsync(_durableRun, Recorded, default);
            _monitoring.Activate(run);
        }

        internal Task AddForTestAsync(string id, bool later = false) =>
            _durableQueue.ObserveAsync(_durableRun!, id, later ? Recorded.AddSeconds(1) : Recorded,
                new LiveCallSnapshot(new(), null, CallCardIdFallback.None, Server + "/" + id + ".mp3"),
                default, RecordingOrigin.Live);

        internal Task<bool> IsPendingForTestAsync(string id) => _durableQueue.IsPendingAsync(_durableRun!, id, 0, default);

        internal async Task AssertPendingAfterRestartAsync(string id)
        {
            var replacement = _durableRun! with { Owner = _durableRun.Owner + 1 };
            await _durableQueue.OpenAsync(replacement, Recorded, default);
            LiveHistoryHandoffTests.Check(await _durableQueue.IsPendingAsync(replacement, id, 0, default),
                "Stopping before native completion must preserve the pending SQLite row across restart.");
        }

        internal Task StopForTestAsync() => _monitoring.StopAsync(_ =>
        {
            _resumeAfterHistory = false;
            InvalidateLivePlayback();
        }, _ => { }, _ => _durableQueue.StopAsync(_durableRun!));

        internal DelayedSelection HoldNextSelection() => _nextSelection = new();

        internal Task StartQueueForTestAsync() => EnsureQueuePlaybackAsync(_playbackOwnership.Live!);

        private Task RequestQueuePlaybackAsync(string reason, bool userInitiated)
        {
            LiveHistoryHandoffTests.Check(reason == "HistoryReleasedAudio" && userInitiated,
                "An interrupted queue should resume after History releases its lease.");
            ++ResumeRequests;
            return ResumedQueue = StartQueueForTestAsync();
        }

        private async Task<CallItem?> GetNextDurableCallAsync(DurableLiveQueue.Run durable, CancellationToken token, int version)
        {
            var delay = Interlocked.Exchange(ref _nextSelection, null);
            var row = await _durableQueue.NextAsync(durable, 0, token);
            if (delay != null)
            {
                delay.Reached.TrySetResult();
                // Model a query that finished just before cancellation but whose
                // result hasn't reached the UI dispatcher yet.
                await delay.Return.Task;
            }
            return row?.ToCallItem(durable.Server);
        }

        private async Task<PlaybackOutcome> PlayAudioAsync(CallItem item, LivePlaybackRequests.Request request)
        {
            _currentPlayingCall = item;
            var completion = new PlaybackCompletion();
            return await PlaybackAttempt.RunAsync(
                () => PlayOnDeviceAsync(item, item.BackendId, 1, request, request.Token, completion),
                request.Token, failure => throw new InvalidOperationException("Live handoff failed.", failure), completion);
        }

        private static bool TryGetDurableRecording(CallItem item, DurableLiveQueue.Run run, out string id, out DateTimeOffset time)
        {
            id = item.BackendId;
            time = item.RecordingTimeUtc.GetValueOrDefault();
            return item.RecordingServer == run.Server && item.RecordingTimeUtc.HasValue && run.Owns(item.RecordingAccess);
        }

        private bool IsCurrentLiveScope(DurableLiveQueue.Run run) => run == _durableRun;

        private string? RequireCurrentRecordingAccount(CallItem item) =>
            _durableRun?.Owns(item.RecordingAccess) == true ? _durableRun.Account :
                throw new InvalidOperationException("The recording belongs to a retired account.");

        private static CallItem? GetNextQueuedCall() => null;
        private static void ClearPrefetchedPlayableUrl(bool deleteCompletedTempFile) { }
        private void ClearPlayingSelection() => _currentPlayingCall = null;
        private void UpdateQueueDerivedState()
        {
            LiveHistoryHandoffTests.Check(_manualPlaybackTarget == null, "There is no manual target in this fixture.");
        }

        internal sealed class DelayedSelection
        {
            internal TaskCompletionSource Reached { get; } = LiveHistoryHandoffTests.Signal();
            internal TaskCompletionSource Return { get; } = LiveHistoryHandoffTests.Signal();
        }

        private sealed record TestPaths(string DbPath) : IDatabasePathProvider;
        private sealed class TestSettings { internal bool AutoPlay => false; }
    }

    public sealed partial class HistoryViewModel : BindableObject
    {
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly CancellationTokenSource? _searchCts = null;
        private readonly ObservableCollection<CallItem> Calls = [new CallItem { BackendId = "history" }];
        private readonly HistoryPagingStub _callHistoryService = new();
        private readonly SemaphoreSlim _prefetchSemaphore = new(1);
        private readonly HistorySearchFilters _activeSearchFilters = new();
        private readonly DateTime? _activeDateFromLocal = null;
        private readonly DateTime? _activeDateToLocal = null;
        private readonly int _activeWindowSize = 1;
        private readonly int _searchGeneration = 1;
        private readonly int _prefetchedNewerStartIndex = 0;
        private List<CallItem>? _prefetchedNewerCalls;
        private int _activeStartIndex;
        private int _activeTotalMatches;
        private bool _historyUpperLimitReached;
        private bool _isPaging;
        private bool _isSnapshotSearchActive = true;
        private bool _isQueuePlaybackRunning;
        private int _currentIndex;
        private CallItem? _activeCall;
        private string _statusText = string.Empty;
        private CallItem? ActiveCall => _activeCall;
        private bool IsHistoryPlaybackRunning => _isQueuePlaybackRunning;
        private bool IsHistoryMediaButtonsEnabled => _isQueuePlaybackRunning || Calls.Count > 0;
        private bool IsPlayEnabled => !_isQueuePlaybackRunning;
        private bool IsStopEnabled => _isQueuePlaybackRunning;
        private bool ShowHistoryPlayButton => IsPlayEnabled;
        private bool ShowHistoryStopButton => IsStopEnabled;
        private bool CanLoadMoreNewer => !_isSnapshotSearchActive && !_isPaging &&
            !_historyUpperLimitReached && _activeStartIndex > 0;
        private string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }

        internal ObservableCollection<CallItem> CallsForTest => Calls;
        internal bool IsRunningForTest => IsHistoryPlaybackRunning;

        internal HistoryViewModel(PlaybackOwnership ownership, HandoffAudio player)
        {
            _playbackOwnership = ownership;
            _audioPlaybackService = player;
            Calls.CollectionChanged += OnHistoryCallsChanged;
        }

        internal void OpenForTest() => TakeHistoryPlaybackOwnership();
        internal Task PlayForTestAsync() => PlayHistoryQueueAsync(() => 0);
        internal Task StopForTestAsync() => StopAsync();

        internal void EnablePagingForTest(Func<CancellationToken, Task<HistoryCallsPage>> fetch)
        {
            _isSnapshotSearchActive = false;
            _activeStartIndex = 1;
            _callHistoryService.Fetch = fetch;
        }

        internal Task HoldPagingGateForTestAsync() => _prefetchSemaphore.WaitAsync();
        internal void ReleasePagingGateForTest() => _prefetchSemaphore.Release();

        private async Task<PlaybackOutcome> PlayAudioAsync(CallItem item, double rate, PlaybackOwnership.Lease lease,
            LivePlaybackRequests.Request request)
        {
            var completion = new PlaybackCompletion();
            return await PlaybackAttempt.RunAsync(
                () => PlayHistoryOnDeviceAsync(lease, item.BackendId, rate, request.Token, completion),
                request.Token, failure => throw new InvalidOperationException("History handoff failed.", failure), completion);
        }

        private static double GetPlaybackRate() => 1;
        private static bool IsWithinHistoryWindow(CallItem call) => true;
        private static void AddAddressAlertIfNew(CallItem call) { }
        private static void StartAddressDetectionForHistoryCalls(IReadOnlyList<CallItem> calls) { }
        private static void StartPrefetchNewerIfNeeded() { }
        private static void ScrollToIndex(int index, ScrollToPosition position) { }
        private void ClearHistoryPlayingCards()
        {
            foreach (var call in Calls)
                call.IsPlaying = false;
        }

        private void RefreshCommandStates() => OnPropertyChanged(nameof(CanLoadMoreNewer));

        private void SetHistoryPlaybackStopped()
        {
            _isQueuePlaybackRunning = false;
            ClearHistoryPlayingCards();
            OnPropertyChanged(nameof(IsHistoryPlaybackRunning));
        }

        private sealed class HistoryPagingStub
        {
            internal Func<CancellationToken, Task<HistoryCallsPage>> Fetch { get; set; } =
                _ => throw new InvalidOperationException("No History paging fixture configured.");

            internal Task<HistoryCallsPage> GetCallsPageAsync(int start, int length, HistorySearchFilters filters,
                DateTime? from, DateTime? to, CancellationToken token) => Fetch(token);
        }
    }

    internal enum ScrollToPosition { Start }
}
