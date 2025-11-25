using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;


namespace JoesScanner.ViewModels
{
    /// <summary>
    /// Main view model for the primary JoesScanner client screen.
    /// Handles streaming calls, playback, filters, theme, and global audio enable state.
    /// </summary>
    public class MainViewModel : BindableObject
    {
        private readonly ICallStreamService _callStreamService;
        private readonly ISettingsService _settingsService;
        private readonly IAudioPlaybackService _audioPlaybackService;

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

        /// <summary>
        /// Optional settings view model reference, used for live filter chips.
        /// Set by SettingsViewModel constructor.
        /// </summary>
        public SettingsViewModel? SettingsViewModel { get; set; }

        /// <summary>
        /// Live collection of calls shown in the UI.
        /// </summary>
        public ObservableCollection<CallItem> Calls { get; } = new();

        /// <summary>
        /// Command to start the call stream.
        /// Bound to the Connect button.
        /// </summary>
        public ICommand StartCommand { get; }

        /// <summary>
        /// Command to stop the call stream.
        /// Bound to the Disconnect button.
        /// </summary>
        public ICommand StopCommand { get; }

        /// <summary>
        /// Command to open the donation site.
        /// Bound to the Donate button in the footer.
        /// </summary>
        public ICommand OpenDonateCommand { get; }

        /// <summary>
        /// Command bound to the global Audio On or Audio Off button on the main page.
        /// Only affects audio (mute or unmute), never starts or stops the stream.
        /// </summary>
        public ICommand ToggleAudioCommand { get; }

        /// <summary>
        /// Command to play a specific call audio when the user taps it.
        /// </summary>
        public ICommand PlayAudioCommand { get; }

        public MainViewModel(
            ICallStreamService callStreamService,
            ISettingsService settingsService,
            IAudioPlaybackService audioPlaybackService)
        {
            _callStreamService = callStreamService ?? throw new ArgumentNullException(nameof(callStreamService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));

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
            _maxCalls = _settingsService.MaxCalls <= 0 ? 20 : _settingsService.MaxCalls;

            // Restore playback speed step (0 = 1x, 1 = 1.5x, 2 = 2x).
            var savedSpeed = Preferences.Get(PlaybackSpeedStepPreferenceKey, 0.0);
            PlaybackSpeedStep = savedSpeed;

            // Always show newest calls at the top now.
            _settingsService.ScrollDirection = "Up";


            // Initial theme.
            var initialTheme = _settingsService.ThemeMode;
            ApplyTheme(initialTheme);

            // Commands.
            StartCommand = new Command(Start, () => !IsRunning);
            StopCommand = new Command(async () => await StopAsync(), () => IsRunning);
            OpenDonateCommand = new Command(async () => await OpenDonateAsync());
            ToggleAudioCommand = new Command(async () => await OnToggleAudioAsync());
            PlayAudioCommand = new Command<CallItem>(async item => await OnCallTappedAsync(item));
        }
        private const string LastConnectedPreferenceKey = "LastConnectedOnExit";
        private const string PlaybackSpeedStepPreferenceKey = "PlaybackSpeedStep";

        /// <summary>
        /// True when connected to the server stream.
        /// </summary>
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
            }
        }

        /// <summary>
        /// Indicates whether audio playback is enabled.
        /// This does not affect streaming, only whether audio is played.
        /// </summary>
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

        /// <summary>
        /// Text shown on the main page audio button.
        /// </summary>
        public string AudioButtonText => AudioEnabled ? "Audio On" : "Audio Off";

        /// <summary>
        /// Background color of the main page audio button.
        /// Blue when audio is enabled, gray when muted.
        /// </summary>
        public Color AudioButtonBackground => AudioEnabled ? Colors.Blue : Colors.LightGray;

        /// <summary>
        /// Base server URL used for all API calls and displayed in the UI.
        /// </summary>
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

        /// <summary>
        /// Number of calls waiting in the playback queue, excluding the call
        /// at the current queue position.
        /// With newest at the top (index 0):
        /// - When no queue position is set, all calls are considered waiting.
        /// - When a queue position is set, waiting calls are those above it (lower indices).
        /// </summary>
        public int CallsWaiting
        {
            get
            {
                if (Calls == null || Calls.Count == 0)
                    return 0;

                // Anchor is the call that is currently playing, or the last one
                // that finished if nothing is playing.
                var anchor = _currentPlayingCall ?? _lastPlayedCall;

                // If we have no anchor at all, then all calls are considered waiting.
                if (anchor == null)
                    return Calls.Count;

                var index = Calls.IndexOf(anchor);

                // If the anchor is not found (trimmed out), treat all calls as waiting.
                if (index < 0)
                    return Calls.Count;

                // Newest is at index 0. Anything above the anchor (lower index)
                // is waiting to be played.
                if (index <= 0)
                    return 0;

                return index;
            }
        }

        /// <summary>
        /// Total number of CallItem objects that have been received from the stream
        /// since the last connect.
        /// </summary>
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

        /// <summary>
        /// Total number of CallItem objects successfully inserted into the Calls
        /// collection for the current session.
        /// </summary>
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

        /// <summary>
        /// Last high level queue or audio event, for quick debugging.
        /// </summary>
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

        /// <summary>
        /// Whether the "Calls waiting" indicator should be visible in the UI.
        /// Always visible whenever audio is on, regardless of the count.
        /// </summary>
        public bool IsCallsWaitingVisible => AudioEnabled;

        // Returns the next call to play from the backlog.
        // Calls are stored newest at index 0, oldest at the end.
        // We walk from oldest to newest so you hear things in order.
        private CallItem? GetNextQueuedCall()
        {
            if (Calls.Count == 0)
                return null;

            // Never start a new call while one is already playing.
            if (_currentPlayingCall != null)
                return null;

            // If we have never played anything, start from the oldest call.
            if (_lastPlayedCall == null)
            {
                return Calls[Calls.Count - 1];
            }

            var index = Calls.IndexOf(_lastPlayedCall);

            // If the anchor is gone (trimmed), start from the oldest call.
            if (index < 0)
                return Calls[Calls.Count - 1];

            // If the anchor is the oldest call already, we are caught up.
            if (index == 0)
                return null;

            // Newest at index 0; next newer than the anchor is at index - 1.
            return Calls[index - 1];
        }

        // Queue playback engine.
        //
        // While audio is on and we are connected, repeatedly finds the next queued
        // call and plays it, updating the queue position as it goes.
        // New calls that arrive while this is running will be picked up on the
        // next iteration, so nothing is skipped.
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

        /// <summary>
        /// Numbered playback speed step used by the slider on the main page.
        /// 0 = 1x, 1 = 1.5x, 2 = 2x.
        /// </summary>
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

        /// <summary>
        /// User friendly label for the current playback speed step.
        /// </summary>
        public string PlaybackSpeedLabel =>
            _playbackSpeedStep switch
            {
                1 => "1.5x",
                2 => "2x",
                _ => "1x"
            };

        /// <summary>
        /// AutoPlay is driven by AudioEnabled.
        /// When true and a new call arrives, it will be auto-played.
        /// </summary>
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

        /// <summary>
        /// Maximum number of calls maintained in the Calls collection.
        /// </summary>
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


        /// <summary>
        /// Tagline bound to the subtitle under the JoesScanner header.
        /// Now just shows the active server URL.
        /// </summary>
        public string TaglineText => $"Server: {ServerUrl}";

        /// <summary>
        /// Donation URL used by the footer button.
        /// </summary>
        public string DonateUrl => "https://www.joesscanner.com";

        /// <summary>
        /// Entry point used by the Connect button, wraps the async Start.
        /// </summary>
        public void Start()
        {
            _ = StartAsync();
        }

        /// <summary>
        /// Starts the streaming of calls from the server.
        /// </summary>
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

            // Reset call state for this session.
            IsRunning = true;
            Calls.Clear();
            _currentPlayingCall = null;
            _lastPlayedCall = null;
            UpdateQueueDerivedState();

            // Fire-and-forget the background stream loop.
            _ = Task.Run(() => RunCallStreamLoopAsync(token));
        }

        /// <summary>
        /// Background loop that continuously pulls calls from the server
        /// and feeds them into the UI. This must never throw out of the
        /// method except for cancellation.
        /// </summary>
        private async Task RunCallStreamLoopAsync(CancellationToken token)
        {
            try
            {
                await foreach (var call in _callStreamService.GetCallStreamAsync(token))
                {
                    if (token.IsCancellationRequested)
                        break;

                    TotalCallsReceived++;

                    try
                    {
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            if (token.IsCancellationRequested)
                                return;

                            // Always show newest calls at the top (index 0).
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
                        // Never let a UI or audio error kill the stream.
                        System.Diagnostics.Debug.WriteLine($"Error updating UI for call: {ex}");
                        // Swallow and continue to the next call.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when StopAsync cancels the token.
            }
            catch (Exception ex)
            {
                // Unexpected stream error. Log it but do not crash the app.
                System.Diagnostics.Debug.WriteLine($"Error in call stream loop: {ex}");
            }
            finally
            {
                // When the loop stops (typically due to StopAsync),
                // clean up connection state on the UI thread.
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    // Only clean up if this token is still the active one.
                    if (_cts == null || _cts.Token != token)
                        return;

                    // Mark as not running and update preference.
                    IsRunning = false;
                    Preferences.Set(LastConnectedPreferenceKey, false);

                    // Stop audio and clear playback flags.
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

        /// <summary>
        /// Fully stops the call stream and audio playback.
        /// </summary>
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

            // Stop any current audio playback via CTS (if we ever wire _audioCts).
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
                // Swallow the exception so the app keeps running normally.
            }

            // Clear any IsPlaying flags on all calls.
            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            _currentPlayingCall = null;
            // We intentionally do NOT touch _queuePosition here.
            // The queue anchor will be managed by the queue engine itself.
            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));
        }

        /// <summary>
        /// Backward compatible alias used by SettingsViewModel in older code.
        /// </summary>
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

            LastQueueEvent = $"Call tapped at {DateTime.Now:T}";

            try
            {
                if (!AudioEnabled)
                {
                    // Audio off: pure preview.
                    await PlaySingleCallWithoutQueueAsync(item);
                    return;
                }

                // Audio on: treat this as the next queue item.
                await PlayAudioAsync(item);

                // If audio is still on, we are connected, and autoplay is enabled,
                // have the queue engine continue from this call forward.
                if (AudioEnabled && IsRunning && AutoPlay)
                {
                    _ = EnsureQueuePlaybackAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnCallTappedAsync: {ex}");
                LastQueueEvent = "Error while playing tapped call (see debug output)";
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

                    // When we mute, treat everything that exists right now as
                    // "already handled" so that when we turn audio back on, we
                    // only play calls that arrive AFTER this point.
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
                }
                else
                {
                    // Turning audio ON.
                    // If we are connected and have calls waiting, kick the queue engine
                    // so playback resumes with calls that arrived AFTER the mute.
                    if (IsRunning && AutoPlay && CallsWaiting > 0)
                    {
                        _ = EnsureQueuePlaybackAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let a toggle failure affect the stream.
                System.Diagnostics.Debug.WriteLine($"Error in OnToggleAudioAsync: {ex}");
                LastQueueEvent = "Error while toggling audio (see debug output)";
            }
        }

        /// <summary>
        /// Calculates the effective playback rate based on the slider step.
        /// Only speeds up playback when there are calls waiting in the queue.
        /// </summary>
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

        /// <summary>
        /// Trims the Calls collection to at most MaxCalls entries.
        /// Always keeps newest calls at the top and removes the oldest from the bottom.
        /// </summary>
        private void EnforceMaxCalls()
        {
            var max = MaxCalls <= 0 ? 1 : MaxCalls;

            if (Calls.Count <= max)
                return;

            while (Calls.Count > max)
            {
                // Prefer to trim the oldest call that is not currently playing
                // and not the last played anchor.
                var indexToRemove = -1;

                for (var i = Calls.Count - 1; i >= 0; i--)
                {
                    var candidate = Calls[i];

                    if (candidate != _currentPlayingCall && candidate != _lastPlayedCall)
                    {
                        indexToRemove = i;
                        break;
                    }
                }

                // If every call is protected, still trim the very oldest call.
                if (indexToRemove < 0)
                    indexToRemove = Calls.Count - 1;

                var removed = Calls[indexToRemove];
                Calls.RemoveAt(indexToRemove);

                // If we trimmed away the currently playing call, clear it.
                if (removed == _currentPlayingCall)
                    _currentPlayingCall = null;

                // If we trimmed away the last played anchor, clear it.
                if (removed == _lastPlayedCall)
                    _lastPlayedCall = null;
            }

            // Anchors may now be null, so refresh derived state.
            UpdateQueueDerivedState();
        }

        // Refreshes CallsWaiting and auto-adjusts playback speed when there is backlog.
        // This must never touch the call stream state (IsRunning, _cts, etc.).
        private void UpdateQueueDerivedState()
        {
            // Refresh bindings
            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));

            // Only auto-adjust when audio is on and there is backlog.
            if (!AudioEnabled)
                return;

            var waiting = CallsWaiting;
            if (waiting <= 0)
                return;

            // If it starts to back up more than 10 calls, automatically turn on 1.5x.
            // If it gets to 20 calls or more, automatically move to 2x.
            //
            // This only manipulates the playback speed step; it does not affect
            // the connection or queue anchor.
            if (waiting >= 20)
            {
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

            try
            {
                // Mark only this call as playing for UI feedback.
                foreach (var call in Calls)
                {
                    if (call.IsPlaying)
                        call.IsPlaying = false;
                }

                item.IsPlaying = true;

                // Always play at normal speed in this mode.
                await _audioPlaybackService.PlayAsync(item.AudioUrl, 1.0);
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

                // This call must never hang forever. It will complete either
                // when playback ends or when token is canceled (mute / stop).
                await _audioPlaybackService.PlayAsync(item.AudioUrl, rate, token);
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

        /// <summary>
        /// Opens the donation site in the system browser.
        /// </summary>
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

        /// <summary>
        /// On app start or when the main page appears, call this to reconnect
        /// automatically if we were connected when the app last exited.
        /// </summary>
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

        /// <summary>
        /// Applies a theme string (System, Light, Dark) to the MAUI app.
        /// </summary>
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