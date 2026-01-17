using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Input;

namespace JoesScanner.ViewModels
{
    // Main view model for the primary JoesScanner client screen.
    // Handles streaming calls, playback, theme, jump-to-live, and global audio enable state.
    public class MainViewModel : BindableObject
    {
        private readonly ICallStreamService _callStreamService;
        private readonly ISettingsService _settingsService;
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly ISystemMediaService _systemMediaService;
        private readonly HttpClient _audioHttpClient;
        private readonly FilterService _filterService = FilterService.Instance;
        private readonly ISubscriptionService _subscriptionService;
        private readonly ITelemetryService _telemetryService;

        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _audioCts;

        // When true, the live queue continues to run but audio output is suppressed.
        // This is used when the user navigates to the History tab.
        private volatile bool _isMainAudioSoftMuted;

        private bool _isRunning;
        private string _serverUrl = string.Empty;
        private bool _autoPlay;
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
            ISubscriptionService subscriptionService, ITelemetryService telemetryService)
        {
            _callStreamService = callStreamService ?? throw new ArgumentNullException(nameof(callStreamService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));
            _systemMediaService = systemMediaService ?? throw new ArgumentNullException(nameof(systemMediaService));
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

            _audioHttpClient = new HttpClient
            {
                // Do not use HttpClient.Timeout for long call downloads.
                // Per call watchdog timeouts are enforced via CancellationToken.
                Timeout = Timeout.InfiniteTimeSpan
            };

            // Initialize server URL from settings or default.
            var initialServerUrl = _settingsService.ServerUrl;
            if (string.IsNullOrWhiteSpace(initialServerUrl))
            {
                initialServerUrl = SettingsViewModel.DefaultServerUrl;
                _settingsService.ServerUrl = initialServerUrl;
            }

            _serverUrl = initialServerUrl;

            // Global audio flag persisted across runs.
            // Default is true so a fresh install behaves like a radio.
            _audioEnabled = Preferences.Get("AudioEnabled", true);

            // AutoPlay is tied to AudioEnabled.
            _autoPlay = _audioEnabled;
            _settingsService.AutoPlay = _autoPlay;
            // Call retention is now fixed after removing queue call control settings.
            // MaxCallsToKeep is enforced when calls are inserted.

            // Restore playback speed step (0 = 1x, 1 = 1.5x, 2 = 2x).
            var savedSpeed = Preferences.Get(PlaybackSpeedStepPreferenceKey, 0.0);
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
            PlaybackSpeedDownCommand = new Command(DecreasePlaybackSpeedStep);
            PlaybackSpeedUpCommand = new Command(IncreasePlaybackSpeedStep);
            PreviousCallCommand = new Command(async () => await NavigateToAdjacentCallAsync(1));
            NextCallCommand = new Command(async () => await NavigateToAdjacentCallAsync(-1));

            // React when filters change (mute / disable / clear).
            _filterService.RulesChanged += FilterServiceOnRulesChanged;

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
                // Cancel any in-flight playback immediately, but do not reset queue state.
                if (_audioCts != null)
                {
                    try { _audioCts.Cancel(); } catch { }
                }

                try { await _audioPlaybackService.StopAsync(); } catch { }
                return;
            }

            // Unmuted: if the queue is running and audio is enabled, resume playback.
            if (IsRunning && AudioEnabled)
            {
                _ = EnsureQueuePlaybackAsync();
            }
        }

        private Task SystemPlayAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (!IsRunning)
                    await StartMonitoringAsync();
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

        private const string LastConnectedPreferenceKey = "LastConnectedOnExit";
        private const string PlaybackSpeedStepPreferenceKey = "PlaybackSpeedStep";

        private const string ServiceAuthUsername = "secappass";
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

                UpdateQueueDerivedState();

                Preferences.Set("AudioEnabled", value);

                try
                {
                    if (IsRunning && _currentPlayingCall == null)
                    {
                        _systemMediaService.UpdateNowPlaying("Connected", "Joes Scanner", _audioEnabled);
                    }
                }
                catch
                {
                }
            }
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
                if (_serverUrl == value)
                    return;

                _serverUrl = value ?? string.Empty;
                _settingsService.ServerUrl = _serverUrl;

                OnPropertyChanged();
                OnPropertyChanged(nameof(TaglineText));

                // If the server changes, recompute whether we should show the subscription badge.
                UpdateSubscriptionSummaryFromSettings();
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

                AppLog.Add(_lastQueueEvent);
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

            AppLog.Add($"Connection status changed to {status} ({text})");

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
                if (clamped > 2) clamped = 2;
                clamped = Math.Round(clamped);

                if (Math.Abs(_playbackSpeedStep - clamped) < 0.001)
                    return;

                _playbackSpeedStep = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlaybackSpeedLabel));

                Preferences.Set(PlaybackSpeedStepPreferenceKey, _playbackSpeedStep);
            }
        }

        public string PlaybackSpeedLabel =>
            _playbackSpeedStep switch
            {
                1 => "1.5x",
                2 => "2x",
                _ => "1x"
            };

        public bool AutoPlay
        {
            get => _autoPlay;
            set
            {
                if (_autoPlay == value)
                    return;

                _autoPlay = value;
                _settingsService.AutoPlay = value;
                OnPropertyChanged();
            }
        }

        public string TaglineText => $"Server: {ServerUrl}";
        public string DonateUrl => "https://www.joesscanner.com/products/one-time-donation/";

        public void Start()
        {
            AppLog.Add("User clicked Start Monitoring");
            _ = StartMonitoringAsync();
        }

        public async Task StartMonitoringAsync()
        {
            if (IsRunning)
                return;

            var serverUrl = _settingsService.ServerUrl ?? string.Empty;
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
                AppLog.Add($"Subscription check: server={serverUrl}, user={username}");

                try
                {
                    var subResult = await _subscriptionService.EnsureSubscriptionAsync(CancellationToken.None);
                    if (!subResult.IsAllowed)
                    {
                        AppLog.Add($"Subscription check failed: {subResult.ErrorCode} {subResult.Message}");

                        SetConnectionStatus(ConnectionStatus.AuthFailed,
                            subResult.Message ?? "Subscription not active");

                        return;
                    }

                    AppLog.Add("Subscription check passed, starting stream.");
                    UpdateSubscriptionSummaryFromSettings();
                }
                catch (Exception ex)
                {
                    AppLog.Add($"Subscription check error: {ex.Message}");
                    SetConnectionStatus(ConnectionStatus.Error, "Subscription check error");
                    return;
                }
            }
            else
            {
                AppLog.Add($"Custom server detected ({serverUrl}), skipping Joe's Scanner subscription check.");
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

            Preferences.Set(LastConnectedPreferenceKey, true);

            SetConnectionStatus(ConnectionStatus.Connecting);
            IsRunning = true;

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

                            // Ensure filter rules exist and apply filters
                            _filterService.EnsureRulesForCall(call);

                            if (_filterService.ShouldHide(call))
                            {
                                LastQueueEvent = $"Call dropped by filter at {DateTime.Now:T}";
                                return;
                            }


                            call.IsMutedByFilter = _filterService.ShouldMute(call);

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

                            // If audio is enabled and autoplay is enabled, keep playback running
                            if (AudioEnabled && AutoPlay)
                            {
                                _ = EnsureQueuePlaybackAsync();
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error updating UI for call: {ex}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in call stream loop: {ex}");

                try
                {
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        SetConnectionStatus(ConnectionStatus.Error, ex.Message);
                    });
                }
                catch
                {
                    // Swallow any cross thread failures on shutdown
                }
            }
            finally
            {
                try
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        // Only stop if this is still the active run
                        if (_cts == null || _cts.Token != token)
                            return;

                        _telemetryService.StopMonitoringHeartbeat("stream_loop_end");

                        IsRunning = false;
                        Preferences.Default.Set(LastConnectedPreferenceKey, false);

                        if (_audioPlaybackService != null)
                        {
                            try
                            {
                                await _audioPlaybackService.StopAsync();
                            }
                            catch
                            {
                                // Ignore audio stop failures
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
                    // Ignore shutdown failures
                }
            }
        }

        public async Task StopMonitoringAsync()
        {
            AppLog.Add("User clicked Stop monitoring.");
            _telemetryService.StopMonitoringHeartbeat("user_stop");

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

            Preferences.Set(LastConnectedPreferenceKey, false);
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
                System.Diagnostics.Debug.WriteLine($"Error stopping audio: {ex}");
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

        private async Task OnCallTappedAsync(CallItem? item)
        {
            if (item == null)
                return;

            try
            {
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

                // Transition the queue to this point by making it the new anchor.
                // While anchored behind live, new calls will be stored in pending.
                _currentPlayingCall = item;
                UpdateQueueDerivedState();

                await PlayAudioAsync(item, allowMutedPlayback: true);

                if (AudioEnabled && IsRunning && AutoPlay && CallsWaiting > 0)
                {
                    LastQueueEvent = "Tapped call finished; restarting queue";
                    _ = EnsureQueuePlaybackAsync();
                }
                else
                {
                    LastQueueEvent = "Tapped call finished";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnCallTappedAsync: {ex}");
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

                if (AudioEnabled && IsRunning && AutoPlay && CallsWaiting > 0)
                {
                    LastQueueEvent = "Jump to live finished; restarting queue";
                    _ = EnsureQueuePlaybackAsync();
                }
                else
                {
                    LastQueueEvent = "Jump to live finished";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in JumpToLiveAsync: {ex}");
                LastQueueEvent = "Error jumping to live (see debug output)";
            }
        }

        private async Task OnToggleAudioAsync()
        {
            var newValue = !AudioEnabled;

            try
            {
                AudioEnabled = newValue;

                AutoPlay = newValue;

                LastQueueEvent = newValue ? "Audio toggled ON" : "Audio toggled OFF";

                if (!newValue)
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
                System.Diagnostics.Debug.WriteLine($"Error in OnToggleAudioAsync: {ex}");
                LastQueueEvent = "Error while toggling audio (see debug output)";
            }
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
                System.Diagnostics.Debug.WriteLine($"Error in EnsureQueuePlaybackAsync: {ex}");
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
                1 => 1.5,
                2 => 2.0,
                _ => 1.0
            };
        }

        private void EnforceMaxCalls()
        {
            var max = MaxCallsToKeep;
            while (Calls.Count > max)
            {
                var lastIndex = Calls.Count - 1;
                if (lastIndex >= 0)
                {
                    Calls.RemoveAt(lastIndex);
                }
            }

            UpdateQueueDerivedState();
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
                    _systemMediaService.UpdateNowPlaying(item.Talkgroup, BuildNowPlayingSubtitle(item), AudioEnabled);
                }
                catch
                {
                }

                await _audioPlaybackService.PlayAsync(playbackUrl, 1.0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in PlaySingleCallWithoutQueueAsync: {ex}");
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
                    _systemMediaService.UpdateNowPlaying(item.Talkgroup, BuildNowPlayingSubtitle(item), AudioEnabled);
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
                System.Diagnostics.Debug.WriteLine($"Error in PlayAudioAsync: {ex}");
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
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] Error downloading audio with auth: {ex}");
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
            var shouldReconnect = Preferences.Get(LastConnectedPreferenceKey, false);

            if (!shouldReconnect)
                return;

            if (IsRunning)
                return;

            await StartMonitoringAsync();
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

        private void DecreasePlaybackSpeedStep()
        {
            if (PlaybackSpeedStep <= 0)
                return;

            PlaybackSpeedStep = PlaybackSpeedStep - 1;
            LastQueueEvent = $"Speed down to {PlaybackSpeedLabel}";
        }

        private void IncreasePlaybackSpeedStep()
        {
            if (PlaybackSpeedStep >= 2)
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

                LastQueueEvent = direction == 1 ? "Previous call" : "Next call";
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
    }
}