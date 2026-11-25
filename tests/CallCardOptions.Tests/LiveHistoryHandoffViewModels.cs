using System.Collections.ObjectModel;
using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.Services
{
    internal sealed class RecordingAccessException(string message) : InvalidOperationException(message);
    internal sealed class FirewallAuthenticationException(string message) : HttpRequestException(message);
    internal readonly record struct RecorderRequestScope(Uri Endpoint, Guid? ServerId = null);

    internal static class ScannerConnectionStore
    {
        internal static string? Normalize(string url) => RecordingStorageReference.ServerKey(url);
    }
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
        private readonly LivePlaybackScheduler _scheduler = new(new FavoriteTalkgroupSnapshot([]));
        private readonly LiveBacklogGuard _backlog = new();
        private Guid? _visibleLiveServer;
        private LivePlaybackPreferences _livePlaybackPreferences = LivePlaybackPreferences.Default;
        internal LivePlaybackPreferences PlaybackOptionsForTest
        {
            get => _livePlaybackPreferences;
            set
            {
                _livePlaybackPreferences = value;
                RefreshAutomaticAudioSource();
            }
        }
        private IReadOnlySet<(Guid, FavoriteTalkgroupIdentity)> _favoritesForAudio = new HashSet<(Guid, FavoriteTalkgroupIdentity)>();
        private IReadOnlySet<(Guid, FavoriteTalkgroupIdentity)> ReadFavoriteAudioMembership() => _favoritesForAudio;
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
        private DurableLiveQueue.Run? _secondRecorder;
        private Guid? _firstRecorderId;
        private Guid? _secondRecorderId;
        private CallItem? _currentPlayingCall;
        private CallItem? _manualPlaybackTarget;
        private CallItem? _retryPlaybackCall;
        private bool _retryIsAutomatic;
        private bool ManualBacklogExemption => _manualLiveTraversal != null || _playbackRequests.HasManualSelection ||
            _transportLookups.HasManualSelection;
        private CallItem? _lastPlayedCall;
        private CallItem? _lastTransportCall;
        private bool _userRunShouldDriveQueue;
        private bool _isQueuePlaybackRunning;
        private int _queuePlaybackVersion;
        private int _audioToggleSerial;
        private int _refreshingCallMetadata;
        private bool HasEnabledScannerConnections() => Uri.TryCreate(_settingsService.ServerUrl, UriKind.Absolute, out _);
        private DelayedSelection? _nextSelection;

        internal int ResumeRequests { get; private set; }
        internal Task ResumedQueue { get; private set; } = Task.CompletedTask;
        internal Func<Task>? BeforeCommitForTest { get; set; }
        internal Func<string, CancellationToken, Task>? PrepareForTest { get; set; }
        internal Func<CallItem, string>? AudioIdentityForTest { get; set; }
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
        private bool CanResumePlayback => _retryPlaybackCall != null || ShowPausedPlaybackActions;
        private bool ShowPausedPlaybackActions => IsRunning && _interruption?.RequiresPlay == true;
        private void NotifyPlaybackResumeState() { }
        private bool AudioEnabled { get; set; } = true;
        internal bool IsAudioEnabledForTest => AudioEnabled;
        internal int CallsWaiting => _durableQueue.CountReadyAsync(AutomaticQueueScopes(), LocalRecoveryFloor,
            _currentPlayingCall?.LiveArrivalSequence, default, _automaticAudioFilter).GetAwaiter().GetResult();
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

        internal void ConfigureAudioSourceRecordersForTest(Guid first, Guid second)
        {
            _firstRecorderId = first;
            _secondRecorderId = second;
            _selectedAudioSource = default;
            _loadedAudioSourcePreference = true;
            SetLiveAudioRecorders([(first, "Recorder A", true), (second, "Recorder B", true)]);
        }

        internal Task<bool> WaitForSourceForTestAsync() => WaitForAutomaticAudioSourceAsync(default);

        internal Task SetAudioFavoritesForTestAsync(params (Guid Server, string Talkgroup)[] favorites)
        {
            _favoritesForAudio = favorites.Select(item => (item.Server, new FavoriteTalkgroupIdentity(
                "id:SYS", "", "", "id:" + item.Talkgroup))).ToHashSet();
            return RefreshFavoriteAudioSourceAsync();
        }

        internal int DisplayedCallsForTest => Calls.Count;

        internal async Task ReconnectSecondaryRecorderForTestAsync()
        {
            var prior = _secondRecorder!;
            await _durableQueue.StopAsync(prior);
            _secondRecorder = prior with { Owner = prior.Owner + 1 };
            await _durableQueue.StartListeningAsync(_secondRecorder, Recorded, default);
            OpenedTransportsForTest++;
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
            _durableRun = new DurableLiveQueue.Run(Server, run.Id, null, Protected: account != null, Account: account, ServerId: _firstRecorderId);
            if (fresh)
                await LiveStreamRecovery.StartListeningAsync(_durableQueue, _durableRun, Recorded, run.Token);
            else
                await _durableQueue.OpenAsync(_durableRun, Recorded, default);
            if (_secondRecorderId is { } secondId)
            {
                _secondRecorder = new DurableLiveQueue.Run("https://second.example", run.Id + 1000, null, ServerId: secondId);
                await _durableQueue.StartListeningAsync(_secondRecorder, Recorded, run.Token);
                OpenedTransportsForTest++;
            }
            LiveHistoryHandoffTests.Check(ActivateFreshListeningSession(run), "The fixture run must activate.");
            _userRunShouldDriveQueue = driveQueue;
            OpenedTransportsForTest++;
        }

        internal async Task AddForTestAsync(string id, bool later = false, bool audio = true, bool visible = true,
            RecordingOrigin origin = RecordingOrigin.Live, Guid? serverId = null, string? talkgroup = null, DateTimeOffset? recordedAt = null)
        {
            var run = serverId.HasValue && _secondRecorder is { } second && second.ServerId == serverId ? second : _durableRun!;
            var metadata = new CallCardMetadata { SystemId = "SYS", TalkgroupId = talkgroup };
            var snapshot = new LiveCallSnapshot(metadata, null, CallCardIdFallback.None, audio ? run.Server + "/" + id + ".mp3" : null);
            var recorded = recordedAt ?? (later ? Recorded.AddSeconds(1) : Recorded);
            var observation = await _durableQueue.ObserveWithPositionAsync(run, id, recorded,
                snapshot, default, origin, existingOnly: false, skipAutomatic: !AudioEnabled);
            if (visible && observation.ArrivalSequence is { } arrival)
                Calls.Insert(0, new DurableLiveQueue.Recording(id, arrival, recorded.UtcTicks, snapshot,
                    new RecordingAccessContext(origin, run.Protected, run.Account), run.Owner).ToCallItem(run.Server, run.ServerId));
        }

        internal async Task<Func<Task>> HoldPlaybackLagForTestAsync(Guid server, DateTimeOffset since, PlaybackLagEvidence evidence)
        {
            var scope = LiveQueueScopes().Single(run => run.ServerId == server);
            var token = _monitoring.Current!.Token;
            var recovery = await LiveStreamRecovery.OpenAsync(_durableQueue, scope, token);
            recovery.ObserveTransportRecovery(_lagGeneration.Capture,
                lag => OnPlaybackLag(scope, recovery, lag, token, commit => commit()));
            recovery.TransportSucceeded(since);
            await recovery.TransportLostAsync();
            var window = recovery.OutstandingWindow!;
            return () => recovery.CompleteAsync(window, default, evidence);
        }

        internal async Task<string?> PendingHeadForTestAsync() =>
            (await _durableQueue.NextAsync(_durableRun!, 0, default))?.Id;

        internal async Task DeliverForTestAsync(string id)
        {
            await AddForTestAsync(id);
            RequestQueueForLiveArrival("TestLiveArrival");
            await ResumedQueue;
        }

        internal Task MarkCompletedForTestAsync(string id, Guid? recorder = null) =>
            _durableQueue.CommitCompletionAsync(RecorderForTest(recorder), id);

        internal Task SetAudioForTestAsync(bool enabled)
        {
            if (enabled)
                BeginAudioAtLiveEdge(skipOutstandingRecovery: true);
            AudioEnabled = enabled;
            if (!enabled)
            {
                ResetLiveTraversal();
                InvalidateLivePlayback();
            }
            return WaitForLiveEdgeAsync(CancellationToken.None);
        }

        internal Task<bool> IsPendingForTestAsync(string id, Guid? recorder = null) =>
            _durableQueue.IsPendingAsync(RecorderForTest(recorder), id, 0, default);

        private DurableLiveQueue.Run RecorderForTest(Guid? recorder) =>
            recorder != null && _secondRecorder?.ServerId == recorder ? _secondRecorder : _durableRun!;

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
            if (_interruption != null)
                StopInterruptionTracking();
        }, _ => { }, async _ =>
        {
            await FlushLiveCompletionAsync();
            await _durableQueue.StopAsync(_durableRun!);
            if (_secondRecorder != null)
                await _durableQueue.StopAsync(_secondRecorder);
        });

        internal DelayedSelection HoldNextSelection() => _nextSelection = new();

        internal Task StartQueueForTestAsync() => EnsureQueuePlaybackAsync(_playbackOwnership.Live!);
        internal Task PressPlayStopForTestAsync() => ToggleConnectionAsync();
        internal Task PressSystemPlayForTestAsync() => SystemPlayAsync();
        internal void TrackInterruptionsForTest(TimeProvider clock) => StartInterruptionTracking(clock);
        internal Task SignalInterruptionForTestAsync(LiveInterruptionSource source, bool blocked, bool requiresPlay = false,
            DateTimeOffset? beganAt = null) => ObserveInterruption(source, blocked, requiresPlay, beganAt);
        internal Task SignalFocusForTestAsync(PlaybackCompletion owner, bool blocked) => ObserveClipFocusInterruption(owner, blocked);
        internal bool InterruptionIsBlockingForTest => IsLiveInterruptionBlockingPlayback;
        internal bool HasInterruptCheckpointForTest => _interruption?.Since.HasValue == true;
        internal Task PressVisibleCardForTestAsync(CallItem item) => PlayVisibleCallAsync(item);
        internal CallItem CardForTest(string id, Guid? recorder = null) =>
            Calls.Single(card => card.BackendId == id && (recorder == null || card.ServerId == recorder));
        internal void EvictCardForTest(CallItem card) => Calls.Remove(card);
        internal Func<Task>? RequestNativeResumeForTest { get; set; }

        private Task ResumeMobilePlaybackAsync()
        {
            if (RequestNativeResumeForTest is not { } resume)
                throw new InvalidOperationException("Unexpected mobile resume in the fixture.");
            _interruption?.AllowExplicitPlay();
            return resume();
        }

        internal Task SelectForTestAsync(string id) => OnCallTappedAsync(Calls.FirstOrDefault(card => card.BackendId == id) ??
            new CallItem
            {
                BackendId = id, RecordingServer = Server,
                RecordingAccess = new RecordingAccessContext(RecordingOrigin.Live, false)
            });
        internal Task NavigateForTestAsync(int direction) => NavigateToAdjacentCallAsync(direction);
        internal void UseFavoritesViewForTest(Func<CallItem, bool>? filter) => _visibleFavoriteFilter = filter;
        internal bool IsFavoriteForTest(CallItem card) => card.ServerId is { } id &&
            FavoriteTalkgroupIdentity.From(card.CardMetadata) is { } identity && _favoritesForAudio.Contains((id, identity));
        internal Task JumpForTestAsync() => JumpToLiveAsync();

        internal Task SelectAddressForTestAsync(string id) => SelectForTestAsync(id);

        private Task RequestQueuePlaybackAsync(string reason, bool userInitiated)
        {
            if (_playbackOwnership.Live == null || !IsRunning || IsLiveInterruptionBlockingPlayback || !AudioEnabled)
                return Task.CompletedTask;
            if (_interruption?.Since.HasValue == true)
                return RecoverInterruptedPlaybackAsync(userInitiated);
            LiveHistoryHandoffTests.Check((reason is "HistoryReleasedAudio" or "TappedCallFinished" or "JumpToLiveFinished"
                or "JumpToLiveResume" or "TestLiveArrival" or "UserPlay" or "ManualPreparationReleased"
                or "AutomaticAudioSourceChanged" or "FavoritesChanged" or "InterruptionEnded" or "ReturnedToLiveAfterInterruption"
                or "RecorderLagReconciled" or "RecorderLagAfterAutomaticRetry") &&
                (userInitiated || _settingsService.AutoPlay),
                "Only the current listening intent can drive the queue.");
            ++ResumeRequests;
            return ResumedQueue = EnsureQueuePlaybackAsync(_playbackOwnership.Live!);
        }

        private async Task<(CallItem Call, LivePlaybackScheduler.Choice Choice)?> GetNextDurableCallAsync(
            CancellationToken token, int version, ManualLiveTraversal? traversal)
        {
            var delay = Interlocked.Exchange(ref _nextSelection, null);
            var next = await ReadNextLiveRecordingAsync(token, traversal);
            if (delay != null)
            {
                delay.Reached.TrySetResult();
                // The journal can finish before the dispatcher receives the result.
                await delay.Return.Task;
            }
            if (next == null)
                return null;
            var card = ResolveLivePlaybackCard(next, traversal);
            card.IsCompletedReplay = next.Row.Row.IsCompletedReplay;
            card.LiveAudioRevision = next.Row.Row.AudioRevision;
            return (card, next.Choice);
        }

        private async Task<PlaybackAudioLease> PrepareManualAudioAsync(CallItem item, CancellationToken token)
        {
            if (PrepareForTest is { } prepare)
                await prepare(item.BackendId, token);
            return new(item.BackendId);
        }

        internal Func<CancellationToken, Task<IReadOnlyList<LiveAudioPreloader.Candidate>>>? ForecastForTest { get; set; }

        private void StartAutomaticPreloads(LivePlaybackScheduler.Choice current, CancellationToken token, long revision)
        {
            if (AudioEnabled && ForecastForTest is { } forecast)
                _ = _automaticPreloads.Start(forecast, token, revision);
        }

        private async Task<PlaybackOutcome> PlayAudioAsync(CallItem item, LivePlaybackRequests.Request request,
            bool allowFilteredPlayback = false, PlaybackAudioLease? preparedAudio = null,
            LivePlaybackScheduler.Choice? forecastFrom = null)
        {
            if (IsFilteredForPlayback(item, allowFilteredPlayback))
            {
                if (ScopeOf(item) is { } filtered)
                    await _durableQueue.MarkAsync(filtered, item.BackendId, PlaybackOutcome.Skipped, request.Token);
                return PlaybackOutcome.Skipped;
            }
            _currentPlayingCall = item;
            var completion = new PlaybackCompletion();
            try
            {
                var outcome = await PlaybackAttempt.RunAsync(async () =>
                {
                    if (preparedAudio == null && PrepareForTest is { } prepare)
                        await prepare(item.BackendId, request.Token);
                    var rate = await GetEffectivePlaybackRateAsync(item, request.Token);
                    await PlayOnDeviceAsync(item, AudioIdentityForTest?.Invoke(item) ?? item.BackendId,
                        rate, request, request.Token, completion, forecastFrom: forecastFrom);
                }, request.Token, failure => ReportPlaybackFailure(failure, request), completion);
                if (request.CanContinue && outcome == PlaybackOutcome.Failed && !request.IsManual)
                {
                    if (ScopeOf(item) is { } failed)
                        await _durableQueue.DeferAudioAsync(failed, item.BackendId, item.LiveAudioRevision, request.Token);
                    return PlaybackOutcome.Withheld;
                }
                if (request.CanContinue && outcome == PlaybackOutcome.Completed)
                    _lastTransportCall = item;
                return outcome;
            }
            finally
            {
                if (request.IsCurrent && _currentPlayingCall == item)
                    _currentPlayingCall = null;
            }
        }

        private Task RequestReadyCountForTestAsync() => ObserveReadyCountAsync(default);

        private void RequestReadyCount() { }

        private void ResetScheduler()
        {
            _automaticPreloads.Retire();
            _backlog.Reset();
            _scheduler.Reset();
        }

        private async Task ObserveReadyCountAsync(CancellationToken token)
        {
            var ready = await _durableQueue.CountReadyAsync(AutomaticQueueScopes(), LocalRecoveryFloor,
                _currentPlayingCall?.LiveArrivalSequence, token, _automaticAudioFilter);
            _backlog.Observe(ready, PlaybackOptionsForTest, _manualLiveTraversal != null ||
                _playbackRequests.HasManualSelection || _transportLookups.HasManualSelection);
        }

        private async Task<bool> TryAutomaticBacklogJumpAsync(MonitoringLifecycle.Run run, PlaybackOwnership.Lease live,
            int version, CancellationToken token)
        {
            var sourceRevision = _automaticAudioRevision;
            await ObserveReadyCountAsync(token);
            if (!_backlog.JumpPending || sourceRevision != _automaticAudioRevision)
                return false;
            using var jump = _transportLookups.Begin(manual: false, token);
            if (!await SkipAutomaticAudioBacklogAsync(AutomaticQueueScopes(), jump, sourceRevision))
                return false;
            ResetScheduler();
            return true;
        }

        internal Task ReadBacklogForTestAsync() => RequestReadyCountForTestAsync();
        internal bool BacklogJumpForTest => _backlog.JumpPending;
        internal bool BacklogWarningForTest => _backlog.WarningActive;
        internal Guid? VisibleServerForTest { set => _visibleLiveServer = value; }

        private void SetPlaybackRetry(CallItem? item, bool automatic = false)
        {
            _retryIsAutomatic = item != null && automatic;
            _retryPlaybackCall = item;
        }
        private static void ReportPlaybackFailure(Exception error, LivePlaybackRequests.Request request)
        {
            if (error is not IOException)
                throw new InvalidOperationException("Live handoff failed.", error);
        }


        private static bool TryGetDurableRecording(CallItem item, DurableLiveQueue.Run run, out string id)
        {
            id = item.BackendId;
            return item.RecordingServer == run.Server && item.ServerId == run.ServerId && id.Length != 0 && run.Owns(item.RecordingAccess);
        }

        private DurableLiveQueue.Run[] LiveQueueScopes() =>
            _monitoring.CanRecover(_monitoring.Current) && _durableRun is { } scope ?
                _secondRecorder is { } second ? [scope, second] : [scope] : [];
        private DurableLiveQueue.Run? ScopeOf(CallItem item) => LiveQueueScopes().FirstOrDefault(scope =>
            TryGetDurableRecording(item, scope, out _));
        private bool IsCurrentLiveScope(DurableLiveQueue.Run run) => LiveQueueScopes().Contains(run);
        private static long LocalRecoveryFloor(DurableLiveQueue.Run run) => 0;
        private CallItem ResolveDurableCard(DurableLiveQueue.Run run, DurableLiveQueue.Recording row, CallItem? capturedReplay = null) =>
            Calls.FirstOrDefault(card => card.BackendId == row.Id && card.LiveArrivalSequence == row.ArrivalSequence) ??
            (capturedReplay != null && TryGetDurableRecording(capturedReplay, run, out var id) && id == row.Id ? capturedReplay : null) ??
            row.ToCallItem(run.Server, run.ServerId);
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

        private static void RequireLiveRecordingEntitlement(CallItem item) { }

        private string? RequireCurrentRecordingAccount(CallItem item)
        {
            // A stopped manual replay keeps its original authorization scope.
            var scope = item.ServerId.HasValue && _secondRecorder?.ServerId == item.ServerId ? _secondRecorder : _durableRun;
            return scope?.Owns(item.RecordingAccess) == true ? scope.Account :
                throw new InvalidOperationException("The recording belongs to a retired account.");
        }

        private static CallItem? GetNextQueuedCall() => null;
        private static int GetBacklogCountForStartingCall(CallItem item) => 0;
        private static void ClearPrefetchedPlayableUrl(bool deleteCompletedTempFile) { }
        private void ClearPlayingSelection()
        {
            ClearActiveLiveCard();
            _currentPlayingCall = null;
        }
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
            internal MobileAudioPreferences MobileAudio { get; } = MobileAudioPreferences.Default;
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
            internal bool ShouldMute(CallItem item) => item.BackendId == Muted || item.BackendId == Hidden;
        }
    }

    public sealed partial class HistoryViewModel : BindableObject
    {
        private static readonly DateTimeOffset HistoryTime = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly HistoryPageTransport _callHistoryService = new();
        private readonly ObservableCollection<CallItem> Calls = new();
        private HistoryRecorder? _historyRecorder = new(Guid.NewGuid(), "https://history.example");
        private long _accountVersion = 1;
        private bool _isQueuePlaybackRunning;
        private CallItem? _activeCall;
        private string _statusText = string.Empty;

        private CallItem? ActiveCall => _activeCall;
        private bool IsHistoryPlaybackRunning => _isQueuePlaybackRunning;
        private bool IsHistoryMediaButtonsEnabled => _isQueuePlaybackRunning || Calls.Count > 0;
        private bool ShowHistoryPlayButton => !ShowHistoryStopButton;
        private bool ShowHistoryStopButton => _isQueuePlaybackRunning || _historySelectionRequests.HasManualSelection;
        private bool IsLoading { get; set; }
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
        internal CallItem? ActiveCallForTest => ActiveCall;
        internal string StatusForTest => StatusText;
        internal bool IsRunningForTest => IsHistoryPlaybackRunning;
        internal double RateForTest { get; set; } = 1;
        internal Func<CallItem, CancellationToken, Task<PlaybackAudioLease>>? PrepareForTest { get; set; }
        internal HistoryPageTransport PagesForTest => _callHistoryService;

        internal HistoryViewModel(PlaybackOwnership ownership, HandoffAudio player)
        {
            _playbackOwnership = ownership;
            _audioPlaybackService = player;
            var first = MakeCallForTest("history", -1);
            _historyResults = new(NewSnapshot(false), HistoryPageCursor.Start, [first], false);
            Calls.Add(first);
            Calls.CollectionChanged += OnHistoryCallsChanged;
        }

        internal void OpenForTest() => TakeHistoryPlaybackOwnership();
        internal Task PlayForTestAsync() => SelectHistoryRecordingAsync(HistoryNavigation.Play);
        internal Task SelectForTestAsync(CallItem call) => SelectHistoryRecordingAsync(HistoryNavigation.Recording, call);
        internal Task PreviousForTestAsync() => SelectHistoryRecordingAsync(HistoryNavigation.Previous);
        internal Task NextForTestAsync() => SelectHistoryRecordingAsync(HistoryNavigation.Next);
        internal Task StopForTestAsync() => StopAsync();
        internal Task LoadOlderForTestAsync() => LoadMoreOlderAsync();

        internal Task SearchForTestAsync(CallItem[] calls, bool hasMore, bool hosted = false)
        {
            var snapshot = NewSnapshot(hosted);
            _callHistoryService.Initial = new(snapshot, HistoryPageCursor.Start, calls, hasMore);
            return SearchAsync();
        }

        internal CallItem MakeCallForTest(string id, int seconds, int? archiveOffset = null) =>
            new()
            {
                BackendId = id,
                ServerId = _historyRecorder!.Id,
                RecordingServer = _historyRecorder.Endpoint,
                RecordingTimeUtc = HistoryTime.AddSeconds(seconds),
                HistoryArchiveOffset = archiveOffset
            };

        internal void ChangeRecorderForTest()
        {
            RetireHistorySearchRequests();
            _historyRecorder = new(Guid.NewGuid(), "https://other.example");
            ClearSearchResults();
        }

        internal void ChangeFiltersForTest() => ClearSearchResults();

        internal void ChangeAccountForTest()
        {
            _accountVersion++;
            RetireHistorySearchRequests();
            ClearSearchResults();
        }

        private HistorySearchSnapshot NewSnapshot(bool hosted) =>
            new(new(_historyRecorder!, hosted, hosted ? "test" : null, hosted ? _accountVersion : null),
                CurrentFilters(), HistoryTime.AddDays(-1), HistoryTime, hosted ? 2 : null);

        private HistorySearchFilters CurrentFilters() => new()
        {
            Site = new() { Value = "test-site", RecorderId = _historyRecorder?.Id }
        };

        private static HistoryDateRange CurrentDates() =>
            new(new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 18), TimeSpan.Zero);

        private void RetireHistorySearchRequests() => RetireHistoryPageRequest();
        private static void ClearHistoryResultAnnotations() { }
        private static void RefreshHistoryDateAccess() { }
        private static void StartAddressDetectionForHistoryCalls(IReadOnlyList<CallItem> calls) { }

        private void RequireHistoryRecordingAccess(HistorySearchSnapshot snapshot, CallItem call)
        {
            if (snapshot.Access.AccountVersion is { } version && version != _accountVersion)
                throw new RecordingAccessException("The recorder sign-in changed. Search again.");
        }

        private Task RunAuthorizedHistoryAudioAsync(HistorySearchSnapshot snapshot, CallItem call,
            CancellationToken token, Func<CancellationToken, Task> play)
        {
            RequireHistoryRecordingAccess(snapshot, call);
            return play(token);
        }

        private Task<PlaybackAudioLease> AcquireHistoryRecordingAsync(HistorySearchSnapshot snapshot, CallItem call,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RequireHistoryRecordingAccess(snapshot, call);
            return PrepareForTest?.Invoke(call, token) ?? Task.FromResult(new PlaybackAudioLease(call.BackendId));
        }

        private double GetPlaybackRate() => RateForTest;
        private static void AddAddressAlertIfNew(CallItem call) { }
        private static void ScrollToIndex(int index, ScrollToPosition position) { }
        private void RefreshCommandStates() => OnPropertyChanged(nameof(IsHistoryMediaButtonsEnabled));
    }

    internal sealed class HistoryPageTransport : ICallHistoryService
    {
        internal HistorySearchResult? Initial { get; set; }
        internal Func<HistorySearchResult, CancellationToken, Task<HistorySearchResult>>? Older { get; set; }
        internal int PageRequests { get; private set; }

        public Task<HistorySearchResult> SearchAsync(HistoryRecorder recorder, HistorySearchFilters filters,
            HistoryDateRange dates, CancellationToken token = default) => Task.FromResult(Initial!);

        public Task<HistorySearchResult> LoadMoreAsync(HistorySearchResult previous, CancellationToken token = default)
        {
            PageRequests++;
            return Older?.Invoke(previous, token) ?? throw new InvalidOperationException("Unexpected older-page request.");
        }

        public Task<HistoryLookupData> GetLookupDataAsync(HistorySearchFilters? filters, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task<HistoryLookupData> GetRecorderLookupsAsync(HistoryRecorder recorder, bool refresh,
            CancellationToken token = default) => throw new NotSupportedException();
    }

    internal enum ScrollToPosition { Start }
}
