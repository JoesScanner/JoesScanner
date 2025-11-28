using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.Controls;
using System;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Input;
using System.Threading.Tasks;


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

        // Basic auth credentials for the TR server
        private string _basicAuthUsername = string.Empty;
        private string _basicAuthPassword = string.Empty;
        private string _savedBasicAuthUsername = string.Empty;
        private string _savedBasicAuthPassword = string.Empty;

        // Inline server validation status
        private bool _isValidatingServer;
        private bool _hasServerValidationResult;
        private string _serverValidationMessage = string.Empty;
        private bool _serverValidationIsError;

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

        // Password visibility command
        public ICommand ToggleBasicAuthPasswordVisibilityCommand { get; }

        public const string DefaultServerUrl = "https://app.joesscanner.com";

        public SettingsViewModel(ISettingsService settingsService, MainViewModel mainViewModel)
        {
            _settings = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };


            Current = this;

            ToggleBasicAuthPasswordVisibilityCommand =
                new Command(() => IsBasicAuthPasswordVisible = !IsBasicAuthPasswordVisible);

            // Seed state from persisted settings
            InitializeFromSettings();

            // Commands
            SaveCommand = new Command(SaveSettings);
            ResetServerCommand = new Command(ResetServerUrl);
            ValidateServerCommand = new Command(async () => await ValidateServerUrlAsync());

        }

        public bool IsValidatingServer
        {
            get => _isValidatingServer;
            private set
            {
                if (_isValidatingServer == value)
                    return;

                _isValidatingServer = value;
                OnPropertyChanged();
            }
        }

        public bool HasServerValidationResult
        {
            get => _hasServerValidationResult;
            private set
            {
                if (_hasServerValidationResult == value)
                    return;

                _hasServerValidationResult = value;
                OnPropertyChanged();
            }
        }

        public string ServerValidationMessage
        {
            get => _serverValidationMessage;
            private set
            {
                if (_serverValidationMessage == value)
                    return;

                _serverValidationMessage = value;
                OnPropertyChanged();
            }
        }

        public bool ServerValidationIsError
        {
            get => _serverValidationIsError;
            private set
            {
                if (_serverValidationIsError == value)
                    return;

                _serverValidationIsError = value;
                OnPropertyChanged();
            }
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
                var newValue = value ?? string.Empty;
                if (string.Equals(_serverUrl, newValue, StringComparison.Ordinal))
                    return;

                _serverUrl = newValue;
                OnPropertyChanged();

                // Auto-track default vs custom based on the current text.
                var defaultUrl = DefaultServerUrl;
                var isDefault = string.Equals(_serverUrl, defaultUrl, StringComparison.OrdinalIgnoreCase);

                if (_useDefaultConnection != isDefault)
                {
                    _useDefaultConnection = isDefault;
                    OnPropertyChanged(nameof(UseDefaultConnection));
                }

                UpdateHasChanges();
            }
        }


        // Basic auth username for the TR server
        public string BasicAuthUsername
        {
            get => _basicAuthUsername;
            set
            {
                if (string.Equals(_basicAuthUsername, value, StringComparison.Ordinal))
                    return;

                _basicAuthUsername = value ?? string.Empty;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        // Basic auth password for the TR server
        public string BasicAuthPassword
        {
            get => _basicAuthPassword;
            set
            {
                if (string.Equals(_basicAuthPassword, value, StringComparison.Ordinal))
                    return;

                _basicAuthPassword = value ?? string.Empty;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }
        // Password visibility state
        private bool _isBasicAuthPasswordVisible;

        // True when the password Entry should mask input
        public bool IsBasicAuthPasswordHidden => !_isBasicAuthPasswordVisible;

        // Button text
        public string BasicAuthPasswordToggleText =>
            _isBasicAuthPasswordVisible ? "Hide" : "Show";

        public bool IsBasicAuthPasswordVisible
        {
            get => _isBasicAuthPasswordVisible;
            set
            {
                if (_isBasicAuthPasswordVisible == value)
                    return;

                _isBasicAuthPasswordVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBasicAuthPasswordHidden));
                OnPropertyChanged(nameof(BasicAuthPasswordToggleText));
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

            // Basic auth seed
            _basicAuthUsername = _settings.BasicAuthUsername ?? string.Empty;
            _basicAuthPassword = _settings.BasicAuthPassword ?? string.Empty;
            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;

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
            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;

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

            // Basic auth
            _settings.BasicAuthUsername = BasicAuthUsername;
            _settings.BasicAuthPassword = BasicAuthPassword;

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
            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;


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

            // Start: inline "validating" state
            IsValidatingServer = true;
            HasServerValidationResult = true;
            ServerValidationMessage = "Validating server...";
            ServerValidationIsError = false;

            if (string.IsNullOrEmpty(url))
            {
                ServerValidationMessage = "Enter a server url first.";
                ServerValidationIsError = true;
                IsValidatingServer = false;
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);

                // Apply basic auth using current edited values, if present
                if (!string.IsNullOrWhiteSpace(BasicAuthUsername))
                {
                    var raw = $"{BasicAuthUsername}:{BasicAuthPassword ?? string.Empty}";
                    var bytes = Encoding.ASCII.GetBytes(raw);
                    var base64 = Convert.ToBase64String(bytes);

                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Basic", base64);
                }

                using var response = await _httpClient.SendAsync(request);

                var statusCode = response.StatusCode;
                var statusInt = (int)statusCode;

                if (response.IsSuccessStatusCode
                    || statusCode == HttpStatusCode.NotImplemented) // treat 501 as reachable
                {
                    if (statusCode == HttpStatusCode.NotImplemented)
                    {
                        ServerValidationMessage =
                            "Server reachable. Connection looks good.";
                    }
                    else
                    {
                        ServerValidationMessage =
                            $"Server reachable (HTTP {statusInt} {response.ReasonPhrase}).";
                    }

                    ServerValidationIsError = false;
                }
                else if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
                {
                    ServerValidationMessage =
                        $"Authentication failed (HTTP {statusInt} {response.ReasonPhrase}). " +
                        "Check basic auth username/password and that the server/firewall is configured to allow this client.";
                    ServerValidationIsError = true;
                }
                else
                {
                    ServerValidationMessage =
                        $"Server responded with HTTP {statusInt} {response.ReasonPhrase}.";
                    ServerValidationIsError = true;
                }


            }
            catch (HttpRequestException ex)
            {
                ServerValidationMessage = $"Could not reach server: {ex.Message}";
                ServerValidationIsError = true;
            }
            catch (TaskCanceledException)
            {
                // This is what HttpClient throws on timeout
                ServerValidationMessage = "Server did not respond in time (validation timed out).";
                ServerValidationIsError = true;
            }
            catch (Exception ex)
            {
                ServerValidationMessage = $"Unexpected error validating server: {ex.Message}";
                ServerValidationIsError = true;
            }
            finally
            {
                IsValidatingServer = false;
            }
        }

        private void UpdateHasChanges()
        {
            var has =
                !string.Equals(_serverUrl, _savedServerUrl, StringComparison.OrdinalIgnoreCase)
                || _useDefaultConnection != _savedUseDefaultConnection
                || !string.Equals(_basicAuthUsername, _savedBasicAuthUsername, StringComparison.Ordinal)
                || !string.Equals(_basicAuthPassword, _savedBasicAuthPassword, StringComparison.Ordinal);

            HasChanges = has;
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
            BasicAuthUsername = _savedBasicAuthUsername;
            BasicAuthPassword = _savedBasicAuthPassword;
            HasChanges = false;
        }





    }
}
