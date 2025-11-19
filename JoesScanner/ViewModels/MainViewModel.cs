using System;
using System.Collections.ObjectModel;
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
            _scrollNewestAtBottom = string.Equals(
                _settingsService.ScrollDirection,
                "Down",
                StringComparison.OrdinalIgnoreCase);

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
            PlayAudioCommand = new Command<CallItem>(async item => await PlayAudioAsync(item));
        }

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
        /// Number of calls currently in the list, shown as "calls waiting" in the UI.
        /// </summary>
        public int CallsWaiting => Calls?.Count ?? 0;

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
        /// When true, newest calls appear at the bottom.
        /// When false, newest calls appear at the top.
        /// </summary>
        public bool ScrollNewestAtBottom
        {
            get => _scrollNewestAtBottom;
            set
            {
                if (_scrollNewestAtBottom == value)
                    return;

                _scrollNewestAtBottom = value;
                _settingsService.ScrollDirection = value ? "Down" : "Up";
                OnPropertyChanged();

                // Reorder existing calls when the scroll direction changes.
                var sorted = new ObservableCollection<CallItem>();
                if (_scrollNewestAtBottom)
                {
                    foreach (var call in Calls)
                        sorted.Add(call);
                }
                else
                {
                    for (int i = Calls.Count - 1; i >= 0; i--)
                        sorted.Add(Calls[i]);
                }

                Calls.Clear();
                foreach (var call in sorted)
                    Calls.Add(call);
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
            if (IsRunning)
                return;

            // Ensure any previous stream is fully stopped.
            await StopAsync();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            IsRunning = true;
            Calls.Clear();

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var call in _callStreamService.GetCallStreamAsync(token))
                    {
                        if (token.IsCancellationRequested)
                            break;

                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            if (token.IsCancellationRequested)
                                return;

                            // Snapshot the settings view model once for this call.
                            var settingsVm = SettingsViewModel;

                            if (settingsVm != null)
                            {
                                // 1) Let settings know we have seen this call.
                                settingsVm.OnCallSeen(call);

                                // 2) If this call is filtered out, do nothing further.
                                if (!settingsVm.IsCallAllowed(call))
                                    return;
                            }

                            // 3) Insert at top or bottom depending on scroll preference.
                            if (_scrollNewestAtBottom)
                            {
                                Calls.Add(call);
                            }
                            else
                            {
                                Calls.Insert(0, call);
                            }

                            EnforceMaxCalls();

                            // 4) Auto-play new calls only when audio is enabled.
                            if (AutoPlay && AudioEnabled)
                            {
                                await PlayAudioAsync(call);
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when the stream is canceled.
                }
                catch
                {
                    // Swallow other exceptions for now, UI already shows failures.
                }
                finally
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await StopAsync();
                    });
                }
            });
        }



        /// <summary>
        /// Fully stops the call stream and audio playback.
        /// </summary>
        private async Task StopAsync()
        {
            // Stop the call stream
            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch
                {
                }

                _cts = null;
            }

            // Stop any current audio playback via CTS
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

            // Also let the audio service clean up its player
            await _audioPlaybackService.StopAsync();
        }

        /// <summary>
        /// Stops only audio playback and clears IsPlaying flags.
        /// Stream continues running so transcriptions still update.
        /// NOTE: not called by the Audio On/Off button anymore.
        /// </summary>
        public async Task StopAudioFromToggleAsync()
        {
            await _audioPlaybackService.StopAsync();

            foreach (var call in Calls)
            {
                if (call.IsPlaying)
                    call.IsPlaying = false;
            }
        }

        /// <summary>
        /// Backward compatible alias used by SettingsViewModel in older code.
        /// </summary>
        public Task StopAudioFromSettingsAsync()
        {
            return StopAudioFromToggleAsync();
        }

        /// <summary>
        /// Toggles AudioEnabled. This only controls whether audio is allowed to play
        /// and whether new calls are auto-played. It does NOT touch the stream or
        /// forcibly stop the current clip.
        /// </summary>
        private async Task OnToggleAudioAsync()
        {
            var newValue = !AudioEnabled;

            // Flip the audio flag.
            AudioEnabled = newValue;

            // Tie AutoPlay to AudioEnabled: when audio is off, do not auto-play.
            AutoPlay = newValue;

            // Let any currently playing audio finish naturally.
            await Task.CompletedTask;
        }

        /// <summary>
        /// Calculates the effective playback rate based on the slider step.
        /// Only used when autoplay is on.
        /// </summary>
        private double GetEffectivePlaybackRate(CallItem current)
        {
            if (!AutoPlay)
                return 1.0;

            if (Calls == null || Calls.Count <= 1)
                return 1.0;

            return _playbackSpeedStep switch
            {
                1 => 1.5,
                2 => 2.0,
                _ => 1.0
            };
        }

        /// <summary>
        /// Trims the Calls collection to at most MaxCalls entries.
        /// </summary>
        private void EnforceMaxCalls()
        {
            var max = MaxCalls <= 0 ? 1 : MaxCalls;

            while (Calls.Count > max)
            {
                if (ScrollNewestAtBottom)
                {
                    // Remove oldest at top.
                    Calls.RemoveAt(0);
                }
                else
                {
                    // Remove oldest at bottom.
                    var lastIndex = Calls.Count - 1;
                    if (lastIndex >= 0)
                        Calls.RemoveAt(lastIndex);
                }
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

            item.IsPlaying = true;

            try
            {
                var playbackRate = GetEffectivePlaybackRate(item);

                // Any exception here should NOT kill the stream, so we catch below.
                await _audioPlaybackService.PlayAsync(item.AudioUrl, playbackRate);
            }
            catch
            {
                // Optionally log later; for now, ignore playback errors so the stream keeps running.
            }
            finally
            {
                item.IsPlaying = false;
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
