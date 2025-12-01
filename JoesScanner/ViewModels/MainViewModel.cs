using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

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

        // Optional settings view model reference. Currently not used but kept for future wiring.
        public SettingsViewModel? SettingsViewModel { get; set; }

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
            IAudioPlaybackService audioPlaybackService)
        {
            _callStreamService = callStreamService ?? throw new ArgumentNullException(nameof(callStreamService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));

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
            // Repair any out-of-range stored values and enforce 10–50 with a default of 20.
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

            // NEW: react when filters change (mute/disable/clear)
            _filterService.RulesChanged += FilterServiceOnRulesChanged;
        }

        private const string LastConnectedPreferenceKey = "LastConnectedOnExit";
        private const string PlaybackSpeedStepPreferenceKey = "PlaybackSpeedStep";

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

                // When the stream stops, always reflect that.
                if (!_isRunning)
                {
                    SetConnectionStatus(ConnectionStatus.Stopped);
                }
                // When it becomes true, we will explicitly set Connecting/Connected
                // from StartAsync and the stream loop, so do nothing here.
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
            }
        }

        // Number of calls waiting in the playback queue, excluding the call at the current anchor.
        // With newest at the top (index 0):
        // - When no anchor is set, all calls are considered waiting.
        // - When an anchor is set, waiting calls are those above it (lower indices).
        public int CallsWaiting
        {
            get
            {
                if (Calls == null || Calls.Count == 0)
                    return 0;

                // Anchor is the call that is currently playing, or the last one
                // that finished if nothing is playing.
                var anchor = _currentPlayingCall ?? _lastPlayedCall;

                // A call is "playable" for the queue if:
                //  - it has an audio URL
                //  - it is not hidden or muted by filters
                bool IsPlayable(CallItem c) =>
                    !string.IsNullOrWhiteSpace(c.AudioUrl) &&
                    !_filterService.ShouldHide(c) &&
                    !_filterService.ShouldMute(c);

                // If we have no anchor at all, then all playable calls are considered waiting.
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

                // If the anchor is not found (trimmed out), treat all playable calls as waiting.
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

                // Newest is at index 0. Anything above the anchor (lower index)
                // that is playable is waiting to be played.
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
            }
        }

        // Whether the "Calls waiting" indicator should be visible in the UI.
        // Always visible whenever audio is on, regardless of the count.
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

        // User facing text for the current connection status.
        // This is what we will bind the existing label to.
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

        // Helper flags so the UI can style the indicator without
        // needing to know about the enum directly.
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

        // Color used by your existing little dot in the UI.
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

            // Derived flags for UI styling
            ConnectionStatusIsConnected = status == ConnectionStatus.Connected;
            ConnectionStatusIsWarning   = status == ConnectionStatus.Connecting;
            ConnectionStatusIsError     =
                status == ConnectionStatus.AuthFailed ||
                status == ConnectionStatus.ServerUnreachable ||
                status == ConnectionStatus.Error;

            // Map status to a color for your existing dot.
            Color color;
            if (ConnectionStatusIsConnected)
            {
                color = Color.FromArgb("#15803d"); // green
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
                color = Color.FromArgb("#6b7280"); // gray
            }

            ConnectionStatusColor = color;
        }

        // Returns the next call to play from the backlog.
        // Calls are stored newest at index 0, oldest at the end.
        // We walk from oldest to newest so you hear things in order.
        // We walk from oldest to newest so you hear things in order.
        private CallItem? GetNextQueuedCall()
        {
            if (Calls.Count == 0)
                return null;

            // Never start a new call while one is already playing.
            if (_currentPlayingCall != null)
                return null;

            int startIndex;

            // If we have never played anything, start from the oldest call.
            if (_lastPlayedCall == null)
            {
                startIndex = Calls.Count - 1;
            }
            else
            {
                var anchorIndex = Calls.IndexOf(_lastPlayedCall);

                // If the anchor is gone (trimmed), restart from the oldest call.
                if (anchorIndex < 0)
                {
                    startIndex = Calls.Count - 1;
                }
                else if (anchorIndex == 0)
                {
                    // Anchor is already the oldest; nothing newer to play.
                    return null;
                }
                else
                {
                    // Next newer than the anchor.
                    startIndex = anchorIndex - 1;
                }
            }

            // Walk toward the newest call (index 0) and pick the first one
            // that actually has audio and is not muted/disabled by filters.
            for (var i = startIndex; i >= 0; i--)
            {
                var candidate = Calls[i];

                if (string.IsNullOrWhiteSpace(candidate.AudioUrl))
                    continue;

                if (_filterService.ShouldMute(candidate) || _filterService.ShouldHide(candidate))
                    continue;

                return candidate;
            }

            // No playable calls found.
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
                // Best effort only; never crash on filter updates.
            }
        }

        private void ApplyFiltersToExistingCalls()
        {
            if (Calls == null || Calls.Count == 0)
                return;

            var removedCount = 0;

            // Walk from bottom to top so index removal is safe
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

        // Queue playback engine.
        // While audio is on and we are connected, it repeatedly finds the next
        // queued call (based on _lastPlayedCall) and plays it until we are caught up.
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

        // Numbered playback speed step used by the slider on the main page.
        // 0 = 1x, 1 = 1.5x, 2 = 2x.
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

                // Persist the setting so it survives app restarts.
                Preferences.Set(PlaybackSpeedStepPreferenceKey, _playbackSpeedStep);
            }
        }

        // User friendly label for the current playback speed step.
        public string PlaybackSpeedLabel =>
            _playbackSpeedStep switch
            {
                1 => "1.5x",
                2 => "2x",
                _ => "1x"
            };

        // AutoPlay is driven by AudioEnabled.
        // When true and a new call arrives, it will be auto-played.
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

        // Maximum number of calls maintained in the Calls collection.
        public int MaxCalls
        {
            get => _maxCalls;
            set
            {
                var clamped = value;

                // Clamp to 10–50
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

        // Tagline bound to the subtitle under the JoesScanner header.
        // Now just shows the active server URL.
        public string TaglineText => $"Server: {ServerUrl}";

        // Donation URL used by the footer button.
        public string DonateUrl => "https://www.joesscanner.com";

        // Entry point used by the Connect button, wraps the async Start.
        public void Start()
        {
            _ = StartAsync();
        }

        // Starts the streaming of calls from the server.
        private async Task StartAsync()
        {
            // Already running, nothing to do.
            if (IsRunning)
                return;

            // Ensure any previous CTS is canceled and disposed.
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

            // Create a fresh CTS for this connection.
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Remember that we were connected when the app last ran.
            Preferences.Set(LastConnectedPreferenceKey, true);

            // Reset call state for this connection, but keep history in Calls.
            // We treat everything currently in Calls as "already handled" so
            // only calls that arrive after this point are considered backlog.
            SetConnectionStatus(ConnectionStatus.Connecting);
            IsRunning = true;
            _currentPlayingCall = null;


            if (Calls.Count > 0)
            {
                // Newest call is at index 0, so this becomes our anchor.
                _lastPlayedCall = Calls[0];
            }
            else
            {
                _lastPlayedCall = null;
            }

            UpdateQueueDerivedState();

            // Fire-and-forget the background stream loop.
            _ = Task.Run(() => RunCallStreamLoopAsync(token));
        }

        // Background loop that continuously pulls calls from the server
        // and feeds them into the UI. This must never throw out of the
        // method except for cancellation.
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
                            // Update connection status based on special rows from the service.
                            if (string.Equals(call.Talkgroup, "AUTH", StringComparison.OrdinalIgnoreCase))
                            {
                                // Auth failure row from CallStreamService.
                                SetConnectionStatus(ConnectionStatus.AuthFailed);
                            }
                            else if (string.Equals(call.Talkgroup, "ERROR", StringComparison.OrdinalIgnoreCase))
                            {
                                // Generic connectivity/server error row.
                                SetConnectionStatus(ConnectionStatus.ServerUnreachable);
                            }
                            else
                            {
                                // Any normal (non AUTH/ERROR) call means we are successfully talking
                                // to the server, so move from Connecting/Error to Connected.
                                if (_connectionStatus == ConnectionStatus.Connecting ||
                                    _connectionStatus == ConnectionStatus.AuthFailed ||
                                    _connectionStatus == ConnectionStatus.ServerUnreachable ||
                                    _connectionStatus == ConnectionStatus.Error)
                                {
                                    SetConnectionStatus(ConnectionStatus.Connected);
                                }
                            }

                            // If this is a transcription update for an existing call, patch in place.
                            if (call.IsTranscriptionUpdate &&
                                !string.IsNullOrWhiteSpace(call.BackendId))
                            {
                                var existing = Calls.FirstOrDefault(c => c.BackendId == call.BackendId);
                                if (existing != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(call.Transcription))
                                    {
                                        existing.Transcription = call.Transcription;

                                        // Remove the old "No transcription from server" message
                                        if (!string.IsNullOrWhiteSpace(existing.DebugInfo))
                                        {
                                            var cleaned = existing.DebugInfo
                                                .Replace("No transcription from server", string.Empty, StringComparison.OrdinalIgnoreCase)
                                                .Trim();

                                            // Clean up leftover separators
                                            cleaned = cleaned.Trim(' ', '|', ';');

                                            existing.DebugInfo = cleaned;
                                        }
                                    }

                                    // If the update itself carries any new debug info, append it
                                    if (!string.IsNullOrWhiteSpace(call.DebugInfo))
                                    {
                                        existing.DebugInfo = string.IsNullOrWhiteSpace(existing.DebugInfo)
                                            ? call.DebugInfo
                                            : $"{existing.DebugInfo}; {call.DebugInfo}";
                                    }

                                    LastQueueEvent = $"Transcription updated at {DateTime.Now:T}";
                                }

                                // Do not insert a new row or disturb the queue for updates.
                                return;
                            }

                            // Update filters based on this call (receiver, site, talkgroup).
                            _filterService.EnsureRulesForCall(call);

                            // If any matching filter rule has IsDisabled, drop this call completely.
                            if (_filterService.ShouldHide(call))
                            {
                                LastQueueEvent = $"Call dropped by filter at {DateTime.Now:T}";
                                return;
                            }

                            // Normal new call path: always show newest calls at the top (index 0).
                            Calls.Insert(0, call);
                            TotalCallsInserted++;
                            LastQueueEvent = $"Inserted call at {DateTime.Now:T}";

                            EnforceMaxCalls();

                            // Recompute CallsWaiting, visibility, etc.
                            UpdateQueueDerivedState();

                            // Kick the queue engine if audio + autoplay are enabled.
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
                });
            }
        }

        // Fully stops the call stream and audio playback.
        private async Task StopAsync()
        {
            // Stop the call stream.
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

            // Stop any current audio playback via CTS.
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

            // Mark as not running.
            IsRunning = false;

            // Also let the audio service clean up its player.
            try
            {
                await _audioPlaybackService.StopAsync();
            }
            catch
            {
            }

            // Reset queue position when fully stopped.
            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            _currentPlayingCall = null;
            _lastPlayedCall = null;
            UpdateQueueDerivedState();

            // We are no longer connected, remember this for next launch.
            Preferences.Set(LastConnectedPreferenceKey, false);
        }

        // Called only from the audio toggle path to stop playback.
        // This must never cancel the call stream CTS or change IsRunning.
        public async Task StopAudioFromToggleAsync()
        {
            // Cancel any in-flight audio playback so PlayAudioAsync can finish.
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
                // Stop the underlying audio player. Any errors here should
                // never affect the call stream or UI.
                await _audioPlaybackService.StopAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error stopping audio: {ex}");
            }

            // Clear any IsPlaying flags on all calls.
            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            _currentPlayingCall = null;

            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));
        }

        // Backward compatible alias used by SettingsViewModel in older code.
        public Task StopAudioFromSettingsAsync()
        {
            return StopAudioFromToggleAsync();
        }

        // Handles a user tap on a call in the list.
        //
        // When audio is off:
        //   - Plays only the tapped call at normal speed (preview) and returns.
        //
        // When audio is on:
        //   - Plays the tapped call using the normal queue playback path
        //     so it becomes _lastPlayedCall.
        //   - Then, if autoplay is enabled and we are connected,
        //     resumes the queue from that point forward.
        private async Task OnCallTappedAsync(CallItem? item)
        {
            if (item == null)
                return;

            try
            {
                LastQueueEvent = $"Call tapped at {DateTime.Now:T}";

                if (!AudioEnabled)
                {
                    // Audio off: pure preview, no impact on queue anchor.
                    await PlaySingleCallWithoutQueueAsync(item);
                    return;
                }

                // Clear any existing playing flags.
                foreach (var call in Calls)
                {
                    if (call.IsPlaying)
                        call.IsPlaying = false;
                }

                // This call is now the one we are explicitly playing.
                _currentPlayingCall = item;
                UpdateQueueDerivedState();

                // This will:
                //  - set item.IsPlaying = true during playback
                //  - set item.IsPlaying = false in finally
                //  - set _lastPlayedCall = item when finished
                await PlayAudioAsync(item);

                // After the tapped call finishes:
                // If audio is still on, stream is running, autoplay is enabled,
                // and there are calls waiting, hand control back to the queue engine.
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

        // Skips backlog and moves playback to the newest call.
        // If audio is on, plays the newest call once and treats older calls as already handled.
        // If audio is off, only updates the anchor so that when audio comes on
        // the queue will start from calls that arrive after this point.
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

                // Clear any playing flags before we adjust the anchor.
                foreach (var call in Calls)
                {
                    if (call.IsPlaying)
                        call.IsPlaying = false;
                }

                _currentPlayingCall = null;

                if (!AudioEnabled)
                {
                    // Audio off: move the anchor to the newest call so everything
                    // currently in the list is treated as handled.
                    _lastPlayedCall = newest;
                    UpdateQueueDerivedState();
                    LastQueueEvent = "Jumped to live (audio off)";
                    return;
                }

                // Audio on:
                // - treat backlog behind this newest call as handled by
                //   relying on PlayAudioAsync to set _lastPlayedCall to newest
                // - play the newest call once as the manual "jump"
                LastQueueEvent = "Jump to live (playing newest call)";

                _currentPlayingCall = newest;
                UpdateQueueDerivedState();

                await PlayAudioAsync(newest);

                // After the live call finishes:
                // If audio is still on, stream is running, autoplay is enabled,
                // and there are calls waiting (arrived while that call was playing),
                // restart the queue engine to continue from this point forward.
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

        // Handles the global audio on or off toggle from the UI.
        // This only affects audio playback and autoplay.
        // It does not touch the call stream connection state or tokens.
        private async Task OnToggleAudioAsync()
        {
            var newValue = !AudioEnabled;

            try
            {
                // Flip the audio flag.
                AudioEnabled = newValue;

                // Tie AutoPlay to the audio state so we do not auto play while muted.
                AutoPlay = newValue;

                LastQueueEvent = newValue ? "Audio toggled ON" : "Audio toggled OFF";

                if (!newValue)
                {
                    // Turning audio OFF - stop any current playback immediately,
                    // but do not touch the call stream.
                    await StopAudioFromToggleAsync();

                    // When audio is off we do not want to keep any queue anchor.
                    // This prevents the queue from resuming from an old position
                    // when audio is turned back on.
                    _lastPlayedCall = null;

                    UpdateQueueDerivedState();
                }
                else
                {
                    // Turning audio ON.
                    // Treat everything that currently exists in the list as history
                    // so we start from live and do not play backlog that arrived
                    // while audio was off.
                    if (Calls.Count > 0)
                    {
                        // Newest call is always at index 0.
                        _lastPlayedCall = Calls[0];
                    }
                    else
                    {
                        _lastPlayedCall = null;
                    }

                    UpdateQueueDerivedState();

                    // Do not kick the queue engine from here; we wait for the
                    // next new call, or the user can jump to live explicitly.
                }
            }
            catch (Exception ex)
            {
                // Never let a toggle failure affect the stream.
                System.Diagnostics.Debug.WriteLine($"Error in OnToggleAudioAsync: {ex}");
                LastQueueEvent = "Error while toggling audio (see debug output)";
            }
        }

        // Calculates the effective playback rate based on the slider step.
        // Only speeds up playback when there are calls waiting in the queue.
        private double GetEffectivePlaybackRate(CallItem item)
        {
            // Only speed up playback when there are calls waiting.
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

        // Trims the Calls collection to at most MaxCalls entries.
        // Always keeps newest calls at the top and removes the oldest from the bottom.
        private void EnforceMaxCalls()
        {
            var max = MaxCalls; // already clamped to 10–50

            while (Calls.Count > max)
            {
                var lastIndex = Calls.Count - 1;
                if (lastIndex >= 0)
                {
                    var removed = Calls[lastIndex];
                    Calls.RemoveAt(lastIndex);
                }
            }

            UpdateQueueDerivedState();
        }

        // Refreshes CallsWaiting and auto-adjusts playback speed when there is backlog.
        // This must never touch the call stream state (IsRunning, _cts, etc.).
        private void UpdateQueueDerivedState()
        {
            // Refresh bindings that depend on queue counts / state.
            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));

            // Derive friendly queue status text.
            string status;
            if (!IsRunning)
            {
                status = "Stopped";
            }
            else if (!AudioEnabled)
            {
                status = CallsWaiting > 0
                    ? $"Muted ({CallsWaiting} waiting)"
                    : "Muted (no calls waiting)";
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

            // Update history flags on each call: anything older than the anchor is "history".
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
                        // Newest at index 0; larger index means older call.
                        isHistory = i > anchorIndex;
                    }

                    if (call.IsHistory != isHistory)
                        call.IsHistory = isHistory;
                }
            }

            // Auto adjust playback speed when audio is on and there is backlog.
            if (!AudioEnabled)
                return;

            var waiting = CallsWaiting;
            if (waiting <= 0)
                return;

            // If it starts to back up more than 10 calls, automatically turn on 1.5x.
            // If it gets to 20 calls or more, automatically move to 2x.
            if (waiting >= 20)
            {
                if (PlaybackSpeedStep < 2)
                    PlaybackSpeedStep = 2;
            }
            else if (waiting >= 10 && PlaybackSpeedStep < 1)
            {
                PlaybackSpeedStep = 1;
            }
        }

        // Plays a single call when audio is off, without affecting the queue,
        // waiting count, or playback speed logic. Used for manual preview.
        private async Task PlaySingleCallWithoutQueueAsync(CallItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            // Respect filters for preview playback as well.
            if (_filterService.ShouldMute(item) || _filterService.ShouldHide(item))
                return;

            try
            {
                // Mark only this call as playing for UI feedback.
                foreach (var call in Calls)
                {
                    if (call.IsPlaying)
                        call.IsPlaying = false;
                }

                item.IsPlaying = true;

                // Resolve a playable URL (remote or local, depending on auth)
                var playbackUrl = await GetPlayableAudioUrlAsync(item.AudioUrl, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(playbackUrl))
                    return;

                // Always play at normal speed in this mode.
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

        // Core audio playback helper used by both autoplay and the queue engine.
        // Respects AudioEnabled but never touches the call stream.
        // Uses _audioCts so we can cancel in-flight playback when audio is toggled or stopped.
        private async Task PlayAudioAsync(CallItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            // Do not play audio for muted or disabled items.
            if (_filterService.ShouldMute(item) || _filterService.ShouldHide(item))
                return;

            // If audio is disabled, do not play but keep everything else updating.
            if (!AudioEnabled)
                return;

            // Cancel any existing audio operation so only one play is active at a time.
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

            // New CTS for this specific playback.
            _audioCts = new CancellationTokenSource();
            var token = _audioCts.Token;

            // Clear the playing flag on all other calls.
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
                var rate = GetEffectivePlaybackRate(item);

                // Resolve to a playable URL (remote or local) depending on auth.
                var playbackUrl = await GetPlayableAudioUrlAsync(item.AudioUrl, token);
                if (string.IsNullOrWhiteSpace(playbackUrl))
                    return;

                // This call must never hang forever. It will complete either
                // when playback ends or when token is canceled (mute / stop).
                await _audioPlaybackService.PlayAsync(playbackUrl, rate, token);
            }
            catch (OperationCanceledException)
            {
                // Expected when audio is toggled off or the stream is stopped.
            }
            catch (Exception ex)
            {
                // Ignore playback errors so the stream and UI keep running.
                System.Diagnostics.Debug.WriteLine($"Error in PlayAudioAsync: {ex}");
            }
            finally
            {
                item.IsPlaying = false;

                // This call is now the most recently finished (or aborted) call.
                _lastPlayedCall = item;

                if (_currentPlayingCall == item)
                    _currentPlayingCall = null;

                OnPropertyChanged(nameof(CallsWaiting));
                OnPropertyChanged(nameof(IsCallsWaitingVisible));
            }
        }

        private async Task<string?> GetPlayableAudioUrlAsync(string audioUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
                return null;

            // If no basic auth is configured, just use the URL as-is.
            var username = _settingsService.BasicAuthUsername;
            if (string.IsNullOrWhiteSpace(username))
                return audioUrl;

            // Try to parse both the audio URL and the configured server URL
            if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri))
                return audioUrl; // treat as local or non-HTTP path

            var serverUrl = _settingsService.ServerUrl;
            if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
            {
                // Only intercept if host/scheme match the configured server
                if (!string.Equals(audioUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(audioUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase))
                {
                    return audioUrl;
                }
            }

            try
            {
                var password = _settingsService.BasicAuthPassword ?? string.Empty;
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
            catch (OperationCanceledException)
            {
                // Bubble cancellation back up so the caller behaves the same
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] Error downloading audio with auth: {ex}");
                return null;
            }
        }

        // Opens the donation site in the system browser.
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

        // On app start or when the main page appears, call this to reconnect
        // automatically if we were connected when the app last exited.
        public async Task TryAutoReconnectAsync()
        {
            // Did we leave the app while connected last time
            var shouldReconnect = Preferences.Get(LastConnectedPreferenceKey, false);

            if (!shouldReconnect)
                return;

            // Already running now, nothing to do
            if (IsRunning)
                return;

            await StartAsync();
        }

        // Applies a theme string (System, Light, Dark) to the MAUI app.
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
