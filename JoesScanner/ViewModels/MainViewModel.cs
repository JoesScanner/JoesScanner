using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Input;

namespace JoesScanner.ViewModels
{
    // Main view model for the primary JoesScanner client screen.
    // Handles streaming calls, playback, theme, jump-to-live, and global audio enable state.
    public class MainViewModel : BindableObject
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

        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _audioCts;

        // Set true only when the user explicitly disconnects.
        // Used to decide whether we should clear the "last connected" intent across restarts.
        private volatile bool _userRequestedStop;


        private int _audioToggleSerial;
        // When true, the live queue continues to run but audio output is suppressed.
        // This is used when the user navigates to the History tab.
        private volatile bool _isMainAudioSoftMuted;

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

        // True while we are playing through the backlog.
        private bool _isQueuePlaybackRunning;

        // True when we should resume playback after a temporary soft mute (History tab, etc.).
        // This prevents the UI from starting playback when AutoPlay is off and nothing was playing.
        private bool _resumePlaybackAfterSoftMute;

        // When the user explicitly starts monitoring (presses Play/Start Monitoring), keep queue playback alive
        // even if AutoPlay is off. Otherwise calls can arrive and scroll but never advance audio unless the
        // user taps each call.
        private bool _userRunShouldDriveQueue;

        // New calls received while the user is behind live are stored here so the backlog can play in order.
        // Newest pending call is stored at index 0.
        private readonly object _pendingCallsLock = new();
        private readonly List<CallItem> _pendingCalls = new();

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
            IToneAlertService toneAlertService)
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

			_toneAlertService.ToneDetected += OnToneDetected;
			_toneAlertService.HotTalkgroupsChanged += OnHotTalkgroupsChanged;

            AddressAlerts.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasAddressAlerts));

            _audioHttpClient = new HttpClient
            {
                // Do not use HttpClient.Timeout for long call downloads.
                // Per call watchdog timeouts are enforced via CancellationToken.
                Timeout = Timeout.InfiniteTimeSpan
            };

            // Initialize server URL from settings.
            // IMPORTANT: Do not force a default URL into storage here.
            // If the user has not configured a server, _serverUrl stays blank and
            // StartMonitoringAsync will prompt them to set one in Settings.
            _serverUrl = _settingsService.ServerUrl ?? string.Empty;

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
            StartCommand = new Command(Start, () => !IsRunning);
            StopCommand = new Command(async () => await StopMonitoringAsync(), () => IsRunning);
            OpenDonateCommand = new Command(async () => await OpenDonateAsync());
            ToggleAudioCommand = new Command(async () => await OnToggleAudioAsync());
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
                try
                {
                    if (!IsRunning)
                        return;

                    _ = MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        try { await StopMonitoringAsync(); } catch { }
                    });
                }
                catch
                {
                }
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

            // History tab: soft mute without interrupting the live queue.
            QueueControlBus.MainAudioMuteRequested += (isMuted) =>
            {
                try
                {
                    _ = MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        try { await SetMainAudioSoftMuteAsync(isMuted); } catch { }
                    });
                }
                catch
                {
                }
            };
        }

        private async Task SetMainAudioSoftMuteAsync(bool isMuted)
        {
            _isMainAudioSoftMuted = isMuted;

            if (isMuted)
            {
                // Only resume when unmuted if something was already playing.
                _resumePlaybackAfterSoftMute =
                    IsRunning &&
                    AudioEnabled &&
                    (_isQueuePlaybackRunning || _currentPlayingCall != null);

                // Cancel any in-flight playback immediately, but do not reset queue state.
                if (_audioCts != null)
                {
                    try { _audioCts.Cancel(); } catch { }
                }

                try { await _audioPlaybackService.StopAsync(); } catch { }
                return;
            }

			// Unmuted: never start new playback when nothing was playing.
			if (_resumePlaybackAfterSoftMute && IsRunning && AudioEnabled)
            {
                _resumePlaybackAfterSoftMute = false;
				_ = RequestQueuePlaybackAsync("SoftMuteUnmuteResume", true);
                return;
            }

            _resumePlaybackAfterSoftMute = false;
        }

        private Task SystemPlayAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (!IsRunning)
                    await StartMonitoringAsync();
            
                    if (IsRunning && AudioEnabled)
                        _ = RequestQueuePlaybackAsync("UserPlay", true);
});
        }

        private Task SystemStopAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (IsRunning)
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

        private const string ServiceAuthUsername = "secapppass";
        private const string ServiceAuthPassword = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

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
                OnPropertyChanged(nameof(MediaPlayStopIcon));
                OnPropertyChanged(nameof(IsMediaButtonsEnabled));
                OnPropertyChanged(nameof(IsSpeedButtonsEnabled));
                ((Command)StartCommand).ChangeCanExecute();
                ((Command)StopCommand).ChangeCanExecute();

                if (!_isRunning)
                {
                    SetConnectionStatus(ConnectionStatus.Stopped);
                }
            }
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
			return MainThread.InvokeOnMainThreadAsync(async () =>
			{
				try
				{
					// Always pull AutoPlay from the settings service so the Settings page is the single source of truth.
					_autoPlay = _settingsService.AutoPlay;
            What3WordsLinksEnabled = _settingsService.What3WordsLinksEnabled;
					var canStart = _playbackCoordinator.CanStartQueuePlayback(
						reason,
						_settingsService.ServerUrl,
						userInitiated: userInitiated,
						isRunning: IsRunning,
						audioEnabled: AudioEnabled,
						isMainAudioSoftMuted: _isMainAudioSoftMuted,
						isAlreadyPlaying: _currentPlayingCall != null,
						isQueuePlaybackRunning: _isQueuePlaybackRunning,
						visibleQueueCount: Calls.Count);

					if (!canStart)
						return;

					await EnsureQueuePlaybackAsync();
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
        public string MediaPlayStopIcon => IsRunning ? "mc_stop.png" : "mc_play.png";

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

                OnPropertyChanged();
                OnPropertyChanged(nameof(TaglineText));

                // If the server changes, recompute whether we should show the subscription badge.
                UpdateSubscriptionSummaryFromSettings();

                // Contract: changing servers from Settings must stop queue playback and disconnect.
                // The user must explicitly press Play again to connect and resume.
                if (IsRunning)
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

        // Number of calls waiting in the playback queue, excluding the call at the current anchor.
        // Includes pending calls collected while the user is behind live.
        public int CallsWaiting
        {
            get
            {
                if (Calls == null || Calls.Count == 0)
                {
                    return GetPendingPlayableCount();
                }

                var anchor = _currentPlayingCall ?? _lastPlayedCall;

                bool IsPlayable(CallItem c) =>
                    !string.IsNullOrWhiteSpace(c.AudioUrl) &&
                    !_filterService.ShouldHide(c) &&
                    !_filterService.ShouldMute(c);

                if (anchor == null)
                {
                    var total = 0;
                    for (var i = 0; i < Calls.Count; i++)
                    {
                        if (IsPlayable(Calls[i]))
                            total++;
                    }

                    return total + GetPendingPlayableCount();
                }

                var anchorIndex = Calls.IndexOf(anchor);

                if (anchorIndex < 0)
                {
                    var total = 0;
                    for (var i = 0; i < Calls.Count; i++)
                    {
                        if (IsPlayable(Calls[i]))
                            total++;
                    }

                    return total + GetPendingPlayableCount();
                }

                if (anchorIndex <= 0)
                    return GetPendingPlayableCount();

                var count = 0;
                for (var i = anchorIndex - 1; i >= 0; i--)
                {
                    if (IsPlayable(Calls[i]))
                        count++;
                }

                return count + GetPendingPlayableCount();
            }
        }

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

            return string.Equals(uri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);
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

            // _pendingCalls newest is at index 0. Insert oldest first so ordering remains newest at index 0.
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                Calls.Insert(0, pending[i]);
            }

            EnforceMaxCalls();
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

            Calls.Insert(0, promoted);
            EnforceMaxCalls();

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
            return idx > 0;
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
                else if (anchorIndex == 0)
                {
                    // We are at live for the visible list. If new calls arrived while we were behind live,
                    // promote the oldest pending call and play it next.
                    if (TryPromoteOldestPendingCallToVisible(out var promotedFromLive))
                        return promotedFromLive;

                    return null;
                }
                else
                {
                    startIndex = anchorIndex - 1;
                }
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
            AppLog.Add(() => "User clicked Start Monitoring");

            // Cross-platform: user explicitly started monitoring, so the run should drive queue playback
            // even when AutoPlay is off.
            _userRunShouldDriveQueue = true;

            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    await StartMonitoringAsync();

                    if (IsRunning && AudioEnabled && (_settingsService.AutoPlay || _userRunShouldDriveQueue))
                        _ = RequestQueuePlaybackAsync("UserStartMonitoring", userInitiated: _userRunShouldDriveQueue);
                }
                catch
                {
                }
            });
        }

        public async Task StartMonitoringAsync()
        {
            if (IsRunning)
                return;

            // Starting a new monitoring run resets any prior user-stop intent.
            _userRequestedStop = false;

            var serverUrl = _settingsService.ServerUrl ?? string.Empty;

            if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
            {
                var msg = "Select a server in Settings and press Validate before connecting.";
                try { AppLog.Add(() => $"Connect blocked: missing or invalid server url: '{serverUrl}'"); } catch { }
                SetConnectionStatus(ConnectionStatus.Error, msg);
                return;
            }

            var isJoesScannerServer = false;

            if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
            {
                isJoesScannerServer =
                    string.Equals(serverUri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);
            }

            _telemetryService.TrackConnectionAttempt(serverUrl, isJoesScannerServer);

            var username = _settingsService.BasicAuthUsername ?? string.Empty;

            if (isJoesScannerServer)
            {
                AppLog.Add(() => $"Subscription check: server={serverUrl}, user={username}");


                var password = _settingsService.BasicAuthPassword ?? string.Empty;

                // Hosted Joe's Scanner server requires a valid website account.
                // Do not attempt to connect unless the user has entered credentials.
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    var msg = "Enter your Joe's Scanner username and password in Settings before connecting to the hosted server.";
                    AppLog.Add(() => "Subscription check blocked: missing credentials for hosted server.");

                    try
                    {
                        _settingsService.SubscriptionLastStatusOk = false;
                        _settingsService.SubscriptionLastMessage = msg;
                        _settingsService.SubscriptionLastCheckUtc = DateTime.UtcNow;
                    }
                    catch { }

                    UpdateSubscriptionSummaryFromSettings();
                    SetConnectionStatus(ConnectionStatus.AuthFailed, msg);
                    return;
                }
                try
                {
                    var subResult = await _subscriptionService.EnsureSubscriptionAsync(CancellationToken.None);
                    if (!subResult.IsAllowed)
                    {
                        AppLog.Add(() => $"Subscription check failed: {subResult.ErrorCode} {subResult.Message}");

                        SetConnectionStatus(ConnectionStatus.AuthFailed,
                            subResult.Message ?? "Subscription not active");

                        return;
                    }

                    AppLog.Add(() => "Subscription check passed, starting stream.");
                    UpdateSubscriptionSummaryFromSettings();
                }
                catch (Exception ex)
                {
                    AppLog.Add(() => $"Subscription check error: {ex.Message}");
                    SetConnectionStatus(ConnectionStatus.Error, "Subscription check error");
                    return;
                }
            }
            else
            {
                AppLog.Add(() => $"Custom server detected ({serverUrl}), skipping Joe's Scanner subscription check.");
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
            }

            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }

                _cts.Dispose();
                _cts = null;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            AppStateStore.SetBool("last_connected_on_exit", true);

            SetConnectionStatus(ConnectionStatus.Connecting);
            IsRunning = true;

            if (IsAppleIosTestAccount())
            {
                // Ensure Apple review account immediately produces audible playback.
                AudioEnabled = true;
                AutoPlay = true;
            }

            await EnsureSystemMediaSessionStartedAsync();

            _telemetryService.StartMonitoringHeartbeat(serverUrl);

            _currentPlayingCall = null;
            ClearPendingCalls();


            if (Calls.Count > 0)
            {
                _lastPlayedCall = Calls[0];
            }
            else
            {
                _lastPlayedCall = null;
            }

            UpdateQueueDerivedState();

            _ = Task.Run(() => RunCallStreamLoopAsync(token));

            // Best-effort preload of History lookups. Never blocks connecting.
            // If it fails, History page can still load lookups on demand and cache later.
            _ = Task.Run(() => _historyLookupsCacheService.PreloadAsync(
                forceReload: false,
                reason: "connect",
                cancellationToken: CancellationToken.None));
        }

        public async Task StartMonitoringWithAutoplayAsync()
        {
            await StartMonitoringAsync();

            // Autoplay connect still counts as an active monitoring run.
            _userRunShouldDriveQueue = true;

            try
            {
				if (IsRunning && _audioEnabled && _settingsService.AutoPlay)
					await RequestQueuePlaybackAsync("ConnectAutoplay", false);
            }
            catch
            {
            }

        }


        private async Task EnsureSystemMediaSessionStartedAsync()
        {
            try
            {
#if ANDROID
                await EnsureAndroidNotificationPermissionAsync();
#endif
            }
            catch
            {
            }

            try
            {
                await _systemMediaService.StartSessionAsync(AudioEnabled);
                _systemMediaService.UpdateNowPlaying("Connected", "Joes Scanner", AudioEnabled);
            }
            catch
            {
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
        private static async Task EnsureAndroidNotificationPermissionAsync()
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(33))
                    return;

                var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
                if (status != PermissionStatus.Granted)
                {
                    await Permissions.RequestAsync<Permissions.PostNotifications>();
                }
            }
            catch
            {
            }
        }
#endif

        private async Task RunCallStreamLoopAsync(CancellationToken token)
        {
            var retryDelay = TimeSpan.FromSeconds(2);

            // Important behavior contract:
            // This loop must continue attempting to reconnect until the user explicitly clicks Stop.
            // It must never terminate itself due to transient network, polling, or WebSocket failures.
            while (!token.IsCancellationRequested)
            {
                try
                {
                    try
                    {
                        await foreach (var call in _callStreamService.GetCallStreamAsync(token))
                        {
                            if (token.IsCancellationRequested)
                                break;

                            if (call == null)
                                continue;

                            TotalCallsReceived++;

                            try
                            {
                                await MainThread.InvokeOnMainThreadAsync(() =>
                                {
                                    if (token.IsCancellationRequested)
                                        return;

                                    // Status sentinel messages
                                    if (string.Equals(call.Talkgroup, "AUTH", StringComparison.OrdinalIgnoreCase))
                                    {
                                        SetConnectionStatus(ConnectionStatus.AuthFailed);
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

                                    // Transcription update for an existing call
                                    if (call.IsTranscriptionUpdate && !string.IsNullOrWhiteSpace(call.BackendId))
                                    {
                                        var existing = Calls.FirstOrDefault(c => c.BackendId == call.BackendId);
                                        if (existing != null)
                                        {
                                            if (!string.IsNullOrWhiteSpace(call.Transcription))
                                            {
                                                existing.Transcription = call.Transcription;

                                                
                                                // Carry address detection results over when we receive the transcription update payload.
                                                // The stream produces update items separately, so we must copy these fields onto the existing UI item.
                                                if (call.HasDetectedAddress)
                                                {
                                                    var hadAddress = existing.HasDetectedAddress;

                                                    existing.DetectedAddress = call.DetectedAddress;
                                                    existing.DetectedAddressConfidencePercent = call.DetectedAddressConfidencePercent;
                                                    existing.DetectedAddressCandidates = call.DetectedAddressCandidates;

                                                    if (!hadAddress && existing.HasDetectedAddress)
                                                        AddAddressAlertIfNew(existing);
                                                }
if (call.HasWhat3WordsAddress)
{
    existing.What3WordsAddress = call.What3WordsAddress;
}

if (!string.IsNullOrWhiteSpace(existing.DebugInfo))
                                                {
                                                    var cleaned = existing.DebugInfo
                                                        .Replace("No transcription from server", string.Empty, StringComparison.OrdinalIgnoreCase)
                                                        .Trim();

                                                    cleaned = cleaned.Trim(' ', '|', ';');

                                                    existing.DebugInfo = cleaned;
                                                }
                                            }

                                            if (!string.IsNullOrWhiteSpace(call.DebugInfo))
                                            {
                                                existing.DebugInfo = string.IsNullOrWhiteSpace(existing.DebugInfo)
                                                    ? call.DebugInfo
                                                    : $"{existing.DebugInfo}; {call.DebugInfo}";
                                            }

                                            LastQueueEvent = $"Transcription updated at {DateTime.Now:T}";
                                        }

                                        return;
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
                                        Calls.Insert(0, call);
                                        TotalCallsInserted++;
                                        LastQueueEvent = $"Inserted call at {DateTime.Now:T}";
                                        EnforceMaxCalls();
                                        UpdateQueueDerivedState();
                                    }

								// Keep queue playback running when appropriate.
								// - AutoPlay keeps playback alive.
								// - A user-initiated monitoring run should also keep playback alive even when AutoPlay is off.
								if (AudioEnabled)
								{
                                    if (_settingsService.AutoPlay || _userRunShouldDriveQueue)
                                    {
                                        _ = RequestQueuePlaybackAsync("NewCallInserted", userInitiated: _userRunShouldDriveQueue);
                                    }
                                    else if (IsRunning && _currentPlayingCall == null && !_isQueuePlaybackRunning && CallsWaiting > 0)
                                    {
                                        // If the user has started monitoring and audio is enabled, ensure the queue begins playback
                                        // even when AutoPlay is off (otherwise calls can accumulate silently).
                                        _ = RequestQueuePlaybackAsync("NewCallInserted", userInitiated: true);
                                    }
								}
                                });
                            }
                            catch (Exception ex)
                            {
                                AppLog.DebugWriteLine($"Error updating UI for call: {ex}");
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
                        AppLog.DebugWriteLine($"Error in call stream loop: {ex}");

                        try
                        {
                            await MainThread.InvokeOnMainThreadAsync(() =>
                            {
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
                            if (_cts == null || _cts.Token != token)
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

            // Only perform the full stop cleanup when the user explicitly cancels.
            try
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    // Only stop if this is still the active run
                    if (_cts == null || _cts.Token != token)
                        return;

                    _telemetryService.StopMonitoringHeartbeat("stream_loop_end");

                    IsRunning = false;

                    // Only clear the reconnect flag when the user explicitly disconnected.
                    // If the app was terminated or backgrounded while still "connected", we want the next launch
                    // to auto reconnect.
                    if (_userRequestedStop)
                        AppStateStore.SetBool("last_connected_on_exit", false);

                    if (_audioPlaybackService != null)
                    {
                        try
                        {
                            await _audioPlaybackService.StopAsync();
                        }
                        catch
                        {
                        }
                    }

                    _currentPlayingCall = null;
                    _lastPlayedCall = null;
                    ClearPendingCalls();

                    UpdateQueueDerivedState();
                });
            }
            catch
            {
            }
        }

        public async Task StopMonitoringAsync()
        {
            AppLog.Add(() => "User clicked Stop monitoring.");
            _telemetryService.StopMonitoringHeartbeat("user_stop");

            // Explicit user disconnect. This should clear the "auto reconnect" intent.
            _userRequestedStop = true;

            ClearPendingCalls();

            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }

                _cts.Dispose();
                _cts = null;
            }

            if (_audioCts != null)
            {
                try { _audioCts.Cancel(); } catch { }

                _audioCts.Dispose();
                _audioCts = null;
            }

            IsRunning = false;

            _userRunShouldDriveQueue = false;

            try
            {
                await _audioPlaybackService.StopAsync();
            }
            catch
            {
            }

            try
            {
                await _systemMediaService.StopSessionAsync();
                _systemMediaService.Clear();
            }
            catch
            {
            }

            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            _currentPlayingCall = null;
            _lastPlayedCall = null;
            UpdateQueueDerivedState();

            AppStateStore.SetBool("last_connected_on_exit", false);
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
            UpdateQueueDerivedState();
        }

        public async Task RestartMonitoringIfRunningAsync()
        {
            if (!IsRunning)
                return;

            await StopMonitoringAsync();
            await StartMonitoringAsync();
        }

        public async Task StopAudioFromToggleAsync()
        {
            if (_audioCts != null)
            {
                try { _audioCts.Cancel(); } catch { }

                _audioCts.Dispose();
                _audioCts = null;
            }

            try
            {
                await _audioPlaybackService.StopAsync();
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine($"Error stopping audio: {ex}");
            }

            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            _currentPlayingCall = null;

            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));
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
			if (item == null)
				return;

			// If requested, remember where the queue was so a "peek" play doesn't move the anchor.
			var priorLastPlayed = preserveQueueAnchor ? _lastPlayedCall : null;
			var wasQueuePlaybackRunning = preserveQueueAnchor && _isQueuePlaybackRunning;

			try
			{
				await InterruptAudioForUserActionAsync("User selected call");

				LastQueueEvent = $"Call tapped at {DateTime.Now:T}";

				if (!AudioEnabled)
				{
					await PlaySingleCallWithoutQueueAsync(item);
					return;
				}

				foreach (var call in Calls)
				{
					if (call.IsPlaying)
						call.IsPlaying = false;
				}

				// Mark it as the current selection while it plays.
				_currentPlayingCall = item;
				UpdateQueueDerivedState();

				await PlayAudioAsync(item, allowMutedPlayback: true);

				// After a user-initiated play, do not leave the VM thinking something is still playing.
				// Otherwise the queue restart gate can refuse to start.
				_currentPlayingCall = null;
				foreach (var call in Calls)
				{
					if (call.IsPlaying)
						call.IsPlaying = false;
				}

				// If requested, continue the queue starting from the call that was just played.
				if (continueQueueFromPlayedCall)
				{
					_lastPlayedCall = item;
					UpdateQueueDerivedState();
				}
				// Otherwise, restore the prior anchor so the live queue continues from where it was.
				else if (preserveQueueAnchor)
				{
					_lastPlayedCall = priorLastPlayed;
					UpdateQueueDerivedState();
				}

				var shouldResumeQueue =
					AudioEnabled &&
					IsRunning &&
					(
						_settingsService.AutoPlay ||
						_userRunShouldDriveQueue ||
						wasQueuePlaybackRunning ||
						continueQueueFromPlayedCall
					);

				if (shouldResumeQueue)
				{
					LastQueueEvent = "Tapped call finished; restarting queue";
					_ = RequestQueuePlaybackAsync("TappedCallFinished", true);
				}
				else
				{
					LastQueueEvent = "Tapped call finished";
				}
			}
			catch (Exception ex)
			{
				AppLog.DebugWriteLine($"Error in OnCallTappedAsync: {ex}");
				LastQueueEvent = "Error while playing tapped call (see debug output)";
			}
		}
private async Task JumpToLiveAsync()
        {
            try
            {
                CallItem? newest = null;

                lock (_pendingCallsLock)
                {
                    if (_pendingCalls.Count > 0)
                        newest = _pendingCalls[0];
                }

                if (newest == null)
                {
                    if (Calls.Count == 0)
                    {
                        _currentPlayingCall = null;
                        _lastPlayedCall = null;
                        UpdateQueueDerivedState();
                        LastQueueEvent = "Jump to live ignored (no calls)";
                        return;
                    }

                    newest = Calls[0];
                }
                else
                {
                    // Clear pending backlog when jumping live.
                    ClearPendingCalls();

                    // Ensure newest pending is visible at the top.
                    var idx = Calls.IndexOf(newest);
                    if (idx < 0)
                    {
                        Calls.Insert(0, newest);
                        EnforceMaxCalls();
                    }
                    else if (idx > 0)
                    {
                        Calls.RemoveAt(idx);
                        Calls.Insert(0, newest);
                    }
                }

                foreach (var call in Calls)
                {
                    if (call.IsPlaying)
                        call.IsPlaying = false;
                }

                // Force the UI list to snap back to the newest call even if the user was
                // scrolled into history (which intentionally disables auto-follow).
                try
                {
                    RequestJumpToLiveScroll?.Invoke();
                }
                catch
                {
                }

                _currentPlayingCall = null;

                if (!AudioEnabled)
                {
                    _lastPlayedCall = newest;
                    UpdateQueueDerivedState();
                    LastQueueEvent = "Jumped to live (audio off)";
                    return;
                }

                LastQueueEvent = "Jump to live (playing newest call)";

                _currentPlayingCall = newest;
                UpdateQueueDerivedState();

                await PlayAudioAsync(newest);

				if (AudioEnabled && IsRunning && _settingsService.AutoPlay && CallsWaiting > 0)
                {
                    LastQueueEvent = "Jump to live finished; restarting queue";
					_ = RequestQueuePlaybackAsync("JumpToLiveFinished", true);
                }
                else
                {
                    LastQueueEvent = "Jump to live finished";
                }
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine($"Error in JumpToLiveAsync: {ex}");
                LastQueueEvent = "Error jumping to live (see debug output)";
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

        await InterruptAudioForUserActionAsync($"Speed set to {SpeedStepToLabel(speedStep.Value)}");
        PlaybackSpeedStep = speedStep.Value;
        LastQueueEvent = $"Speed set to {PlaybackSpeedLabel}";
    }
    catch (Exception ex)
    {
        AppLog.DebugWriteLine($"Error in ApplyAudioMenuSelectionAsync: {ex}");
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
        AppLog.DebugWriteLine($"Error in SetAudioEnabledAsync: {ex}");
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
        private async Task EnsureQueuePlaybackAsync()
        {
            if (_isQueuePlaybackRunning)
                return;

            if (!AudioEnabled || !IsRunning)
                return;

            _isQueuePlaybackRunning = true;
            var aborted = false;

            LastQueueEvent = "Queue playback started";

            try
            {
                while (AudioEnabled && IsRunning)
                {
                    var next = GetNextQueuedCall();
                    if (next == null)
                        break;

                    UpdateQueueDerivedState();

                    await PlayAudioAsync(next);

                    if (!AudioEnabled || !IsRunning)
                    {
                        aborted = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                aborted = true;
                AppLog.DebugWriteLine($"Error in EnsureQueuePlaybackAsync: {ex}");
            }
            finally
            {
                _isQueuePlaybackRunning = false;

                UpdateQueueDerivedState();

                LastQueueEvent = aborted ? "Queue playback aborted" : "Queue playback finished";
            }
        }

        private double GetEffectivePlaybackRate(CallItem item)
        {
            var waiting = CallsWaiting;
            if (waiting <= 0)
                return 1.0;

            var step = PlaybackSpeedStep;

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

                for (var i = 0; i < Calls.Count; i++)
                {
                    var call = Calls[i];
                    bool isHistory = false;

                    if (anchorIndex >= 0)
                    {
                        isHistory = i > anchorIndex;
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

        private async Task PlaySingleCallWithoutQueueAsync(CallItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            if (_filterService.ShouldHide(item))
                return;

            try
            {
                foreach (var call in Calls)
                {
                    if (call.IsPlaying)
                        call.IsPlaying = false;
                }

                item.IsPlaying = true;

                var playbackUrl = await GetPlayableAudioUrlAsync(item.AudioUrl, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(playbackUrl))
                    return;

                try
                {
                    _systemMediaService.UpdateNowPlaying(BuildNowPlayingMetadataSnapshot(item), AudioEnabled);
                }
                catch
                {
                }

                await _audioPlaybackService.PlayAsync(playbackUrl, 1.0);
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine($"Error in PlaySingleCallWithoutQueueAsync: {ex}");
            }
            finally
            {
                item.IsPlaying = false;
            }
        }

        private async Task PlayAudioAsync(CallItem? item, bool allowMutedPlayback = false)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            if (!AudioEnabled)
                return;

            if (_filterService.ShouldHide(item))
            {
                // Consume the call so queue playback does not stall.
                LastQueueEvent = $"Call hidden by filter at {DateTime.Now:T}";
                _lastPlayedCall = item;
                if (_currentPlayingCall == item)
                    _currentPlayingCall = null;

                OnPropertyChanged(nameof(CallsWaiting));
                OnPropertyChanged(nameof(IsCallsWaitingVisible));
                return;
            }

            if (!allowMutedPlayback && _filterService.ShouldMute(item))
            {
                // Consume the call so queue playback does not stall.
                LastQueueEvent = $"Call muted by filter at {DateTime.Now:T}";
                _lastPlayedCall = item;
                if (_currentPlayingCall == item)
                    _currentPlayingCall = null;

                OnPropertyChanged(nameof(CallsWaiting));
                OnPropertyChanged(nameof(IsCallsWaitingVisible));
                return;
            }

            if (_audioCts != null)
            {
                try { _audioCts.Cancel(); } catch { }

                _audioCts.Dispose();
                _audioCts = null;
            }

            _audioCts = new CancellationTokenSource();
            var userCts = _audioCts;

            // Playback watchdog (prevents the queue from hanging forever on truly stuck playback).
            var rateForTimeout = GetEffectivePlaybackRate(item);
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

            using var playbackWatchdogCts = new CancellationTokenSource(TimeSpan.FromSeconds(playbackLimitSeconds));
            using var downloadWatchdogCts = new CancellationTokenSource(TimeSpan.FromSeconds(downloadLimitSeconds));

            using var playbackLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, playbackWatchdogCts.Token);
            using var downloadLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, downloadWatchdogCts.Token);

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

            try
            {
                var rate = rateForTimeout;

                if (_isMainAudioSoftMuted)
                {
                    // Soft mute: consume the call silently while preserving queue pacing.
                    var seconds = item.CallDurationSeconds > 0
                        ? (item.CallDurationSeconds / Math.Max(rate, 0.1))
                        : 0.25;

                    seconds = Math.Clamp(seconds, 0.15, 10800);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), playbackToken);
                    return;
                }

                string? downloadedTempPath = null;
                var shouldDeleteTempFile = false;

                // IMPORTANT: Do not use the playback watchdog here.
                // Backgrounded Android downloads can be slower and would trip the playback watchdog incorrectly.
                var playbackUrl = await GetPlayableAudioUrlAsync(item.AudioUrl, downloadToken);
                if (string.IsNullOrWhiteSpace(playbackUrl))
                {
                    if (downloadWatchdogCts.IsCancellationRequested && !userCts.IsCancellationRequested)
                        LastQueueEvent = "Audio download timeout, skipping to next call";

                    return;
                }

                if (IsDownloadedAudioTempFile(playbackUrl))
                {
                    downloadedTempPath = playbackUrl;
                    shouldDeleteTempFile = true;
                }

                try
                {
                    _systemMediaService.UpdateNowPlaying(BuildNowPlayingMetadataSnapshot(item), AudioEnabled);
                }
                catch
                {
                }

                await _audioPlaybackService.PlayAsync(playbackUrl, rate, playbackToken);

                if (shouldDeleteTempFile && !string.IsNullOrWhiteSpace(downloadedTempPath))
                {
                    TryDeleteFile(downloadedTempPath);
                }
            }
            catch (OperationCanceledException)
            {
                if (downloadWatchdogCts.IsCancellationRequested && !userCts.IsCancellationRequested)
                {
                    LastQueueEvent = "Audio download timeout, skipping to next call";
                }
                else if (playbackWatchdogCts.IsCancellationRequested && !userCts.IsCancellationRequested)
                {
                    LastQueueEvent = "Call playback watchdog timeout, skipping to next call";

                    try
                    {
                        await _audioPlaybackService.StopAsync();
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine($"Error in PlayAudioAsync: {ex}");
            }
            finally
            {
                item.IsPlaying = false;

                _lastPlayedCall = item;

                if (_currentPlayingCall == item)
                    _currentPlayingCall = null;

                OnPropertyChanged(nameof(CallsWaiting));
                OnPropertyChanged(nameof(IsCallsWaitingVisible));
            }
        }


        private static bool IsDownloadedAudioTempFile(string path)
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

        private async Task<string?> GetPlayableAudioUrlAsync(string audioUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
                return null;

            if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri))
                return audioUrl;

            var serverUrl = _settingsService.ServerUrl;
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
            {
                return audioUrl;
            }

            if (!string.Equals(audioUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(audioUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return audioUrl;
            }

            var isJoesScannerHost =
                string.Equals(baseUri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);

            string username;
            string password;

            if (isJoesScannerHost)
            {
                username = ServiceAuthUsername;
                password = ServiceAuthPassword;
            }
            else
            {
                username = _settingsService.BasicAuthUsername;
                password = _settingsService.BasicAuthPassword ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(username))
                return audioUrl;

            try
            {
                var raw = $"{username}:{password}";
                var bytes = Encoding.ASCII.GetBytes(raw);
                var base64 = Convert.ToBase64String(bytes);

                using var request = new HttpRequestMessage(HttpMethod.Get, audioUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);

                using var response = await _audioHttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                response.EnsureSuccessStatusCode();

                var ext = Path.GetExtension(audioUri.AbsolutePath);
                if (string.IsNullOrEmpty(ext))
                    ext = ".wav";

                var cacheDir = FileSystem.CacheDirectory;
                var fileName = $"jaas_{Guid.NewGuid():N}{ext}";
                var localPath = Path.Combine(cacheDir, fileName);
                var tempPath = localPath + ".tmp";

                try
                {
                    await using (var target = File.Create(tempPath))
                    await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    {
                        await stream.CopyToAsync(target, cancellationToken);
                    }

                    if (File.Exists(localPath))
                        File.Delete(localPath);

                    File.Move(tempPath, localPath);
                    return localPath;
                }
                catch
                {
                    TryDeleteFile(tempPath);
                    TryDeleteFile(localPath);
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine($"[MainViewModel] Error downloading audio with auth: {ex}");
                return null;
            }
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

        public async Task TryAutoReconnectAsync()
        {
            var shouldReconnect = AppStateStore.GetBool("last_connected_on_exit", false);

            if (!shouldReconnect)
                return;

            if (IsRunning)
                return;

            await StartMonitoringAsync();
        }

        public async Task RefreshStaleTranscriptionsAsync(int maxToRefresh = 20)
        {
            try
            {
                // Snapshot candidates on the UI thread so we do not enumerate the bound collection off-thread.
                var candidates = await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (Calls == null || Calls.Count == 0)
                        return new List<CallItem>();

                    bool NeedsRefresh(CallItem c)
                    {
                        if (c == null)
                            return false;

                        if (string.IsNullOrWhiteSpace(c.BackendId))
                            return false;

                        if (string.IsNullOrWhiteSpace(c.Transcription))
                            return true;

                        var t = c.Transcription.Trim();
                        return t.Contains("No transcription", StringComparison.OrdinalIgnoreCase);
                    }

                    return Calls.Where(NeedsRefresh).Take(maxToRefresh).ToList();
                });

                if (candidates.Count == 0)
                    return;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

                foreach (var item in candidates)
                {
                    if (string.IsNullOrWhiteSpace(item.BackendId))
                        continue;

                    string? refreshed;
                    try
                    {
                        refreshed = await _callStreamService.TryFetchTranscriptionByIdAsync(item.BackendId, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(refreshed))
                        continue;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        try
                        {
                            if (!string.Equals(item.Transcription, refreshed, StringComparison.Ordinal))
                            {
                                item.Transcription = refreshed;

                                
                                var hadAddress = item.HasDetectedAddress;
                                try { _addressDetectionService.Apply(item); } catch { }
                                if (!hadAddress && item.HasDetectedAddress)
                                    AddAddressAlertIfNew(item);
							try { _ = _what3WordsService.ResolveCoordinatesIfNeededAsync(item, CancellationToken.None); } catch { }

							if (!string.IsNullOrWhiteSpace(item.DebugInfo))
                                {
                                    var cleaned = item.DebugInfo
                                        .Replace("No transcription from server", string.Empty, StringComparison.OrdinalIgnoreCase)
                                        .Trim();

                                    cleaned = cleaned.Trim(' ', '|', ';');
                                    item.DebugInfo = cleaned;
                                }

                                LastQueueEvent = $"Transcription catch-up at {DateTime.Now:T}";
                            }
                        }
                        catch
                        {
                        }
                    });
                }
            }
            catch
            {
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
            if (IsRunning)
            {
                await StopMonitoringAsync();
            }
            else
            {
                await StartMonitoringAsync();
            }
        }

        private async Task InterruptAudioForUserActionAsync(string reason)
        {
            try
            {
                if (_audioCts != null)
                {
                    try { _audioCts.Cancel(); } catch { }
                }

                // Cancellation alone does not always stop platform players immediately.
                // Stop the player so user actions (next, previous, speed) take effect right away.
                try { await _audioPlaybackService.StopAsync(); } catch { }
            }
            catch
            {
            }
            finally
            {
                LastQueueEvent = reason;
            }
        }

        private async Task DecreasePlaybackSpeedStepAsync()
        {
            await InterruptAudioForUserActionAsync("Speed down requested");
            DecreasePlaybackSpeedStep();
        }

        private async Task IncreasePlaybackSpeedStepAsync()
        {
            await InterruptAudioForUserActionAsync("Speed up requested");
            IncreasePlaybackSpeedStep();
        }

        private void DecreasePlaybackSpeedStep()
        {
            if (PlaybackSpeedStep <= 0)
                return;

            PlaybackSpeedStep = PlaybackSpeedStep - 1;
            LastQueueEvent = $"Speed down to {PlaybackSpeedLabel}";
        }

        private void IncreasePlaybackSpeedStep()
        {
            if (PlaybackSpeedStep >= 4)
                return;

            PlaybackSpeedStep = PlaybackSpeedStep + 1;
            LastQueueEvent = $"Speed up to {PlaybackSpeedLabel}";
        }

        private async Task NavigateToAdjacentCallAsync(int direction)
        {
            if (Calls == null || Calls.Count == 0)
                return;

            if (direction != 1 && direction != -1)
                return;

            bool IsPlayable(CallItem c) =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.AudioUrl) &&
                !_filterService.ShouldHide(c) &&
                !_filterService.ShouldMute(c);

            var anchor = _currentPlayingCall ?? _lastPlayedCall;
            var anchorIndex = anchor != null ? Calls.IndexOf(anchor) : -1;

            if (anchorIndex < 0)
                anchorIndex = 0;

            var startIndex = anchorIndex + direction;

            for (var i = startIndex; i >= 0 && i < Calls.Count; i += direction)
            {
                var candidate = Calls[i];
                if (!IsPlayable(candidate))
                    continue;

                await InterruptAudioForUserActionAsync(direction == 1 ? "Previous call requested" : "Next call requested");
                await OnCallTappedAsync(candidate);
                return;
            }

            LastQueueEvent = direction == 1 ? "No previous call available" : "No next call available";
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