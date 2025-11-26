using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace JoesScanner.ViewModels
{
    /// <summary>
    /// View model for the Settings page.
    /// Controls connection settings, call list behavior, theme,
    /// and the unified filter list that is populated from live calls.
    /// </summary>
    public class SettingsViewModel : BindableObject
    {
        private readonly ISettingsService _settings;
        private readonly MainViewModel _mainViewModel;
        private readonly HttpClient _httpClient;

        // Connection fields
        private string _serverUrl = string.Empty;
        private bool _useDefaultConnection;

        // Saved snapshot to detect connection changes
        private string _savedServerUrl = string.Empty;
        private bool _savedUseDefaultConnection;
        private bool _hasChanges;

        // True when any setting on this page differs from what was last saved.
        // Used by the Save button to decide when to turn red.
        public bool HasUnsavedSettings
        {
            get
            {
                // Connection changes (server URL / default connection)
                if (HasChanges)
                    return true;

                // Call list settings
                if (_maxCalls != _savedMaxCalls)
                    return true;

                return false;
            }
        }

        // Saved snapshot for other settings that are only committed on Save
        private int _savedMaxCalls;

        // Call display settings
        private int _maxCalls;

        // Theme as a single string: "System", "Light", "Dark"
        private string _themeMode = "System";

        // Disabled entries persisted in settings, keyed as "receiver|site|talkgroup"
        private static readonly HashSet<string> _disabledKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track the "current" instance for static helpers
        public static SettingsViewModel? Current { get; private set; }

        // Sort order for the filter list (shared across instances)
        private static bool _sortAscending = true;

        // Commands
        public ICommand SaveCommand { get; }
        public ICommand ResetServerCommand { get; }
        public ICommand ValidateServerCommand { get; }

        // Filter commands
        public ICommand ToggleReceiverFilterCommand { get; }
        public ICommand ToggleSiteFilterCommand { get; }

        public const string DefaultServerUrl = "https://app.joesscanner.com";

        public SettingsViewModel(ISettingsService settingsService, MainViewModel mainViewModel)
        {
            _settings = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
            _httpClient = new HttpClient();

            Current = this;

            // Seed state from persisted settings
            InitializeFromSettings();

            // Commands
            SaveCommand = new Command(SaveSettings);
            ResetServerCommand = new Command(ResetServerUrl);
            ValidateServerCommand = new Command(async () => await ValidateServerUrlAsync());


        }

        /// <summary>
        /// If true, there are unsaved connection changes.
        /// Used by SettingsPage.xaml.cs to discard or warn when backing out.
        /// </summary>
        public bool HasChanges
        {
            get => _hasChanges;
            private set
            {
                if (_hasChanges == value)
                    return;

                _hasChanges = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnsavedSettings));
            }
        }

        /// <summary>
        /// Current server URL in the edit box.
        /// </summary>
        public string ServerUrl
        {
            get => _serverUrl;
            set
            {
                if (_serverUrl == value)
                    return;

                _serverUrl = value ?? string.Empty;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        /// <summary>
        /// True when "Default connection" is selected.
        /// When true, the server URL will be reset to the default and saved.
        /// </summary>
        public bool UseDefaultConnection
        {
            get => _useDefaultConnection;
            set
            {
                if (_useDefaultConnection == value)
                    return;

                _useDefaultConnection = value;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        /// <summary>
        /// Maximum number of calls kept in the list.
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
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnsavedSettings));
            }
        }

        /// <summary>
        /// Theme mode string: "System", "Light", or "Dark".
        /// </summary>
        public string ThemeMode
        {
            get => _themeMode;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "System" : value;

                if (string.Equals(_themeMode, normalized, StringComparison.OrdinalIgnoreCase))
                    return;

                _themeMode = normalized;
                OnPropertyChanged();

                // Keep boolean helpers in sync
                OnPropertyChanged(nameof(IsThemeSystem));
                OnPropertyChanged(nameof(IsThemeLight));
                OnPropertyChanged(nameof(IsThemeDark));

                // Apply theme immediately and persist to the settings service
                _settings.ThemeMode = _themeMode;
                ApplyTheme(_themeMode);
            }
        }

        /// <summary>
        /// Helper booleans for the three theme radio buttons.
        /// Only the "true" assignment triggers changes.
        /// </summary>
        public bool IsThemeSystem
        {
            get => string.Equals(ThemeMode, "System", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!value)
                    return;
                ThemeMode = "System";
            }
        }

        public bool IsThemeLight
        {
            get => string.Equals(ThemeMode, "Light", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!value)
                    return;
                ThemeMode = "Light";
            }
        }

        public bool IsThemeDark
        {
            get => string.Equals(ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!value)
                    return;
                ThemeMode = "Dark";
            }
        }

        /// <summary>
        /// Sort order flag. True for A to Z, false for Z to A.
        /// </summary>
        public bool SortAscending
        {
            get => _sortAscending;
            set
            {
                if (_sortAscending == value)
                    return;

                _sortAscending = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Reloads view model fields from ISettingsService.
        /// Called once from constructor.
        /// </summary>
        private void InitializeFromSettings()
        {
            // Connection seed
            _serverUrl = _settings.ServerUrl ?? string.Empty;
            _savedServerUrl = _serverUrl;

            // Default connection flag
            var defaultUrl = DefaultServerUrl;
            _useDefaultConnection = string.Equals(_serverUrl, defaultUrl, StringComparison.OrdinalIgnoreCase);
            _savedUseDefaultConnection = _useDefaultConnection;

            // Calls
            _maxCalls = _settings.MaxCalls;

            // Theme – normalize to a safe value and push back into settings if needed
            var rawTheme = _settings.ThemeMode;
            if (string.IsNullOrWhiteSpace(rawTheme) ||
                (!string.Equals(rawTheme, "System", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(rawTheme, "Light", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(rawTheme, "Dark", StringComparison.OrdinalIgnoreCase)))
            {
                _themeMode = "System";
                _settings.ThemeMode = "System";
            }
            else
            {
                _themeMode = rawTheme;
            }

            // Load disabled keys from settings. We reuse ReceiverFilter
            // as "Disabled filter entries" stored as:
            //   receiver|site|talkgroup;receiver2|site2|talkgroup2
            _disabledKeys.Clear();
            var rawDisabled = _settings.ReceiverFilter ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(rawDisabled))
            {
                var entries = rawDisabled.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var entry in entries)
                {
                    var trimmed = entry.Trim();
                    if (trimmed.Length == 0)
                        continue;

                    // Only accept entries that look like our new format
                    if (!trimmed.Contains('|'))
                        continue;

                    _disabledKeys.Add(trimmed);
                }
            }

            // Capture snapshots used for unsaved-change detection.
            _savedServerUrl = _settings.ServerUrl;
            _savedUseDefaultConnection = UseDefaultConnection;
            _savedMaxCalls = _maxCalls;

            // At this point everything matches the persisted state.
            HasChanges = false;
            OnPropertyChanged(nameof(HasUnsavedSettings));

        }

        private void ApplyTheme(string mode)
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
                theme = AppTheme.Unspecified; // follow system
            }

            app.UserAppTheme = theme;
        }

        /// <summary>
        /// Persists connection, call list, and theme settings,
        /// and updates main view model mirrors where needed.
        /// Also persists disabled filter keys.
        /// </summary>
        private void SaveSettings()
        {
            // Connection
            if (UseDefaultConnection)
            {
                _settings.ServerUrl = DefaultServerUrl;
                _mainViewModel.ServerUrl = _settings.ServerUrl;
            }
            else
            {
                _settings.ServerUrl = ServerUrl;
                _mainViewModel.ServerUrl = ServerUrl;
            }

            // Max calls
            _settings.MaxCalls = MaxCalls;
            _mainViewModel.MaxCalls = MaxCalls;

            // Theme (apply on save)
            _settings.ThemeMode = ThemeMode;
            ApplyTheme(ThemeMode);

            // Update our saved snapshots so the UI knows everything is clean.
            _savedServerUrl = _settings.ServerUrl;
            _savedUseDefaultConnection = UseDefaultConnection;
            _savedMaxCalls = _maxCalls;

            // This will also raise HasUnsavedSettings.
            HasChanges = false;
            OnPropertyChanged(nameof(HasUnsavedSettings));

        }

        /// <summary>
        /// Reset server url to the canonical default and mark as default.
        /// This only changes local fields until Save is pressed.
        /// </summary>
        private void ResetServerUrl()
        {
            ServerUrl = DefaultServerUrl;
            UseDefaultConnection = true;
        }

        /// <summary>
        /// Very lightweight server validation. You can expand this to hit
        /// your health endpoint later.
        /// </summary>
        private async Task ValidateServerUrlAsync()
        {
            var url = ServerUrl?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Server validation",
                    "Please enter a server url first.",
                    "OK");
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    await Application.Current.MainPage.DisplayAlert(
                        "Server validation",
                        "Server appears to be reachable.",
                        "OK");
                }
                else
                {
                    await Application.Current.MainPage.DisplayAlert(
                        "Server validation",
                        $"Server responded with status {(int)response.StatusCode}.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Server validation",
                    $"Could not reach server: {ex.Message}",
                    "OK");
            }
        }

        /// <summary>
        /// Used by SettingsPage.xaml.cs when closing the page
        /// without saving. Resets the connection fields to match
        /// the last saved values.
        /// </summary>
        public void DiscardConnectionChanges()
        {
            ServerUrl = _savedServerUrl;
            UseDefaultConnection = _savedUseDefaultConnection;
            HasChanges = false;
        }
        private void UpdateHasChanges()
        {
            var has = !string.Equals(_serverUrl, _savedServerUrl, StringComparison.OrdinalIgnoreCase)
                      || _useDefaultConnection != _savedUseDefaultConnection;

            HasChanges = has;
        }


    }
}
