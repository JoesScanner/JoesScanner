using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;


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
        private int _maxCalls = 25;
        private bool _scrollNewestAtBottom = true;
        private bool _audioEnabled;
        private bool _hasActiveFilters;

        // 0 = 1x, 1 = 1.5x, 2 = 2x
        private double _playbackSpeedStep = 0;

        // Currently playing call, used to compute calls waiting.
        private CallItem? _currentPlayingCall;

        // True while we are playing through a queue of calls after a tap.
        private bool _isQueuePlaybackRunning;

        // Current position in the playback queue. Calls "waiting" are those
        // newer than this call (at lower indices, since newest is at the top).
        private CallItem? _queuePosition;

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
            _maxCalls = _settingsService.MaxCalls <= 0 ? 25 : _settingsService.MaxCalls;

            // Always show newest calls at the top now.
            _scrollNewestAtBottom = false;
            _settingsService.ScrollDirection = "Up";


            // Initial theme.
            var initialTheme = _settingsService.ThemeMode;
            ApplyTheme(initialTheme);

            // Initial filters indicator: true if any filter is configured.
            HasActiveFilters =
                !string.IsNullOrWhiteSpace(_settingsService.ReceiverFilter) ||
                !string.IsNullOrWhiteSpace(_settingsService.TalkgroupFilter);

            // Commands.
            StartCommand = new Command(Start, () => !IsRunning);
            StopCommand = new Command(async () => await StopAsync(), () => IsRunning);
            OpenDonateCommand = new Command(async () => await OpenDonateAsync());
            ToggleAudioCommand = new Command(async () => await OnToggleAudioAsync());
            PlayAudioCommand = new Command<CallItem>(async item => await OnCallTappedAsync(item));
        }
        private const string LastConnectedPreferenceKey = "LastConnectedOnExit";

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
        /// True when any receiver or talkgroup filter is active.
        /// Used to show the Filters indicator on the main page.
        /// </summary>
        public bool HasActiveFilters
        {
            get => _hasActiveFilters;
            set
            {
                if (_hasActiveFilters == value)
                    return;

                _hasActiveFilters = value;
                OnPropertyChanged();
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
                if (!AudioEnabled || Calls == null || Calls.Count == 0)
                    return 0;

                // Use the call that is currently playing if we have one.
                // If nothing is playing, fall back to the queue position anchor.
                var anchor = _currentPlayingCall ?? _queuePosition;
                if (anchor == null)
                    return 0;

                var index = Calls.IndexOf(anchor);
                if (index <= 0)
                    return 0;

                // With newest at the top (index 0), calls at indices 0..index-1
                // are newer than the queue position and therefore "waiting".
                return index;
            }
        }


        /// <summary>
        /// Whether the "Calls waiting" indicator should be visible in the UI.
        /// Always visible whenever audio is on, regardless of the count.
        /// </summary>
        public bool IsCallsWaitingVisible => AudioEnabled;

        /// <summary>
        /// Finds the next call to play, based on the current queue position.
        /// Newest call is at index 0.
        /// - If no queue position is set, start from the newest call (index 0).
        /// - If the position is no longer in the list, also start from the newest.
        /// - If the position is at index i > 0, the next call is at index i - 1 (newer).
        /// - If the position is at index 0, there is nothing newer to play.
        /// </summary>
        private CallItem? GetNextQueuedCall()
        {
            if (Calls.Count == 0)
                return null;

            if (_queuePosition == null)
            {
                // No anchor yet: start from newest.
                return Calls[0];
            }

            var index = Calls.IndexOf(_queuePosition);
            if (index < 0)
            {
                // Anchor not found (maybe trimmed): start from newest.
                return Calls[0];
            }

            if (index == 0)
            {
                // Already at newest; nothing newer to play.
                return null;
            }

            // Newest at top: index - 1 is the next newer call.
            return Calls[index - 1];
        }
        /// <summary>
        /// Queue playback engine.
        /// While audio is on and we are connected, repeatedly finds the next queued
        /// call and plays it, updating the queue position as it goes.
        /// New calls that arrive while this is running will be picked up on the
        /// next iteration, so nothing is skipped.
        /// </summary>
        private async Task EnsureQueuePlaybackAsync()
        {
            if (_isQueuePlaybackRunning)
                return;

            if (!AudioEnabled || !IsRunning)
                return;

            _isQueuePlaybackRunning = true;

            try
            {
                while (AudioEnabled && IsRunning)
                {
                    var next = GetNextQueuedCall();
                    if (next == null)
                        break;

                    // Move the queue anchor to the call we are about to play
                    // so CallsWaiting and the queue state stay in sync.
                    _queuePosition = next;
                    UpdateQueueDerivedState();

                    await PlayAudioAsync(next);

                    if (!AudioEnabled || !IsRunning)
                        break;
                }
            }
            finally
            {
                _isQueuePlaybackRunning = false;

                // Once we have walked the queue and are caught up,
                // clear the anchor so live auto-play can resume.
                _queuePosition = null;
                UpdateQueueDerivedState();
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
                var clamped = value <= 0 ? 1 : value;
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
            _queuePosition = null;
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

                    try
                    {
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            if (token.IsCancellationRequested)
                                return;

                            // Always show newest calls at the top (index 0).
                            Calls.Insert(0, call);

                            EnforceMaxCalls();

                            // Recompute CallsWaiting, visibility, etc.
                            UpdateQueueDerivedState();

                            // Auto-play new calls only when audio is enabled
                            // and we are not walking a manual queue started from a tap.
                            if (AutoPlay && AudioEnabled && _queuePosition == null)
                            {
                                await PlayAudioAsync(call);
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
                    _queuePosition = null;
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
            _queuePosition = null;
            UpdateQueueDerivedState();

            // We are no longer connected, remember this for next launch.
            Preferences.Set(LastConnectedPreferenceKey, false);
        }

        public async Task StopAudioFromToggleAsync()
        {
            // Stop the underlying audio player.
            await _audioPlaybackService.StopAsync();

            // Clear any IsPlaying flags and current-call anchor.
            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            _currentPlayingCall = null;
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

        /// <summary>
        /// Handles a user tap on a call in the list.
        /// When audio is on:
        ///   - Plays that call.
        ///   - Then walks forward toward the newest call at the top using the queue engine.
        /// When audio is off:
        ///   - Plays only the tapped call at normal speed (preview).
        /// </summary>
        private async Task OnCallTappedAsync(CallItem? item)
        {
            if (item == null)
                return;

            // If audio is off, just play this one call and do not walk the queue.
            if (!AudioEnabled)
            {
                await PlaySingleCallWithoutQueueAsync(item);
                return;
            }

            // Audio is on: set the queue position to the tapped call,
            // play it, then let the queue engine walk forward.
            _queuePosition = item;

            await PlayAudioAsync(item);

            if (!AudioEnabled || !IsRunning)
                return;

            await EnsureQueuePlaybackAsync();
        }

        /// <summary>
        /// Toggles AudioEnabled. This controls whether audio is allowed to play
        /// and whether new calls are auto-played.
        /// When turning audio off, stop playback and forget the queue position.
        /// When turning audio on, jump to the newest call and start from there.
        /// </summary>
        private async Task OnToggleAudioAsync()
        {
            var newValue = !AudioEnabled;

            // Flip the audio flag.
            AudioEnabled = newValue;

            // Tie AutoPlay to the audio state so we do not auto-play while muted.
            AutoPlay = newValue;

            // When turning audio off, stop any current playback immediately.
            if (!newValue)
            {
                await StopAudioFromToggleAsync();
            }
            // When turning audio back on, we do not try to catch up automatically.
            // The next new call (or a tap on a call) will drive playback.
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

            while (Calls.Count > max)
            {
                var lastIndex = Calls.Count - 1;
                if (lastIndex >= 0)
                {
                    var removed = Calls[lastIndex];
                    Calls.RemoveAt(lastIndex);

                    // If we trimmed away the current queue position, clear it.
                    if (_queuePosition == removed)
                        _queuePosition = null;
                }
            }

            // Queue position may now point to a different index or be null.
            UpdateQueueDerivedState();
        }

        /// <summary>
        /// Recomputes queue derived UI state: CallsWaiting, IsCallsWaitingVisible,
        /// and applies automatic playback speed adjustments based on backlog.
        /// </summary>
        private void UpdateQueueDerivedState()
        {
            // Refresh bindings
            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));

            // Only auto adjust when audio is on and there is backlog
            if (!AudioEnabled)
                return;

            var waiting = CallsWaiting;
            if (waiting <= 0)
                return;

            // If it starts to back up more than 10 calls, automatically turn on 1.5x.
            // If it gets to 20 calls or more, automatically move to 2x.
            if (waiting >= 20)
            {
                PlaybackSpeedStep = 2;    // 2x
            }
            else if (waiting > 10)
            {
                // Only bump up, do not force down from a higher user choice.
                if (PlaybackSpeedStep < 1)
                    PlaybackSpeedStep = 1; // 1.5x
            }
        }

        /// <summary>
        /// Plays a single call when audio is off, without affecting the queue,
        /// waiting count, or playback speed logic. Used for manual preview.
        /// </summary>
        private async Task PlaySingleCallWithoutQueueAsync(CallItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            try
            {
                // For the UI, briefly mark this call as playing.
                foreach (var call in Calls)
                {
                    if (call.IsPlaying)
                        call.IsPlaying = false;
                }

                item.IsPlaying = true;
                UpdateQueueDerivedState();

                // Always play at normal speed in this mode.
                await _audioPlaybackService.PlayAsync(item.AudioUrl, 1.0);
            }
            catch
            {
                // Swallow any playback errors for now.
            }
            finally
            {
                item.IsPlaying = false;
                UpdateQueueDerivedState();
            }
        }

        /// <summary>
        /// Plays audio for a given call, respecting the global AudioEnabled flag.
        /// Any playback failures are swallowed so they do not affect the call stream.
        /// </summary>
        private async Task PlayAudioAsync(CallItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            // If audio is disabled, do not play but keep everything else updating.
            if (!AudioEnabled)
                return;

            // Clear the playing flag on all other calls.
            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }

            // Mark this as the current playing call.
            _currentPlayingCall = item;
            item.IsPlaying = true;
            OnPropertyChanged(nameof(CallsWaiting));
            OnPropertyChanged(nameof(IsCallsWaitingVisible));

            try
            {
                var playbackRate = GetEffectivePlaybackRate(item);

                // Any exception here should not kill the stream, so we catch below.
                await _audioPlaybackService.PlayAsync(item.AudioUrl, playbackRate);
            }
            catch
            {
                // Optionally log later; for now, ignore playback errors so the stream keeps running.
            }
            finally
            {
                item.IsPlaying = false;

                // When nothing is playing, treat this as caught up.
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
