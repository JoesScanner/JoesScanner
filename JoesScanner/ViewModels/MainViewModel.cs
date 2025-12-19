using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Input;

#if ANDROID
using Android.Content;
using Android.OS;
using AndroidX.Core.Content;
using JoesScanner.Platforms.Android.Services;
#endif

namespace JoesScanner.ViewModels
{
    // Main view model for the primary JoesScanner client screen.
    // Handles streaming calls, playback, theme, jump-to-live, and global audio enable state.
    public class MainViewModel : BindableObject
    {
        private readonly ICallStreamService _callStreamService;
        private readonly ISettingsService _settingsService;
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly HttpClient _audioHttpClient;
        private readonly FilterService _filterService = FilterService.Instance;
        private readonly ISubscriptionService _subscriptionService;

        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _audioCts;

        private bool _isRunning;
        private string _serverUrl = string.Empty;
        private bool _autoPlay;
        private int _maxCalls = 20;
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

        private readonly ConcurrentQueue<CallItem> _bgPlayQueue = new();
        private readonly SemaphoreSlim _bgPlaySignal = new(0);
        private CancellationTokenSource? _bgPlayCts;
        private Task? _bgPlayTask;
        private readonly SemaphoreSlim _playbackMutex = new(1, 1);

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

        public MainViewModel(
            ICallStreamService callStreamService,
            ISettingsService settingsService,
            IAudioPlaybackService audioPlaybackService,
            ISubscriptionService subscriptionService)
        {
            _callStreamService = callStreamService ?? throw new ArgumentNullException(nameof(callStreamService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));

            _audioHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
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

            // Other persisted settings.
            // Repair any out-of-range stored values and enforce 10-50 with a default of 20.
            var storedMax = _settingsService.MaxCalls;
            if (storedMax < 10 || storedMax > 50)
            {
                storedMax = 20;
                _settingsService.MaxCalls = storedMax;
            }
            _maxCalls = storedMax;

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
            StopCommand = new Command(async () => await StopAsync(), () => IsRunning);
            OpenDonateCommand = new Command(async () => await OpenDonateAsync());
            ToggleAudioCommand = new Command(async () => await OnToggleAudioAsync());
            PlayAudioCommand = new Command<CallItem>(async item => await OnCallTappedAsync(item));
            JumpToLiveCommand = new Command(async () => await JumpToLiveAsync());

            // React when filters change (mute / disable / clear).
            _filterService.RulesChanged += FilterServiceOnRulesChanged;

            // Initialize subscription badge from any cached subscription data.
            UpdateSubscriptionSummaryFromSettings();
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

                UpdateQueueDerivedState();

                Preferences.Set("AudioEnabled", value);
            }
        }

        // Text shown on the main page audio button.
        public string AudioButtonText => AudioEnabled ? "Audio On" : "Audio Off";

        // Background color of the main page audio button.
        // Blue when audio is enabled, gray when muted.
        public Color AudioButtonBackground => AudioEnabled ? Colors.Blue : Colors.LightGray;

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
        public int CallsWaiting
        {
            get
            {
                if (Calls == null || Calls.Count == 0)
                    return 0;

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

                    return total;
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

                    return total;
                }

                if (anchorIndex <= 0)
                    return 0;

                var count = 0;
                for (var i = anchorIndex - 1; i >= 0; i--)
                {
                    if (IsPlayable(Calls[i]))
                        count++;
                }

                return count;
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

        // Total number of CallItem objects successfully inserted into the Calls
        // collection for the current session.
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

            AppLog.Add($"Connection status changed to {status} ({text})");
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

        // Returns the next call to play from the backlog.
        private CallItem? GetNextQueuedCall()
        {
            if (Calls.Count == 0)
                return null;

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
                    return null;
                }
                else
                {
                    startIndex = anchorIndex - 1;
                }
            }

            for (var i = startIndex; i >= 0; i--)
            {
                var candidate = Calls[i];

                if (string.IsNullOrWhiteSpace(candidate.AudioUrl))
                    continue;

                if (_filterService.ShouldMute(candidate) || _filterService.ShouldHide(candidate))
                    continue;

                return candidate;
            }

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
            if (Calls == null || Calls.Count == 0)
                return;

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

        public int MaxCalls
        {
            get => _maxCalls;
            set
            {
                var clamped = value;

                if (clamped < 10) clamped = 10;
                if (clamped > 50) clamped = 50;

                if (_maxCalls == clamped)
                    return;

                _maxCalls = clamped;
                _settingsService.MaxCalls = clamped;
                OnPropertyChanged();

                EnforceMaxCalls();
            }
        }

        public string TaglineText => $"Server: {ServerUrl}";

        public string DonateUrl => "https://www.joesscanner.com/products/one-time-donation/";

        public void Start()
        {
            AppLog.Add("User clicked Start Monitoring");
            _ = StartAsync();
        }

        private async Task StartAsync()
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

                    // subscription service should have updated cached fields;
                    // refresh the header badge.
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
                try
                {
                    _cts.Cancel();
                }
                catch
                {
                }

                _cts.Dispose();
                _cts = null;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Preferences.Set(LastConnectedPreferenceKey, true);

            SetConnectionStatus(ConnectionStatus.Connecting);
            IsRunning = true;

#if ANDROID
            StartAndroidForegroundPlaybackService();
#endif

            EnsureBackgroundPlaybackWorkerStarted();

            _currentPlayingCall = null;

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

                    Interlocked.Increment(ref _totalCallsReceived);
                    MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(TotalCallsReceived)));

                    try
                    {
                        // Prepare filter rules off the UI thread.
                        _filterService.EnsureRulesForCall(call);

                        // Decide if this call should be queued for background playback.
                        var isPlayableForAudio =
                            AudioEnabled &&
                            AutoPlay &&
                            !string.IsNullOrWhiteSpace(call.AudioUrl) &&
                            !_filterService.ShouldHide(call) &&
                            !_filterService.ShouldMute(call) &&
                            !call.IsTranscriptionUpdate;

                        if (isPlayableForAudio)
                        {
                            EnsureBackgroundPlaybackWorkerStarted();
                            EnqueueForBackgroundPlayback(call);
                        }

                        // Post UI updates without awaiting the UI thread.
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (token.IsCancellationRequested)
                                return;

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

                            if (_filterService.ShouldHide(call))
                            {
                                LastQueueEvent = $"Call dropped by filter at {DateTime.Now:T}";
                                return;
                            }

                            Calls.Insert(0, call);

                            if (_settingsService.AnnounceNewCalls)
                            {
                                var announcement = string.IsNullOrWhiteSpace(call.AccessibilityAnnouncement)
                                    ? call.AccessibilitySummary
                                    : call.AccessibilityAnnouncement;

                                try
                                {
                                    SemanticScreenReader.Default.Announce(announcement);
                                }
                                catch
                                {
                                }
                            }

                            TotalCallsInserted++;
                            LastQueueEvent = $"Inserted call at {DateTime.Now:T}";

                            EnforceMaxCalls();
                            UpdateQueueDerivedState();
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing call: {ex}");
                    }
                }
            }
            catch (System.OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in call stream loop: {ex}");

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    SetConnectionStatus(ConnectionStatus.Error, ex.Message);
                });
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (_cts == null || _cts.Token != token)
                        return;

                    IsRunning = false;
                    Preferences.Set(LastConnectedPreferenceKey, false);

                    try
                    {
                        await _audioPlaybackService.StopAsync();
                    }
                    catch
                    {
                    }

                    foreach (var c in Calls)
                    {
                        if (c.IsPlaying)
                            c.IsPlaying = false;
                    }

                    _currentPlayingCall = null;
                    _lastPlayedCall = null;
                    UpdateQueueDerivedState();

#if ANDROID
                    StopAndroidForegroundPlaybackService();
#endif

                    StopBackgroundPlaybackWorker();
                    ClearBackgroundPlaybackQueue();
                });
            }
        }

        private async Task StopAsync()
        {
            AppLog.Add("User clicked Stop monitoring.");

            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch
                {
                }

                _cts.Dispose();
                _cts = null;
            }

            if (_audioCts != null)
            {
                try
                {
                    _audioCts.Cancel();
                }
                catch
                {
                }

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

            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            _currentPlayingCall = null;
            _lastPlayedCall = null;

            UpdateQueueDerivedState();

#if ANDROID
            StopAndroidForegroundPlaybackService();
#endif

            StopBackgroundPlaybackWorker();
            ClearBackgroundPlaybackQueue();

            Preferences.Set(LastConnectedPreferenceKey, false);
        }

        public async Task StopAudioFromToggleAsync()
        {
            if (_audioCts != null)
            {
                try
                {
                    _audioCts.Cancel();
                }
                catch
                {
                }

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

                ClearBackgroundPlaybackQueue();

                await _playbackMutex.WaitAsync();
                try
                {
                    _currentPlayingCall = item;

                    try
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            foreach (var call in Calls)
                            {
                                if (call.IsPlaying)
                                    call.IsPlaying = false;
                            }

                            UpdateQueueDerivedState();
                        });
                    }
                    catch
                    {
                    }

                    await PlayAudioAsync(item);
                }
                finally
                {
                    try { _playbackMutex.Release(); } catch { }
                }

                LastQueueEvent = "Tapped call finished";
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
                if (Calls.Count == 0)
                {
                    _currentPlayingCall = null;
                    _lastPlayedCall = null;
                    UpdateQueueDerivedState();
                    LastQueueEvent = "Jump to live ignored (no calls)";
                    return;
                }

                var newest = Calls[0];

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

                ClearBackgroundPlaybackQueue();

                await _playbackMutex.WaitAsync();
                try
                {
                    _currentPlayingCall = newest;
                    UpdateQueueDerivedState();

                    await PlayAudioAsync(newest);
                }
                finally
                {
                    try { _playbackMutex.Release(); } catch { }
                }

                LastQueueEvent = "Jump to live finished";
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
                    ClearBackgroundPlaybackQueue();
                    await StopAudioFromToggleAsync();

                    _lastPlayedCall = null;

                    UpdateQueueDerivedState();
                }
                else
                {
                    EnsureBackgroundPlaybackWorkerStarted();

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

        private void EnsureBackgroundPlaybackWorkerStarted()
        {
            if (_bgPlayTask != null && !_bgPlayTask.IsCompleted)
                return;

            _bgPlayCts?.Cancel();
            _bgPlayCts?.Dispose();
            _bgPlayCts = new CancellationTokenSource();

            var token = _bgPlayCts.Token;

            _bgPlayTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await _bgPlaySignal.WaitAsync(token);
                    }
                    catch (System.OperationCanceledException)
                    {
                        break;
                    }

                    if (token.IsCancellationRequested)
                        break;

                    if (!IsRunning || !AudioEnabled || !AutoPlay)
                        continue;

                    if (!_bgPlayQueue.TryDequeue(out var next))
                        continue;

                    if (next == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(next.AudioUrl))
                        continue;

                    if (_filterService.ShouldHide(next) || _filterService.ShouldMute(next))
                        continue;

                    try
                    {
                        await _playbackMutex.WaitAsync(token);

                        if (!IsRunning || !AudioEnabled || !AutoPlay)
                            continue;

                        await PlayAudioAsync(next);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { _playbackMutex.Release(); } catch { }
                    }
                }
            }, token);
        }

        private void StopBackgroundPlaybackWorker()
        {
            try
            {
                _bgPlayCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                _bgPlaySignal.Release();
            }
            catch
            {
            }
        }

        private void ClearBackgroundPlaybackQueue()
        {
            while (_bgPlayQueue.TryDequeue(out _))
            {
            }
        }

        private void EnqueueForBackgroundPlayback(CallItem call)
        {
            if (call == null)
                return;

            _bgPlayQueue.Enqueue(call);

            try
            {
                _bgPlaySignal.Release();
            }
            catch
            {
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
            var max = MaxCalls;

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
                }
            }

            if (!AudioEnabled)
                return;

            var waiting = CallsWaiting;
            if (waiting <= 0)
                return;

            // Use the configured autospeed threshold from settings.
            // Clamp defensively in case older settings stored out-of-range values.
            var threshold = _settingsService.AutoSpeedThreshold;
            if (threshold < 10)
                threshold = 10;
            if (threshold > 100)
                threshold = 100;

            // When backlog reaches the threshold, bump to 1.5x.
            // When it reaches twice the threshold, bump to 2x.
            if (waiting >= threshold * 2)
            {
                if (PlaybackSpeedStep < 2)
                    PlaybackSpeedStep = 2;
            }
            else if (waiting >= threshold && PlaybackSpeedStep < 1)
            {
                PlaybackSpeedStep = 1;
            }
        }

        private async Task PlaySingleCallWithoutQueueAsync(CallItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            if (_filterService.ShouldMute(item) || _filterService.ShouldHide(item))
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

        private async Task PlayAudioAsync(CallItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            if (_filterService.ShouldMute(item) || _filterService.ShouldHide(item))
                return;

            if (!AudioEnabled)
                return;

            if (_audioCts != null)
            {
                try
                {
                    _audioCts.Cancel();
                }
                catch
                {
                }

                _audioCts.Dispose();
                _audioCts = null;
            }

            _audioCts = new CancellationTokenSource();
            var token = _audioCts.Token;

            _currentPlayingCall = item;

            try
            {
                try
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        foreach (var call in Calls)
                        {
                            if (call.IsPlaying)
                                call.IsPlaying = false;
                        }

                        item.IsPlaying = true;

                        OnPropertyChanged(nameof(CallsWaiting));
                        OnPropertyChanged(nameof(IsCallsWaitingVisible));
                        UpdateQueueDerivedState();
                    });
                }
                catch
                {
                }

                var rate = GetEffectivePlaybackRate(item);

                var playbackUrl = await GetPlayableAudioUrlAsync(item.AudioUrl, token);
                if (string.IsNullOrWhiteSpace(playbackUrl))
                    return;

                await _audioPlaybackService.PlayAsync(playbackUrl, rate, token);
            }
            catch (System.OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in PlayAudioAsync: {ex}");
            }
            finally
            {
                _lastPlayedCall = item;

                if (_currentPlayingCall == item)
                    _currentPlayingCall = null;

                try
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        item.IsPlaying = false;
                        OnPropertyChanged(nameof(CallsWaiting));
                        OnPropertyChanged(nameof(IsCallsWaitingVisible));
                        UpdateQueueDerivedState();
                    });
                }
                catch
                {
                }
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

                await using (var target = File.Create(localPath))
                await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    await stream.CopyToAsync(target, cancellationToken);
                }

                return localPath;
            }
            catch (System.OperationCanceledException)
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

            await StartAsync();
        }

#if ANDROID
        private void StartAndroidForegroundPlaybackService()
        {
            try
            {
                var ctx = Android.App.Application.Context;
                var intent = new Intent(ctx, typeof(AudioForegroundService));

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    ContextCompat.StartForegroundService(ctx, intent);
                else
                    ctx.StartService(intent);
            }
            catch
            {
            }
        }

        private void StopAndroidForegroundPlaybackService()
        {
            try
            {
                var ctx = Android.App.Application.Context;
                var intent = new Intent(ctx, typeof(AudioForegroundService));
                ctx.StopService(intent);
            }
            catch
            {
            }
        }
#endif

        private static void ApplyTheme(string? mode)
        {
            var app = Microsoft.Maui.Controls.Application.Current;
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
