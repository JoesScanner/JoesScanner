using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

                // ShowValidationHeader depends on this
                OnPropertyChanged(nameof(ShowValidationHeader));
            }
        }

        // True only immediately after a successful validation in this app session.
        // On a fresh app launch it is false so the header shows only the plan info.
        private bool _showValidationPrefix;

        // Tracks whether the last validation attempt was Joe's hosted server account validation.
        // Used to decide what short success text to show in the top header.
        private bool _lastValidationWasAccountValidation;

        // Shown in the page header to the left of the Close and Log buttons.
        // Only appears after a successful validation in this app session.
        public bool ShowValidationHeader => _showValidationPrefix && ShowSubscriptionSummary;

        // Header status text controls.
        // Success shows only a short confirmation message.
        // Failure shows the detailed error text.
        public bool ShowServerValidationProgressHeader => IsValidatingServer;

        public bool ShowServerValidationSuccessHeader =>
            HasServerValidationResult && !IsValidatingServer && !ServerValidationIsError;

        public bool ShowServerValidationErrorHeader =>
            HasServerValidationResult && !IsValidatingServer && ServerValidationIsError;

        // Short success message for the top header.
        // Never includes plan details.
        public string ValidationSuccessHeaderText =>
            _lastValidationWasAccountValidation ? "Joe's Scanner account validated." : "Server validated.";

        private void SetShowValidationPrefix(bool value)
        {
            if (_showValidationPrefix == value)
                return;

            _showValidationPrefix = value;
            OnPropertyChanged(nameof(ShowValidationHeader));
        }

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

        // Call display settings

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

            // Validate always saves first, then validates.
            ValidateServerCommand = new Command(async () => await SaveThenValidateServerUrlAsync());

            ToggleMuteFilterCommand = new Command<FilterRule>(OnToggleMuteFilter);
            ToggleDisableFilterCommand = new Command<FilterRule>(OnToggleDisableFilter);
            ClearFilterCommand = new Command<FilterRule>(OnClearFilter);
        }

        // True when any setting on this page differs from what was last saved.
        public bool HasUnsavedSettings => HasChanges;

        // True when there are unsaved connection or credential changes.
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
                OnPropertyChanged(nameof(ConnectionNeedsValidation));

                // While connection is dirty, do not show stale validated header info.
                if (_hasChanges)
                {
                    ClearValidationUiForDirtyConnection();
                }

                UpdateSubscriptionSummaryFromSettings();
            }
        }

        public bool ConnectionNeedsValidation => HasChanges;

        public bool IsValidatingServer
        {
            get => _isValidatingServer;
            private set
            {
                if (_isValidatingServer == value)
                    return;

                _isValidatingServer = value;
                OnPropertyChanged();

                // Header state depends on this
                OnPropertyChanged(nameof(ShowServerValidationProgressHeader));
                OnPropertyChanged(nameof(ShowServerValidationSuccessHeader));
                OnPropertyChanged(nameof(ShowServerValidationErrorHeader));
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

                // Header state depends on this
                OnPropertyChanged(nameof(ShowServerValidationSuccessHeader));
                OnPropertyChanged(nameof(ShowServerValidationErrorHeader));
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

                // Header state depends on this
                OnPropertyChanged(nameof(ShowServerValidationSuccessHeader));
                OnPropertyChanged(nameof(ShowServerValidationErrorHeader));
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

        public bool IsBasicAuthPasswordHidden => !_isBasicAuthPasswordVisible;

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

        private void InitializeFromSettings()
        {
            _serverUrl = _settings.ServerUrl ?? string.Empty;
            _savedServerUrl = _serverUrl;

            _basicAuthUsername = _settings.BasicAuthUsername ?? string.Empty;
            _basicAuthPassword = _settings.BasicAuthPassword ?? string.Empty;
            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;

            var defaultUrl = DefaultServerUrl;
            _useDefaultConnection = string.Equals(_serverUrl, defaultUrl, StringComparison.OrdinalIgnoreCase);
            _savedUseDefaultConnection = _useDefaultConnection;


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

                    if (!trimmed.Contains('|'))
                        continue;

                    _disabledKeys.Add(trimmed);
                }
            }

            _savedServerUrl = _settings.ServerUrl ?? string.Empty;
            _savedUseDefaultConnection = UseDefaultConnection;
            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;

            _showValidationPrefix = false;
            _lastValidationWasAccountValidation = false;
            HasChanges = false;

            OnPropertyChanged(nameof(HasUnsavedSettings));
            OnPropertyChanged(nameof(ConnectionNeedsValidation));
            OnPropertyChanged(nameof(ValidationSuccessHeaderText));

            UpdateSubscriptionSummaryFromSettings();
        }

        private void ClearValidationUiForDirtyConnection()
        {
            HasServerValidationResult = false;
            ServerValidationMessage = string.Empty;
            ServerValidationIsError = false;

            SetShowValidationPrefix(false);

            ShowSubscriptionSummary = false;
            SubscriptionSummary = string.Empty;

            _lastValidationWasAccountValidation = false;
            OnPropertyChanged(nameof(ValidationSuccessHeaderText));
        }

        private void UpdateSubscriptionSummaryFromSettings()
        {
            if (HasChanges)
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

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

            if (!_settings.SubscriptionLastStatusOk)
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

            var planSummary = _settings.SubscriptionLastMessage ?? string.Empty;
            planSummary = planSummary.Trim();

            if (string.IsNullOrEmpty(planSummary))
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

            var priceText = _settings.SubscriptionPriceId ?? string.Empty;
            priceText = priceText.Trim();

            if (!string.IsNullOrEmpty(priceText))
            {
                var looksLikeFriendlyPrice =
                    priceText.Contains(" ", StringComparison.Ordinal) ||
                    priceText.Contains("$", StringComparison.Ordinal);

                if (looksLikeFriendlyPrice &&
                    !planSummary.Contains(priceText, StringComparison.Ordinal))
                {
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
                        var before = planSummary.Substring(0, idxSplit);
                        var after = planSummary.Substring(idxSplit);
                        planSummary = $"{before} - {priceText}{after}";
                    }
                    else
                    {
                        planSummary = $"{planSummary} - {priceText}";
                    }
                }
            }

            planSummary = planSummary
                .Replace(" - Trial end date:", Environment.NewLine + "Trial end date:")
                .Replace(" - Renewal date:", Environment.NewLine + "Renewal date:")
                .Replace(" - Renewal:", Environment.NewLine + "Renewal:");

            SubscriptionSummary = planSummary;
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

        private void SaveSettings()
        {
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

            _settings.BasicAuthUsername = BasicAuthUsername;
            _settings.BasicAuthPassword = BasicAuthPassword;




            _settings.ThemeMode = ThemeMode;
            ApplyTheme(ThemeMode);

            _savedServerUrl = _settings.ServerUrl ?? string.Empty;
            _savedUseDefaultConnection = UseDefaultConnection;
            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;

            HasChanges = false;

            OnPropertyChanged(nameof(HasUnsavedSettings));
            OnPropertyChanged(nameof(ConnectionNeedsValidation));

            UpdateSubscriptionSummaryFromSettings();
        }

        private void ResetServerUrl()
        {
            ServerUrl = DefaultServerUrl;
            UseDefaultConnection = true;
        }

        private async Task SaveThenValidateServerUrlAsync()
        {
            SaveSettings();
            await ValidateServerUrlAsync();
        }

        private async Task ValidateServerUrlAsync()
        {
            var url = ServerUrl?.Trim();

            if (!string.IsNullOrEmpty(url))
            {
                var isDefaultServerForHeader =
                    string.Equals(url, DefaultServerUrl, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(url.TrimEnd('/'), DefaultServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

                _lastValidationWasAccountValidation = isDefaultServerForHeader;
                OnPropertyChanged(nameof(ValidationSuccessHeaderText));
            }
            else
            {
                _lastValidationWasAccountValidation = false;
                OnPropertyChanged(nameof(ValidationSuccessHeaderText));
            }

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
                            "Scanner account username and password are not configured. Enter them in the connection box first.";
                        ServerValidationIsError = true;

                        _settings.SubscriptionLastStatusOk = false;
                        _settings.AuthSessionToken = string.Empty;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        UpdateSubscriptionSummaryFromSettings();
                        return;
                    }

                    var authUrl = "https://joesscanner.com/wp-json/joes-scanner/v1/auth";

                    var appVersion = AppInfo.Current.VersionString ?? string.Empty;
                    var appBuild = AppInfo.Current.BuildString ?? string.Empty;

                    var platform = DeviceInfo.Platform.ToString();
                    var type = DeviceInfo.Idiom.ToString();
                    var model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
                    var osVersion = DeviceInfo.VersionString ?? string.Empty;

                    var payload = new
                    {
                        username = accountUsername,
                        password = accountPassword,

                        client = "JoesScannerApp",
                        version = appVersion,

                        device_platform = platform,
                        device_type = type,
                        device_model = model,
                        app_version = appVersion,
                        app_build = appBuild,
                        os_version = osVersion,
                        device_id = _settings.DeviceInstallId,

                        session_token = _settings.AuthSessionToken
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
                        _settings.AuthSessionToken = string.Empty;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        UpdateSubscriptionSummaryFromSettings();
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
                        _settings.AuthSessionToken = string.Empty;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        UpdateSubscriptionSummaryFromSettings();
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
                        _settings.AuthSessionToken = string.Empty;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        UpdateSubscriptionSummaryFromSettings();
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
                        _settings.AuthSessionToken = string.Empty;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        UpdateSubscriptionSummaryFromSettings();
                        return;
                    }

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

                    // IMPORTANT: top header must not show plan details.
                    ServerValidationMessage = "Joe's Scanner account validated.";
                    ServerValidationIsError = false;

                    var nowUtc = DateTime.UtcNow;
                    _settings.SubscriptionLastCheckUtc = nowUtc;
                    _settings.SubscriptionLastStatusOk = true;
                    _settings.SubscriptionPriceId = priceIdRaw;
                    _settings.SubscriptionLastLevel = planLabel;
                    _settings.SubscriptionRenewalUtc = null;

                    // Plan details are stored for the connection box summary only.
                    _settings.SubscriptionLastMessage = planSummary;

                    _settings.AuthSessionToken = authResponse.SessionToken ?? string.Empty;
                    await TrySendSessionPingAsync(authResponse.SessionToken);

                    SetShowValidationPrefix(true);
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

        public void DiscardConnectionChanges()
        {
            ServerUrl = _savedServerUrl;
            UseDefaultConnection = _savedUseDefaultConnection;
            BasicAuthUsername = _savedBasicAuthUsername;
            BasicAuthPassword = _savedBasicAuthPassword;
            HasChanges = false;
        }

        private static string CombineDeviceModel(string manufacturer, string model)
        {
            var mfg = (manufacturer ?? string.Empty).Trim();
            var mdl = (model ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(mfg))
                return mdl;

            if (string.IsNullOrWhiteSpace(mdl))
                return mfg;

            return mfg + " " + mdl;
        }

        private async Task TrySendSessionPingAsync(string? sessionToken)
        {
            sessionToken = (sessionToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sessionToken))
                return;

            try
            {
                var pingUrl = "https://joesscanner.com/wp-json/joes-scanner/v1/ping";

                var appVersion = AppInfo.Current.VersionString ?? string.Empty;
                var appBuild = AppInfo.Current.BuildString ?? string.Empty;

                var platform = DeviceInfo.Platform.ToString();
                var type = DeviceInfo.Idiom.ToString();
                var model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
                var osVersion = DeviceInfo.VersionString ?? string.Empty;

                var payload = new
                {
                    session_token = sessionToken,
                    device_id = _settings.DeviceInstallId,

                    device_platform = platform,
                    device_type = type,
                    device_model = model,

                    app_version = appVersion,
                    app_build = appBuild,
                    os_version = osVersion
                };

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _httpClient.PostAsync(pingUrl, content);

                _ = response.IsSuccessStatusCode;
            }
            catch
            {
            }
        }

        class AuthResponseDto
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("session_token")]
            public string? SessionToken { get; set; }

            [JsonPropertyName("subscription")]
            public AuthSubscriptionDto? Subscription { get; set; }
        }

        private sealed class AuthSubscriptionDto
        {
            [JsonPropertyName("active")]
            public bool Active { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }

            [JsonPropertyName("level")]
            public string? Level { get; set; }

            [JsonPropertyName("level_label")]
            public string? LevelLabel { get; set; }

            [JsonPropertyName("price_id")]
            public string? PriceId { get; set; }

            [JsonPropertyName("period_end_at")]
            public string? PeriodEndAt { get; set; }

            [JsonPropertyName("trial_ends_at")]
            public string? TrialEndsAt { get; set; }
        }
    }
}
