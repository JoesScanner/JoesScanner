using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JoesScanner.ViewModels
{
    // View model for the Settings page.
    // Controls connection settings, call list behavior, theme,
    // and the unified filter list that is populated from live calls.
    public class SettingsViewModel : BindableObject
    {
        private readonly ISettingsService _settings;
        private readonly MainViewModel _mainViewModel;
        private readonly HttpClient _httpClient;
        private readonly FilterService _filterService = FilterService.Instance;

        public ObservableCollection<FilterRule> FilterRules => _filterService.Rules;

        // Connection fields
        private string _serverUrl = string.Empty;
        private bool _useDefaultConnection;

        // Basic auth credentials for the TR server
        private string _basicAuthUsername = string.Empty;
        private string _basicAuthPassword = string.Empty;
        private string _savedBasicAuthUsername = string.Empty;
        private string _savedBasicAuthPassword = string.Empty;

        // Subscription summary for the Settings connection box header.
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

        // True only immediately after a successful validation in this app session.
        // On a fresh app launch it is false so the header shows only the plan info.
        private bool _showValidationPrefix;

        // Password visibility state
        private bool _isBasicAuthPasswordVisible;

        // Inline server validation status
        private bool _isValidatingServer;
        private bool _hasServerValidationResult;
        private string _serverValidationMessage = string.Empty;
        private bool _serverValidationIsError;

        // Saved snapshot to detect connection changes
        private string _savedServerUrl = string.Empty;
        private bool _savedUseDefaultConnection;
        private bool _hasChanges;

        // Saved snapshot for settings that are only committed on Save
        private int _savedMaxCalls;
        private int _savedAutoSpeedThreshold;
        private bool _savedAnnounceNewCalls;

        // Call display settings
        private int _maxCalls;
        private int _autoSpeedThreshold;
        private bool _announceNewCalls;

        // Theme as a single string: "System", "Light", "Dark"
        private string _themeMode = "System";

        // Disabled entries persisted in settings, keyed as "receiver|site|talkgroup"
        private static readonly HashSet<string> _disabledKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track the current instance for static helpers
        public static SettingsViewModel? Current { get; private set; }

        // Sort order for the filter list (shared across instances)
        private static bool _sortAscending = true;

        // Commands
        public ICommand ToggleMuteFilterCommand { get; }
        public ICommand ToggleDisableFilterCommand { get; }
        public ICommand ClearFilterCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ResetServerCommand { get; }
        public ICommand ValidateServerCommand { get; }
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

            InitializeFromSettings();

            SaveCommand = new Command(SaveSettings);
            ResetServerCommand = new Command(ResetServerUrl);
            ValidateServerCommand = new Command(async () => await ValidateServerUrlAsync());

            ToggleMuteFilterCommand = new Command<FilterRule>(OnToggleMuteFilter);
            ToggleDisableFilterCommand = new Command<FilterRule>(OnToggleDisableFilter);
            ClearFilterCommand = new Command<FilterRule>(OnClearFilter);
        }

        // True when any setting on this page differs from what was last saved.
        // Used by the Save button to decide when to turn red.
        public bool HasUnsavedSettings =>
            HasChanges ||
            _maxCalls != _savedMaxCalls ||
            _autoSpeedThreshold != _savedAutoSpeedThreshold;

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

        // True when there are unsaved connection or credential changes.
        // Used by SettingsPage.xaml.cs to discard or warn when backing out.
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

        // Current server URL in the edit box.
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

        // True when "Default connection" is selected.
        // When true, the server URL will be reset to the default and saved.
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

        // Maximum number of calls kept in the list.
        public int MaxCalls
        {
            get => _maxCalls;
            set
            {
                var clamped = value;
                if (clamped < 5) clamped = 5;
                if (clamped > 50) clamped = 50;

                if (_maxCalls == clamped)
                    return;

                _maxCalls = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnsavedSettings));
            }
        }

        // Threshold in calls waiting where autospeed kicks in.
        // Controls when the app will start increasing playback speed automatically.
        public int AutoSpeedThreshold
        {
            get => _autoSpeedThreshold;
            set
            {
                var clamped = value;
                if (clamped < 10) clamped = 10;
                if (clamped > 100) clamped = 100;

                if (_autoSpeedThreshold == clamped)
                    return;

                _autoSpeedThreshold = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnsavedSettings));
            }
		}

		// When true, new incoming calls will be announced via the platform screen reader.
		public bool AnnounceNewCalls
		{
			get => _announceNewCalls;
			set
			{
				if (_announceNewCalls == value)
					return;

				_announceNewCalls = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(HasUnsavedSettings));
			}
		}


        // Theme mode string: "System", "Light", or "Dark".
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

                OnPropertyChanged(nameof(IsThemeSystem));
                OnPropertyChanged(nameof(IsThemeLight));
                OnPropertyChanged(nameof(IsThemeDark));

                _settings.ThemeMode = _themeMode;
                ApplyTheme(_themeMode);
            }
        }

        // Helper booleans for the three theme radio buttons.
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

        // Sort order flag. True for A to Z, false for Z to A.
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

        private void OnToggleMuteFilter(FilterRule? rule)
        {
            if (rule == null)
                return;

            _filterService.ToggleMute(rule);
        }

        private void OnToggleDisableFilter(FilterRule? rule)
        {
            if (rule == null)
                return;

            _filterService.ToggleDisable(rule);
        }

        private void OnClearFilter(FilterRule? rule)
        {
            if (rule == null)
                return;

            _filterService.ClearRule(rule);
        }

        // Reloads view model fields from ISettingsService.
        // Called once from constructor.
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
            _autoSpeedThreshold = _settings.AutoSpeedThreshold;
            _announceNewCalls = _settings.AnnounceNewCalls;

            // Theme - normalize to a safe value and push back into settings if needed
            var rawTheme = _settings.ThemeMode;
            if (string.IsNullOrWhiteSpace(rawTheme)
                || (!string.Equals(rawTheme, "System", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(rawTheme, "Light", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(rawTheme, "Dark", StringComparison.OrdinalIgnoreCase)))
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

                    // Only accept entries that look like our current format
                    if (!trimmed.Contains('|'))
                        continue;

                    _disabledKeys.Add(trimmed);
                }
            }

            // Capture snapshots used for unsaved change detection
            _savedServerUrl = _settings.ServerUrl ?? string.Empty;
            _savedUseDefaultConnection = UseDefaultConnection;
            _savedMaxCalls = _maxCalls;
            _savedAutoSpeedThreshold = _autoSpeedThreshold;
            _savedAnnounceNewCalls = _announceNewCalls;
            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;

            // At this point everything matches the persisted state
            _showValidationPrefix = false;
            HasChanges = false;
            OnPropertyChanged(nameof(HasUnsavedSettings));
            UpdateSubscriptionSummaryFromSettings();
        }
        private void UpdateSubscriptionSummaryFromSettings()
        {
            // Only show this when pointed at the hosted Joe's Scanner server
            // and a scanner username is present.
            var serverUrl = ServerUrl;
            var username = BasicAuthUsername;

            if (string.IsNullOrWhiteSpace(serverUrl) ||
                !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(username))
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

            // If the last validation failed, do not show stale info.
            if (!_settings.SubscriptionLastStatusOk)
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

            // Base summary that we cached from the successful auth call.
            var planSummary = _settings.SubscriptionLastMessage ?? string.Empty;
            planSummary = planSummary.Trim();

            if (string.IsNullOrEmpty(planSummary))
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

            // New: if the summary is missing the price text but we have it in settings,
            // inject it between the plan name and the date.
            var priceText = _settings.SubscriptionPriceId ?? string.Empty;
            priceText = priceText.Trim();

            if (!string.IsNullOrEmpty(priceText))
            {
                // Heuristic: treat it as a user-friendly price string, not a GUID,
                // when it contains a space or a dollar sign.
                var looksLikeFriendlyPrice =
                    priceText.Contains(" ", StringComparison.Ordinal) ||
                    priceText.Contains("$", StringComparison.Ordinal);

                if (looksLikeFriendlyPrice &&
                    !planSummary.Contains(priceText, StringComparison.Ordinal))
                {
                    // Find where the date portion starts (if present).
                    var idxTrial = planSummary.IndexOf(" - Trial end date:", StringComparison.Ordinal);
                    var idxRenewalDate = planSummary.IndexOf(" - Renewal date:", StringComparison.Ordinal);
                    var idxRenewal = planSummary.IndexOf(" - Renewal:", StringComparison.Ordinal);

                    var idxSplit = idxTrial >= 0
                        ? idxTrial
                        : (idxRenewalDate >= 0
                            ? idxRenewalDate
                            : (idxRenewal >= 0 ? idxRenewal : -1));

                    if (idxSplit >= 0)
                    {
                        // Insert " - {priceText}" before the date portion.
                        var before = planSummary.Substring(0, idxSplit);
                        var after = planSummary.Substring(idxSplit);
                        planSummary = $"{before} - {priceText}{after}";
                    }
                    else
                    {
                        // No date portion; just append the price.
                        planSummary = $"{planSummary} - {priceText}";
                    }
                }
            }

            // Split the two logical groups onto separate lines.
            // First line: plan / price / interval
            // Second line: trial or renewal info.
            planSummary = planSummary
                .Replace(" - Trial end date:", Environment.NewLine + "Trial end date:")
                .Replace(" - Renewal date:", Environment.NewLine + "Renewal date:")
                .Replace(" - Renewal:", Environment.NewLine + "Renewal:");

            if (_showValidationPrefix)
            {
                // Keep the validation prefix on the same line as the first group.
                SubscriptionSummary = $"Joe's Scanner account validated. {planSummary}";
            }
            else
            {
                SubscriptionSummary = planSummary;
            }

            ShowSubscriptionSummary = true;
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
                theme = AppTheme.Unspecified;
            }

            app.UserAppTheme = theme;
        }

        // Persists connection, call list, and theme settings,
        // and updates main view model mirrors where needed.
        // Also persists disabled filter keys.
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

            // Autospeed threshold
            _settings.AutoSpeedThreshold = AutoSpeedThreshold;

            // Theme
            _settings.ThemeMode = ThemeMode;
            ApplyTheme(ThemeMode);

            // Update snapshots
            _savedServerUrl = _settings.ServerUrl ?? string.Empty;
            _savedUseDefaultConnection = UseDefaultConnection;
            _savedMaxCalls = _maxCalls;
            _savedAutoSpeedThreshold = _autoSpeedThreshold;
            _savedAnnounceNewCalls = _announceNewCalls;
            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;


            HasChanges = false;
            OnPropertyChanged(nameof(HasUnsavedSettings));
        }

        // Reset server url to the canonical default and mark as default.
        // This only changes local fields until Save is pressed.
        private void ResetServerUrl()
        {
            ServerUrl = DefaultServerUrl;
            UseDefaultConnection = true;
        }

        // Lightweight server validation and SureCart auth check.
        private async Task ValidateServerUrlAsync()
        {
            var url = ServerUrl?.Trim();

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

            var isDefaultServer =
                string.Equals(url, DefaultServerUrl, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(url.TrimEnd('/'), DefaultServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

            try
            {
                if (isDefaultServer)
                {
                    var accountUsername = _settings.BasicAuthUsername;
                    var accountPassword = _settings.BasicAuthPassword;

                    if (string.IsNullOrWhiteSpace(accountUsername) || string.IsNullOrWhiteSpace(accountPassword))
                    {
                        ServerValidationMessage =
                            "Scanner account username and password are not configured. Enter them in the connection box and save first.";
                        ServerValidationIsError = true;

                        _settings.SubscriptionLastStatusOk = false;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        return;
                    }

                    var authUrl = "https://joesscanner.com/wp-json/joes-scanner/v1/auth";

                    var payload = new
                    {
                        username = accountUsername,
                        password = accountPassword,
                        client = "JoesScannerApp",
                        version = "1.0.0"
                    };

                    var json = JsonSerializer.Serialize(payload);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");

                    using var authResponseMessage = await _httpClient.PostAsync(authUrl, content);
                    var responseBody = await authResponseMessage.Content.ReadAsStringAsync();

                    if (!authResponseMessage.IsSuccessStatusCode)
                    {
                        var statusInt = (int)authResponseMessage.StatusCode;
                        ServerValidationMessage =
                            $"Auth server responded with HTTP {statusInt} {authResponseMessage.ReasonPhrase}.";
                        ServerValidationIsError = true;

                        _settings.SubscriptionLastStatusOk = false;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        return;
                    }

                    AuthResponseDto? authResponse = null;
                    try
                    {
                        authResponse = JsonSerializer.Deserialize<AuthResponseDto>(
                            responseBody,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                    }

                    if (authResponse == null)
                    {
                        ServerValidationMessage = "Auth server returned an unexpected response.";
                        ServerValidationIsError = true;

                        _settings.SubscriptionLastStatusOk = false;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        return;
                    }

                    if (!authResponse.Ok)
                    {
                        var code = authResponse.Error ?? "unknown_error";
                        var msg = authResponse.Message ?? "Account validation failed.";
                        ServerValidationMessage =
                            $"Account validation failed ({code}): {msg}";
                        ServerValidationIsError = true;

                        _settings.SubscriptionLastStatusOk = false;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        return;
                    }

                    var sub = authResponse.Subscription;
                    if (sub == null || !sub.Active)
                    {
                        var subStatus = sub?.Status ?? "unknown";
                        ServerValidationMessage =
                            $"Account validated but subscription is not active (status: {subStatus}).";
                        ServerValidationIsError = true;

                        _settings.SubscriptionLastStatusOk = false;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        return;
                    }

                    // Plan name and price text from the API.
                    var planLabelRaw = sub.LevelLabel ?? sub.Level ?? string.Empty;
                    var periodEndRaw = sub.PeriodEndAt ?? sub.TrialEndsAt ?? string.Empty;
                    var statusRaw = sub.Status ?? string.Empty;
                    var priceIdRaw = sub.PriceId ?? string.Empty;

                    var planLabel = planLabelRaw.Trim();
                    var priceText = priceIdRaw.Trim();
                    var statusText = statusRaw.Trim().ToLowerInvariant();
                    var periodEnd = periodEndRaw.Trim();

                    string formattedDate = string.Empty;
                    if (!string.IsNullOrEmpty(periodEnd))
                    {
                        if (DateTime.TryParse(
                                periodEnd,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind,
                                out var dt)
                            || DateTime.TryParse(periodEnd, out dt))
                        {
                            if (dt.Kind == DateTimeKind.Unspecified)
                                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

                            formattedDate = dt.ToLocalTime().ToString("yyyy-MM-dd");
                        }
                    }

                    string dateLabel = statusText == "trialing"
                        ? "Trial end date:"
                        : "Renewal:";

                    string planSummary;

                    // Prefer: Plan + price + date, then back off as fields are missing.
                    if (!string.IsNullOrEmpty(planLabel) &&
                        !string.IsNullOrEmpty(priceText) &&
                        !string.IsNullOrEmpty(formattedDate))
                    {
                        planSummary = $"Plan: {planLabel} - {priceText} - {dateLabel} {formattedDate}";
                    }
                    else if (!string.IsNullOrEmpty(planLabel) &&
                             !string.IsNullOrEmpty(priceText))
                    {
                        planSummary = $"Plan: {planLabel} - {priceText}";
                    }
                    else if (!string.IsNullOrEmpty(planLabel) &&
                             !string.IsNullOrEmpty(formattedDate))
                    {
                        planSummary = $"Plan: {planLabel} - {dateLabel} {formattedDate}";
                    }
                    else if (!string.IsNullOrEmpty(planLabel))
                    {
                        planSummary = $"Plan: {planLabel}";
                    }
                    else if (!string.IsNullOrEmpty(formattedDate))
                    {
                        planSummary = $"{dateLabel} {formattedDate}";
                    }
                    else
                    {
                        planSummary = string.Empty;
                    }

                    ServerValidationMessage = string.IsNullOrEmpty(planSummary)
                        ? "Joe's Scanner account validated."
                        : $"Joe's Scanner account validated. {planSummary}";

                    ServerValidationIsError = false;

                    var nowUtc = DateTime.UtcNow;
                    _settings.SubscriptionLastCheckUtc = nowUtc;
                    _settings.SubscriptionLastStatusOk = true;
                    _settings.SubscriptionPriceId = priceIdRaw;
                    _settings.SubscriptionLastLevel = planLabel;
                    _settings.SubscriptionRenewalUtc = null;
                    _settings.SubscriptionLastMessage = planSummary;

                    _showValidationPrefix = true;
                    UpdateSubscriptionSummaryFromSettings();
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, url);

                    if (!string.IsNullOrWhiteSpace(BasicAuthUsername))
                    {
                        var raw = $"{BasicAuthUsername}:{BasicAuthPassword ?? string.Empty}";
                        var bytes = Encoding.ASCII.GetBytes(raw);
                        var base64 = Convert.ToBase64String(bytes);

                        request.Headers.Authorization =
                            new AuthenticationHeaderValue("Basic", base64);
                    }

                    using var serverResponse = await _httpClient.SendAsync(request);

                    var statusCode = serverResponse.StatusCode;
                    var statusInt = (int)statusCode;

                    if (serverResponse.IsSuccessStatusCode
                        || statusCode == HttpStatusCode.NotImplemented)
                    {
                        if (statusCode == HttpStatusCode.NotImplemented)
                        {
                            ServerValidationMessage = "Server reachable. Connection looks good.";
                        }
                        else
                        {
                            ServerValidationMessage =
                                $"Server reachable (HTTP {statusInt} {serverResponse.ReasonPhrase}).";
                        }

                        ServerValidationIsError = false;
                    }
                    else if (statusCode == HttpStatusCode.Unauthorized
                             || statusCode == HttpStatusCode.Forbidden)
                    {
                        ServerValidationMessage =
                            $"Authentication failed (HTTP {statusInt} {serverResponse.ReasonPhrase}). " +
                            "Check basic auth username and password and that the server or firewall is configured to allow this client.";
                        ServerValidationIsError = true;
                    }
                    else
                    {
                        ServerValidationMessage =
                            $"Server responded with HTTP {statusInt} {serverResponse.ReasonPhrase}.";
                        ServerValidationIsError = true;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                ServerValidationMessage = $"Could not reach server: {ex.Message}";
                ServerValidationIsError = true;
            }
            catch (TaskCanceledException)
            {
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

        // Used by SettingsPage.xaml.cs when closing the page
        // without saving. Resets the connection fields to match
        // the last saved values.
        public void DiscardConnectionChanges()
        {
            ServerUrl = _savedServerUrl;
            UseDefaultConnection = _savedUseDefaultConnection;
            BasicAuthUsername = _savedBasicAuthUsername;
            BasicAuthPassword = _savedBasicAuthPassword;
            HasChanges = false;
        }

        private sealed class AuthResponseDto
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("subscription")]
            public AuthSubscriptionDto? Subscription { get; set; }
        }

        private sealed class AuthSubscriptionDto
        {
            [JsonPropertyName("active")]
            public bool Active { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }

            // Plan name from the API ("Subscription", "Subscription Annual", etc.)
            [JsonPropertyName("level")]
            public string? Level { get; set; }

            // Optional extra label if the API ever provides it.
            [JsonPropertyName("level_label")]
            public string? LevelLabel { get; set; }

            // Human readable price text ("$6 - every month").
            [JsonPropertyName("price_id")]
            public string? PriceId { get; set; }

            // Keep these as strings; we already parse them manually.
            [JsonPropertyName("period_end_at")]
            public string? PeriodEndAt { get; set; }

            [JsonPropertyName("trial_ends_at")]
            public string? TrialEndsAt { get; set; }
        }

    }
}
