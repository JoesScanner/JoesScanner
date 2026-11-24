using System.Collections.ObjectModel;
using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.Services
{
    internal sealed class RecordingAccessException(string message) : InvalidOperationException(message);
}

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
        private readonly DurableLiveQueue _durableQueue;
        private readonly DurablePlaybackCommit _playbackCommit;
        private readonly TestSettings _settingsService = new();
        private readonly TestFilter _filterService = new();
        private readonly TestCallStream _callStreamService = new();
        private readonly List<CallItem> Calls = [];
        private readonly List<CallItem> AddressAlerts = [];
        private readonly List<CallItem> _hiddenAddressAlerts = [];
        private bool HasAddressAlerts => AddressAlerts.Count != 0;
        private DurableLiveQueue.Run? _durableRun;
        private CallItem? _currentPlayingCall;
        private CallItem? _manualPlaybackTarget;
        private CallItem? _retryPlaybackCall;
        private CallItem? _lastPlayedCall;
        private CallItem? _lastTransportCall;
        private bool _userRunShouldDriveQueue;
        private bool _isQueuePlaybackRunning;
        private bool IsLiveInterruptionBlockingPlayback => false;
        private int _queuePlaybackVersion;
        private int _audioToggleSerial;
        private int _refreshingCallMetadata;
        private bool HasEnabledScannerConnections() => Uri.TryCreate(_settingsService.ServerUrl, UriKind.Absolute, out _);
        private DelayedSelection? _nextSelection;

        internal int ResumeRequests { get; private set; }
        internal Task ResumedQueue { get; private set; } = Task.CompletedTask;
        internal Func<Task>? BeforeCommitForTest { get; set; }
        internal Func<string, CancellationToken, Task>? PrepareForTest { get; set; }
        internal string? RetryForTest => _retryPlaybackCall?.BackendId;
        internal bool AutoPlayForTest { get => _settingsService.AutoPlay; set => _settingsService.AutoPlay = value; }
        internal string? HiddenForTest { set => _filterService.Hidden = value; }
        internal string? MutedForTest { set => _filterService.Muted = value; }
        internal int PlaybackSpeedStep { get; set; }
        internal int OpenedTransportsForTest { get; private set; }
        internal int MetadataUpdatesForTest { get; private set; }
        internal Func<CancellationToken, Task>? RefreshMetadataForTest { set => _callStreamService.Refresh = value; }
        internal int MetadataRequestsForTest => _callStreamService.Requests;
        internal string? ServerUrlForTest { set => _settingsService.ServerUrl = value; }

        private bool IsRunning => _monitoring.CanRecover(_monitoring.Current);
        private bool CanResumePlayback => _retryPlaybackCall != null;
        private bool AudioEnabled { get; set; } = true;
        internal int CallsWaiting => CountRepresentedWaitingCalls();
        private Action? RequestJumpToLiveScroll { get; set; }
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

        internal Task StartForTestAsync(string? account = null, bool fresh = false) =>
            OpenListeningForTestAsync(_monitoring.TryBeginStart()!, account, fresh, driveQueue: true);

        private async Task<MonitoringLifecycle.Run?> StartMonitoringCoreAsync(bool driveQueue, long? automaticStopRevision = null)
        {
            if (_monitoring.TryBeginStart(automaticStopRevision) is not { } run)
                return null;

            await OpenListeningForTestAsync(run, account: null, fresh: true, driveQueue);
            return run;
        }

        private async Task OpenListeningForTestAsync(MonitoringLifecycle.Run run, string? account, bool fresh, bool driveQueue)
        {
            await run.PreviousCleanup;
            ResetLiveTraversal();
            _durableRun = new DurableLiveQueue.Run(Server, run.Id, null, Protected: account != null, Account: account);
            if (fresh)
                await LiveStreamRecovery.StartListeningAsync(_durableQueue, _durableRun, Recorded, run.Token);
            else
                await _durableQueue.OpenAsync(_durableRun, Recorded, default);
            LiveHistoryHandoffTests.Check(ActivateFreshListeningSession(run), "The fixture run must activate.");
            _userRunShouldDriveQueue = driveQueue;
            OpenedTransportsForTest++;
        }

        internal async Task AddForTestAsync(string id, bool later = false, bool audio = true, bool visible = true,
            RecordingOrigin origin = RecordingOrigin.Live)
        {
            var snapshot = new LiveCallSnapshot(new(), null, CallCardIdFallback.None, audio ? Server + "/" + id + ".mp3" : null);
            var run = _durableRun!;
            var observation = await _durableQueue.ObserveWithPositionAsync(run, id, later ? Recorded.AddSeconds(1) : Recorded,
                snapshot, default, origin, existingOnly: false);
            if (visible && observation.ArrivalSequence is { } arrival)
                Calls.Add(new DurableLiveQueue.Recording(id, arrival, Recorded.UtcTicks, snapshot,
                    new RecordingAccessContext(origin, run.Protected, run.Account), run.Owner).ToCallItem(Server));
        }

        internal async Task<string?> PendingHeadForTestAsync() =>
            (await _durableQueue.NextAsync(_durableRun!, 0, default))?.Id;

        internal async Task DeliverForTestAsync(string id)
        {
            await AddForTestAsync(id);
            RequestQueueForLiveArrival("TestLiveArrival");
            await ResumedQueue;
        }

        internal Task MarkCompletedForTestAsync(string id) => _durableQueue.CommitCompletionAsync(_durableRun!, id);

        internal Task SetAudioForTestAsync(bool enabled)
        {
            if (enabled)
                BeginAudioAtLiveEdge();
            AudioEnabled = enabled;
            if (!enabled)
            {
                ResetLiveTraversal();
                InvalidateLivePlayback();
            }
            return WaitForLiveEdgeAsync(CancellationToken.None);
        }

        internal Task<bool> IsPendingForTestAsync(string id) => _durableQueue.IsPendingAsync(_durableRun!, id, 0, default);

        internal async Task AssertPendingAfterRestartAsync(string id)
        {
            var replacement = _durableRun! with { Owner = _durableRun.Owner + 1 };
            await _durableQueue.OpenAsync(replacement, Recorded, default);
            LiveHistoryHandoffTests.Check(await _durableQueue.IsPendingAsync(replacement, id, 0, default),
                "Stopping before native completion must preserve the pending SQLite row across restart.");
        }

        internal Task StopForTestAsync() => StopMonitoringAsync();

        private Task StopMonitoringCoreAsync() => _monitoring.StopAsync(_ =>
        {
            _userRunShouldDriveQueue = false;
            ResetLiveTraversal();
            InvalidateLivePlayback();
        }, _ => { }, async _ =>
        {
            await FlushLiveCompletionAsync();
            await _durableQueue.StopAsync(_durableRun!);
        });

        internal DelayedSelection HoldNextSelection() => _nextSelection = new();

        internal Task StartQueueForTestAsync() => EnsureQueuePlaybackAsync(_playbackOwnership.Live!);
        internal Task PressPlayStopForTestAsync() => ToggleConnectionAsync();
        internal Task PressSystemPlayForTestAsync() => SystemPlayAsync();
        internal Task PressVisibleCardForTestAsync(CallItem item) => PlayVisibleCallAsync(item);
        internal CallItem CardForTest(string id) => Calls.Single(card => card.BackendId == id);
        private Task ResumeMobilePlaybackAsync() => throw new InvalidOperationException("Unexpected mobile resume in the fixture.");

        internal Task SelectForTestAsync(string id) => OnCallTappedAsync(Calls.FirstOrDefault(card => card.BackendId == id) ??
            new CallItem
            {
                BackendId = id, RecordingServer = Server,
                RecordingAccess = new RecordingAccessContext(RecordingOrigin.Live, false)
            });
        internal Task NavigateForTestAsync(int direction) => NavigateToAdjacentCallAsync(direction);
        internal Task JumpForTestAsync() => JumpToLiveAsync();

        internal Task SelectAddressForTestAsync(string id) => SelectForTestAsync(id);

        private Task RequestQueuePlaybackAsync(string reason, bool userInitiated)
        {
            LiveHistoryHandoffTests.Check((reason is "HistoryReleasedAudio" or "TappedCallFinished" or "JumpToLiveFinished"
                or "JumpToLiveResume" or "TestLiveArrival" or "UserPlay") && (userInitiated || _settingsService.AutoPlay),
                "Only the current listening intent can drive the queue.");
            ++ResumeRequests;
            return ResumedQueue = EnsureQueuePlaybackAsync(_playbackOwnership.Live!);
        }

        private async Task<CallItem?> GetNextDurableCallAsync(CancellationToken token,
            int version, long? afterArrival, IReadOnlyList<long>? visibleReplays)
        {
            var delay = Interlocked.Exchange(ref _nextSelection, null);
            var next = await _durableQueue.NextAsync(LiveQueueScopes(), LocalRecoveryFloor, token, afterArrival, visibleReplays);
            if (delay != null)
            {
                delay.Reached.TrySetResult();
                // Model a query that finished just before cancellation but whose
                // result hasn't reached the UI dispatcher yet.
                await delay.Return.Task;
            }
            return next?.Row.ToCallItem(next.Scope.Server);
        }

        private async Task<PlaybackOutcome> PlayAudioAsync(CallItem item, LivePlaybackRequests.Request request,
            bool allowMutedPlayback = false)
        {
            if (IsFilteredForPlayback(item, allowMutedPlayback))
            {
                await _durableQueue.MarkAsync(_durableRun!, item.BackendId, PlaybackOutcome.Skipped, request.Token);
                return PlaybackOutcome.Skipped;
            }
            _currentPlayingCall = item;
            var completion = new PlaybackCompletion();
            var outcome = await PlaybackAttempt.RunAsync(async () =>
            {
                if (PrepareForTest is { } prepare)
                    await prepare(item.BackendId, request.Token);
                var rate = await GetEffectivePlaybackRateAsync(item, request.Token);
                await PlayOnDeviceAsync(item, item.BackendId, rate, request, request.Token, completion);
            }, request.Token, failure => ReportPlaybackFailure(failure, request), completion);
            if (request.CanContinue && outcome == PlaybackOutcome.Failed)
                SetPlaybackRetry(item);
            if (request.CanContinue && outcome == PlaybackOutcome.Completed)
                _lastTransportCall = item;
            return outcome;
        }

        private LivePlaybackRequests.Request BeginManualPlayback(PlaybackOwnership.Lease live)
        {
            var request = BeginLiveManualRequest(live);
            SetPlaybackRetry(null);
            return request;
        }

        private void SetPlaybackRetry(CallItem? item) => _retryPlaybackCall = item;
        private static void ReportPlaybackFailure(Exception error, LivePlaybackRequests.Request request)
        {
            if (error is not IOException)
                throw new InvalidOperationException("Live handoff failed.", error);
        }
        private static Task PlaySingleCallWithoutQueueAsync(CallItem item, LivePlaybackRequests.Request request) =>
            throw new InvalidOperationException("This fixture always enables audio.");

        private static bool TryGetDurableRecording(CallItem item, DurableLiveQueue.Run run, out string id)
        {
            id = item.BackendId;
            return item.RecordingServer == run.Server && id.Length != 0 && run.Owns(item.RecordingAccess);
        }

        private DurableLiveQueue.Run[] LiveQueueScopes() =>
            _monitoring.CanRecover(_monitoring.Current) && _durableRun is { } scope ? [scope] : [];
        private DurableLiveQueue.Run? ScopeOf(CallItem item) => LiveQueueScopes().FirstOrDefault(scope =>
            TryGetDurableRecording(item, scope, out _));
        private bool IsCurrentLiveScope(DurableLiveQueue.Run run) => LiveQueueScopes().Contains(run);
        private static long LocalRecoveryFloor(DurableLiveQueue.Run run) => 0;
        private static CallItem ResolveDurableCard(DurableLiveQueue.Run run, DurableLiveQueue.Recording row) => row.ToCallItem(run.Server);
        private static Task RefreshTransportCardAsync(CallItem item, CancellationToken token) => Task.CompletedTask;
        private void ShowDurableCard(CallItem item)
        {
            if (!Calls.Any(card => card.BackendId == item.BackendId))
                Calls.Add(item);
        }
        private void DrainPendingCallsToVisible() { }
        private IReadOnlyList<CallItem> SnapshotLiveCalls() => Calls;
        private void ClearPendingCalls() { }
        private async Task RefreshRecorderMetadataAsync(DurableLiveQueue.Run scope, CallItem[] candidates)
        {
            var run = _monitoring.Current!;
            await _callStreamService.RefreshCallMetadataAsync(candidates, run.Token);
            if (_monitoring.CanRecover(run) && IsCurrentLiveScope(scope))
                MetadataUpdatesForTest += candidates.Length;
        }
        private void InsertLiveCardInDisplayOrder(CallItem item) => Calls.Add(item);
        private bool IsPlayableForQueue(CallItem item) => !string.IsNullOrWhiteSpace(item.AudioUrl) && !IsFilteredForPlayback(item);

        private string? RequireCurrentRecordingAccount(CallItem item) =>
            _durableRun?.Owns(item.RecordingAccess) == true ? _durableRun.Account :
                throw new InvalidOperationException("The recording belongs to a retired account.");

        private static CallItem? GetNextQueuedCall() => null;
        private static int GetBacklogCountForStartingCall(CallItem item) => 0;
        private static void ClearPrefetchedPlayableUrl(bool deleteCompletedTempFile) { }
        private void ClearPlayingSelection() => _currentPlayingCall = null;
        private static void UpdateQueueDerivedState() { }

        internal sealed class DelayedSelection
        {
            internal TaskCompletionSource Reached { get; } = LiveHistoryHandoffTests.Signal();
            internal TaskCompletionSource Return { get; } = LiveHistoryHandoffTests.Signal();
        }

        private sealed record TestPaths(string DbPath) : IDatabasePathProvider;
        private sealed class TestSettings
        {
            internal bool AutoPlay { get; set; }
            internal string? ServerUrl { get; set; } = Server;
        }
        private sealed class TestCallStream
        {
            internal int Requests { get; private set; }
            internal Func<CancellationToken, Task>? Refresh { get; set; }

            internal Task RefreshCallMetadataAsync(IReadOnlyList<CallItem> calls, CancellationToken token)
            {
                Requests++;
                return Refresh?.Invoke(token) ?? Task.CompletedTask;
            }
        }
        private sealed class TestFilter
        {
            internal string? Hidden { get; set; }
            internal string? Muted { get; set; }
            internal bool ShouldHide(CallItem item) => item.BackendId == Hidden;
            internal bool ShouldMute(CallItem item) => item.BackendId == Muted;
        }
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
