using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Windows.Input;
using JoesScanner.Helpers;
#if ANDROID
using JoesScanner.Platforms.Android.Services;
#elif IOS
using JoesScanner.Platforms.iOS.Services;
#endif

namespace JoesScanner.ViewModels
{
    // Main view model for the primary JoesScanner client screen.
    // Handles streaming calls, playback, theme, jump-to-live, and global audio enable state.
    public partial class MainViewModel : BindableObject
    {
        // View hint: "Jump to live" is a user action and should force the main queue list
        // to visually snap back to the newest call even if the user had scrolled into history.
        public event Action? RequestJumpToLiveScroll;

        private const string AppleIosTestAccountEmail = "iostest@joesscanner.com";
        private const string AppleIosTestAccountEmailLegacy = "iostest@jeosscanner.com";

        private readonly ICallStreamService _callStreamService;
        private readonly ISettingsService _settingsService;
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly ISystemMediaService _systemMediaService;
        private readonly HttpClient _audioHttpClient;
        private readonly FilterService _filterService = FilterService.Instance;
        private readonly ISubscriptionService _subscriptionService;
        private readonly ITelemetryService _telemetryService;
        private readonly IHistoryLookupsCacheService _historyLookupsCacheService;
			        private readonly IAddressDetectionService _addressDetectionService;
			        private readonly IWhat3WordsService _what3WordsService;
        private readonly IToneAlertService _toneAlertService;
private readonly IPlaybackCoordinator _playbackCoordinator;

        private readonly MonitoringLifecycle _monitoring = new();
        private readonly DurableLiveQueue _durableQueue;
        private readonly DurablePlaybackCommit _playbackCommit;
        private readonly LiveQueueWake _queueWake = new();
        private readonly LivePlaybackRequests _transportLookups = new();
        private DurableLiveQueue.Run? _durableRun;
        private CallItem? _manualPlaybackTarget;
        private readonly LivePlaybackRequests _playbackRequests = new();
        private readonly PlaybackOwnership _playbackOwnership;
        private readonly SemaphoreSlim _liveCompletionGate = new(1);

        private int _refreshingCallMetadata;

        private int _audioToggleSerial;
        private bool _isRunning;
        private string _serverUrl = string.Empty;
        private bool _autoPlay;
        private bool _what3WordsLinksEnabled = true;
        private const int MaxCallsToKeep = 100;
        private bool _audioEnabled;
        private int _totalCallsReceived;
        private int _totalCallsInserted;
        private string _lastQueueEvent = string.Empty;

        // 0 = 1x, 1 = 1.5x, 2 = 2x
        private double _playbackSpeedStep = 0;

        // Currently playing call, used to compute calls waiting.
        private CallItem? _currentPlayingCall;

        // Most recently finished call, used as the anchor when nothing is playing.
        private CallItem? _lastPlayedCall;
        private CallItem? _retryPlaybackCall;
        private CallItem? _awaitingAutomaticAudio;
        private CallItem? _notifiedRecording;
        private (string? Server, string Id)? _lastAudioNotificationRetry;
        public bool CanSkipUnavailableRecording => _retryPlaybackCall != null && _unavailableRecording;
        private bool _unavailableRecording;

        // True while we are playing through the backlog.
        private bool _isQueuePlaybackRunning;
        private int _queuePlaybackVersion;

        private bool _resumeAfterHistory;

        // When the user explicitly starts monitoring (presses Play/Start Monitoring), keep queue playback alive
        // even if AutoPlay is off. Otherwise calls can arrive and scroll but never advance audio unless the
        // user taps each call.
        private bool _userRunShouldDriveQueue;

        // New calls received while the user is behind live are stored here so the backlog can play in order.
        // Newest pending call is stored at index 0.
        private readonly object _pendingCallsLock = new();
        private readonly List<CallItem> _pendingCalls = new();

        // One-call lookahead used to reduce dead air between backlog items when catch-up speed is active.
        // This only prepares the upcoming call URL or authenticated temp file and does not start playback.
        private readonly object _prefetchLock = new();
        private CallItem? _prefetchedCall;
        private Task<string?>? _prefetchedPlayableUrlTask;
        private CancellationTokenSource? _prefetchCts;

        // Timestamp of when the previous queue playback attempt finished. Used to measure audible gap drift.
        private DateTimeOffset? _lastQueuePlaybackFinishedAtUtc;

        // Optional settings view model reference. Currently not used but kept for future wiring.
        public SettingsViewModel? SettingsViewModel { get; set; }

        // Subscription badge (top right of header)
        private string _subscriptionSummary = string.Empty;
        public string SubscriptionSummary
        {
            get => _subscriptionSummary;
            private set
            {
                var newValue = value ?? string.Empty;
                if (_subscriptionSummary == newValue)
                    return;

                _subscriptionSummary = newValue;
                OnPropertyChanged();
            }
        }

        private bool _showSubscriptionSummary;
        public bool ShowSubscriptionSummary
        {
            get => _showSubscriptionSummary;
            private set
            {
                if (_showSubscriptionSummary == value)
                    return;

                _showSubscriptionSummary = value;
                OnPropertyChanged();
            }
        }

        private string _queueStatusText = "Idle";
        public string QueueStatusText
        {
            get => _queueStatusText;
            private set
            {
                if (_queueStatusText == value)
                    return;

                _queueStatusText = value;
                OnPropertyChanged();
            }
        }

        // Live collection of calls shown in the UI.
        public ObservableCollection<CallItem> Calls { get; } = new();

        public ObservableCollection<CallItem> AddressAlerts { get; } = new();
        public bool HasAddressAlerts => AddressAlerts.Count > 0;

        // We only show up to 3 alerts at once, but we keep older ones queued until dismissed.
        // This avoids losing alerts when multiple address hits come in quickly.
        private readonly List<CallItem> _hiddenAddressAlerts = new();


        // Command to start the call stream. Bound to the Connect button.
        public ICommand StartCommand { get; }

        // Command to stop the call stream. Bound to the Disconnect button.
        public ICommand StopCommand { get; }

        // Command to open the donation site. Bound to the Donate button in the footer.
        public ICommand OpenDonateCommand { get; }

        // Command bound to the global Audio On or Audio Off button on the main page.
        // Only affects audio (mute or unmute), never starts or stops the stream.
        public ICommand ToggleAudioCommand { get; }
        public ICommand ResumeMobilePlaybackCommand { get; }
        public ICommand SkipUnavailableRecordingCommand { get; }
        public bool CanResumePlayback => _retryPlaybackCall != null || IsMobilePlaybackPaused;

        public bool IsMobilePlaybackPaused =>
#if ANDROID
            AndroidAudioEnvironment.RequiresPlay;
#elif IOS
            IosAudioEnvironment.RequiresPlay;
#else
            false;
#endif

#if ANDROID
        private const string MonitoringExplicitlyStoppedKey = "android_monitoring_explicitly_stopped";
        private Task? _androidNotificationPermissionPrompt;
#elif IOS
        private const string MonitoringExplicitlyStoppedKey = "ios_monitoring_explicitly_stopped";
        private LiveStreamRecovery? _iosForegroundRecovery;
#else
        private const string MonitoringExplicitlyStoppedKey = "monitoring_explicitly_stopped";
#endif

        private readonly MonitoringIntentStore _monitoringIntent =
            new(MonitoringExplicitlyStoppedKey, message => AppLog.Add(message));
        private (long Owner, string? Server, string? Account) _startingMonitoringScope;

        // Command to play a specific call audio when the user taps it.
        public ICommand PlayAudioCommand { get; }

        // Command to skip backlog and jump playback to the newest call.
        public ICommand JumpToLiveCommand { get; }

        // Command bound to the premium media play or stop button.
        // When not running, starts monitoring. When running, stops monitoring.
        public ICommand ToggleConnectionCommand { get; }

        // Command bound to the premium media speed down button.
        public ICommand PlaybackSpeedDownCommand { get; }

        // Command bound to the premium media speed up button.
        public ICommand PlaybackSpeedUpCommand { get; }

        // Command bound to the premium media previous call button.
        public ICommand PreviousCallCommand { get; }

        // Command bound to the premium media next call button.
        public ICommand NextCallCommand { get; }

        public MainViewModel(
            ICallStreamService callStreamService,
            ISettingsService settingsService,
            IAudioPlaybackService audioPlaybackService,
            ISystemMediaService systemMediaService,
            ISubscriptionService subscriptionService,
            ITelemetryService telemetryService,
			IHistoryLookupsCacheService historyLookupsCacheService,
			IPlaybackCoordinator playbackCoordinator,
            IAddressDetectionService addressDetectionService,
            IWhat3WordsService what3WordsService,
            IToneAlertService toneAlertService,
            DurableLiveQueue durableQueue,
            PlaybackOwnership playbackOwnership)
        {
            _callStreamService = callStreamService ?? throw new ArgumentNullException(nameof(callStreamService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));
            _systemMediaService = systemMediaService ?? throw new ArgumentNullException(nameof(systemMediaService));
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
            _historyLookupsCacheService = historyLookupsCacheService ?? throw new ArgumentNullException(nameof(historyLookupsCacheService));
			_playbackCoordinator = playbackCoordinator ?? throw new ArgumentNullException(nameof(playbackCoordinator));
			_addressDetectionService = addressDetectionService ?? throw new ArgumentNullException(nameof(addressDetectionService));
			_what3WordsService = what3WordsService ?? throw new ArgumentNullException(nameof(what3WordsService));
			_toneAlertService = toneAlertService ?? throw new ArgumentNullException(nameof(toneAlertService));
            _durableQueue = durableQueue;
            _playbackOwnership = playbackOwnership;
            _playbackCommit = new DurablePlaybackCommit(durableQueue.CommitCompletionAsync);

			_toneAlertService.ToneDetected += OnToneDetected;
			_toneAlertService.HotTalkgroupsChanged += OnHotTalkgroupsChanged;

            AddressAlerts.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasAddressAlerts));

            _audioHttpClient = ServerAuthenticationHandler.CreateClient(_settingsService);
            _audioHttpClient.Timeout = Timeout.InfiniteTimeSpan;

            // Initialize server URL from settings.
            // IMPORTANT: Do not force a default URL into storage here.
            // If the user has not configured a server, _serverUrl stays blank and
            // StartMonitoringAsync will prompt them to set one in Settings.
            _serverUrl = _settingsService.ServerUrl ?? string.Empty;

            // Scope filter rules to the configured server.
            _filterService.SetServerUrl(_serverUrl);

            // Global audio flag persisted across runs.
            // Default is true so a fresh install behaves like a radio.
            _audioEnabled = AppStateStore.GetBool("audio_enabled", true);

			// AutoPlay is an explicit user setting (independent from AudioEnabled).
			// Do not cache it here; it must reflect the Settings page immediately.
			_autoPlay = _settingsService.AutoPlay;
            What3WordsLinksEnabled = _settingsService.What3WordsLinksEnabled;
            // Call retention is now fixed after removing queue call control settings.
            // MaxCallsToKeep is enforced when calls are inserted.

            // Restore playback speed step.
            // V2 steps: 0=1x, 1=1.25x, 2=1.5x, 3=1.75x, 4=2x
            var savedSpeed = AppStateStore.GetDouble("playback_speed_step", 0.0);
            PlaybackSpeedStep = savedSpeed;

            // Always show newest calls at the top now.
            _settingsService.ScrollDirection = "Up";

            // Initial theme.
            var initialTheme = _settingsService.ThemeMode;
            ApplyTheme(initialTheme);

            SetConnectionStatus(ConnectionStatus.Stopped);

            // Commands.
            StartCommand = new Command(Start, () => _monitoring.Current == null);
            StopCommand = new Command(async () => await StopMonitoringAsync(), () => _monitoring.Current != null);
            OpenDonateCommand = new Command(async () => await OpenDonateAsync());
            ToggleAudioCommand = new Command(async () => await OnToggleAudioAsync());
            ResumeMobilePlaybackCommand = new Command(async () => await ResumeMobilePlaybackAsync());
            SkipUnavailableRecordingCommand = new Command(async () => await SkipUnavailableRecordingAsync());
#if ANDROID
            AndroidAudioEnvironment.ResumeStateChanged += () => OnPropertyChanged(nameof(CanResumePlayback));
#elif IOS
            IosAudioEnvironment.ResumeStateChanged += () => OnPropertyChanged(nameof(CanResumePlayback));
            IosAudioEnvironment.ForegroundRecovered += OnIosForegroundRecovered;
#endif
            PlayAudioCommand = new Command<CallItem>(async item => await OnCallTappedAsync(item));
            JumpToLiveCommand = new Command(async () => await JumpToLiveAsync());

            ToggleConnectionCommand = new Command(async () => await ToggleConnectionAsync());
            PlaybackSpeedDownCommand = new Command(async () => await DecreasePlaybackSpeedStepAsync());
            PlaybackSpeedUpCommand = new Command(async () => await IncreasePlaybackSpeedStepAsync());
            PreviousCallCommand = new Command(async () => await NavigateToAdjacentCallAsync(1));
            NextCallCommand = new Command(async () => await NavigateToAdjacentCallAsync(-1));

            // React when filters change (mute / disable / clear).
            _filterService.RulesChanged += FilterServiceOnRulesChanged;

            try
            {
                if (_settingsService is SettingsService concrete)
                {
                    concrete.AddressDetectionSettingsChanged += (_, __) => OnAddressDetectionSettingsChanged();
                    concrete.What3WordsSettingsChanged += (_, __) =>
                    {
                        try { What3WordsLinksEnabled = _settingsService.What3WordsLinksEnabled; } catch { }
                    };
                }
            }
            catch
            {
            }


            // Initialize subscription badge from any cached subscription data.
            UpdateSubscriptionSummaryFromSettings();

            // System media controls (Bluetooth, lock screen, notification actions).
            _systemMediaService.SetHandlers(
                onPlay: SystemPlayAsync,
                onStop: SystemStopAsync,
                onNext: SystemNextAsync,
                onPrevious: SystemPreviousAsync);

            // When another tab (History, etc.) takes over playback, stop the live queue.
            QueueControlBus.StopMainQueueRequested += () =>
            {
                _ = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (_monitoring.Current != null)
                    {
                        try { await StopMonitoringAsync(); } catch { }
                    }
                });
            };

            // When another tab (History, etc.) opens, stop main tab audio playback if it is running.
            // This does not disconnect the live queue; it only stops audio.
            QueueControlBus.StopMainAudioRequested += () =>
            {
                try
                {
                    _ = MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        try { await StopAudioFromToggleAsync(); } catch { }
                    });
                }
                catch
                {
                }
            };

            _playbackOwnership.Changed += OnPlaybackOwnershipChanged;
        }

        private async Task ResumeMobilePlaybackAsync()
        {
            if (_playbackOwnership.Live is not { } live)
                return;
            if (_retryPlaybackCall is { } retry)
            {
                if (_playbackCommit.Pending is { } pending && pending.Id == retry.BackendId &&
                    DurableLiveQueue.ServerKey(retry.RecordingServer ?? retry.LiveState?.ServerUrl ?? "") == pending.Run.Server)
                {
                    var run = _monitoring.Current;
                    try
                    {
                        await FlushLiveCompletionAsync(live.Token);
                        if (_playbackOwnership.IsCurrent(live) && ReferenceEquals(_retryPlaybackCall, retry) && _monitoring.CanRecover(run))
                        {
                            SetPlaybackRetry(null);
                            await RequestQueuePlaybackAsync("PlaybackProgressSaved", true);
                        }
                    }
                    catch (OperationCanceledException) when (live.Token.IsCancellationRequested) { }
                    catch (Exception ex) { AppLog.Add(() => $"Live queue completion could not be saved ({ex.GetType().Name})."); }
                    return;
                }
                await OnCallTappedAsync(retry);
                return;
            }
#if ANDROID
            SystemMediaService.PrepareAndroidPlaybackService();
            AndroidAudioEnvironment.RequestPlay();
#elif IOS
            IosAudioEnvironment.RequestPlay();
#endif
#if ANDROID || IOS
            if (IsRunning && AudioEnabled)
                await RequestQueuePlaybackAsync("UserResumeAudio", true);
#else
            await Task.CompletedTask;
#endif
        }

        private async Task SkipUnavailableRecordingAsync()
        {
            var item = _retryPlaybackCall;
            var run = _monitoring.Current;
            if (_playbackOwnership.Live is not { } live || !CanSkipUnavailableRecording || item == null || !_monitoring.CanRecover(run) ||
                _durableRun is not { } durable || !TryGetDurableRecording(item, durable, out var id, out _))
                return;

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(run!.Token, live.Token);
            try
            {
                await _durableQueue.MarkAsync(durable, id, PlaybackOutcome.Skipped, cancellation.Token);
                if (_playbackOwnership.IsCurrent(live) && _monitoring.CanRecover(run) && ReferenceEquals(_retryPlaybackCall, item))
                {
                    SetPlaybackRetry(null);
                    LastQueueEvent = "Unavailable recording skipped.";
                    await RequestQueuePlaybackAsync("SkippedUnavailableRecording", true);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine(() => $"Live recording could not be skipped ({ex.GetType().Name}).");
                LastQueueEvent = "Could not save the skip. The recording is still pending.";
            }
        }

        private void ResumeAwaitingAudio(string? server, string id, LiveCallSnapshot? observation)
        {
            if (server == null || DurableLiveQueue.ServerKey(server) is not { } key)
                return;
            bool Matches(CallItem item) => item.BackendId == id &&
                DurableLiveQueue.ServerKey(item.RecordingServer ?? item.LiveState?.ServerUrl ?? "") == key;
            var url = observation?.AudioUrl;
            if (url != null && _currentPlayingCall is { } active && Matches(active) && active.AudioUrl != url)
                ApplyObservation(active);
            if (_awaitingAutomaticAudio is not { } waiting || !Matches(waiting))
                return;
            if (url == null)
            {
                if (_lastAudioNotificationRetry == (key, id))
                    return;
                _lastAudioNotificationRetry = (key, id);
            }
            else
            {
                if (waiting.AudioUrl == url)
                    return;
                ApplyObservation(waiting);
                _notifiedRecording = waiting;
            }
            SetPlaybackRetry(null);
            _ = RequestQueuePlaybackAsync("RecordingAudioUpdated", true);

            void ApplyObservation(CallItem item)
            {
                if (item.LiveState is { } state && observation != null)
                {
                    state.Observe(observation, LiveCallState.NextVersion());
                    item.ApplyLiveUpdate();
                }
                else if (url != null)
                    item.AudioUrl = url;
            }
        }

        private Task SystemPlayAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (_playbackOwnership.Live == null)
                    return;
                var run = _monitoring.Current;
                if (run is { IsActive: false })
                    return;

                if (CanResumePlayback)
                {
                    await ResumeMobilePlaybackAsync();
                    return;
                }
                run ??= await StartMonitoringCoreAsync();

                if (_monitoring.CanRecover(run) && AudioEnabled)
                    _ = RequestQueuePlaybackAsync("UserPlay", true);
            });
        }

#if IOS
        private void OnIosForegroundRecovered(DateTimeOffset since)
        {
            if (!_monitoring.CanRecover(_monitoring.Current))
                return;

            _iosForegroundRecovery?.Request(since);
            if (AudioEnabled && (_settingsService.AutoPlay || _userRunShouldDriveQueue))
                _ = RequestQueuePlaybackAsync("IosForegroundRecovery", _userRunShouldDriveQueue);
        }
#endif

        private Task SystemStopAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (_monitoring.Current != null)
                    await StopMonitoringAsync();
            });
        }

        private Task SystemNextAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (IsRunning)
                    await NavigateToAdjacentCallAsync(-1);
            });
        }

        private Task SystemPreviousAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (IsRunning)
                    await NavigateToAdjacentCallAsync(1);
            });
        }

        // True when connected to the server stream.
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value)
                    return;

                _isRunning = value;
                OnPropertyChanged();
                NotifyMonitoringControls();
                OnPropertyChanged(nameof(IsMediaButtonsEnabled));
                OnPropertyChanged(nameof(IsSpeedButtonsEnabled));

                if (!_isRunning)
                {
                    SetConnectionStatus(ConnectionStatus.Stopped);
                }
            }
        }

        private void NotifyMonitoringControls()
        {
            OnPropertyChanged(nameof(MediaPlayStopIcon));
            ((Command)StartCommand).ChangeCanExecute();
            ((Command)StopCommand).ChangeCanExecute();
        }

        // Indicates whether audio playback is enabled.
        // This does not affect streaming, only whether audio is played.
        public bool AudioEnabled
        {
            get => _audioEnabled;
            set
            {
                if (_audioEnabled == value)
                    return;

                _audioEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AudioButtonText));
                OnPropertyChanged(nameof(AudioButtonBackground));
                OnPropertyChanged(nameof(IsSpeedButtonsEnabled));

                AppStateStore.SetBool("audio_enabled", value);

                // Refresh UI state immediately, then do any heavier work on the UI thread.
                UpdateQueueDerivedState();

                var serial = global::System.Threading.Interlocked.Increment(ref _audioToggleSerial);

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (serial != _audioToggleSerial)
                        return;

                    if (!_audioEnabled)
                    {
                        try
                        {
                            await StopAudioFromToggleAsync();
                        }
                        catch
                        {
                        }

                        // When audio is off, keep the queue at live and do not hold calls in pending.
                        _currentPlayingCall = null;
                        // If the user was behind live, make any pending calls visible as transcription-only rows.
                        // This preserves what "Calls waiting" was indicating and prevents silent drops.
                        DrainPendingCallsToVisible();

                        _lastPlayedCall = Calls.Count > 0 ? Calls[0] : null;

                        UpdateQueueDerivedState();

                        try
                        {
                            if (IsRunning && _currentPlayingCall == null)
                                _systemMediaService.UpdateNowPlaying("Connected", "Joes Scanner", _audioEnabled);
                        }
                        catch
                        {
                        }

                        return;
                    }

                    // Audio toggled on: ensure anchor is sane and restart playback if needed.
                    if (_lastPlayedCall == null && Calls.Count > 0)
                        _lastPlayedCall = Calls[0];

                    UpdateQueueDerivedState();

                    try
                    {
                        if (IsRunning && _currentPlayingCall == null)
                            _systemMediaService.UpdateNowPlaying("Connected", "Joes Scanner", _audioEnabled);
                    }
                    catch
                    {
                    }

					if (IsRunning && _audioEnabled && (_settingsService.AutoPlay || _userRunShouldDriveQueue))
						_ = RequestQueuePlaybackAsync("AudioEnabledOn", userInitiated: _userRunShouldDriveQueue);
                });
            }
        }

		private Task RequestQueuePlaybackAsync(string reason, bool userInitiated)
		{
            if (_playbackOwnership.Live is not { } live)
                return Task.CompletedTask;
            var run = _monitoring.Current;
			return MainThread.InvokeOnMainThreadAsync(async () =>
			{
				try
				{
                    if (!_playbackOwnership.IsCurrent(live) || !_monitoring.CanRecover(run))
                        return;
					// Always pull AutoPlay from the settings service so the Settings page is the single source of truth.
					_autoPlay = _settingsService.AutoPlay;
            What3WordsLinksEnabled = _settingsService.What3WordsLinksEnabled;
					var canStart = _playbackCoordinator.CanStartQueuePlayback(
						reason,
						_settingsService.ServerUrl,
						userInitiated: userInitiated,
						isRunning: IsRunning,
						audioEnabled: AudioEnabled,
						isMainAudioSoftMuted: false,
						isAlreadyPlaying: _currentPlayingCall != null,
						isQueuePlaybackRunning: _isQueuePlaybackRunning,
						visibleQueueCount: _durableRun != null ? Math.Max(1, GetQueuedPlayableCandidateCount()) : GetQueuedPlayableCandidateCount());

					if (!canStart || _playbackRequests.HasManualSelection || _retryPlaybackCall != null)
						return;

                    _queueWake.Signal();
					await EnsureQueuePlaybackAsync(live);
				}
				catch
				{
				}
			});
		}


        // Text shown on the main page audio button.
        public string AudioButtonText => AudioEnabled ? "Audio On" : "Audio Off";

        // Background color of the main page audio button.
        // Blue when audio is enabled, gray when muted.
        public Color AudioButtonBackground => AudioEnabled ? Colors.Blue : Colors.LightGray;

        // Icon for the premium media play/stop button.
        // Shows play when disconnected, stop when connected.
        public string MediaPlayStopIcon => _monitoring.Current != null ? "mc_stop.png" : "mc_play.png";

        // Base server URL used for all API calls and displayed in the UI.
        public string ServerUrl
        {
            get => _serverUrl;
            set
            {
                var newValue = value ?? string.Empty;
                if (string.Equals(_serverUrl, newValue, StringComparison.Ordinal))
                    return;

                var oldValue = _serverUrl;

                _serverUrl = newValue;
                _settingsService.ServerUrl = _serverUrl;

                // Scope filter rules to the new server.
                _filterService.SetServerUrl(_serverUrl);

                OnPropertyChanged();
                OnPropertyChanged(nameof(TaglineText));

                // If the server changes, recompute whether we should show the subscription badge.
                UpdateSubscriptionSummaryFromSettings();

                // Contract: changing servers from Settings must stop queue playback and disconnect.
                // The user must explicitly press Play again to connect and resume.
                if (_monitoring.Current != null)
                {
                    _ = StopForServerChangeAsync(oldValue, _serverUrl);
                }
            }
        }

        private async Task StopForServerChangeAsync(string oldServerUrl, string newServerUrl)
        {
            try
            {
                AppLog.Add(() => $"Server changed in Settings. old={oldServerUrl} new={newServerUrl}. Stopping playback and disconnecting.");

                // Ensure we fully stop the monitoring loop and any in flight audio.
                await StopMonitoringAsync();

                // Provide a small hint in the queue status so it is obvious what happened.
                LastQueueEvent = "Server changed. Press Play to reconnect.";
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Error stopping for server change: {ex.Message}");
            }
        }

        private bool _changingHostedAccount;
        private readonly SemaphoreSlim _hostedAccountTransition = new(1);

        internal async Task ApplyHostedAccountAsync(string nextUsername, bool authorityChanged, Func<bool> canApply, Action save)
        {
            await _hostedAccountTransition.WaitAsync();
            try
            {
                if (!canApply())
                    return;
                var selected = _settingsService.ServerUrl ?? "";
                var switching = authorityChanged || RecordingPlaybackAccess.AccountKey(_settingsService, selected) !=
                    RecordingPlaybackAccess.AccountKeyForUsername(nextUsername);
                if (switching)
                {
                    _changingHostedAccount = true;
                    await StopMonitoringAsync();
                    if (canApply())
                        await ClearQueueForServerSwitchAsync();
                }
                if (canApply())
                    save();
            }
            finally
            {
                _changingHostedAccount = false;
                _hostedAccountTransition.Release();
            }
        }

        // Number of calls waiting in the playback queue, excluding the call at the current anchor.
        // Includes pending calls collected while the user is behind live.
        public int CallsWaiting => GetQueuedPlayableCandidateCount();

        private int GetPendingPlayableCount()
        {
            bool IsPlayable(CallItem c) =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.AudioUrl) &&
                !_filterService.ShouldHide(c) &&
                !_filterService.ShouldMute(c);

            var count = 0;

            lock (_pendingCallsLock)
            {
                for (var i = 0; i < _pendingCalls.Count; i++)
                {
                    if (IsPlayable(_pendingCalls[i]))
                        count++;
                }
            }

            return count;
        }

        // Total number of CallItem objects that have been received from the stream
        // since the last connect.
        public int TotalCallsReceived
        {
            get => _totalCallsReceived;
            private set
            {
                if (_totalCallsReceived == value)
                    return;

                _totalCallsReceived = value;
                OnPropertyChanged();
            }
        }

        // Total number of CallItem objects successfully queued for the current session.
        // Includes both visible Calls and hidden pending calls.
        public int TotalCallsInserted
        {
            get => _totalCallsInserted;
            private set
            {
                if (_totalCallsInserted == value)
                    return;

                _totalCallsInserted = value;
                OnPropertyChanged();
            }
        }

        // Last high level queue or audio event, for quick debugging.
        public string LastQueueEvent
        {
            get => _lastQueueEvent;
            private set
            {
                if (_lastQueueEvent == value)
                    return;

                _lastQueueEvent = value ?? string.Empty;
                OnPropertyChanged();

                AppLog.Add(() => _lastQueueEvent);
            }
        }

        // Whether the "Calls waiting" indicator should be visible in the UI.
        public bool IsCallsWaitingVisible => AudioEnabled;

        // Media button enable state (used by the ImageButtons in the header).
        // Requirement:
        // - When disconnected, all media buttons except Play/Stop should be disabled.
        public bool IsMediaButtonsEnabled => ConnectionStatusIsConnected;

        // Speed buttons are only meaningful when connected and audio is enabled.
        public bool IsSpeedButtonsEnabled => ConnectionStatusIsConnected && AudioEnabled;

        // Represents the current connection state of the call stream.
        private enum ConnectionStatus
        {
            Stopped,
            Connecting,
            Connected,
            AuthFailed,
            ServerUnreachable,
            Error
        }

        private ConnectionStatus _connectionStatus = ConnectionStatus.Stopped;

        private string _connectionStatusText = "Stopped";
        public string ConnectionStatusText
        {
            get => _connectionStatusText;
            private set
            {
                if (_connectionStatusText == value)
                    return;

                _connectionStatusText = value;
                OnPropertyChanged();
            }
        }

        private bool _connectionStatusIsConnected;
        public bool ConnectionStatusIsConnected
        {
            get => _connectionStatusIsConnected;
            private set
            {
                if (_connectionStatusIsConnected == value)
                    return;

                _connectionStatusIsConnected = value;
                OnPropertyChanged();
            }
        }

        private bool _connectionStatusIsWarning;
        public bool ConnectionStatusIsWarning
        {
            get => _connectionStatusIsWarning;
            private set
            {
                if (_connectionStatusIsWarning == value)
                    return;

                _connectionStatusIsWarning = value;
                OnPropertyChanged();
            }
        }

        private bool _connectionStatusIsError;
        public bool ConnectionStatusIsError
        {
            get => _connectionStatusIsError;
            private set
            {
                if (_connectionStatusIsError == value)
                    return;

                _connectionStatusIsError = value;
                OnPropertyChanged();
            }
        }

        private Color _connectionStatusColor = Colors.Gray;
        public Color ConnectionStatusColor
        {
            get => _connectionStatusColor;
            private set
            {
                if (_connectionStatusColor == value)
                    return;

                _connectionStatusColor = value;
                OnPropertyChanged();
            }
        }

        // Central helper to update the status and all derived properties in one place.
        private void SetConnectionStatus(ConnectionStatus status, string? detailMessage = null)
        {
            if (_connectionStatus == status && string.IsNullOrWhiteSpace(detailMessage))
                return;

            _connectionStatus = status;

            string text = status switch
            {
                ConnectionStatus.Stopped => "Stopped",
                ConnectionStatus.Connecting => "Connecting",
                ConnectionStatus.Connected => "Connected",
                ConnectionStatus.AuthFailed => "Authentication failed",
                ConnectionStatus.ServerUnreachable => "Server unreachable",
                ConnectionStatus.Error => "Error",
                _ => "Unknown"
            };

            if (!string.IsNullOrWhiteSpace(detailMessage))
            {
                text = $"{text}: {detailMessage}";
            }

            ConnectionStatusText = text;

            ConnectionStatusIsConnected = status == ConnectionStatus.Connected;
            ConnectionStatusIsWarning = status == ConnectionStatus.Connecting;
            ConnectionStatusIsError =
                status == ConnectionStatus.AuthFailed ||
                status == ConnectionStatus.ServerUnreachable ||
                status == ConnectionStatus.Error;

            Color color;
            if (ConnectionStatusIsConnected)
            {
                color = Color.FromArgb("#15803d");
            }
            else if (ConnectionStatusIsError)
            {
                color = Colors.Red;
            }
            else if (ConnectionStatusIsWarning)
            {
                color = Colors.Goldenrod;
            }
            else
            {
                color = Color.FromArgb("#6b7280");
            }

            ConnectionStatusColor = color;

            OnPropertyChanged(nameof(IsMediaButtonsEnabled));
            OnPropertyChanged(nameof(IsSpeedButtonsEnabled));

            AppLog.Add(() => $"Connection status changed to {status} ({text})");

            try
            {
                if (status == ConnectionStatus.Connected && _currentPlayingCall == null)
                {
                    _systemMediaService.UpdateNowPlaying("Connected", "Joes Scanner", AudioEnabled);
                }
            }
            catch
            {
            }
        }

        // Determine whether current server is the hosted Joe's Scanner service.
        private bool IsJoesScannerHostedServer()
        {
            var serverUrl = _settingsService.ServerUrl;
            if (string.IsNullOrWhiteSpace(serverUrl))
                return false;

            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
                return false;

            return HostedServerRules.IsHostedServerUri(uri);
        }

        // Public hook so SettingsViewModel can refresh the header badge after a validation run.
        public void RefreshSubscriptionSummaryFromSettings()
        {
            UpdateSubscriptionSummaryFromSettings();
        }

        // Build the subscription badge based on cached subscription info.
        private void UpdateSubscriptionSummaryFromSettings()
        {
            // Only show for the default Joe's Scanner hosted server, and only when account auth is set.
            if (!IsJoesScannerHostedServer() ||
                string.IsNullOrWhiteSpace(_settingsService.BasicAuthUsername))
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

            var levelLabel = _settingsService.SubscriptionLastLevel;
            var renewalUtc = _settingsService.SubscriptionRenewalUtc;
            var lastStatusOk = _settingsService.SubscriptionLastStatusOk;
            var lastMessage = _settingsService.SubscriptionLastMessage;

            string summary;

            if (!lastStatusOk)
            {
                if (!string.IsNullOrWhiteSpace(lastMessage))
                {
                    summary = lastMessage;
                }
                else if (!string.IsNullOrWhiteSpace(levelLabel))
                {
                    summary = $"{levelLabel} • status unknown";
                }
                else
                {
                    summary = "Subscription status unknown";
                }
            }
            else
            {
                if (renewalUtc.HasValue)
                {
                    var renewalLocal = DateTime.SpecifyKind(renewalUtc.Value, DateTimeKind.Utc)
                        .ToLocalTime()
                        .ToString("yyyy-MM-dd");

                    summary = !string.IsNullOrWhiteSpace(levelLabel)
                        ? $"{levelLabel} • renews {renewalLocal}"
                        : $"Renews {renewalLocal}";
                }
                else if (!string.IsNullOrWhiteSpace(lastMessage))
                {
                    // Covers trial accounts where there is no concrete renewal date yet.
                    summary = lastMessage;
                }
                else if (!string.IsNullOrWhiteSpace(levelLabel))
                {
                    summary = $"{levelLabel} • active";
                }
                else
                {
                    summary = "Subscription active";
                }
            }

            SubscriptionSummary = summary;
            ShowSubscriptionSummary = true;
        }

        private void ClearPendingCalls()
        {
            lock (_pendingCallsLock)
            {
                _pendingCalls.Clear();
            }
        }

        private void DrainPendingCallsToVisible()
        {
            List<CallItem> pending;

            lock (_pendingCallsLock)
            {
                if (_pendingCalls.Count == 0)
                    return;

                pending = new List<CallItem>(_pendingCalls);
                _pendingCalls.Clear();
            }

            for (var i = pending.Count - 1; i >= 0; i--)
            {
                if (!Calls.Contains(pending[i]))
                    InsertLiveCardInDisplayOrder(pending[i]);
            }
        }

        private void EnqueuePendingCall(CallItem call)
        {
            if (call == null)
                return;

            lock (_pendingCallsLock)
            {
                _pendingCalls.Insert(0, call);

                while (_pendingCalls.Count > MaxCallsToKeep)
                {
                    _pendingCalls.RemoveAt(_pendingCalls.Count - 1);
                }
            }
        }

        private bool TryPromoteOldestPendingCallToVisible(out CallItem? promoted)
        {
            promoted = null;

            lock (_pendingCallsLock)
            {
                if (_pendingCalls.Count == 0)
                    return false;

                // Oldest is at the end
                promoted = _pendingCalls[_pendingCalls.Count - 1];
                _pendingCalls.RemoveAt(_pendingCalls.Count - 1);
            }

            if (promoted == null)
                return false;

            InsertLiveCardInDisplayOrder(promoted);

            return true;
        }

        private bool ShouldHoldIncomingCalls()
        {
            if (!IsRunning)
                return false;
            // If audio is off, we are not stabilizing playback, so do not hold calls in pending.
            if (!AudioEnabled)
                return false;

            var anchor = _currentPlayingCall ?? _lastPlayedCall;
            if (anchor == null)
                return false;

            var idx = Calls.IndexOf(anchor);
            return idx > 0 || (idx < 0 && Calls.Count > 0 && HasDurablePosition(anchor) &&
                CompareCardOrder(anchor, Calls[0]) < 0);
        }

        private bool IsPlayableForQueue(CallItem? call)
        {
            return call != null &&
                   !string.IsNullOrWhiteSpace(call.AudioUrl) &&
                   !_filterService.ShouldHide(call) &&
                   !_filterService.ShouldMute(call);
        }

        private CallItem? PeekOldestPlayablePendingCall()
        {
            lock (_pendingCallsLock)
            {
                if (_pendingCalls.Count == 0)
                    return null;

                for (var i = _pendingCalls.Count - 1; i >= 0; i--)
                {
                    var candidate = _pendingCalls[i];
                    if (IsPlayableForQueue(candidate))
                        return candidate;
                }
            }

            return null;
        }

        private CallItem? PeekNextPlayableQueuedCallAfter(CallItem? currentItem)
        {
            if (currentItem == null)
                return null;

            if (Calls.Count == 0)
                return PeekOldestPlayablePendingCall();

            var anchorIndex = Calls.IndexOf(currentItem);
            if (anchorIndex < 0)
            {
                if (_durableRun is { } durable && TryGetDurableRecording(currentItem, durable, out _, out _))
                    return null;
                anchorIndex = Calls.Count - 1;
            }

            for (var i = anchorIndex - 1; i >= 0; i--)
            {
                var candidate = Calls[i];
                if (IsPlayableForQueue(candidate))
                    return candidate;
            }

            return PeekOldestPlayablePendingCall();
        }

        private int GetQueuedPlayableCandidateCount()
        {
            var count = 0;

            if (Calls != null && Calls.Count > 0)
            {
                var anchor = _currentPlayingCall ?? _lastPlayedCall;
                var startIndex = Calls.Count - 1;
                var offscreenAnchor = false;

                if (anchor != null)
                {
                    var anchorIndex = Calls.IndexOf(anchor);
                    if (anchorIndex >= 0)
                        startIndex = anchorIndex - 1;
                    else
                        offscreenAnchor = HasDurablePosition(anchor);
                }

                for (var i = Calls.Count - 1; i >= 0; i--)
                {
                    var call = Calls[i];
                    var newer = offscreenAnchor
                        ? CompareCardOrder(call, anchor!) > 0
                        : i <= startIndex;
                    if ((newer || (call.HasUnplayedLateAudio && call != _currentPlayingCall)) &&
                        IsPlayableForQueue(call))
                        count++;
                }
            }

            count += GetPendingPlayableCount();
            return count;
        }

        private void ClearPrefetchedPlayableUrl(bool deleteCompletedTempFile)
        {
            Task<string?>? priorTask = null;
            CancellationTokenSource? priorCts = null;

            lock (_prefetchLock)
            {
                priorTask = _prefetchedPlayableUrlTask;
                priorCts = _prefetchCts;
                _prefetchedPlayableUrlTask = null;
                _prefetchedCall = null;
                _prefetchCts = null;
            }

            if (priorTask != null && priorCts != null)
                _ = CancelPrefetchAsync(priorTask, priorCts, deleteCompletedTempFile);
        }

        private static async Task CancelPrefetchAsync(Task<string?> prefetch, CancellationTokenSource source, bool deleteTempFile)
        {
            using (source)
            {
                try
                {
                    source.Cancel();
                    var path = await prefetch.ConfigureAwait(false);
                    if (deleteTempFile && IsDownloadedAudioTempFile(path))
                        TryDeleteFile(path!);
                }
                catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, source.Token))
                {
                }
                catch (Exception ex)
                {
                    AppLog.DebugWriteLine(() => $"Live audio prefetch failed: {ex.GetType().Name}");
                }
            }
        }

        private async Task<string?> TryTakePrefetchedPlayableUrlAsync(CallItem item, CancellationToken cancellationToken)
        {
            Task<string?>? prefetchedTask = null;
            CancellationTokenSource? prefetchedCts = null;

            lock (_prefetchLock)
            {
                if (!ReferenceEquals(_prefetchedCall, item) || _prefetchedPlayableUrlTask == null)
                    return null;

                prefetchedTask = _prefetchedPlayableUrlTask;
                prefetchedCts = _prefetchCts;
                _prefetchedPlayableUrlTask = null;
                _prefetchedCall = null;
                _prefetchCts = null;
            }

            try
            {
                return await prefetchedTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, cancellationToken))
            {
                if (prefetchedCts != null)
                {
                    _ = CancelPrefetchAsync(prefetchedTask, prefetchedCts, deleteTempFile: true);
                    prefetchedCts = null;
                }
                return null;
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine(() => $"Live audio prefetch failed; trying a foreground request: {ex.GetType().Name}");
                return null;
            }
            finally
            {
                try { prefetchedCts?.Dispose(); } catch { }
            }
        }

        private void StartPlayableUrlPrefetchForUpcomingCall(CallItem currentItem)
        {
            var nextCall = PeekNextPlayableQueuedCallAfter(currentItem);
            var server = nextCall?.RecordingServer ?? nextCall?.LiveState?.ServerUrl ?? _settingsService.ServerUrl;
            // Protected acquisitions always authorize the exact recording first.
            if (nextCall?.RecordingAccess?.Protected == true || HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server))
                nextCall = null;
            if (nextCall == null || string.IsNullOrWhiteSpace(nextCall.AudioUrl))
            {
                ClearPrefetchedPlayableUrl(deleteCompletedTempFile: true);
                return;
            }

            lock (_prefetchLock)
            {
                if (ReferenceEquals(_prefetchedCall, nextCall) && _prefetchedPlayableUrlTask != null)
                    return;
            }

            ClearPrefetchedPlayableUrl(deleteCompletedTempFile: true);

            CancellationTokenSource? prefetchCts = null;
            Task<string?>? prefetchTask = null;

            try
            {
                prefetchCts = new CancellationTokenSource();
                prefetchTask = GetPlayableAudioUrlAsync(nextCall.AudioUrl, prefetchCts.Token, forceDownload: true);
            }
            catch
            {
                try { prefetchCts?.Dispose(); } catch { }
                return;
            }

            lock (_prefetchLock)
            {
                _prefetchedCall = nextCall;
                _prefetchedPlayableUrlTask = prefetchTask;
                _prefetchCts = prefetchCts;
            }
        }

        // Returns the next call to play from the backlog.
        // When the user is behind live, new incoming calls are held in _pendingCalls so playback remains stable.
        private CallItem? GetNextQueuedCall()
        {
            var isAppleTestAccount = IsAppleIosTestAccount();

            if (Calls.Count == 0)
            {
                // Nothing visible yet. If we have pending calls, promote the oldest so it can be played.
                if (TryPromoteOldestPendingCallToVisible(out var promotedFromEmpty))
                    return promotedFromEmpty;

                return null;
            }

            if (_currentPlayingCall != null)
                return null;

            int startIndex;

            if (_lastPlayedCall == null)
            {
                startIndex = Calls.Count - 1;
            }
            else
            {
                var anchorIndex = Calls.IndexOf(_lastPlayedCall);

                if (anchorIndex < 0)
                {
                    startIndex = Calls.Count - 1;
                }
                else
                {
                    startIndex = anchorIndex - 1;
                }
            }

            // A previously silent card can become playable after the cursor passed it.
            // Take these in existing list order before moving farther forward.
            for (var i = Calls.Count - 1; i > startIndex; i--)
            {
                if (Calls[i].HasUnplayedLateAudio)
                    return Calls[i];
            }

            CallItem? skippable = null;

            for (var i = startIndex; i >= 0; i--)
            {
                var candidate = Calls[i];

                if (candidate == null)
                    continue;

                if (string.IsNullOrWhiteSpace(candidate.AudioUrl))
                    continue;

                if (isAppleTestAccount)
                {
                    return candidate;
                }

                if (_filterService.ShouldHide(candidate))
                {
                    skippable ??= candidate;
                    continue;
                }

                if (_filterService.ShouldMute(candidate))
                {
                    skippable ??= candidate;
                    continue;
                }

                return candidate;
            }

            // No playable calls in the visible backlog.
            // If a muted or hidden call exists, return it so playback can advance past it.
            if (skippable != null)
                return skippable;

            // If we are behind live but the remaining visible calls are not playable, promote pending calls anyway.
            if (TryPromoteOldestPendingCallToVisible(out var promotedFromBehind))
                return promotedFromBehind;

            return null;
        }

        private async void FilterServiceOnRulesChanged(object? sender, EventArgs e)
        {
            try
            {
                await MainThread.InvokeOnMainThreadAsync(ApplyFiltersToExistingCalls);
            }
            catch
            {
            }
        }

        private void ApplyFiltersToExistingCalls()
        {
            if (Calls != null && Calls.Count > 0)
            {
                var removedCount = 0;

                for (int i = Calls.Count - 1; i >= 0; i--)
                {
                    var call = Calls[i];
                    if (_filterService.ShouldHide(call))
                    {
                        Calls.RemoveAt(i);
                        removedCount++;
                    }
                }

                if (removedCount > 0)
                {
                    LastQueueEvent = $"Filters updated: removed {removedCount} calls at {DateTime.Now:T}";
                    UpdateQueueDerivedState();
                }
            }

            // Also prune pending calls
            var removedPending = 0;
            lock (_pendingCallsLock)
            {
                for (int i = _pendingCalls.Count - 1; i >= 0; i--)
                {
                    var call = _pendingCalls[i];
                    if (_filterService.ShouldHide(call))
                    {
                        _pendingCalls.RemoveAt(i);
                        removedPending++;
                    }
                }
            }

            if (removedPending > 0)
            {
                LastQueueEvent = $"Filters updated: removed {removedPending} pending calls at {DateTime.Now:T}";
                UpdateQueueDerivedState();
            }
        }

        // Numbered playback speed step used by the slider on the main page.
        public double PlaybackSpeedStep
        {
            get => _playbackSpeedStep;
            set
            {
                var clamped = value;
                if (clamped < 0) clamped = 0;
                if (clamped > 4) clamped = 4;
                clamped = Math.Round(clamped);

                if (Math.Abs(_playbackSpeedStep - clamped) < 0.001)
                    return;

                _playbackSpeedStep = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlaybackSpeedLabel));

                AppStateStore.SetDouble("playback_speed_step", _playbackSpeedStep);
            }
        }

        public string PlaybackSpeedLabel =>
            _playbackSpeedStep switch
            {
                1 => "1.25x",
                2 => "1.5x",
                3 => "1.75x",
                4 => "2x",
                _ => "1x"
            };

        public bool What3WordsLinksEnabled
        {
            get => _what3WordsLinksEnabled;
            private set
            {
                if (_what3WordsLinksEnabled == value)
                    return;

                _what3WordsLinksEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool AutoPlay
        {
			get
			{
				// Always reflect the latest value from the Settings DB.
				// This prevents stale values from forcing autoplay after the user turns it off in Settings.
				_autoPlay = _settingsService.AutoPlay;
            What3WordsLinksEnabled = _settingsService.What3WordsLinksEnabled;
				return _autoPlay;
			}
            set
            {
				if (_settingsService.AutoPlay == value && _autoPlay == value)
                    return;

                _autoPlay = value;
                _settingsService.AutoPlay = value;
                OnPropertyChanged();
            }
        }

        public string TaglineText => $"Server: {ServerUrl}";
        public string DonateUrl => OperatingSystem.IsIOS()
            ? "https://joesscanner.com"
            : "https://www.joesscanner.com/products/one-time-donation/";
        public void Start()
        {
            AppLog.Add("User clicked Start Monitoring");
            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    var run = await StartMonitoringCoreAsync(driveQueue: true);
                    if (_monitoring.CanRecover(run) && AudioEnabled)
                        _ = RequestQueuePlaybackAsync("UserStartMonitoring", userInitiated: true);
                }
                catch
                {
                }
            });
        }

        public Task StartMonitoringAsync() =>
            MainThread.InvokeOnMainThreadAsync(async () => { await StartMonitoringCoreAsync(); });

        private async Task<MonitoringLifecycle.Run?> StartMonitoringCoreAsync(bool driveQueue = false,
            long? automaticStopRevision = null)
        {
            if (_changingHostedAccount)
                return null;
            var run = _monitoring.TryBeginStart(automaticStopRevision);
            if (run == null)
                return null;

            var intentRevision = _monitoringIntent.BeginStart();
            var token = run.Token;
            try
            {
                NotifyMonitoringControls();
                SetConnectionStatus(ConnectionStatus.Connecting);
                var serverUrl = _settingsService.ServerUrl ?? string.Empty;
                if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
                {
                    SetConnectionStatus(ConnectionStatus.Error, "Select a server in Settings and press Validate before connecting.");
                    return null;
                }

#if ANDROID
                // Request foreground promotion while an activity is still visible.
                // The old run can drain audio while the replacement is promoted.
                SystemMediaService.BeginAndroidMonitoringRun(run.Id);
#endif
                await run.WaitForInitializationAsync(run.PreviousCleanup);
                token.ThrowIfCancellationRequested();
                try { await FlushLiveCompletionAsync(token); }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    AppLog.Add(() => $"Live queue completion could not be saved before connecting ({ex.GetType().Name}).");
                    SetConnectionStatus(ConnectionStatus.Error, "Could not save playback progress. Try connecting again.");
                    return null;
                }
                token.ThrowIfCancellationRequested();

                var isJoesScannerServer = HostedServerRules.IsHostedServerUri(serverUri);
                _telemetryService.TrackConnectionAttempt(serverUrl, isJoesScannerServer);

                var protectedServer = HostedServerRules.RequiresApiFirewallCredentials(_settingsService, serverUrl);
                var account = protectedServer ? RecordingPlaybackAccess.AccountKey(_settingsService, serverUrl) : null;
                var serverKey = DurableLiveQueue.ServerKey(serverUrl);
                _startingMonitoringScope = (run.Id, serverKey, account);
                int? verifiedTier = null;
                if (protectedServer)
                {
                    AppLog.Add("Verifying Joe's Scanner subscription before connecting.");
                    try
                    {
                        var result = await _subscriptionService.EnsureSubscriptionAsync(token).WaitAsync(token);
                        token.ThrowIfCancellationRequested();
                        if (!result.IsAllowed || account == null ||
                            account != RecordingPlaybackAccess.AccountKey(_settingsService, serverUrl))
                        {
                            SetConnectionStatus(ConnectionStatus.AuthFailed, result.Message ?? "Subscription not active");
                            return null;
                        }

                        UpdateSubscriptionSummaryFromSettings();
                        verifiedTier = result.VerifiedOnlineTier;
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        AppLog.Add(() => $"Subscription check failed ({ex.GetType().Name}).");
                        SetConnectionStatus(ConnectionStatus.Error, "Subscription check error");
                        return null;
                    }
                }
                else
                {
                    ShowSubscriptionSummary = false;
                    SubscriptionSummary = string.Empty;
                }

                await EnsureSystemMediaSessionStartedAsync(run);
                token.ThrowIfCancellationRequested();

                LiveStreamRecovery? recovery = null;
#if ANDROID
                if (_playbackOwnership.Live != null)
                    AndroidAudioEnvironment.RequestPlay();
#elif IOS
                if (!automaticStopRevision.HasValue && _playbackOwnership.Live != null)
                    IosAudioEnvironment.RequestPlay();
                recovery = _iosForegroundRecovery = new LiveStreamRecovery();
#endif

                if (IsAppleIosTestAccount())
                {
                    AudioEnabled = true;
                    AutoPlay = true;
                }

                _currentPlayingCall = null;
                SetPlaybackRetry(null);
                foreach (var card in Calls)
                    card.ConsumeLateAudio();
                ClearPendingCalls();
                ClearPrefetchedPlayableUrl(deleteCompletedTempFile: true);
                _lastPlayedCall = Calls.FirstOrDefault();

                var durable = serverKey != null
                    ? new DurableLiveQueue.Run(serverKey, run.Id, verifiedTier, protectedServer, account)
                    : null;
                if (durable != null && !IsCurrentLiveScope(durable))
                    return null;
                _durableRun = durable;
                if (durable != null)
                {
                    try
                    {
                        var explicitlyStopped = await Task.Run(() =>
                            _monitoringIntent.WasExplicitlyStopped(durable.Server, durable.Account, durable.Protected), token);
                        await _durableQueue.OpenAsync(durable, DateTimeOffset.UtcNow, token, explicitlyStopped);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        _durableRun = null;
                        AppLog.Add(() => $"Live queue storage could not be opened ({ex.GetType().Name}).");
                        SetConnectionStatus(ConnectionStatus.Error, "Live queue storage is unavailable. Try connecting again.");
                        return null;
                    }
                }
                token.ThrowIfCancellationRequested();

                if (durable != null)
                    await _monitoringIntent.SaveStartedAsync(intentRevision, durable.Server, durable.Account, token);
                token.ThrowIfCancellationRequested();
                if ((durable != null && !IsCurrentLiveScope(durable)) || !_monitoring.Activate(run))
                    return null;

                _userRunShouldDriveQueue = driveQueue;
                IsRunning = true;
                UpdateQueueDerivedState();
                if (!_monitoring.CanRecover(run))
                    return null;
                _ = Task.Run(() => RunCallStreamLoopAsync(run, recovery, durable), token);
                _telemetryService.StartMonitoringHeartbeat(serverUrl);

                _ = Task.Run(() => _historyLookupsCacheService.PreloadAsync(
                    forceReload: false, reason: "connect", cancellationToken: token), token);
                if (durable != null && (_settingsService.AutoPlay || driveQueue))
                    _ = RequestQueuePlaybackAsync("RestoreLocalLiveQueue", driveQueue);
                return run;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                await _monitoring.AbortStartAsync(run, RetireMonitoringPlatform, CleanupMonitoringRunAsync);
            }
        }

        public Task StartMonitoringWithAutoplayAsync() => RestoreMonitoringAsync(driveQueue: true);

        private async Task EnsureSystemMediaSessionStartedAsync(MonitoringLifecycle.Run run)
        {
#if ANDROID
            await EnsureAndroidNotificationPermissionAsync(run);
#endif
            run.Token.ThrowIfCancellationRequested();
            try
            {
                await _systemMediaService.StartSessionAsync(AudioEnabled, run.Id);
                run.Token.ThrowIfCancellationRequested();
                _systemMediaService.UpdateNowPlaying("Connected", "Joes Scanner", AudioEnabled);
            }
            catch (Exception ex) when (!run.Token.IsCancellationRequested)
            {
                AppLog.DebugWriteLine(() => $"Could not initialize monitoring media controls ({ex.GetType().Name}).");
            }
        }

        private bool IsAppleIosTestAccount()
        {
            var username = _settingsService.BasicAuthUsername ?? string.Empty;
            username = username.Trim();
            return string.Equals(username, AppleIosTestAccountEmail, StringComparison.OrdinalIgnoreCase)
                || string.Equals(username, AppleIosTestAccountEmailLegacy, StringComparison.OrdinalIgnoreCase);
        }

#if ANDROID
        private async Task EnsureAndroidNotificationPermissionAsync(MonitoringLifecycle.Run run)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33))
                return;

            if (_androidNotificationPermissionPrompt is not { IsCompleted: false })
            {
                PermissionStatus status;
                try
                {
                    status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>().WaitAsync(run.Token);
                }
                catch (Exception ex) when (!run.Token.IsCancellationRequested)
                {
                    AppLog.DebugWriteLine(() => $"Could not check notification permission ({ex.GetType().Name}).");
                    return;
                }
                run.Token.ThrowIfCancellationRequested();
                if (status == PermissionStatus.Granted)
                    return;
                _androidNotificationPermissionPrompt = RequestAndroidNotificationPermissionAsync();
            }

            await run.WaitForInitializationAsync(_androidNotificationPermissionPrompt);
        }

        private static async Task RequestAndroidNotificationPermissionAsync()
        {
            try
            {
                await Permissions.RequestAsync<Permissions.PostNotifications>();
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine(() => $"Could not request notification permission ({ex.GetType().Name}).");
            }
        }
#endif

        private async Task RunCallStreamLoopAsync(MonitoringLifecycle.Run run, LiveStreamRecovery? recovery, DurableLiveQueue.Run? durable)
        {
            var token = run.Token;
            var retryDelay = TimeSpan.FromSeconds(2);

            // Important behavior contract:
            // This loop must continue attempting to reconnect until the user explicitly clicks Stop.
            // It must never terminate itself due to transient network, polling, or WebSocket failures.
            while (!token.IsCancellationRequested && (durable == null || IsCurrentLiveScope(durable)))
            {
                try
                {
                    try
                    {
                        await foreach (var call in _callStreamService.GetCallStreamAsync(
                            await MainThread.InvokeOnMainThreadAsync(SnapshotLiveCalls), token, recovery))
                        {
                            if (token.IsCancellationRequested || durable != null && !IsCurrentLiveScope(durable))
                                break;

                            if (call == null)
                                continue;

                            if (call.IsRecordingNotification)
                            {
                                var notification = call.LiveState;
                                var saved = durable != null && notification != null &&
                                    DurableLiveQueue.ServerKey(notification.ServerUrl) == durable.Server &&
                                    await SaveLiveObservationAsync(durable, notification.Id, null, notification.Read(),
                                        RecordingOrigin.Unknown, existingOnly: true, token);
                                await MainThread.InvokeOnMainThreadAsync(() =>
                                {
                                    if (!_monitoring.CanRecover(run) || durable != null && !IsCurrentLiveScope(durable))
                                        return;
                                    ResumeAwaitingAudio(call.RecordingServer, call.BackendId, notification?.Read());
                                    if (saved)
                                        RequestQueueForLiveArrival("SavedRecordingNotification");
                                });
                                continue;
                            }

                            var savedObservation = durable != null && call.LiveState is { } observed &&
                                DurableLiveQueue.ServerKey(observed.ServerUrl) == durable.Server &&
                                await SaveLiveObservationAsync(durable, observed.Id, call.RecordingTimeUtc, observed.Read(),
                                    call.RecordingAccess?.Origin ?? RecordingOrigin.Unknown, call.IsLiveUpdate, token);

                            try
                            {
                                await MainThread.InvokeOnMainThreadAsync(() =>
                                {
                                    if (!_monitoring.CanRecover(run) || durable != null && !IsCurrentLiveScope(durable))
                                        return;

                                    if (!call.IsLiveUpdate && call.RecordingAccess is { } access)
                                    {
                                        call.LivePlaybackOwner = run.Id;
                                        call.RecordingAccess = access with { Account = durable?.Account };
                                    }
                                    if (call.LiveState is { } audioObservation && audioObservation.Read() is { AudioUrl: not null } audioSnapshot)
                                        ResumeAwaitingAudio(audioObservation.ServerUrl, audioObservation.Id, audioSnapshot);

                                    if (!call.IsLiveUpdate || !string.IsNullOrEmpty(call.Transcription))
                                        TotalCallsReceived++;

                                    if (call.IsLiveUpdate && call.LiveState is { } state)
                                    {
                                        ApplyLiveUpdate(state, call);
                                        RequestQueueForLiveArrival("UpdatedLiveRecording");
                                        return;
                                    }
                                    if (call.LiveState is { } incoming && FindLiveCard(incoming) is { } restored)
                                    {
                                        ApplyLiveUpdate(incoming, call, restored);
                                        RequestQueueForLiveArrival("UpdatedRestoredRecording");
                                        return;
                                    }

                                    // Status sentinel messages
                                    if (string.Equals(call.Talkgroup, "AUTH", StringComparison.OrdinalIgnoreCase))
                                    {
                                        SetConnectionStatus(ConnectionStatus.AuthFailed, call.Transcription);
                                        return;
                                    }

                                    if (string.Equals(call.Talkgroup, "ERROR", StringComparison.OrdinalIgnoreCase))
                                    {
                                        SetConnectionStatus(ConnectionStatus.ServerUnreachable);
                                        return;
                                    }

                                    if (string.Equals(call.Talkgroup, "HEARTBEAT", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (_connectionStatus == ConnectionStatus.Connecting ||
                                            _connectionStatus == ConnectionStatus.AuthFailed ||
                                            _connectionStatus == ConnectionStatus.ServerUnreachable ||
                                            _connectionStatus == ConnectionStatus.Error)
                                        {
                                            SetConnectionStatus(ConnectionStatus.Connected);
                                        }

                                        return;
                                    }

                                    if (_connectionStatus == ConnectionStatus.Connecting ||
                                        _connectionStatus == ConnectionStatus.AuthFailed ||
                                        _connectionStatus == ConnectionStatus.ServerUnreachable ||
                                        _connectionStatus == ConnectionStatus.Error)
                                    {
                                        SetConnectionStatus(ConnectionStatus.Connected);
                                    }


                                    var isAppleTestAccount = IsAppleIosTestAccount();

                                    // Ensure filter rules exist and apply filters.
                                    // For the Apple iOS test account, bypass hide and mute so calls always appear and play.
                                    _filterService.EnsureRulesForCall(call);

                                    if (!isAppleTestAccount)
                                    {
                                        if (_filterService.ShouldHide(call))
                                        {
                                            LastQueueEvent = $"Call dropped by filter at {DateTime.Now:T}";
                                            if (savedObservation)
                                                RequestQueueForLiveArrival("SavedFilteredRecording");
                                            return;
                                        }

                                        call.IsMutedByFilter = _filterService.ShouldMute(call);
                                    }
                                    else
                                    {
                                        call.IsMutedByFilter = false;
                                    }

                                    // Address detection and what3words should be applied for brand new calls too.
                                    // On iOS, the stream often sends the final transcription on the initial item,
                                    // so relying on later transcription update items can prevent alerts from ever appearing.
                                    try
                                    {
                                        var hadAddress = call.HasDetectedAddress;
                                        try { _addressDetectionService.Apply(call); } catch { }
                                        if (!hadAddress && call.HasDetectedAddress)
                                            AddAddressAlertIfNew(call);

                                        try { _ = _what3WordsService.ResolveCoordinatesIfNeededAsync(call, CancellationToken.None); } catch { }
                                    }
                                    catch
                                    {
                                    }

                                    // If the user is behind live (replaying from an older point), hold incoming calls as pending.
                                    if (ShouldHoldIncomingCalls())
                                    {
                                        EnqueuePendingCall(call);
                                        TotalCallsInserted++;
                                        LastQueueEvent = $"Inserted call (pending) at {DateTime.Now:T}";
                                        UpdateQueueDerivedState();
                                    }
                                    else
                                    {
                                        InsertLiveCardInDisplayOrder(call);
                                        TotalCallsInserted++;
                                        LastQueueEvent = $"Inserted call at {DateTime.Now:T}";
                                        UpdateQueueDerivedState();
                                    }

                                    RequestQueueForLiveArrival("NewCallInserted");
                                });
                            }
                            catch (Exception ex)
                            {
                                AppLog.DebugWriteLine(() => $"Error updating UI for call: {ex}");
                                if (savedObservation)
                                {
                                    await MainThread.InvokeOnMainThreadAsync(() =>
                                    {
                                        if (_monitoring.CanRecover(run))
                                            RequestQueueForLiveArrival("SavedRecording");
                                    });
                                }
                            }
                        }

                        // The stream should only end on cancellation. If it ends for any other reason,
                        // treat it as a transient disconnect and retry.
                        if (!token.IsCancellationRequested)
                        {
                            try
                            {
                                await MainThread.InvokeOnMainThreadAsync(() =>
                                {
                                    if (_monitoring.CanRecover(run))
                                        SetConnectionStatus(ConnectionStatus.ServerUnreachable, "Stream ended. Reconnecting.");
                                });
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLog.DebugWriteLine(() => $"Error in call stream loop: {ex}");

                        try
                        {
                            await MainThread.InvokeOnMainThreadAsync(() =>
                            {
                                if (_monitoring.CanRecover(run))
                                    SetConnectionStatus(ConnectionStatus.ServerUnreachable, ex.Message);
                            });
                        }
                        catch
                        {
                        }
                    }

                    if (token.IsCancellationRequested)
                        break;

                    // Transition to Connecting during the retry window so the UI reflects an auto reconnect.
                    try
                    {
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            if (!_monitoring.CanRecover(run))
                                return;

                            SetConnectionStatus(ConnectionStatus.Connecting);
                        });
                    }
                    catch
                    {
                    }

                    try
                    {
                        await Task.Delay(retryDelay, token);
                    }
                    catch
                    {
                    }
                }
                catch (Exception ex)
                {
                    // Absolute guard: do not allow an unexpected exception to terminate monitoring.
                    if (token.IsCancellationRequested)
                        break;

                    AppLog.Add(() => $"Call stream loop guard caught exception: {ex.Message}");

                    try
                    {
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            if (_monitoring.CanRecover(run))
                                SetConnectionStatus(ConnectionStatus.ServerUnreachable, "Unexpected stream error. Reconnecting.");
                        });
                    }
                    catch
                    {
                    }

                    try
                    {
                        await Task.Delay(retryDelay, token);
                    }
                    catch
                    {
                    }
                }
            }

        }

        private Task<bool> SaveLiveObservationAsync(DurableLiveQueue.Run run, string id, DateTimeOffset? recordedAt,
            LiveCallSnapshot snapshot, RecordingOrigin origin, bool existingOnly, CancellationToken token) =>
            DurableLiveQueue.PersistObservationAsync(
                () => _durableQueue.ObserveAsync(run, id, recordedAt, snapshot, token, origin, existingOnly),
                ex => AppLog.Add(() => $"Live queue storage unavailable; retrying the recording ({ex.GetType().Name})."), token);

        public Task StopMonitoringAsync() =>
            MainThread.InvokeOnMainThreadAsync(StopMonitoringCoreAsync);

        private Task StopMonitoringCoreAsync()
        {
            var durable = _durableRun;
            Task? saveIntent = null;
            var cleanup = _monitoring.StopAsync(run => saveIntent = RecordMonitoringStop(run), RetireMonitoringPlatform, async run =>
            {
                await CleanupMonitoringRunAsync(run);
                if (durable?.Owner == run.Id)
                {
                    try
                    {
                        await FlushLiveCompletionAsync();
                        await _durableQueue.StopAsync(durable);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Add(() => $"Live queue Stop could not be saved ({ex.GetType().Name}).");
                    }
                }
            });
            return saveIntent == null ? cleanup : Task.WhenAll(cleanup, saveIntent);
        }

        private Task RecordMonitoringStop(MonitoringLifecycle.Run? run)
        {
            var stopped = _durableRun;
            var starting = _startingMonitoringScope.Owner == run?.Id ? _startingMonitoringScope : default;
            // The veto depends only on captured run state, even if reading
            // connection settings would currently need SQLite.
            var saveIntent = _monitoringIntent.SaveStoppedAsync(stopped?.Server ?? starting.Server,
                stopped != null ? stopped.Account : starting.Account);
            _startingMonitoringScope = default;
            _durableRun = null;
            _notifiedRecording = null;
            _lastAudioNotificationRetry = null;
            AppLog.Add("Stop monitoring requested.");
#if IOS
            _iosForegroundRecovery = null;
#endif
            InvalidateLivePlayback();
            _resumeAfterHistory = false;
            _userRunShouldDriveQueue = false;

            if (run?.IsActive == true)
                _telemetryService.StopMonitoringHeartbeat("user_stop");

            IsRunning = false;
            SetConnectionStatus(ConnectionStatus.Stopped);
            ClearPendingCalls();
            _lastPlayedCall = null;
            SetPlaybackRetry(null);
            UpdateQueueDerivedState();
            return saveIntent;
        }

        private void RetireMonitoringPlatform(MonitoringLifecycle.Run run)
        {
#if ANDROID
            try { SystemMediaService.EndAndroidMonitoringRun(run.Id); }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine(() => $"Could not stop the Android audio service ({ex.GetType().Name}).");
            }
#endif
            NotifyMonitoringControls();
        }

        private async Task CleanupMonitoringRunAsync(MonitoringLifecycle.Run run)
        {
            // Detach remote commands before waiting for an outgoing clip to drain.
            try { await _systemMediaService.StopSessionAsync(run.Id); }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine(() => $"Could not close monitoring media controls ({ex.GetType().Name}).");
            }

#if ANDROID
            if (_playbackOwnership.Live != null)
                AndroidAudioEnvironment.RequestPlay();
#endif
        }

        private (string? Server, string? Account) CurrentMonitoringScope()
        {
            var selected = _settingsService.ServerUrl ?? "";
            return (DurableLiveQueue.ServerKey(selected),
                HostedServerRules.RequiresApiFirewallCredentials(_settingsService, selected)
                    ? RecordingPlaybackAccess.AccountKey(_settingsService, selected) : null);
        }

        // Clears the visible main queue (Calls), any pending calls, and any address alerts.
        // Used when the user validates a different server so we don't show calls from the prior server.
        public async Task ClearQueueForServerSwitchAsync()
        {
            try
            {
                ClearPendingCalls();
            }
            catch
            {
            }

            try
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    try
                    {
                        Calls.Clear();
                    }
                    catch
                    {
                    }

                    try
                    {
                        AddressAlerts.Clear();
                        _hiddenAddressAlerts.Clear();
                        OnPropertyChanged(nameof(HasAddressAlerts));
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }

            _currentPlayingCall = null;
            _lastPlayedCall = null;
            SetPlaybackRetry(null);
            UpdateQueueDerivedState();
        }

        public Task RestartMonitoringIfRunningAsync() =>
            MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (!_monitoring.CanRecover(_monitoring.Current))
                    return;

                var stop = StopMonitoringCoreAsync();
                var revision = _monitoring.StopRevision;
                await stop;
                if (revision == _monitoring.StopRevision)
                    await StartMonitoringCoreAsync();
            });

        public Task StopAudioFromToggleAsync()
        {
            _resumeAfterHistory = false;
            InvalidateLivePlayback();
            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));
            return Task.CompletedTask;
        }

        public Task StopAudioFromSettingsAsync()
        {
            return StopAudioFromToggleAsync();
        }

        private Task OnCallTappedAsync(CallItem? item)
		{
			return OnCallTappedAsync(item, preserveQueueAnchor: false);
		}

		// Address alert taps should play the call and then continue the queue starting from that call.
		public async Task PlayFromAddressAlertAsync(CallItem? item)
		{
			if (item == null)
				return;

			// Address alerts can outlive the underlying call if the call falls out of the in-memory queue.
			// If the call is gone, retire the alert so tapping it can't cause navigation/playback issues.
			var resolved = ResolveCallForAddressAlert(item);
			if (resolved == null)
			{
				DismissAddressAlert(item);
				AppLog.Add(() => $"AddrAlert: Tap ignored (stale). callId={item.BackendId ?? "(no id)"}");
				return;
			}

			await OnCallTappedAsync(resolved, preserveQueueAnchor: false, continueQueueFromPlayedCall: true);
		}

		public CallItem? ResolveCallForAddressAlert(CallItem alertItem)
		{
			try
			{
				// Prefer direct reference match first.
				if (Calls.Contains(alertItem))
					return alertItem;

				var id = alertItem.BackendId;
				if (!string.IsNullOrWhiteSpace(id))
				{
					var match = Calls.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.BackendId) && c.BackendId == id);
					if (match != null)
						return match;
				}
			}
			catch { }

			return null;
		}

        private async Task OnCallTappedAsync(CallItem? item, bool preserveQueueAnchor, bool continueQueueFromPlayedCall = false)
        {
            if (item == null || _playbackOwnership.Live is not { } live)
                return;

#if ANDROID
            using var androidPlayback = _monitoring.Current == null ? SystemMediaService.BeginAndroidStandalonePlayback() : null;
#endif
            var priorLastPlayed = preserveQueueAnchor ? _lastPlayedCall : null;
            var wasQueuePlaybackRunning = preserveQueueAnchor && _isQueuePlaybackRunning;
            using var request = BeginManualPlayback(live);
            _manualPlaybackTarget = item;
            var token = request.Token;

            try
            {
                await FlushLiveCompletionAsync(request.Token);
                if (!request.CanContinue)
                    return;

                LastQueueEvent = $"Call tapped at {DateTime.Now:T}";
                ClearPlayingSelection();
                if (!AudioEnabled)
                {
                    await PlaySingleCallWithoutQueueAsync(item, request);
                    return;
                }

                var shouldResumeQueueAfterManualPlayback =
                    IsRunning && (_settingsService.AutoPlay || _userRunShouldDriveQueue ||
                        wasQueuePlaybackRunning || continueQueueFromPlayedCall);

                var outcome = await PlayAudioAsync(item, request, allowMutedPlayback: true);
                if (!request.CanContinue || !outcome.AdvancesQueue())
                    return;

                if (continueQueueFromPlayedCall)
                    _lastPlayedCall = item;
                else if (preserveQueueAnchor)
                    _lastPlayedCall = priorLastPlayed;
                UpdateQueueDerivedState();

                if (shouldResumeQueueAfterManualPlayback)
                {
                    LastQueueEvent = "Tapped call finished; restarting queue";
                    _manualPlaybackTarget = null;
                    request.Dispose();
                    _ = RequestQueuePlaybackAsync("TappedCallFinished", true);
                }
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

        private async Task JumpToLiveAsync()
        {
            if (_playbackOwnership.Live is not { } live)
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
                await FlushLiveCompletionAsync(request.Token);
                if (!request.CanContinue)
                    return;

                ClearPlayingSelection();
                var durable = _durableRun;
                var run = _monitoring.Current;
                var playTarget = AudioEnabled;
                CallItem? newest;
                if (durable != null && _monitoring.CanRecover(run))
                {
                    var failedId = failedRecording != null && TryGetDurableRecording(failedRecording, durable, out var id, out _)
                        ? id : null;
                    var jump = await _durableQueue.JumpToLiveAsync(durable, LocalRecoveryFloor(durable), !playTarget, token, failedId);
                    if (!request.CanContinue || !_monitoring.CanRecover(run))
                        return;
                    if (jump == null)
                    {
                        resumeOnExit = true;
                        LastQueueEvent = "No live call available";
                        return;
                    }

                    var row = jump.Target;
                    playTarget = jump.PlayTarget;
                    // A skipped or already-terminal edge should not hold up
                    // future Live arrivals, even if its explicit replay fails.
                    resumeOnExit = !playTarget || row.Snapshot == null;
                    newest = ResolveDurableCard(durable, row);
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
                    lock (_pendingCallsLock)
                        newest = _pendingCalls.FirstOrDefault();
                    newest ??= Calls.FirstOrDefault();
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


		private Task OnToggleAudioAsync()
		{
		    var newValue = !AudioEnabled;
		    var reason = newValue ? "Audio toggled ON" : "Audio toggled OFF";
		    return SetAudioEnabledAsync(newValue, reason);
		}


public async Task ApplyAudioMenuSelectionAsync(double? speedStep)
{
    try
    {
        if (speedStep == null)
        {
            await SetAudioEnabledAsync(false, "Audio menu set OFF");
            return;
        }

        if (!AudioEnabled)
        {
            await SetAudioEnabledAsync(true, "Audio menu set ON");
        }

        PlaybackSpeedStep = speedStep.Value;
        LastQueueEvent = $"Catch-up speed set to {PlaybackSpeedLabel}";
    }
    catch (Exception ex)
    {
        AppLog.DebugWriteLine(() => $"Error in ApplyAudioMenuSelectionAsync: {ex}");
        try
        {
            AppLog.Add(() => $"Audio(VM): Menu apply error. {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
        }
    }
}

private async Task SetAudioEnabledAsync(bool enabled, string reason)
{
    try
    {
        AudioEnabled = enabled;

        LastQueueEvent = reason;

        if (!enabled)
        {
            await StopAudioFromToggleAsync();

            _lastPlayedCall = null;

            UpdateQueueDerivedState();
        }
        else
        {
            if (Calls.Count > 0)
            {
                _lastPlayedCall = Calls[0];
            }
            else
            {
                _lastPlayedCall = null;
            }

            UpdateQueueDerivedState();
        }
    }
    catch (Exception ex)
    {
        AppLog.DebugWriteLine(() => $"Error in SetAudioEnabledAsync: {ex}");
        try
        {
            AppLog.Add(() => $"Audio(VM): Enable error. {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
        }
    }
}

private static string SpeedStepToLabel(double step)
{
    var rounded = (int)Math.Round(step);
    return rounded switch
    {
        1 => "1.25x",
        2 => "1.5x",
        3 => "1.75x",
        4 => "2x",
        _ => "1x"
    };
}
        private async Task<CallItem?> GetNextDurableCallAsync(DurableLiveQueue.Run durable, CancellationToken token, int version)
        {
            var floor = LocalRecoveryFloor(durable);
            var row = await _durableQueue.NextAsync(durable, floor, token);
            if (version != _queuePlaybackVersion || token.IsCancellationRequested || _playbackRequests.HasManualSelection)
                return null;
            if (row == null)
            {
                var transient = GetNextQueuedCall();
                if (transient == null || transient.RecordingTimeUtc == null)
                    return transient;
                if (DurableLiveQueue.DurableAudioUrl(transient.AudioUrl) != null ||
                    !TryGetDurableRecording(transient, durable, out var id, out _))
                    return null;
                return await _durableQueue.IsPendingAsync(durable, id, floor, token) ? transient : null;
            }

            var card = ResolveDurableCard(durable, row);
            ShowDurableCard(card);
            return card;
        }

        private CallItem ResolveDurableCard(DurableLiveQueue.Run durable, DurableLiveQueue.Recording row)
        {
            bool Matches(CallItem? item) => item?.LiveState is { } state && state.Id == row.Id &&
                DurableLiveQueue.ServerKey(state.ServerUrl) == durable.Server && durable.Owns(item.RecordingAccess);

            var card = Matches(_notifiedRecording) ? _notifiedRecording :
                Calls.FirstOrDefault(Matches);
            if (card == null)
            {
                lock (_pendingCallsLock)
                    card = _pendingCalls.Find(Matches);
                card ??= Matches(_currentPlayingCall) ? _currentPlayingCall :
                    Matches(_retryPlaybackCall) ? _retryPlaybackCall :
                    Matches(_lastPlayedCall) ? _lastPlayedCall : null;
            }
            if (card == null)
                return row.ToCallItem(durable.Server);

            if (row.Snapshot is { } snapshot && card.LiveState is { } state)
            {
                state.Observe(snapshot, 0);
                card.ApplyLiveUpdate();
            }
            return card;
        }

        private void ShowDurableCard(CallItem card)
        {
            if (ReferenceEquals(_notifiedRecording, card))
                _notifiedRecording = null;
            lock (_pendingCallsLock)
                _pendingCalls.Remove(card);
            if (!Calls.Contains(card) && card.LiveState is { } state && FindLiveCard(state) == null && !_filterService.ShouldHide(card))
                InsertLiveCardInDisplayOrder(card);
        }

        private void InsertLiveCardInDisplayOrder(CallItem card)
        {
            var index = 0;
            while (index < Calls.Count && CompareCardOrder(Calls[index], card) >= 0)
                index++;
            if (index < MaxCallsToKeep)
            {
                Calls.Insert(index, card);
                EnforceMaxCalls();
            }
        }

        private static int CompareCardOrder(CallItem left, CallItem right)
        {
            if (left.RecordingTimeUtc is not { } leftTime || right.RecordingTimeUtc is not { } rightTime)
                return left.Timestamp.CompareTo(right.Timestamp);
            var time = leftTime.CompareTo(rightTime);
            return time != 0 ? time : StringComparer.Ordinal.Compare(left.BackendId, right.BackendId);
        }

        private bool HasDurablePosition(CallItem item) =>
            _durableRun is { } durable && TryGetDurableRecording(item, durable, out _, out _);

        private long LocalRecoveryFloor(DurableLiveQueue.Run run)
        {
            var now = DateTimeOffset.UtcNow;
            return DurableLiveQueue.HistoricalFloor(run.Protected || HostedServerRules.RequiresApiFirewallCredentials(_settingsService, run.Server),
                run.VerifiedTier, IsCurrentLiveScope(run) &&
                _settingsService.SubscriptionLastStatusOk && _settingsService.SubscriptionLastValidatedOnline &&
                (_settingsService.SubscriptionExpiresUtc is not { } expires || expires > now.UtcDateTime),
                _settingsService.SubscriptionTierLevel, now);
        }

        private bool IsCurrentLiveScope(DurableLiveQueue.Run run)
        {
            var selected = _settingsService.ServerUrl ?? "";
            return DurableLiveQueue.ServerKey(selected) == run.Server &&
                HostedServerRules.RequiresApiFirewallCredentials(_settingsService, selected) == run.Protected &&
                (!run.Protected || run.Account == RecordingPlaybackAccess.AccountKey(_settingsService, selected));
        }

        private static bool TryGetDurableRecording(CallItem item, DurableLiveQueue.Run run, out string id, out DateTimeOffset time)
        {
            id = item.BackendId;
            time = item.RecordingTimeUtc.GetValueOrDefault();
            return item.LiveState is { } state && DurableLiveQueue.ServerKey(state.ServerUrl) == run.Server &&
                run.Owns(item.RecordingAccess) &&
                item.RecordingTimeUtc != null && id.Length != 0;
        }

        private async Task<double> GetEffectivePlaybackRateAsync(CallItem item, CancellationToken token)
        {
            var step = PlaybackSpeedStep;
            if (step == 0)
                return 1.0;

            var catchingUp = _durableRun is { } durable && TryGetDurableRecording(item, durable, out var id, out var time)
                ? await _durableQueue.HasNewerPendingAsync(durable, LocalRecoveryFloor(durable), new(id, time.UtcTicks), token)
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

        private int GetBacklogCountForStartingCall(CallItem? item)
        {
            if (item == null)
                return 0;

            bool IsPlayable(CallItem c) =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.AudioUrl) &&
                !_filterService.ShouldHide(c) &&
                !_filterService.ShouldMute(c);

            var count = 0;

            if (Calls != null && Calls.Count > 0)
            {
                var itemIndex = Calls.IndexOf(item);
                if (itemIndex > 0)
                {
                    for (var i = itemIndex - 1; i >= 0; i--)
                    {
                        if (IsPlayable(Calls[i]))
                            count++;
                    }
                }
            }

            count += GetPendingPlayableCount();
            return count;
        }

        private void EnforceMaxCalls()
        {
			var max = MaxCallsToKeep;
			List<CallItem>? removed = null;
			while (Calls.Count > max)
			{
				var lastIndex = Calls.Count - 1;
				if (lastIndex < 0)
					break;

				removed ??= new List<CallItem>();
				removed.Add(Calls[lastIndex]);
				Calls.RemoveAt(lastIndex);
			}

			if (removed != null && removed.Count > 0)
			{
				PurgeStaleAddressAlerts();
			}

            UpdateQueueDerivedState();
        }

		private void PurgeStaleAddressAlerts()
		{
			try
			{
				if (AddressAlerts.Count == 0 && _hiddenAddressAlerts.Count == 0)
					return;

				HashSet<string> liveIds = new(StringComparer.Ordinal);
				foreach (var c in Calls)
				{
					if (!string.IsNullOrWhiteSpace(c.BackendId))
						liveIds.Add(c.BackendId);
				}

				bool IsLive(CallItem a)
				{
					if (Calls.Contains(a))
						return true;
					return !string.IsNullOrWhiteSpace(a.BackendId) && liveIds.Contains(a.BackendId);
				}

				for (var i = AddressAlerts.Count - 1; i >= 0; i--)
				{
					var a = AddressAlerts[i];
					if (!IsLive(a))
						AddressAlerts.RemoveAt(i);
				}

				for (var i = _hiddenAddressAlerts.Count - 1; i >= 0; i--)
				{
					var a = _hiddenAddressAlerts[i];
					if (!IsLive(a))
						_hiddenAddressAlerts.RemoveAt(i);
				}

				OnPropertyChanged(nameof(HasAddressAlerts));
			}
			catch { }
		}

        private void UpdateQueueDerivedState()
        {
            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));

            string status;
            if (!IsRunning)
            {
                status = "Stopped";
            }
            else if (!AudioEnabled)
            {
                status = "Muted";
            }
            else if (_currentPlayingCall != null)
            {
                status = "Playing";
            }
            else if (CallsWaiting > 0)
            {
                status = $"Waiting ({CallsWaiting} calls)";
            }
            else
            {
                status = "Idle";
            }

            QueueStatusText = status;

            if (Calls != null && Calls.Count > 0)
            {
                var anchor = _currentPlayingCall ?? _lastPlayedCall;
                var anchorIndex = anchor != null ? Calls.IndexOf(anchor) : -1;
                var offscreenAnchor = anchorIndex < 0 && anchor != null && HasDurablePosition(anchor);

                for (var i = 0; i < Calls.Count; i++)
                {
                    var call = Calls[i];
                    bool isHistory = false;

                    if (anchorIndex >= 0)
                    {
                        isHistory = i > anchorIndex;
                    }
                    else if (offscreenAnchor)
                    {
                        isHistory = CompareCardOrder(call, anchor!) < 0;
                    }

                    if (call.IsHistory != isHistory)
                        call.IsHistory = isHistory;


                    var isMutedByFilter = _filterService.ShouldMute(call);
                    if (call.IsMutedByFilter != isMutedByFilter)
                        call.IsMutedByFilter = isMutedByFilter;
                }
            }
        }

        private static string BuildNowPlayingSubtitle(CallItem item)
        {
            if (item == null)
                return string.Empty;

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.Source))
                parts.Add(item.Source);

            if (!string.IsNullOrWhiteSpace(item.Site))
                parts.Add(item.Site);

            if (!string.IsNullOrWhiteSpace(item.VoiceReceiver))
                parts.Add(item.VoiceReceiver);

            return string.Join(" | ", parts);
        }

        private NowPlayingMetadata BuildNowPlayingMetadataSnapshot(CallItem item)
        {
            const string appName = "Joes Scanner";

            var artistToken = BluetoothLabelMapping.NormalizeToken(_settingsService.BluetoothLabelArtist, BluetoothLabelMapping.TokenAppName);
            var titleToken = BluetoothLabelMapping.NormalizeToken(_settingsService.BluetoothLabelTitle, BluetoothLabelMapping.TokenTranscription);
            var albumToken = BluetoothLabelMapping.NormalizeToken(_settingsService.BluetoothLabelAlbum, BluetoothLabelMapping.TokenTalkgroup);
            var composerToken = BluetoothLabelMapping.NormalizeToken(_settingsService.BluetoothLabelComposer, BluetoothLabelMapping.TokenSite);
            var genreToken = BluetoothLabelMapping.NormalizeToken(_settingsService.BluetoothLabelGenre, BluetoothLabelMapping.TokenReceiver);

            return new NowPlayingMetadata
            {
                Artist = BluetoothLabelMapping.Resolve(item, artistToken, appName),
                Title = BluetoothLabelMapping.Resolve(item, titleToken, appName),
                Album = BluetoothLabelMapping.Resolve(item, albumToken, appName),
                Composer = BluetoothLabelMapping.Resolve(item, composerToken, appName),
                Genre = BluetoothLabelMapping.Resolve(item, genreToken, appName)
            };
        }

        private async Task PlaySingleCallWithoutQueueAsync(CallItem item, LivePlaybackRequests.Request request)
        {
            if (_filterService.ShouldHide(item))
                return;

            string? playbackUrl = null;
            item.IsPlaying = true;
            _currentPlayingCall = item;
            try
            {
                playbackUrl = await AcquireRecordingAudioAsync(item, request.Token);
                if (!request.CanContinue || string.IsNullOrWhiteSpace(playbackUrl))
                    return;

                try { _systemMediaService.UpdateNowPlaying(BuildNowPlayingMetadataSnapshot(item), AudioEnabled); } catch { }
                var completion = new PlaybackCompletion();
                await PlayOnDeviceAsync(item, playbackUrl, 1.0, request, request.Token, completion);
                if (completion.Confirmed && request.CanContinue && HasDurablePosition(item))
                    CompleteQueuedCall(item);
            }
            finally
            {
                if (IsDownloadedAudioTempFile(playbackUrl))
                    TryDeleteFile(playbackUrl!);
                if (request.IsCurrent)
                    ClearPlayingSelection();
            }
        }

        private async Task<PlaybackOutcome> PlayAudioAsync(CallItem item, LivePlaybackRequests.Request request,
            bool allowMutedPlayback = false)
        {
            if (_playbackOwnership.Live == null || !AudioEnabled || !request.CanContinue)
                return PlaybackOutcome.Canceled;

            var completion = new PlaybackCompletion();
            Exception? failure = null;
            var outcome = _filterService.ShouldHide(item) ||
                          (!allowMutedPlayback && _filterService.ShouldMute(item))
                ? PlaybackOutcome.Skipped
                : await PlaybackAttempt.RunAsync(async () =>
                {
                    if (_durableRun is { } durable && TryGetDurableRecording(item, durable, out var id, out _))
                        await _durableQueue.MarkPlayingAsync(durable, id, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    await PlayAudioCoreAsync(item, request, completion);
                }, request.Token,
                    ex => { failure = ex; ReportPlaybackFailure(ex, request); }, completion);

            if (request.CanContinue)
            {
                if (outcome.AdvancesQueue())
                {
                    if (outcome == PlaybackOutcome.Skipped && _durableRun is { } durable &&
                        TryGetDurableRecording(item, durable, out var id, out _))
                    {
                        try { await _durableQueue.MarkAsync(durable, id, outcome, request.Token); }
                        catch (Exception ex) when (!request.Token.IsCancellationRequested)
                        {
                            ReportPlaybackFailure(ex, request);
                            SetPlaybackRetry(item);
                            return PlaybackOutcome.Failed;
                        }
                        if (!request.CanContinue)
                            return PlaybackOutcome.Canceled;
                    }
                    CompleteQueuedCall(item);
                    _lastQueuePlaybackFinishedAtUtc = DateTimeOffset.UtcNow;
                }
                else if (outcome == PlaybackOutcome.Failed)
                {
                    // A fresh verification can remove a historical row from the
                    // eligible set. Keep it pending and reselect eligible Live.
                    if (!request.IsManual && failure is RecordingAccessException && _durableRun is { } current &&
                        TryGetDurableRecording(item, current, out var withheldId, out _) &&
                        !await _durableQueue.IsPendingAsync(current, withheldId, LocalRecoveryFloor(current), request.Token))
                        return PlaybackOutcome.Withheld;
                    if (!request.CanContinue)
                        return PlaybackOutcome.Canceled;
                    SetPlaybackRetry(item);
                    if (failure is RecordingAudioUnavailableException && _durableRun is { } scope &&
                        TryGetDurableRecording(item, scope, out _, out _))
                    {
                        _unavailableRecording = true;
                        _awaitingAutomaticAudio = request.IsManual ? null : item;
                        OnPropertyChanged(nameof(CanSkipUnavailableRecording));
                    }
                }
                if (_currentPlayingCall == item)
                    _currentPlayingCall = null;
                OnPropertyChanged(nameof(CallsWaiting));
                OnPropertyChanged(nameof(IsCallsWaitingVisible));
            }
            return outcome;
        }

        private async Task PlayAudioCoreAsync(CallItem item, LivePlaybackRequests.Request request, PlaybackCompletion completion)
        {
            var userToken = request.Token;

            // Playback watchdog (prevents the queue from hanging forever on truly stuck playback).
            var rateForTimeout = await GetEffectivePlaybackRateAsync(item, userToken);
            userToken.ThrowIfCancellationRequested();
            var playbackSeconds = item.CallDurationSeconds > 0
                ? (item.CallDurationSeconds / Math.Max(rateForTimeout, 0.1))
                : 0;

            var playbackLimitSeconds = playbackSeconds > 0
                ? (playbackSeconds * 2) + 120
                : 300;

            playbackLimitSeconds = Math.Clamp(playbackLimitSeconds, 180, 10800);

            // Download watchdog (separate, longer timeout).
            // Android background networking can be throttled; tying download time to call duration causes false timeouts.
            const int downloadLimitSeconds = 900; // 15 minutes

#if ANDROID || IOS
            // Mobile playback has its own prepare and audible-playback watchdogs. Time spent
            // waiting for a call to end, delayed focus or explicit resume is unbounded.
            using var playbackWatchdogCts = new CancellationTokenSource();
#else
            using var playbackWatchdogCts = new CancellationTokenSource(TimeSpan.FromSeconds(playbackLimitSeconds));
#endif
            using var downloadWatchdogCts = new CancellationTokenSource(TimeSpan.FromSeconds(downloadLimitSeconds));

            using var playbackLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(userToken, playbackWatchdogCts.Token);
            using var downloadLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(userToken, downloadWatchdogCts.Token);

            var playbackToken = playbackLinkedCts.Token;
            var downloadToken = downloadLinkedCts.Token;

            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            item.IsPlaying = true;
            _currentPlayingCall = item;

            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));

            string? downloadedTempPath = null;
            var acquiringAudio = false;
            try
            {
                var rate = rateForTimeout;
                var selectedAtUtc = DateTimeOffset.UtcNow;
                var gapMs = _lastQueuePlaybackFinishedAtUtc.HasValue
                    ? (selectedAtUtc - _lastQueuePlaybackFinishedAtUtc.Value).TotalMilliseconds
                    : 0;

                if (gapMs > 0)
                {
                    AppLog.Add(() => $"QueueTiming: selected backendId={item.BackendId ?? "(none)"} gapSincePriorFinishMs={gapMs:0}");
                }

                var acquisitionStopwatch = System.Diagnostics.Stopwatch.StartNew();

                // IMPORTANT: Do not use the playback watchdog here.
                // Backgrounded Android downloads can be slower and would trip the playback watchdog incorrectly.
                acquiringAudio = true;
                userToken.ThrowIfCancellationRequested();
                var playbackUrl = await AcquireRecordingAudioAsync(item, downloadToken, allowPrefetch: true);

                acquisitionStopwatch.Stop();
                acquiringAudio = false;
                if (IsDownloadedAudioTempFile(playbackUrl))
                    downloadedTempPath = playbackUrl;
                userToken.ThrowIfCancellationRequested();

                if (downloadWatchdogCts.IsCancellationRequested)
                    throw new TimeoutException("Audio download timeout");

                if (string.IsNullOrWhiteSpace(playbackUrl))
                {
                    throw new InvalidOperationException("Audio could not be acquired for this recording.");
                }

                StartPlayableUrlPrefetchForUpcomingCall(item);

                try
                {
                    _systemMediaService.UpdateNowPlaying(BuildNowPlayingMetadataSnapshot(item), AudioEnabled);
                }
                catch
                {
                }

                AppLog.Add(() => $"QueueTiming: backendId={item.BackendId ?? "(none)"} acquisitionMs={acquisitionStopwatch.Elapsed.TotalMilliseconds:0} rate={rate:0.###}");

                if (gapMs > 0)
                {
                    var audibleGapMs = gapMs + acquisitionStopwatch.Elapsed.TotalMilliseconds;
                    AppLog.Add(() => $"QueueTiming: backendId={item.BackendId ?? "(none)"} audibleGapEstimateMs={audibleGapMs:0}");
                }

                var playbackStopwatch = System.Diagnostics.Stopwatch.StartNew();
                await PlayOnDeviceAsync(item, playbackUrl, rate, request, playbackToken, completion);
                if (!request.CanContinue)
                    return;

                if (playbackWatchdogCts.IsCancellationRequested && !completion.Confirmed)
                    throw new TimeoutException("Call playback watchdog timeout");
                playbackStopwatch.Stop();

                AppLog.Add(() => $"QueueTiming: backendId={item.BackendId ?? "(none)"} playbackMs={playbackStopwatch.Elapsed.TotalMilliseconds:0} totalCallSeconds={item.CallDurationSeconds:0.###}");

            }
            catch (OperationCanceledException ex) when (acquiringAudio &&
                !userToken.IsCancellationRequested &&
                LivePlaybackRequests.IsExpectedCancellation(ex, downloadWatchdogCts.Token))
            {
                throw new TimeoutException("Audio download timeout", ex);
            }
            catch (OperationCanceledException ex) when (!acquiringAudio &&
                !userToken.IsCancellationRequested &&
                LivePlaybackRequests.IsExpectedCancellation(ex, playbackWatchdogCts.Token))
            {
                throw new TimeoutException("Call playback watchdog timeout", ex);
            }
            finally
            {
                if (downloadedTempPath != null)
                    TryDeleteFile(downloadedTempPath);
                if (request.IsCurrent)
                {
                    item.IsPlaying = false;
                    if (_currentPlayingCall == item)
                        _currentPlayingCall = null;

                    OnPropertyChanged(nameof(CallsWaiting));
                    OnPropertyChanged(nameof(IsCallsWaitingVisible));
                }
            }
        }


        private static bool IsDownloadedAudioTempFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                var cacheDir = FileSystem.CacheDirectory;
                if (string.IsNullOrWhiteSpace(cacheDir))
                    return false;

                var fullPath = Path.GetFullPath(path);
                var fullCache = Path.GetFullPath(cacheDir);

                if (!fullPath.StartsWith(fullCache, StringComparison.OrdinalIgnoreCase))
                    return false;

                var name = Path.GetFileName(fullPath);
                return name.StartsWith("jaas_", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private async Task<string?> GetPlayableAudioUrlAsync(string audioUrl, CancellationToken cancellationToken, bool forceDownload = false)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
                return null;

            if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri) || audioUri.Scheme is not ("http" or "https"))
                return audioUrl;

            if (!string.IsNullOrEmpty(audioUri.UserInfo))
                audioUri = new UriBuilder(audioUri) { UserName = string.Empty, Password = string.Empty }.Uri;

            var authentication = ServerAuthentication.For(_settingsService);
            if (!forceDownload && !authentication.RequiresFirewall(audioUri) && authentication.GetCustomAuthorization(audioUri) == null)
                return audioUri.AbsoluteUri;

            using var request = new HttpRequestMessage(HttpMethod.Get, audioUri);
            using var response = await _audioHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var extension = Path.GetExtension(audioUri.AbsolutePath);
            if (string.IsNullOrEmpty(extension))
                extension = ".wav";

            var localPath = Path.Combine(FileSystem.CacheDirectory, $"jaas_{Guid.NewGuid():N}{extension}");
            var tempPath = localPath + ".tmp";
            try
            {
                await using (var target = File.Create(tempPath))
                await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    await stream.CopyToAsync(target, cancellationToken);
                }

                File.Move(tempPath, localPath);
                return localPath;
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        private async Task<string> AcquireRecordingAudioAsync(CallItem item, CancellationToken token, bool allowPrefetch = false)
        {
            var access = await AuthorizeRecordingAsync(item, token);
            if (allowPrefetch && !access.Protected && await TryTakePrefetchedPlayableUrlAsync(item, token) is { Length: > 0 } prefetched)
                return prefetched;

            var lastUrl = item.AudioUrl;
            return await RecordingAudioRecovery.AcquireAsync(lastUrl, async refreshToken =>
            {
                if (!string.IsNullOrWhiteSpace(item.AudioUrl) && item.AudioUrl != lastUrl)
                {
                    if (access.Protected)
                        await AuthorizeRecordingAsync(item, refreshToken);
                    return lastUrl = item.AudioUrl;
                }

                var scope = _durableRun;
                var refreshed = await _callStreamService.RefreshRecordingAudioAsync(item, access.Server, access.RecordedAt, refreshToken);
                if (item.LiveState is { } state && scope is { } durable && _durableRun == durable && IsCurrentLiveScope(durable) &&
                    TryGetDurableRecording(item, durable, out _, out _))
                    await _durableQueue.ObserveAsync(durable, state.Id, null, state.Read(), refreshToken, existingOnly: true);
                refreshToken.ThrowIfCancellationRequested();
                RequireCurrentRecordingAccount(item);
                if (refreshed != null)
                    item.AudioUrl = lastUrl = refreshed;
                return refreshed;
            }, (url, acquireToken) =>
            {
                RequireCurrentRecordingAccount(item);
                return GetPlayableAudioUrlAsync(url, acquireToken, forceDownload: true);
            }, token);
        }

        private async Task<(string Server, bool Protected, DateTimeOffset? RecordedAt)> AuthorizeRecordingAsync(
            CallItem item, CancellationToken token)
        {
            var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
            var key = DurableLiveQueue.ServerKey(server);
            var context = item.RecordingAccess ?? new RecordingAccessContext(RecordingOrigin.Unknown, false);
            var protectedSource = context.Protected || HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server);
            var account = protectedSource ? RequireCurrentRecordingAccount(item) : null;
            var owner = item.LivePlaybackOwner;
            var recordedAt = item.RecordingTimeUtc;
            if (key != null && !string.IsNullOrWhiteSpace(item.BackendId) && (_durableRun != null || protectedSource))
            {
                var persisted = await _durableQueue.GetAccessAsync(key, item.BackendId, account, token);
                if (persisted is { } record)
                {
                    context = record.Access with { Protected = record.Access.Protected || protectedSource };
                    owner = record.Owner;
                    recordedAt = record.RecordedAt;
                }
            }
            protectedSource |= context.Protected;
            if (protectedSource && key != DurableLiveQueue.ServerKey(_settingsService.ServerUrl ?? ""))
                throw new RecordingAccessException("Select this recording's server before playing hosted audio.");
            var currentLive = _monitoring.CanRecover(_monitoring.Current) && _monitoring.Current?.Id == owner &&
                (!protectedSource || context.Account != null);
            await RecordingPlaybackAccess.VerifyAsync(_settingsService, _subscriptionService, server,
                recordedAt, context, currentLive, requireAccount: protectedSource, token);
            return (server, protectedSource, recordedAt);
        }

        private string? RequireCurrentRecordingAccount(CallItem item)
        {
            var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
            if (item.RecordingAccess?.Protected != true && !HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server))
                return null;
            var account = RecordingPlaybackAccess.AccountKey(_settingsService, server);
            if (_changingHostedAccount || account == null || item.RecordingAccess?.Account != account ||
                !RecordingPlaybackAccess.IsSelected(_settingsService, server))
                throw new RecordingAccessException("This recording belongs to a different hosted account or server.");
            return account;
        }

        private async Task OpenDonateAsync()
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri(DonateUrl));
            }
            catch
            {
            }
        }

        public Task TryAutoReconnectAsync() => RestoreMonitoringAsync(driveQueue: false);

        private Task RestoreMonitoringAsync(bool driveQueue) =>
            MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (_monitoring.Current != null || _monitoring.ExplicitlyStopped)
                    return;

                var revision = _monitoring.StopRevision;
                var scope = CurrentMonitoringScope();
                if (!await Task.Run(() => _monitoringIntent.CanRestore(scope.Server, scope.Account)) ||
                    scope != CurrentMonitoringScope())
                    return;

                var run = await StartMonitoringCoreAsync(driveQueue, automaticStopRevision: revision);
                if (driveQueue && _monitoring.CanRecover(run) && AudioEnabled && _settingsService.AutoPlay)
                    await RequestQueuePlaybackAsync("ConnectAutoplay", false);
            });

        private List<CallItem> SnapshotLiveCalls()
        {
            var calls = Calls.ToList();
            lock (_pendingCallsLock)
                calls.AddRange(_pendingCalls);
            if (_durableRun is { } run)
                calls.RemoveAll(item => !run.Owns(item.RecordingAccess));
            return calls;
        }

        private CallItem? FindLiveCard(LiveCallState state)
        {
            var run = _durableRun;
            var existing = state.FindCard(Calls, _pendingCalls, _pendingCallsLock);
            if (existing != null && (run == null || run.Owns(existing.RecordingAccess)))
                return existing;

            // A restored card can own a different in-memory state from the
            // stream's copy. Its server and backend identity are unchanged.
            var server = DurableLiveQueue.ServerKey(state.ServerUrl);
            bool Matches(CallItem item) => item.LiveState is { } restored &&
                restored.Id == state.Id && DurableLiveQueue.ServerKey(restored.ServerUrl) == server &&
                (run == null || run.Owns(item.RecordingAccess));
            existing = Calls.FirstOrDefault(Matches);
            if (existing != null)
                return existing;
            lock (_pendingCallsLock)
                return _pendingCalls.Find(item => Matches(item));
        }

        private void ApplyLiveUpdate(LiveCallState state, CallItem? update = null, CallItem? existing = null)
        {
            existing ??= FindLiveCard(state);
            if (existing == null)
                return;
            if (existing.LiveState is { } restored && !ReferenceEquals(restored, state))
                restored.Observe(state.Read(), LiveCallState.NextVersion());
            var alreadyPlayable = !string.IsNullOrWhiteSpace(existing.AudioUrl);
            var transcriptionChanged = existing.ApplyLiveUpdate();
            if (!alreadyPlayable && !string.IsNullOrWhiteSpace(existing.AudioUrl))
            {
                UpdateQueueDerivedState();
                RequestQueueForLiveArrival("LateAudioAvailable");
            }
            if (!transcriptionChanged)
                return;

            var hadAddress = existing.HasDetectedAddress;
            if (update != null && update.Transcription == existing.Transcription)
            {
                // Reuse detection performed by the stream. A newer observation may have
                // already changed the transcript, in which case these results are stale.
                existing.DetectedAddress = update.DetectedAddress;
                existing.DetectedAddressConfidencePercent = update.DetectedAddressConfidencePercent;
                existing.DetectedAddressCandidates = update.DetectedAddressCandidates;
                existing.AddressDetectionAppliedHash = update.AddressDetectionAppliedHash;
                existing.What3WordsAddress = update.What3WordsAddress;
                existing.What3WordsAppliedHash = update.What3WordsAppliedHash;
            }
            else
            {
                try { _addressDetectionService.Apply(existing); } catch { }
            }
            if (!hadAddress && existing.HasDetectedAddress)
                AddAddressAlertIfNew(existing);
            try { _ = _what3WordsService.ResolveCoordinatesIfNeededAsync(existing, CancellationToken.None); } catch { }

            LastQueueEvent = $"Transcription updated at {DateTime.Now:T}";
        }

        private void RequestQueueForLiveArrival(string reason)
        {
            if (!AudioEnabled)
                return;
            if (_playbackOwnership.Live == null)
            {
                _resumeAfterHistory |= IsRunning && (_settingsService.AutoPlay || _userRunShouldDriveQueue || CallsWaiting > 0);
                return;
            }
            if (_isQueuePlaybackRunning)
            {
                _queueWake.Signal();
                return;
            }
            if (_settingsService.AutoPlay || _userRunShouldDriveQueue)
                _ = RequestQueuePlaybackAsync(reason, _userRunShouldDriveQueue);
            else if (IsRunning && _currentPlayingCall == null && !_isQueuePlaybackRunning && CallsWaiting > 0)
                _ = RequestQueuePlaybackAsync(reason, userInitiated: true);
        }

        public async Task RefreshStaleCallMetadataAsync(int maxToRefresh = 20)
        {
            if (Interlocked.Exchange(ref _refreshingCallMetadata, 1) != 0)
                return;

            try
            {
                var (run, durable, candidates) = await MainThread.InvokeOnMainThreadAsync(() =>
                    (_monitoring.Current, _durableRun, SnapshotLiveCalls()
                        .Where(call => call.LiveState?.NeedsNotificationLookup() == true)
                        .Take(maxToRefresh).ToArray()));
                if (candidates.Length == 0)
                    return;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run?.Token ?? CancellationToken.None);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                await _callStreamService.RefreshCallMetadataAsync(candidates, timeout.Token).ConfigureAwait(false);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (timeout.IsCancellationRequested || _changingHostedAccount ||
                        run != _monitoring.Current || durable != null && !IsCurrentLiveScope(durable))
                        return;
                    foreach (var candidate in candidates)
                    {
                        if (candidate.LiveState is { } state)
                            ApplyLiveUpdate(state);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Call metadata catch-up failed ({ex.GetType().Name}).");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshingCallMetadata, 0);
            }
        }

private void AddAddressAlertIfNew(CallItem call)
{
    try
    {
        // MAUI/iOS requires collection mutations on the UI thread.
        if (!MainThread.IsMainThread)
        {
            try
            {
                AppLog.Add(() => $"AddrAlert: Add request off UI thread. Scheduling on UI thread. callId={call?.BackendId ?? "(null)"}");
                MainThread.BeginInvokeOnMainThread(() => AddAddressAlertIfNew(call));
            }
            catch
            {
            }
            return;
        }

        if (call == null || !call.HasDetectedAddress)
        {
            try
            {
                if (call == null)
                    AppLog.Add("AddrAlert: Add skipped. call is null");
                else
                    AppLog.Add(() => $"AddrAlert: Add skipped. HasDetectedAddress=false callId={call.BackendId ?? "(no id)"}");
            }
            catch
            {
            }
            return;
        }

        try
        {
            AppLog.Add(() => $"AddrAlert: Add attempt. callId={call.BackendId ?? "(no id)"} addr='{call.DetectedAddress ?? ""}' conf={call.DetectedAddressConfidencePercent:0.##}% beforeVisible={AddressAlerts.Count} beforeHidden={_hiddenAddressAlerts.Count} hasVisibleFlag={HasAddressAlerts}");
        }
        catch
        {
        }

        var id = call.BackendId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (AddressAlerts.Any(a => !string.IsNullOrWhiteSpace(a.BackendId) &&
                                      string.Equals(a.BackendId, id, StringComparison.Ordinal)))
            {
                try { AppLog.Add(() => $"AddrAlert: Add skipped. Duplicate visible. callId={id}"); } catch { }
                return;
            }

            if (_hiddenAddressAlerts.Any(a => !string.IsNullOrWhiteSpace(a.BackendId) &&
                                             string.Equals(a.BackendId, id, StringComparison.Ordinal)))
            {
                try { AppLog.Add(() => $"AddrAlert: Add skipped. Duplicate hidden. callId={id}"); } catch { }
                return;
            }
        }
        else
        {
            // Fallback: avoid duplicating the same object reference.
            if (AddressAlerts.Any(a => ReferenceEquals(a, call)) || _hiddenAddressAlerts.Any(a => ReferenceEquals(a, call)))
            {
                try { AppLog.Add("AddrAlert: Add skipped. Duplicate by reference."); } catch { }
                return;
            }
        }

        AddressAlerts.Insert(0, call);

        try
        {
            AppLog.Add(() => $"AddrAlert: Added. nowVisible={AddressAlerts.Count} nowHidden={_hiddenAddressAlerts.Count} hasVisibleFlag={HasAddressAlerts}");
        }
        catch
        {
        }

        // Keep only 3 visible; queue the oldest visible alert instead of discarding it.
        if (AddressAlerts.Count > 3)
        {
            var oldestVisible = AddressAlerts[AddressAlerts.Count - 1];
            AddressAlerts.RemoveAt(AddressAlerts.Count - 1);
            _hiddenAddressAlerts.Add(oldestVisible);

            try
            {
                AppLog.Add(() => $"AddrAlert: Moved oldest to hidden. nowVisible={AddressAlerts.Count} nowHidden={_hiddenAddressAlerts.Count}");
            }
            catch
            {
            }

            // Safety cap: don't let this grow unbounded.
            if (_hiddenAddressAlerts.Count > 25)
                _hiddenAddressAlerts.RemoveRange(0, _hiddenAddressAlerts.Count - 25);
        }
    }
    catch
    {
        try { AppLog.Add("AddrAlert: Add failed (exception swallowed)"); } catch { }
    }
}

public void DismissAddressAlert(CallItem call)
{
    try
    {
        // MAUI/iOS requires collection mutations on the UI thread.
        if (!MainThread.IsMainThread)
        {
            try
            {
                AppLog.Add(() => $"AddrAlert: Dismiss request off UI thread. Scheduling on UI thread. callId={call?.BackendId ?? "(null)"}");
                MainThread.BeginInvokeOnMainThread(() => DismissAddressAlert(call));
            }
            catch
            {
            }
            return;
        }

        if (call == null)
            return;

        try
        {
            AppLog.Add(() => $"AddrAlert: Dismiss attempt. callId={call.BackendId ?? "(no id)"} beforeVisible={AddressAlerts.Count} beforeHidden={_hiddenAddressAlerts.Count}");
        }
        catch
        {
        }

        var existing = AddressAlerts.FirstOrDefault(a =>
            (!string.IsNullOrWhiteSpace(a.BackendId) && !string.IsNullOrWhiteSpace(call.BackendId) &&
             string.Equals(a.BackendId, call.BackendId, StringComparison.Ordinal)) ||
            ReferenceEquals(a, call));

        if (existing != null)
            AddressAlerts.Remove(existing);
        else
        {
            // Also allow dismissing something that was previously hidden.
            var hiddenExisting = _hiddenAddressAlerts.FirstOrDefault(a =>
                (!string.IsNullOrWhiteSpace(a.BackendId) && !string.IsNullOrWhiteSpace(call.BackendId) &&
                 string.Equals(a.BackendId, call.BackendId, StringComparison.Ordinal)) ||
                ReferenceEquals(a, call));

            if (hiddenExisting != null)
                _hiddenAddressAlerts.Remove(hiddenExisting);
        }

        // Backfill a newly freed slot with the most recently hidden alert.
        while (AddressAlerts.Count < 3 && _hiddenAddressAlerts.Count > 0)
        {
            var idx = _hiddenAddressAlerts.Count - 1;
            var next = _hiddenAddressAlerts[idx];
            _hiddenAddressAlerts.RemoveAt(idx);
            AddressAlerts.Add(next);
        }

        try
        {
            AppLog.Add(() => $"AddrAlert: Dismiss done. afterVisible={AddressAlerts.Count} afterHidden={_hiddenAddressAlerts.Count} hasVisibleFlag={HasAddressAlerts}");
        }
        catch
        {
        }
    }
    catch
    {
        try { AppLog.Add("AddrAlert: Dismiss failed (exception swallowed)"); } catch { }
    }
}


        private void OnAddressDetectionSettingsChanged()
        {
            try
            {
                foreach (var call in Calls)
                {
                    var hadAddress = call.HasDetectedAddress;
                    try { _addressDetectionService.Apply(call); } catch { }
                    if (!hadAddress && call.HasDetectedAddress)
                        AddAddressAlertIfNew(call);
					try { _ = _what3WordsService.ResolveCoordinatesIfNeededAsync(call, CancellationToken.None); } catch { }
                }
            }
            catch
            {
            }
        }

        private async Task ToggleConnectionAsync()
        {
            if (_monitoring.Current != null)
            {
                await StopMonitoringAsync();
            }
            else
            {
                await StartMonitoringAsync();
            }
        }

        private LivePlaybackRequests.Request BeginManualPlayback(PlaybackOwnership.Lease live)
        {
            // Retire an automatic loop immediately. Its canceled I/O may still be unwinding
            // when the newer manual selection finishes and requests queue playback again.
            ++_queuePlaybackVersion;
            _isQueuePlaybackRunning = false;
            _transportLookups.RetireCurrent();
            _manualPlaybackTarget = null;
            var request = _playbackRequests.Begin(manual: true, live.Token);
            SetPlaybackRetry(null);
#if ANDROID
            AndroidAudioEnvironment.RequestPlay();
            if (IsRunning)
                SystemMediaService.PrepareAndroidPlaybackService();
#elif IOS
            IosAudioEnvironment.RequestPlay();
#endif
            ClearPrefetchedPlayableUrl(deleteCompletedTempFile: true);
            return request;
        }

        private void SetPlaybackRetry(CallItem? item)
        {
            if (ReferenceEquals(_retryPlaybackCall, item))
                return;
            _retryPlaybackCall = item;
            _unavailableRecording = false;
            _awaitingAutomaticAudio = null;
            OnPropertyChanged(nameof(CanResumePlayback));
            OnPropertyChanged(nameof(CanSkipUnavailableRecording));
        }

        private void CompleteQueuedCall(CallItem item)
        {
            var wasLate = item.ConsumeLateAudio();
            if (!wasLate || _lastPlayedCall == null || CompareCardOrder(item, _lastPlayedCall) > 0)
                _lastPlayedCall = item;
        }

        private void ClearPlayingSelection()
        {
            if (_currentPlayingCall is { } active)
                active.IsPlaying = false;
            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }
            _currentPlayingCall = null;
        }

        private void ReportPlaybackFailure(Exception ex, LivePlaybackRequests.Request request)
        {
            // Log failure types without recording URLs or request details.
            AppLog.DebugWriteLine(() => ex is HttpRequestException { StatusCode: { } status }
                ? $"Live playback failed: {ex.GetType().Name}, HTTP {(int)status}"
                : $"Live playback failed: {ex.GetType().Name}");
            if (!request.CanContinue)
                return;

            UpdateQueueDerivedState();
            LastQueueEvent = ex switch
            {
                RecordingAudioUnavailableException unavailable => unavailable.Message,
                RecordingAccessException access => access.Message,
                FirewallAuthenticationException firewall => firewall.Message,
                TimeoutException watchdog => watchdog.Message,
                OperationCanceledException canceled when LivePlaybackRequests.IsTimeout(canceled) => "Audio request timed out",
                OperationCanceledException => "Audio request was unexpectedly canceled",
                HttpRequestException => "Audio request failed (see debug output)",
                _ => "Error while playing call (see debug output)"
            };
        }

        private Task DecreasePlaybackSpeedStepAsync()
        {
            DecreasePlaybackSpeedStep();
            return Task.CompletedTask;
        }

        private Task IncreasePlaybackSpeedStepAsync()
        {
            IncreasePlaybackSpeedStep();
            return Task.CompletedTask;
        }

        private void DecreasePlaybackSpeedStep()
        {
            if (PlaybackSpeedStep <= 0)
                return;

            PlaybackSpeedStep = PlaybackSpeedStep - 1;
            LastQueueEvent = $"Catch-up speed down to {PlaybackSpeedLabel}";
        }

        private void IncreasePlaybackSpeedStep()
        {
            if (PlaybackSpeedStep >= 4)
                return;

            PlaybackSpeedStep = PlaybackSpeedStep + 1;
            LastQueueEvent = $"Catch-up speed up to {PlaybackSpeedLabel}";
        }

        private async Task NavigateToAdjacentCallAsync(int direction)
        {
            if (_playbackOwnership.Live is not { } live || (direction != 1 && direction != -1))
                return;

            var anchor = (_playbackRequests.HasManualSelection ? _manualPlaybackTarget : null) ??
                _currentPlayingCall ?? _retryPlaybackCall ?? _lastPlayedCall;
            var run = _monitoring.Current;
            var older = direction == 1;
            using var lookup = _transportLookups.Begin(manual: true, live.Token);
            if (_durableRun is { } durable && _monitoring.CanRecover(run) &&
                (anchor == null || TryGetDurableRecording(anchor, durable, out _, out _)))
            {
                var token = lookup.Token;
                try
                {
                    DurableLiveQueue.Position? position = null;
                    if (anchor != null && TryGetDurableRecording(anchor, durable, out var id, out var time))
                        position = new(id, time.UtcTicks);
                    else if (older)
                        position = (await _durableQueue.FindTransportTargetAsync(durable, LocalRecoveryFloor(durable),
                            anchor: null, older: true, token))?.Order;

                    while (position != null && lookup.CanContinue && _monitoring.CanRecover(run))
                    {
                        var row = await _durableQueue.FindTransportTargetAsync(durable, LocalRecoveryFloor(durable),
                            position, older, token);
                        if (row == null || !lookup.CanContinue || !_monitoring.CanRecover(run))
                            break;
                        position = row.Order;
                        var candidate = ResolveDurableCard(durable, row);
                        if (row.Snapshot == null)
                            await RefreshTransportCardAsync(candidate, token);
                        if (!lookup.CanContinue || !_monitoring.CanRecover(run))
                            return;
                        if (_filterService.ShouldHide(candidate) || _filterService.ShouldMute(candidate))
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
                var index = anchor == null ? 0 : Calls.IndexOf(anchor);
                for (var i = index + direction; index >= 0 && i >= 0 && i < Calls.Count; i += direction)
                {
                    if (!IsPlayableForQueue(Calls[i]))
                        continue;
                    await OnCallTappedAsync(Calls[i]);
                    return;
                }
            }

            if (lookup.CanContinue)
                LastQueueEvent = older ? "No previous call available" : "No next call available";
        }

        private async Task RefreshTransportCardAsync(CallItem item, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(item.AudioUrl))
                return;
            // Terminal rows keep their identity and authorization context. A replay
            // can use the same scoped lookup as recovery when the payload was cleared.
            var access = await AuthorizeRecordingAsync(item, token);
            await _callStreamService.RefreshRecordingAudioAsync(item, access.Server, access.RecordedAt, token);
            token.ThrowIfCancellationRequested();
            item.ApplyLiveUpdate();
        }

        private static void ApplyTheme(string? mode)
        {
            var app = Application.Current;
            if (app == null)
                return;

            AppTheme theme;

            if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase))
            {
                theme = AppTheme.Light;
            }
            else if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase))
            {
                theme = AppTheme.Dark;
            }
            else
            {
                theme = AppTheme.Unspecified;
            }

            app.UserAppTheme = theme;
        }

		private void OnToneDetected(string audioUrl)
		{
			if (string.IsNullOrWhiteSpace(audioUrl))
				return;

			try
			{
				MainThread.BeginInvokeOnMainThread(() =>
				{
					try
					{
						var match = Calls.FirstOrDefault(c =>
							!string.IsNullOrWhiteSpace(c.AudioUrl)
							&& string.Equals(c.AudioUrl, audioUrl, StringComparison.OrdinalIgnoreCase));

						if (match == null)
							return;

						var key = match.ToneHotKey;
						var minutes = Math.Clamp(_settingsService.AudioToneHighlightMinutes, 1, 99);
						_toneAlertService.SetTalkgroupHot(key, TimeSpan.FromMinutes(minutes));
						RefreshToneHotFlags();

						try { AppLog.Add(() => $"AudioTone: talkgroup hot for {minutes} minutes. key={key}"); } catch { }
					}
					catch
					{
					}
				});
			}
			catch
			{
			}
		}

		private void OnHotTalkgroupsChanged()
		{
			try
			{
				MainThread.BeginInvokeOnMainThread(RefreshToneHotFlags);
			}
			catch
			{
			}
		}

		private void RefreshToneHotFlags()
		{
			try
			{
				foreach (var c in Calls)
					c.IsToneHot = _toneAlertService.IsTalkgroupHot(c.ToneHotKey);
			}
			catch
			{
			}
		}
    }
}
