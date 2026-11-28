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
        internal event Action<CallItem>? LiveCardAdmitted;

        private const string AppleIosTestAccountEmail = "iostest@joesscanner.com";
        private const string AppleIosTestAccountEmailLegacy = "iostest@jeosscanner.com";

        private readonly ICallStreamService _callStreamService;
        private readonly ISettingsService _settingsService;
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly PlaybackAudioCache _audioCache;
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
        private CallItem? _manualPlaybackTarget;
        private readonly LivePlaybackRequests _playbackRequests = new();
        private readonly PlaybackOwnership _playbackOwnership;

        private int _refreshingCallMetadata;

        private int _audioToggleSerial;
        private bool _isRunning;
        private string _serverUrl = string.Empty;
        private bool _autoPlay;
        private bool _what3WordsLinksEnabled = true;
        private const int MaxCallsToKeep = LiveCallDisplay.CardLimit;
        private volatile bool _audioEnabled;
        private int _totalCallsReceived;
        private int _totalCallsInserted;
        private string _lastQueueEvent = string.Empty;

        // 0 = 1x, 1 = 1.5x, 2 = 2x
        private double _playbackSpeedStep = 0;

        // Currently playing call, used to compute calls waiting.
        private CallItem? _currentPlayingCall;

        // Most recently finished call, used as the anchor when nothing is playing.
        private CallItem? _lastPlayedCall;
        private CallItem? _lastTransportCall;
        private CallItem? _retryPlaybackCall;
        private bool _retryIsAutomatic;

        public bool CanSkipUnavailableRecording => _retryPlaybackCall != null && _unavailableRecording;
        private bool _unavailableRecording;

        // True while we are playing through the backlog.
        private bool _isQueuePlaybackRunning;
        private int _queuePlaybackVersion;


        // When the user explicitly starts monitoring (presses Play/Start Monitoring), keep queue playback alive
        // even if AutoPlay is off. Otherwise calls can arrive and scroll but never advance audio unless the
        // user taps each call.
        private bool _userRunShouldDriveQueue;


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
        public ICommand RetryPlaybackCommand { get; }
        public ICommand SkipUnavailableRecordingCommand { get; }
        public bool CanResumePlayback => _retryPlaybackCall != null || IsMobilePlaybackPaused;
        public bool CanRetryPlayback => _retryPlaybackCall != null && !IsMobilePlaybackPaused;
        public bool ShowPausedPlaybackActions => _monitoring.Current?.IsActive == true && IsMobilePlaybackPaused;

        public bool IsMobilePlaybackPaused =>
            _interruption?.RequiresPlay == true ||
#if ANDROID
            AndroidAudioEnvironment.RequiresPlay;
#elif IOS
            IosAudioEnvironment.RequiresPlay;
#else
            false;
#endif

#if ANDROID
        private const string MonitoringExplicitlyStoppedKey = "android_monitoring_explicitly_stopped";
#elif IOS
        private const string MonitoringExplicitlyStoppedKey = "ios_monitoring_explicitly_stopped";
#else
        private const string MonitoringExplicitlyStoppedKey = "monitoring_explicitly_stopped";
#endif

        private readonly MonitoringIntentStore _monitoringIntent =
            new(MonitoringExplicitlyStoppedKey, message => AppLog.Add(message));

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
            PlaybackOwnership playbackOwnership, ScannerConnectionStore connectionStore, FavoritesService favorites,
            PlaybackAudioCache audioCache, MonitoringDeviceService monitoringDevice)
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
            _audioCache = audioCache;
            MonitoringDevice = monitoringDevice;
            _ = _audioCache.InitializeAsync();
            _connectionStore = connectionStore;
            Favorites = favorites;
            _scheduler = new(favorites);
            _privacyNetwork = PrivacyNetworkPolicy.For(settingsService);
            _settingsService.LocalOnlyModeChanged += OnPrivacyNetworkChanged;
            HostedEntitlement.For(settingsService).Changed += OnHostedEntitlementChanged;
            Favorites.Changed += () => MainThread.BeginInvokeOnMainThread(() => _ = RefreshFavoriteAudioSourceAsync());
            _connections.StatusChanged += OnRecorderStatusChanged;
            _connections.Retired += RetireRecorder;
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
            RetryPlaybackCommand = new Command(async () => await RunLivePlaybackInputAsync(ResumeMobilePlaybackAsync));
            SkipUnavailableRecordingCommand = new Command(async () => await RunLivePlaybackInputAsync(SkipUnavailableRecordingAsync));
#if ANDROID
            AndroidAudioEnvironment.ResumeStateChanged += NotifyPlaybackResumeState;
            AndroidAudioEnvironment.InterruptionChanged += OnAndroidAudioInterruption;
            AndroidAudioEnvironment.ClipFocusInterrupted += OnAndroidFocusInterruption;
            SystemMediaService.AndroidForeground.StateChanged += OnAndroidForegroundChanged;
#elif IOS
            IosAudioEnvironment.ResumeStateChanged += NotifyPlaybackResumeState;
            IosAudioEnvironment.RecoveryInterruptionChanged += OnIosAudioInterruption;
            IosAudioEnvironment.BackgroundMonitoringCheckpoint += OnIosBackgroundCheckpoint;
            IosAudioEnvironment.ForegroundRecovered += OnIosForegroundRecovered;
#endif
            PlayAudioCommand = new Command<CallItem>(async item => await RunLivePlaybackInputAsync(() => PlayVisibleCallAsync(item)));
            JumpToLiveCommand = new Command(async () => await RunLivePlaybackInputAsync(JumpToLiveAsync));

            ToggleConnectionCommand = new Command(async () => await ToggleConnectionAsync());
            PlaybackSpeedDownCommand = new Command(async () => await DecreasePlaybackSpeedStepAsync());
            PlaybackSpeedUpCommand = new Command(async () => await IncreasePlaybackSpeedStepAsync());
            PreviousCallCommand = new Command(async () => await RunLivePlaybackInputAsync(() => NavigateToAdjacentCallAsync(1)));
            NextCallCommand = new Command(async () => await RunLivePlaybackInputAsync(() => NavigateToAdjacentCallAsync(-1)));

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
                onNext: () => SystemNavigateAsync(-1),
                onPrevious: () => SystemNavigateAsync(1));

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
            if (!IsMobilePlaybackPaused && _retryPlaybackCall is { } retry)
            {
                if (ScopeOf(retry) is { } durable && TryGetDurableRecording(retry, durable, out var id) &&
                    _playbackCommit.IsPending(durable, id))
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
            _interruption?.AllowExplicitPlay();
            NotifyPlaybackResumeState();
#if ANDROID
            SystemMediaService.PrepareAndroidPlaybackService();
            AndroidAudioEnvironment.RequestPlay();
#elif IOS
            IosAudioEnvironment.RequestPlay();
#endif
#if ANDROID || IOS
            await RecoverInterruptedPlaybackAsync(userInitiated: true);
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
                ScopeOf(item) is not { } durable || !TryGetDurableRecording(item, durable, out var id))
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

#if IOS
        private void OnIosForegroundRecovered(DateTimeOffset since, DateTimeOffset? suspendedSince)
        {
            if (!_monitoring.CanRecover(_monitoring.Current))
                return;

            RequestRecorderRecovery(since);
            if (suspendedSince is { } start)
            {
                _ = ObserveInterruption(LiveInterruptionSource.Background, true, beganAt: start);
                _ = ObserveInterruption(LiveInterruptionSource.Background, false);
            }
            if (AudioEnabled && (_settingsService.AutoPlay || _userRunShouldDriveQueue))
                _ = RequestQueuePlaybackAsync("IosForegroundRecovery", _userRunShouldDriveQueue);
            SaveInterruptionValue(PlaybackInterruptionKey + "_background", string.Empty,
                onlyIf: since.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
#endif

        // True when connected to the server stream.
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value)
                    return;

                _isRunning = value;
                MonitoringDevice.SetMonitoring(value);
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
            NotifyPlaybackResumeState();
            ((Command)StartCommand).ChangeCanExecute();
            ((Command)StopCommand).ChangeCanExecute();
        }

        private void NotifyPlaybackResumeState()
        {
            OnPropertyChanged(nameof(CanResumePlayback));
            OnPropertyChanged(nameof(CanRetryPlayback));
            OnPropertyChanged(nameof(ShowPausedPlaybackActions));
            OnPropertyChanged(nameof(MediaPlayStopIcon));
            OnPropertyChanged(nameof(MediaPlayStopDescription));
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

                if (value)
                    BeginAudioAtLiveEdge(skipOutstandingRecovery: true);
                _audioEnabled = value;
                if (!value)
                    _ = StopAudioFromToggleAsync();
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
                        _currentPlayingCall = null;

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

                    try
                    {
                        if (!await WaitForLiveEdgeAsync(_monitoring.Current?.Token ?? CancellationToken.None))
                            return;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Add(() => $"Could not move audio to Live ({ex.GetType().Name}).");
                        return;
                    }
                    if (serial != _audioToggleSerial)
                        return;

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
                    if (!_playbackOwnership.IsCurrent(live) || !_monitoring.CanRecover(run) || IsLiveInterruptionBlockingPlayback)
                        return;
                    if (_interruption?.Since.HasValue == true)
                    {
                        await RecoverInterruptedPlaybackAsync(userInitiated);
                        return;
                    }
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
						visibleQueueCount: AutomaticQueueScopes().Length != 0 || _manualLiveTraversal != null ? Math.Max(1, CallsWaiting) : 0);

					if (!canStart || _playbackRequests.HasManualSelection ||
                        (_retryPlaybackCall != null && !HasLagRecoveryForAutomaticRetry))
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
        public string MediaPlayStopIcon => _monitoring.Current == null || ShowPausedPlaybackActions ? "mc_play.png" : "mc_stop.png";
        public string MediaPlayStopDescription => _monitoring.Current == null || ShowPausedPlaybackActions ? "Play" : "Stop";

        // Base server URL used for all API calls and displayed in the UI.
        public string ServerUrl
        {
            get => _serverUrl;
            set
            {
                var newValue = value ?? string.Empty;
                if (string.Equals(_serverUrl, newValue, StringComparison.Ordinal))
                    return;

                _serverUrl = newValue;
                _settingsService.ServerUrl = _serverUrl;

                // Scope filter rules to the new server.
                _filterService.SetServerUrl(_serverUrl);

                OnPropertyChanged();
                OnPropertyChanged(nameof(TaglineText));

                // If the server changes, recompute whether we should show the subscription badge.
                UpdateSubscriptionSummaryFromSettings();
            }
        }

        private volatile bool _changingHostedAccount;
        private readonly SemaphoreSlim _hostedAccountTransition = new(1);

        internal async Task ApplyHostedAccountAsync(string nextUsername, bool authorityChanged, Func<bool> canApply, Action save)
        {
            await _hostedAccountTransition.WaitAsync();
            try
            {
                if (!canApply())
                    return;
                var switching = authorityChanged || RecordingPlaybackAccess.AccountKey(_settingsService,
                    HostedServerRules.DefaultServerUrl, independent: true) != RecordingPlaybackAccess.AccountKeyForUsername(nextUsername);
                if (switching)
                {
                    _changingHostedAccount = true;
                    _connections.Retire(profile => RequiresHostedAccount(profile.Endpoint));
                    foreach (var call in Calls.Where(call => call.RecordingAccess?.Protected == true).ToArray())
                        Calls.Remove(call);
                    foreach (var call in AddressAlerts.Where(call => call.RecordingAccess?.Protected == true).ToArray())
                        AddressAlerts.Remove(call);

                    if (_retryPlaybackCall?.RecordingAccess?.Protected == true)
                        SetPlaybackRetry(null);
                }
                if (canApply())
                    save();
            }
            finally
            {
                if (_changingHostedAccount)
                {
                    _changingHostedAccount = false;
                    ReconcileScannerConnections();
                }
                _hostedAccountTransition.Release();
            }
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
        // The display window may omit older calls already accepted by the journal.
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

        private async void FilterServiceOnRulesChanged(object? sender, EventArgs e)
        {
            try { await MainThread.InvokeOnMainThreadAsync(ApplyAutomaticAudioFiltersAsync); }
            catch (Exception ex) { AppLog.Add(() => $"Could not apply Live audio filters ({ex.GetType().Name})."); }
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
                    await StartMonitoringAsync();
                }
                catch
                {
                }
            });
        }

        private async Task<MonitoringLifecycle.Run?> StartMonitoringCoreAsync(bool driveQueue,
            long? automaticStopRevision = null)
        {
            var run = _monitoring.TryBeginStart(automaticStopRevision);
            if (run == null)
                return null;

            var token = run.Token;
            var intentRevision = _monitoringIntent.BeginStart();
            try
            {
                NotifyMonitoringControls();
                if (!HasEnabledScannerConnections())
                {
                    SetConnectionStatus(ConnectionStatus.Error, "Enable a saved connection in Settings before connecting.");
                    return null;
                }
                SetConnectionStatus(ConnectionStatus.Connecting);

#if ANDROID
                SystemMediaService.BeginAndroidMonitoringRun(run.Id);
#endif
                ResetLiveTraversal();
                InvalidateLivePlayback();
                await run.WaitForInitializationAsync(run.PreviousCleanup);
                try { await FlushLiveCompletionAsync(token); }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    AppLog.Add(() => $"Live queue completion could not be saved before connecting ({ex.GetType().Name}).");
                    SetConnectionStatus(ConnectionStatus.Error, "Could not save playback progress. Try connecting again.");
                    return null;
                }
                await EnsureSystemMediaSessionStartedAsync(run);
                token.ThrowIfCancellationRequested();

                if (!ActivateFreshListeningSession(run))
                    return null;
                _userRunShouldDriveQueue = driveQueue;
                IsRunning = true;
                StartInterruptionTracking();
#if ANDROID
                if (_playbackOwnership.Live != null)
                    AndroidAudioEnvironment.RequestPlay();
#elif IOS
                if (!automaticStopRevision.HasValue && _playbackOwnership.Live != null)
                    IosAudioEnvironment.RequestPlay();
#endif
                RefreshNativeInterruptionState();
                if (IsAppleIosTestAccount())
                    AudioEnabled = true;
                ReconcileScannerConnections();
                UpdateQueueDerivedState();

                var selected = _profiles.FirstOrDefault(profile => profile.Enabled &&
                    profile.Endpoint == ScannerConnectionStore.Normalize(_settingsService.ServerUrl));
                if (selected != null)
                {
                    _ = _monitoringIntent.SaveStartedAsync(intentRevision, selected.Endpoint, null, token);
                    _telemetryService.StartMonitoringHeartbeat(selected.Endpoint);
                }
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

        private async Task EnsureSystemMediaSessionStartedAsync(MonitoringLifecycle.Run run)
        {
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

        private async Task ReceiveRecorderCallAsync(DurableLiveQueue.Run durable, CallItem call, CancellationToken token)
        {
            call.ServerId = durable.ServerId;
            if (call.IsRecordingNotification || call.IsOrderingUpdate)
            {
                if (call.LiveState is not { } notification || !IsCurrentLiveScope(durable))
                    return;
                var update = notification.Read();
                var observation = await SaveLiveObservationAsync(durable, notification.Id, null, update,
                    RecordingOrigin.Unknown, existingOnly: true, token);
                call.ConfirmLiveAdmission(observation.ArrivalSequence.HasValue, observation.ConflictingRecordingTime);
                if (observation.ConflictingRecordingTime)
                    return;
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!IsCurrentLiveScope(durable))
                        return;
                    if (durable.FindCard(Calls, ActiveLiveCard, notification.Id) is { } card &&
                        (!call.IsOrderingUpdate || !ReferenceEquals(card.LiveState, notification)))
                        ApplyLiveUpdate(notification, existing: card);
                    if (observation.PlaybackChanged)
                        RequestQueueForLiveArrival("SavedRecordingNotification");
                });
                return;
            }

            if (call.LiveState is not { } incoming)
                return;
            var snapshot = incoming.Read();
            var skipAutomatic = !AudioEnabled || IsFilteredForPlayback(durable.ServerId, snapshot.Metadata);
            var saved = await SaveLiveObservationAsync(durable, incoming.Id, call.RecordingTimeUtc, snapshot,
                call.RecordingAccess?.Origin ?? RecordingOrigin.Unknown,
                call.IsLiveUpdate && call.RecoveryWindowId == null && !call.IsListeningBaseline, token,
                call.RecoveryWindowId, call.IsListeningBaseline, skipAutomatic);
            call.ConfirmLiveAdmission(saved.ArrivalSequence.HasValue, saved.ConflictingRecordingTime);
            if (saved.ConflictingRecordingTime)
                return;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (!IsCurrentLiveScope(durable) || saved.IsRecoveryBacklog)
                    return;

                var existing = durable.FindCard(Calls, ActiveLiveCard, incoming.Id);
                // The first HTTP snapshot is already in History. It can update a
                // card still on screen when that recorder was disabled and re-enabled.
                if (call.IsListeningBaseline && existing == null)
                    return;

                call.LiveArrivalSequence = saved.ArrivalSequence;
                call.LivePlaybackOwner = durable.Owner;
                if (call.RecordingAccess is { } access)
                    call.RecordingAccess = access with { Account = durable.Account };

                if (!call.IsLiveUpdate || !string.IsNullOrEmpty(call.Transcription))
                    TotalCallsReceived++;

                if (call.IsLiveUpdate || existing != null)
                {
                    if (existing != null)
                        ApplyLiveUpdate(incoming, existing, call);
                    if (saved.PlaybackChanged)
                        RequestQueueForLiveArrival("UpdatedLiveRecording");
                    return;
                }

                _filterService.EnsureRulesForCall(call, new RecordingMetadataScope(durable.ServerId, durable.Server, durable.Protected, durable.Account).StorageKey);
                call.IsMutedByFilter = _filterService.ShouldMute(call);

                try
                {
                    var hadAddress = call.HasDetectedAddress;
                    _addressDetectionService.Apply(call);
                    if (!hadAddress && call.HasDetectedAddress)
                        AddAddressAlertIfNew(call);
                    _ = _what3WordsService.ResolveCoordinatesIfNeededAsync(call, token);
                }
                catch { }

                if (InsertLiveCardInDisplayOrder(call) && saved.FirstAdmission)
                {
                    try { LiveCardAdmitted?.Invoke(call); }
                    catch (Exception ex) { AppLog.DebugWriteLine(() => $"Live display observer failed ({ex.GetType().Name})."); }
                }
                TotalCallsInserted++;
                LastQueueEvent = $"Inserted call at {DateTime.Now:T}";
                UpdateQueueDerivedState();
                RequestQueueForLiveArrival("NewCallInserted");
                ObserveTransmission(durable, call, snapshot, saved, token);
            });
        }

        private Task<DurableLiveQueue.Observation> SaveLiveObservationAsync(DurableLiveQueue.Run run, string id, DateTimeOffset? recordedAt,
            LiveCallSnapshot snapshot, RecordingOrigin origin, bool existingOnly, CancellationToken token,
            string? recoveryWindowId = null, bool historyBaseline = false, bool skipAutomatic = false) =>
            _durableQueue.ObserveWithPositionAsync(run, id, recordedAt, snapshot, token, origin, existingOnly,
                ex => AppLog.Add(() => $"Live queue storage unavailable; retrying the recording ({ex.GetType().Name})."),
                recoveryWindowId: recoveryWindowId, historyBaseline: historyBaseline,
                skipAutomatic: skipAutomatic);

        private Task StopMonitoringCoreAsync()
        {
            Task? saveIntent = null;
            var cleanup = _monitoring.StopAsync(run => saveIntent = RecordMonitoringStop(run),
                RetireMonitoringPlatform, CleanupMonitoringRunAsync);
            return saveIntent == null ? cleanup : Task.WhenAll(cleanup, saveIntent);
        }

        private Task RecordMonitoringStop(MonitoringLifecycle.Run? run)
        {
            Interlocked.Increment(ref _audioToggleSerial);
            ResetLiveTraversal();
            InvalidateLivePlayback();
            _connections.Retire(_ => true);
            var saveIntent = _monitoringIntent.SaveStoppedAsync(null, null);
            StopInterruptionTracking();

            _userRunShouldDriveQueue = false;

            if (run?.IsActive == true)
                _telemetryService.StopMonitoringHeartbeat("user_stop");

            IsRunning = false;
            SetConnectionStatus(ConnectionStatus.Stopped);
            _lastPlayedCall = null;
            _lastTransportCall = null;
            SetPlaybackRetry(null);
            PublishScannerConnectionRows();
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

        public Task StopAudioFromToggleAsync()
        {
            ResetLiveTraversal();
            SetPlaybackRetry(null);
            InvalidateLivePlayback();
            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));
            return Task.CompletedTask;
        }

        public Task StopAudioFromSettingsAsync()
        {
            return StopAudioFromToggleAsync();
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

			await RunLivePlaybackInputAsync(() => OnCallTappedAsync(resolved));
		}

		public CallItem? ResolveCallForAddressAlert(CallItem alertItem) =>
            RecordingIdentity.Find(Calls, alertItem);

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
        AppLog.DebugWriteLine(() => $"Error in ApplyAudioMenuSelectionAsync: {DiagnosticRedaction.Describe(ex)}");
        try
        {
            AppLog.Add(() => $"Audio(VM): Menu apply error. {DiagnosticRedaction.Describe(ex)}");
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
        AppLog.DebugWriteLine(() => $"Error in SetAudioEnabledAsync: {DiagnosticRedaction.Describe(ex)}");
        try
        {
            AppLog.Add(() => $"Audio(VM): Enable error. {DiagnosticRedaction.Describe(ex)}");
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
        private CallItem ResolveDurableCard(DurableLiveQueue.Run durable, DurableLiveQueue.Recording row, CallItem? capturedReplay = null)
        {
            bool Matches(CallItem? item) => durable.MatchesCard(item, row.Id);

            var card = Calls.FirstOrDefault(Matches);
            if (card == null)
            {
                card = Matches(capturedReplay) ? capturedReplay :
                    Matches(_currentPlayingCall) ? _currentPlayingCall :
                    Matches(_retryPlaybackCall) ? _retryPlaybackCall :
                    Matches(_lastPlayedCall) ? _lastPlayedCall : null;
            }
            if (card == null)
                return row.ToCallItem(durable.Server, durable.ServerId);

            card.LiveArrivalSequence = row.ArrivalSequence;
            card.LiveAudioRevision = row.AudioRevision;
            card.LivePlaybackOwner = durable.Owner;
            if (row.Snapshot is { } snapshot && card.LiveState is { } state)
            {
                state.Observe(snapshot, 0);
                card.ApplyLiveUpdate();
            }
            return card;
        }

        private void ShowDurableCard(CallItem card)
        {
            if (!Calls.Contains(card) && ScopeOf(card) is { } scope && scope.FindCard(Calls, null, card.BackendId) == null)
                InsertLiveCardInDisplayOrder(card);
        }

        private bool InsertLiveCardInDisplayOrder(CallItem card)
        {
            var index = 0;
            while (index < Calls.Count && CompareCardOrder(Calls[index], card) >= 0)
                index++;
            var shouldPrune = Calls.Count == MaxCallsToKeep;
            if (!LiveCallDisplay.InsertCard(Calls, card, index, _currentPlayingCall))
                return false;
            if (shouldPrune)
                PurgeStaleAddressAlerts();
            UpdateQueueDerivedState();
            return true;
        }

        private static int CompareCardOrder(CallItem left, CallItem right)
        {
            if (left.RecordingTimeUtc is not { } leftTime || right.RecordingTimeUtc is not { } rightTime)
                return left.Timestamp.CompareTo(right.Timestamp);
            var time = leftTime.CompareTo(rightTime);
            return time != 0 ? time : StringComparer.Ordinal.Compare(left.BackendId, right.BackendId);
        }

        private bool HasDurablePosition(CallItem item) =>
            ScopeOf(item) != null;

        private long LocalRecoveryFloor(DurableLiveQueue.Run run)
        {
            var now = DateTimeOffset.UtcNow;
            if (!run.Protected && !HostedServerRules.RequiresApiFirewallCredentials(_settingsService, run.Server))
                return 0;
            var tier = HostedEntitlement.For(_settingsService).Current()?.Tier;
            return DurableLiveQueue.HistoricalFloor(true, tier, IsCurrentLiveScope(run), tier ?? 0, now);
        }

        private static bool TryGetDurableRecording(CallItem item, DurableLiveQueue.Run run, out string id)
        {
            id = item.BackendId;
            return item.ServerId == run.ServerId && item.LiveState is { } state && DurableLiveQueue.ServerKey(state.ServerUrl) == run.Server &&
                run.Owns(item.RecordingAccess) && id.Length != 0;
        }

        private void PurgeStaleAddressAlerts()
		{
			try
			{
				if (AddressAlerts.Count == 0 && _hiddenAddressAlerts.Count == 0)
					return;

				for (var i = AddressAlerts.Count - 1; i >= 0; i--)
				{
					var a = AddressAlerts[i];
					if (ResolveCallForAddressAlert(a) == null)
						AddressAlerts.RemoveAt(i);
				}

				for (var i = _hiddenAddressAlerts.Count - 1; i >= 0; i--)
				{
					var a = _hiddenAddressAlerts[i];
					if (ResolveCallForAddressAlert(a) == null)
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

        private async Task<PlaybackAudioLease> PrepareManualAudioAsync(CallItem item, CancellationToken token)
        {
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
            preparation.CancelAfter(TimeSpan.FromMinutes(15));
            return await AcquireRecordingAudioAsync(item, preparation.Token) ?? throw new RecordingAudioUnavailableException();
        }

        private async Task<PlaybackOutcome> PlayAudioAsync(CallItem item, LivePlaybackRequests.Request request,
            bool allowFilteredPlayback = false, PlaybackAudioLease? preparedAudio = null,
            LivePlaybackScheduler.Choice? forecastFrom = null)
        {
            if (_playbackOwnership.Live == null || (!AudioEnabled && !allowFilteredPlayback) || !request.CanContinue)
                return PlaybackOutcome.Canceled;

            if (!request.IsManual && ScopeOf(item) == null)
                return PlaybackOutcome.Withheld;

            var owner = preparedAudio?.Owner ?? CapturePlaybackOwner(item);
            using var lifetime = owner == null ? null : CancellationTokenSource.CreateLinkedTokenSource(owner.Token, request.Token);
            var token = lifetime?.Token ?? request.Token;
            var completion = new PlaybackCompletion();
            completion.Bind(owner);
            var reachedNative = false;
            var outcome = IsFilteredForPlayback(item, allowFilteredPlayback)
                ? PlaybackOutcome.Skipped
                : await PlaybackAttempt.RunAsync(() =>
                    PlayAudioCoreAsync(item, request, completion, preparedAudio, () => reachedNative = true, forecastFrom, owner, token),
                    token, ex => { if (owner?.Token.IsCancellationRequested != true) ReportPlaybackFailure(ex, request); }, completion);

            if (owner?.Token.IsCancellationRequested == true)
                return PlaybackOutcome.Canceled;
            if (!request.CanContinue)
                return outcome;

            if (outcome.AdvancesQueue())
            {
                if (outcome == PlaybackOutcome.Skipped && ScopeOf(item) is { } durable &&
                    TryGetDurableRecording(item, durable, out var id))
                    await _durableQueue.MarkAsync(durable, id, outcome, token);
                CompleteQueuedCall(item);
                _lastQueuePlaybackFinishedAtUtc = DateTimeOffset.UtcNow;
            }
            else if (outcome == PlaybackOutcome.Failed && !request.IsManual)
            {
                if (item.IsCompletedReplay || ScopeOf(item) is not { } failedScope ||
                    !TryGetDurableRecording(item, failedScope, out var failedId))
                    return PlaybackOutcome.Withheld;

                if (completion.Confirmed)
                {
                    // Preserve a confirmed native end until its durable write succeeds.
                    SetPlaybackRetry(item, automatic: true);
                }
                else
                {
                    if (reachedNative || IsFilteredForPlayback(item))
                        await _durableQueue.MarkAsync(failedScope, failedId, PlaybackOutcome.Skipped, token);
                    else
                        await _durableQueue.DeferAudioAsync(failedScope, failedId, item.LiveAudioRevision, token);
                    outcome = PlaybackOutcome.Withheld;
                }
            }

            RequestReadyCount();
            return outcome;
        }

        private async Task PlayAudioCoreAsync(CallItem item, LivePlaybackRequests.Request request, PlaybackCompletion completion,
            PlaybackAudioLease? preparedAudio, Action enteringNative, LivePlaybackScheduler.Choice? forecastFrom,
            RecorderOperation? owner, CancellationToken token)
        {
            var rate = await GetEffectivePlaybackRateAsync(item, token);
            token.ThrowIfCancellationRequested();
            _currentPlayingCall = item;
            RequestReadyCount();
            PlaybackAudioLease? acquired = null;
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var gapMs = _lastQueuePlaybackFinishedAtUtc is { } last ? (DateTimeOffset.UtcNow - last).TotalMilliseconds : 0;
                if (gapMs > 0)
                    AppLog.Add(() => $"QueueTiming: selected backendId={item.BackendId} gapSincePriorFinishMs={gapMs:0}");

                if (preparedAudio == null)
                {
                    using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(token);
                    // Bound automatic preparation so a remote recorder cannot hold the shared queue.
                    acquisition.CancelAfter(request.IsManual ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(5));
                    try { acquired = await AcquireRecordingAudioAsync(item, acquisition.Token, automatic: !request.IsManual, owner: owner); }
                    catch (OperationCanceledException ex) when (!token.IsCancellationRequested && acquisition.IsCancellationRequested)
                    {
                        throw new TimeoutException("Audio preparation timed out.", ex);
                    }
                }

                token.ThrowIfCancellationRequested();
                var audio = preparedAudio ?? acquired ?? throw new RecordingAudioUnavailableException();
                if (!request.IsManual && ScopeOf(item) == null)
                    return;
                using var authorization = ProtectLiveRecording(item, token);
                authorization?.Validate();
                if (!request.IsManual && !item.IsCompletedReplay && ScopeOf(item) is { } pending &&
                    TryGetDurableRecording(item, pending, out var id))
                    await _durableQueue.MarkPlayingAsync(pending, id, token);

                token.ThrowIfCancellationRequested();
                if (IsFilteredForPlayback(item, request.IsManual && preparedAudio != null))
                    return;

                try { _systemMediaService.UpdateNowPlaying(BuildNowPlayingMetadataSnapshot(item), AudioEnabled); } catch { }
                AppLog.Add(() => $"QueueTiming: backendId={item.BackendId} acquisitionMs={started.Elapsed.TotalMilliseconds:0} rate={rate:0.###}");

                using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(authorization?.Token ?? token);
#if !(ANDROID || IOS)
                var expectedSeconds = item.CallDurationSeconds > 0 ? item.CallDurationSeconds / Math.Max(rate, 0.1) : 90;
                watchdog.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(expectedSeconds * 2 + 120, 180, 10800)));
#endif
                started.Restart();
                try
                {
                    await PlayOnDeviceAsync(item, audio.Url, rate, request, watchdog.Token, completion, () =>
                    {
                        authorization?.Validate();
                        enteringNative();
                    }, forecastFrom, audio.Invalidate, owner);
                    if (!completion.Confirmed)
                        authorization?.Validate();
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && authorization?.Token.IsCancellationRequested == true)
                {
                    throw RecordingPlaybackAccess.VerificationRequired();
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested && watchdog.IsCancellationRequested)
                {
                    throw new TimeoutException("Call playback watchdog timeout.", ex);
                }
                if (request.CanContinue)
                    AppLog.Add(() => $"QueueTiming: backendId={item.BackendId} playbackMs={started.Elapsed.TotalMilliseconds:0} totalCallSeconds={item.CallDurationSeconds:0.###}");
            }
            finally
            {
                acquired?.Dispose();
                if (request.IsCurrent)
                {
                    item.IsPlaying = false;
                    if (_currentPlayingCall == item)
                        _currentPlayingCall = null;
                    RequestReadyCount();
                }
            }
        }

        private async Task<(string Server, bool Protected, DateTimeOffset? RecordedAt)> AuthorizeRecordingAsync(
            CallItem item, CancellationToken token, bool cached = false)
        {
            var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
            if (item.ServerId != null && !CanUseRecorder(item, server))
                throw new RecordingAccessException("The recording belongs to a recorder whose address has changed.");
            var key = DurableLiveQueue.ServerKey(server);
            var context = item.RecordingAccess ?? new RecordingAccessContext(RecordingOrigin.Unknown, false);
            var protectedSource = context.Protected || HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server);
            var account = RequireCurrentRecordingAccount(item);
            var owner = item.LivePlaybackOwner;
            var recordedAt = item.RecordingTimeUtc;
            if (key != null && !string.IsNullOrWhiteSpace(item.BackendId) && (item.ServerId != null || protectedSource))
            {
                var persisted = await _durableQueue.GetAccessAsync(key, item.BackendId, account, token, protectedSource, item.ServerId);
                if (persisted is { } record)
                {
                    context = record.Access with { Protected = record.Access.Protected || protectedSource };
                    owner = record.Owner;
                    recordedAt = record.RecordedAt;
                }
            }
            protectedSource |= context.Protected;
            if (protectedSource && !CanUseRecorder(item, server))
                throw new RecordingAccessException("The recording's hosted server is no longer configured.");
            var currentLive = ScopeOf(item) is { } scope && scope.Owner == owner && (!protectedSource || context.Account != null);
            if (cached && protectedSource)
                RecordingPlaybackAccess.RequireCachedEntitlement(_settingsService, server, account, recordedAt,
                    currentLive && context.Origin == RecordingOrigin.Live, independent: item.ServerId != null);
            else
                await RecordingPlaybackAccess.VerifyAsync(_settingsService, _subscriptionService, server,
                    recordedAt, context, currentLive, requireAccount: protectedSource, token, independent: item.ServerId != null);
            return (server, protectedSource, recordedAt);
        }

        private string? RequireCurrentRecordingAccount(CallItem item)
        {
            var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
            if (item.RecordingAccess?.Protected != true && !HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server))
            {
                if (item.ServerId is not { } id)
                    return null;
                var recorderAccount = CustomRecorderAccount(id, server);
                if (!CanUseRecorder(item, server) || recorderAccount != item.RecordingAccess?.Account)
                    throw new RecordingAccessException("The recording belongs to a different recorder login.");
                return recorderAccount;
            }
            var account = RecordingPlaybackAccess.AccountKey(_settingsService, server, independent: item.ServerId != null);
            if (_changingHostedAccount || account == null || item.RecordingAccess?.Account != account ||
                !CanUseRecorder(item, server))
                throw new RecordingAccessException("This recording belongs to a different hosted account or server.");
            return account;
        }

        private async Task OpenDonateAsync()
        {
            try
            {
                if (_privacyNetwork.AllowsCloud)
                    await Launcher.Default.OpenAsync(new Uri(DonateUrl));
            }
            catch
            {
            }
        }

        private List<CallItem> SnapshotLiveCalls() => Calls.ToList();

        private void ApplyLiveUpdate(LiveCallState state, CallItem existing, CallItem? update = null)
        {
            if (existing.LiveState is { } restored && !ReferenceEquals(restored, state))
                restored.MergeRecorderState(state);
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

        if (RecordingIdentity.Find(AddressAlerts, call) != null || RecordingIdentity.Find(_hiddenAddressAlerts, call) != null)
            return;

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

        var existing = RecordingIdentity.Find(AddressAlerts, call);

        if (existing != null)
            AddressAlerts.Remove(existing);
        else
        {
            // Also allow dismissing something that was previously hidden.
            var hiddenExisting = RecordingIdentity.Find(_hiddenAddressAlerts, call);

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

        private void SetPlaybackRetry(CallItem? item, bool automatic = false)
        {
            _retryIsAutomatic = item != null && automatic;
            if (ReferenceEquals(_retryPlaybackCall, item))
                return;
            _retryPlaybackCall = item;
            _unavailableRecording = false;

            NotifyPlaybackResumeState();
            OnPropertyChanged(nameof(CanSkipUnavailableRecording));
        }

        private void CompleteQueuedCall(CallItem item)
        {
            _lastTransportCall = item;
            var wasLate = item.ConsumeLateAudio();
            if (!wasLate || _lastPlayedCall == null || CompareCardOrder(item, _lastPlayedCall) > 0)
                _lastPlayedCall = item;
        }

        private void ClearPlayingSelection()
        {
            ClearActiveLiveCard();
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
            AppLog.Basic(() => ex is HttpRequestException { StatusCode: { } status }
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

        private async Task RefreshTransportCardAsync(CallItem item, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(item.AudioUrl))
                return;
            // Terminal rows keep their identity and authorization context. A replay
            // can use the same scoped lookup as recovery when the payload was cleared.
            var access = await AuthorizeRecordingAsync(item, token);
            await RefreshRecorderAudioAsync(item, access.Server, access.RecordedAt, token);
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
