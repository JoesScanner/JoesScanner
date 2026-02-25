using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.ApplicationModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JoesScanner.ViewModels
{
    // View model for the Settings page.
    // Controls connection settings, call list behavior, theme,
    // and the unified filter list that is populated from live calls.
    public class SettingsViewModel : BindableObject
    {
        private readonly ISettingsService _settings;
        private readonly MainViewModel _mainViewModel;
        private readonly ITelemetryService _telemetryService;
        private readonly HttpClient _httpClient;
        private readonly FilterService _filterService = FilterService.Instance;

        private readonly IFilterProfileStore _filterProfileStore;

        private readonly ObservableCollection<FilterProfile> _settingsFilterProfiles = new();
        private readonly ObservableCollection<string> _settingsFilterProfileNameOptions = new();
        private int _pageOpenInProgress;

        private string _selectedSettingsFilterProfileNameOption = NoneSettingsProfileNameOption;
        private bool _isCustomSettingsFilterProfileName;
        public ObservableCollection<FilterProfile> SettingsFilterProfiles => _settingsFilterProfiles;


        public ObservableCollection<string> SettingsFilterProfileNameOptions => _settingsFilterProfileNameOptions;

        public string SelectedSettingsFilterProfileNameOption
        {
            get => _selectedSettingsFilterProfileNameOption;
            set
            {
                var newValue = string.IsNullOrWhiteSpace(value) ? NoneSettingsProfileNameOption : value;
                if (string.Equals(_selectedSettingsFilterProfileNameOption, newValue, StringComparison.Ordinal))
                    return;

                _selectedSettingsFilterProfileNameOption = newValue;

                if (string.Equals(newValue, NoneSettingsProfileNameOption, StringComparison.Ordinal))
                {
                    _isCustomSettingsFilterProfileName = false;
                    SettingsFilterProfileNameDraft = string.Empty;
                    _ = SelectSettingsFilterProfileAsync(null, apply: false);
                }
                else
                {
                    _isCustomSettingsFilterProfileName = false;
                    SettingsFilterProfileNameDraft = newValue;
                    TrySelectSettingsFilterProfileFromNameOption(newValue);
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomSettingsFilterProfileName));
            }
        }

        public bool IsCustomSettingsFilterProfileName => _isCustomSettingsFilterProfileName;

        private FilterProfile? _selectedSettingsFilterProfile;

        private string _settingsFilterProfileNameDraft = string.Empty;
        public FilterProfile? SelectedSettingsFilterProfile
        {
            get => _selectedSettingsFilterProfile;
            private set
            {
                if (ReferenceEquals(_selectedSettingsFilterProfile, value))
                    return;

                _selectedSettingsFilterProfile = value;
                SettingsFilterProfileNameDraft = _selectedSettingsFilterProfile?.Name ?? string.Empty;
                SyncSettingsProfileNameDropdownFromDraft();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedSettingsFilterProfileDisplay));
            }
        }

        public string SettingsFilterProfileNameDraft
        {
            get => _settingsFilterProfileNameDraft;
            set
            {
                var newValue = value ?? string.Empty;
                if (string.Equals(_settingsFilterProfileNameDraft, newValue, StringComparison.Ordinal))
                    return;

                _settingsFilterProfileNameDraft = newValue;
                OnPropertyChanged();
            }
        }

        public string SelectedSettingsFilterProfileDisplay =>
            SelectedSettingsFilterProfile?.Name ?? string.Empty;
        private const string NoneSettingsProfileNameOption = "";

        private void RefreshSettingsProfileNameOptions()
        {
            _settingsFilterProfileNameOptions.Clear();
            _settingsFilterProfileNameOptions.Add(NoneSettingsProfileNameOption);
            foreach (var name in _settingsFilterProfiles.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))
                _settingsFilterProfileNameOptions.Add(name);
SyncSettingsProfileNameDropdownFromDraft();
            OnPropertyChanged(nameof(SettingsFilterProfileNameOptions));
        }

        private void SyncSettingsProfileNameDropdownFromDraft()
        {
            var draft = (SettingsFilterProfileNameDraft ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(draft))
            {
                _selectedSettingsFilterProfileNameOption = NoneSettingsProfileNameOption;
                _isCustomSettingsFilterProfileName = false;
                OnPropertyChanged(nameof(SelectedSettingsFilterProfileNameOption));
                OnPropertyChanged(nameof(IsCustomSettingsFilterProfileName));
                return;
            }

            if (!string.IsNullOrWhiteSpace(draft) && _settingsFilterProfileNameOptions.Any(n => string.Equals(n, draft, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedSettingsFilterProfileNameOption = _settingsFilterProfileNameOptions.First(n => string.Equals(n, draft, StringComparison.OrdinalIgnoreCase));
                _isCustomSettingsFilterProfileName = false;
            }
            else
            {
                _selectedSettingsFilterProfileNameOption = NoneSettingsProfileNameOption;
                _isCustomSettingsFilterProfileName = true;
            }

            OnPropertyChanged(nameof(SelectedSettingsFilterProfileNameOption));
            OnPropertyChanged(nameof(IsCustomSettingsFilterProfileName));
        }

        private void TrySelectSettingsFilterProfileFromNameOption(string nameOption)
        {
            if (string.IsNullOrWhiteSpace(nameOption))
                return;

            if (string.Equals(nameOption, NoneSettingsProfileNameOption, StringComparison.Ordinal))
            {
                return;
            }

            var profile = _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Name, nameOption, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
                _ = SelectSettingsFilterProfileAsync(profile, apply: true);
        }

        public ObservableCollection<FilterRule> FilterRules => _filterService.Rules;

        // Connection fields
        private string _serverUrl = string.Empty;
        private string _authServerBaseUrl = string.Empty;
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
        private string _savedAuthServerBaseUrl = string.Empty;
        private bool _savedUseDefaultConnection;
        private bool _hasChanges;

                private bool _autoPlay;
        private bool _savedAutoPlay;

// Windows-only startup behavior (only acted on in WINDOWS builds).
        private bool _windowsAutoConnectOnStart;
        private bool _savedWindowsAutoConnectOnStart;

        private bool _windowsStartWithWindows;
        private bool _savedWindowsStartWithWindows;

        private bool _mobileAutoConnectOnStart;
        private bool _savedMobileAutoConnectOnStart;

        // Saved snapshot for settings that are only committed on Save

        // Call display settings

        // Theme as a single string: "System", "Light", "Dark"
        private string _themeMode = "System";

        // Bluetooth label mapping
        private string _bluetoothLabelArtistToken = BluetoothLabelMapping.TokenAppName;
        private string _bluetoothLabelTitleToken = BluetoothLabelMapping.TokenTranscription;
        private string _bluetoothLabelAlbumToken = BluetoothLabelMapping.TokenTalkgroup;
        private string _bluetoothLabelComposerToken = BluetoothLabelMapping.TokenSite;
        private string _bluetoothLabelGenreToken = BluetoothLabelMapping.TokenReceiver;

        private string _savedBluetoothLabelArtistToken = BluetoothLabelMapping.TokenAppName;
        private string _savedBluetoothLabelTitleToken = BluetoothLabelMapping.TokenTranscription;
        private string _savedBluetoothLabelAlbumToken = BluetoothLabelMapping.TokenTalkgroup;
        private string _savedBluetoothLabelComposerToken = BluetoothLabelMapping.TokenSite;
        private string _savedBluetoothLabelGenreToken = BluetoothLabelMapping.TokenReceiver;

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

        public SettingsViewModel(ISettingsService settingsService, MainViewModel mainViewModel, ITelemetryService telemetryService, IFilterProfileStore filterProfileStore)
        {
            _settings = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

            _filterProfileStore = filterProfileStore ?? throw new ArgumentNullException(nameof(filterProfileStore));

            // IMPORTANT (Apple + HTTP): iOS/macOS native networking enforces App Transport Security (ATS).
            // We support user-configured HTTP custom servers, so for validation requests we force the
            // managed HTTP stack on Apple to avoid ATS blocks in the native handler.
#if IOS || MACCATALYST
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
#else
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
#endif

            // Seed the profile name options immediately so the Settings profile picker never renders empty
            // while profiles are still loading.
            _settingsFilterProfileNameOptions.Clear();
            _settingsFilterProfileNameOptions.Add(NoneSettingsProfileNameOption);
            _selectedSettingsFilterProfileNameOption = NoneSettingsProfileNameOption;
            _isCustomSettingsFilterProfileName = false;

            OnPropertyChanged(nameof(SettingsFilterProfileNameOptions));
            OnPropertyChanged(nameof(SelectedSettingsFilterProfileNameOption));
            OnPropertyChanged(nameof(IsCustomSettingsFilterProfileName));

            Current = this;

            ToggleBasicAuthPasswordVisibilityCommand =
                new Command(() => IsBasicAuthPasswordVisible = !IsBasicAuthPasswordVisible);

            InitializeFromSettings();

            SaveCommand = new Command(async () => await SaveSettingsAsync());
            ResetServerCommand = new Command(ResetServerUrl);

            // Validate always saves first, then validates.
            ValidateServerCommand = new Command(async () => await SaveThenValidateServerUrlAsync());

            ToggleMuteFilterCommand = new Command<FilterRule>(OnToggleMuteFilter);
            ToggleDisableFilterCommand = new Command<FilterRule>(OnToggleDisableFilter);
            ClearFilterCommand = new Command<FilterRule>(OnClearFilter);
        }


        public async Task OnPageOpenedAsync()
        {
            if (Interlocked.Exchange(ref _pageOpenInProgress, 1) == 1)
                return;

            try
            {
                await LoadSettingsFilterProfilesAsync(applySelectedProfile: true);
            }
            catch (Exception ex)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"SettingsViewModel.OnPageOpenedAsync failed: {ex}");
                }
                catch
                {
                }
            }
            finally
            {
                Interlocked.Exchange(ref _pageOpenInProgress, 0);
            }
        }

        public async Task LoadSettingsFilterProfilesAsync(bool applySelectedProfile)
        {
            // NOTE:
            // On iOS (especially when autoplay/monitoring is active), doing synchronous file IO and JSON parsing
            // on the UI thread during navigation can cause hangs or watchdog kills. The underlying store is sync,
            // even though it exposes an async signature (Task.FromResult).
            //
            // Run the store call on a background thread, then marshal collection updates back to the UI thread.
            var profiles = await Task.Run(async () => await _filterProfileStore.GetProfilesAsync(CancellationToken.None));

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _settingsFilterProfiles.Clear();
                foreach (var p in profiles)
                    _settingsFilterProfiles.Add(p);

                RefreshSettingsProfileNameOptions();

                var selectedId = AppStateStore.GetString("settings_selected_filter_profile_id", string.Empty);
                if (string.IsNullOrWhiteSpace(selectedId))
                {
                    SelectedSettingsFilterProfile = null;
                    return;
                }

                var selected = _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.Ordinal));
                SelectedSettingsFilterProfile = selected;

                if (applySelectedProfile && selected != null)
                    ApplySettingsProfile(selected);
            });
        }

        public async Task SelectSettingsFilterProfileAsync(FilterProfile? profile, bool apply)
        {
            SelectedSettingsFilterProfile = profile;
            AppStateStore.SetString("settings_selected_filter_profile_id", profile?.Id ?? string.Empty);

            if (apply && profile != null)
                ApplySettingsProfile(profile);
        }

        public async Task<FilterProfile?> SaveCurrentSettingsFiltersAsync(string name)
        {
            name = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var existing = _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var profileId = existing?.Id;

            var existingFilters = existing?.Filters ?? new FilterProfileFilters();
            var records = _filterService.GetActiveStateRecords();

            var profile = new FilterProfile
            {
                Id = profileId ?? string.Empty,
                Name = name,
                Filters = existingFilters,
                Rules = records
            };

            await _filterProfileStore.SaveOrUpdateAsync(profile, CancellationToken.None);
            await LoadSettingsFilterProfilesAsync(applySelectedProfile: false);

            var saved = _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (saved != null)
                await SelectSettingsFilterProfileAsync(saved, apply: false);

            return saved;
        }

        public async Task<bool> RenameSelectedSettingsProfileAsync(string newName)
        {
            var current = SelectedSettingsFilterProfile;
            if (current == null)
                return false;

            newName = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName))
                return false;

            await _filterProfileStore.RenameAsync(current.Id, newName, CancellationToken.None);
            await LoadSettingsFilterProfilesAsync(applySelectedProfile: false);

            var refreshed = _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Id, current.Id, StringComparison.Ordinal));
            if (refreshed != null)
                await SelectSettingsFilterProfileAsync(refreshed, apply: false);

            return true;
        }

        public async Task<bool> DeleteSelectedSettingsProfileAsync()
        {
            var current = SelectedSettingsFilterProfile;
            if (current == null)
                return false;

            await _filterProfileStore.DeleteAsync(current.Id, CancellationToken.None);
            AppStateStore.SetString("settings_selected_filter_profile_id", string.Empty);
            SelectedSettingsFilterProfile = null;

            await LoadSettingsFilterProfilesAsync(applySelectedProfile: false);
            return true;
        }

        private void ApplySettingsProfile(FilterProfile profile)
        {
            if (profile == null)
                return;

            var records = profile.Rules ?? new List<FilterRuleStateRecord>();
            _filterService.ApplyStateRecords(records, resetOthers: true);
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



                // Changing the server URL must clear user/pass in the UI immediately.
                // If this URL was used before, pull cached credentials for convenience.
                var normalized = (newValue ?? string.Empty).Trim();

                _basicAuthUsername = string.Empty;
                _basicAuthPassword = string.Empty;

                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    try
                    {
                        if (_settings.TryGetServerCredentials(normalized, out var cachedUser, out var cachedPass))
                        {
                            _basicAuthUsername = cachedUser ?? string.Empty;
                            _basicAuthPassword = cachedPass ?? string.Empty;
                        }
                    }
                    catch
                    {
                    }
                }

                OnPropertyChanged(nameof(BasicAuthUsername));
                OnPropertyChanged(nameof(BasicAuthPassword));

                // Server change invalidates any prior validation badge until Validate is pressed.
                SetShowValidationPrefix(false);

                UpdateHasChanges();
            }
        }


        public string AuthServerBaseUrl
        {
            get => _authServerBaseUrl;
            set
            {
                var newValue = value ?? string.Empty;
                if (string.Equals(_authServerBaseUrl, newValue, StringComparison.Ordinal))
                    return;

                _authServerBaseUrl = newValue;
                OnPropertyChanged();
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


        public bool AutoPlay
        {
            get => _autoPlay;
            set
            {
                if (_autoPlay == value)
                    return;

                _autoPlay = value;
                OnPropertyChanged();

                // Persist immediately so app start can honor it without requiring Save.
                _settings.AutoPlay = value;
                _savedAutoPlay = value;
                UpdateHasChanges();
            }
        }

public bool WindowsAutoConnectOnStart
        {
            get => _windowsAutoConnectOnStart;
            set
            {
                if (_windowsAutoConnectOnStart == value)
                    return;

                _windowsAutoConnectOnStart = value;
                OnPropertyChanged();

                // Autosave Windows-only behavior immediately.
                // This setting should not require manual Save or server validation.
                _settings.WindowsAutoConnectOnStart = value;
                _savedWindowsAutoConnectOnStart = value;

                UpdateHasChanges();
            }
        }


        public bool MobileAutoConnectOnStart
        {
            get => _mobileAutoConnectOnStart;
            set
            {
                if (_mobileAutoConnectOnStart == value)
                    return;

                _mobileAutoConnectOnStart = value;
                OnPropertyChanged();

                // Autosave mobile/mac behavior immediately.
                _settings.MobileAutoConnectOnStart = value;
                _savedMobileAutoConnectOnStart = value;

                UpdateHasChanges();
            }
        }

public bool WindowsStartWithWindows
        {
            get => _windowsStartWithWindows;
            set
            {
                if (_windowsStartWithWindows == value)
                    return;

                _windowsStartWithWindows = value;
                OnPropertyChanged();

                // Autosave and apply Windows startup registration immediately.
                // Best effort only. If this fails due to policy restrictions,
                // keep the toggle state saved and allow the user to try again.
                _settings.WindowsStartWithWindows = value;
                _savedWindowsStartWithWindows = value;

                UpdateHasChanges();
            }
        }

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

        public IReadOnlyList<BluetoothLabelMapping.Option> BluetoothLabelOptions =>
            BluetoothLabelMapping.Options;


        private BluetoothLabelMapping.Option GetBluetoothOptionOrDefault(string token, string defaultToken)
        {
            var key = string.IsNullOrWhiteSpace(token) ? defaultToken : token;

            var opt = BluetoothLabelOptions.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));
            if (opt != null)
                return opt;

            // Fall back to default token if stored token is unknown (corrupt/old settings)
            opt = BluetoothLabelOptions.FirstOrDefault(o => string.Equals(o.Key, defaultToken, StringComparison.Ordinal));
            return opt ?? BluetoothLabelOptions.First();
        }


        private BluetoothLabelMapping.Option _bluetoothLabelArtistOption = BluetoothLabelMapping.Options.First();
        private BluetoothLabelMapping.Option _bluetoothLabelTitleOption = BluetoothLabelMapping.Options.First();
        private BluetoothLabelMapping.Option _bluetoothLabelAlbumOption = BluetoothLabelMapping.Options.First();
        private BluetoothLabelMapping.Option _bluetoothLabelComposerOption = BluetoothLabelMapping.Options.First();
        private BluetoothLabelMapping.Option _bluetoothLabelGenreOption = BluetoothLabelMapping.Options.First();

        public BluetoothLabelMapping.Option BluetoothLabelArtistOption
        {
            get => _bluetoothLabelArtistOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelArtistOption?.Key, value.Key, StringComparison.Ordinal))
                    return;

                _bluetoothLabelArtistOption = value;
                _bluetoothLabelArtistToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        public BluetoothLabelMapping.Option BluetoothLabelTitleOption
        {
            get => _bluetoothLabelTitleOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelTitleOption?.Key, value.Key, StringComparison.Ordinal))
                    return;

                _bluetoothLabelTitleOption = value;
                _bluetoothLabelTitleToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        public BluetoothLabelMapping.Option BluetoothLabelAlbumOption
        {
            get => _bluetoothLabelAlbumOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelAlbumOption?.Key, value.Key, StringComparison.Ordinal))
                    return;

                _bluetoothLabelAlbumOption = value;
                _bluetoothLabelAlbumToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        public BluetoothLabelMapping.Option BluetoothLabelComposerOption
        {
            get => _bluetoothLabelComposerOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelComposerOption?.Key, value.Key, StringComparison.Ordinal))
                    return;

                _bluetoothLabelComposerOption = value;
                _bluetoothLabelComposerToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        public BluetoothLabelMapping.Option BluetoothLabelGenreOption
        {
            get => _bluetoothLabelGenreOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelGenreOption?.Key, value.Key, StringComparison.Ordinal))
                    return;

                _bluetoothLabelGenreOption = value;
                _bluetoothLabelGenreToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
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
            // Load persisted settings into local view model fields.
            // This keeps the Settings UI in sync with the DB (single source of truth).
            _authServerBaseUrl = _settings.AuthServerBaseUrl ?? string.Empty;
            _serverUrl = _settings.ServerUrl ?? string.Empty;

            _basicAuthUsername = _settings.BasicAuthUsername ?? string.Empty;
            _basicAuthPassword = _settings.BasicAuthPassword ?? string.Empty;

            _autoPlay = _settings.AutoPlay;
            _savedAutoPlay = _autoPlay;

            _windowsAutoConnectOnStart = _settings.WindowsAutoConnectOnStart;
            _savedWindowsAutoConnectOnStart = _windowsAutoConnectOnStart;

            _mobileAutoConnectOnStart = _settings.MobileAutoConnectOnStart;
            _savedMobileAutoConnectOnStart = _mobileAutoConnectOnStart;

            _windowsStartWithWindows = _settings.WindowsStartWithWindows;
            _savedWindowsStartWithWindows = _windowsStartWithWindows;

            _bluetoothLabelArtistToken = _settings.BluetoothLabelArtist ?? string.Empty;
            _savedBluetoothLabelArtistToken = _bluetoothLabelArtistToken;

            _bluetoothLabelTitleToken = _settings.BluetoothLabelTitle ?? string.Empty;
            _savedBluetoothLabelTitleToken = _bluetoothLabelTitleToken;

            _bluetoothLabelAlbumToken = _settings.BluetoothLabelAlbum ?? string.Empty;
            _savedBluetoothLabelAlbumToken = _bluetoothLabelAlbumToken;

            _bluetoothLabelComposerToken = _settings.BluetoothLabelComposer ?? string.Empty;
            _savedBluetoothLabelComposerToken = _bluetoothLabelComposerToken;

            _bluetoothLabelGenreToken = _settings.BluetoothLabelGenre ?? string.Empty;
            _savedBluetoothLabelGenreToken = _bluetoothLabelGenreToken;

            // Snapshots used by HasUnsavedSettings comparisons.
            _savedAuthServerBaseUrl = _authServerBaseUrl;
            _savedUseDefaultConnection = UseDefaultConnection;

            HasChanges = false;

            OnPropertyChanged(nameof(AuthServerBaseUrl));
            OnPropertyChanged(nameof(ServerUrl));
            OnPropertyChanged(nameof(BasicAuthUsername));
            OnPropertyChanged(nameof(BasicAuthPassword));
            OnPropertyChanged(nameof(AutoPlay));

            OnPropertyChanged(nameof(WindowsAutoConnectOnStart));
            OnPropertyChanged(nameof(MobileAutoConnectOnStart));
            OnPropertyChanged(nameof(WindowsStartWithWindows));


            OnPropertyChanged(nameof(HasUnsavedSettings));
            OnPropertyChanged(nameof(ConnectionNeedsValidation));

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

        private async Task SaveSettingsAsync()
        {
            // IMPORTANT: Connection credentials and active server selection are persisted only by Validate.
            // Save is for non-connection settings.

            _settings.WindowsAutoConnectOnStart = WindowsAutoConnectOnStart;
            _settings.WindowsStartWithWindows = WindowsStartWithWindows;
            _settings.MobileAutoConnectOnStart = MobileAutoConnectOnStart;

#if WINDOWS
            try
            {
                // Best effort. If this fails (policy restrictions, permissions, etc.),
                // the setting remains saved and the user can toggle again.
                WindowsStartupManager.TrySetRunOnLogin(_settings.WindowsStartWithWindows);
            }
            catch
            {
            }
#endif

            _settings.ThemeMode = ThemeMode;
            ApplyTheme(ThemeMode);

            _settings.BluetoothLabelArtist = _bluetoothLabelArtistToken;
            _settings.BluetoothLabelTitle = _bluetoothLabelTitleToken;
            _settings.BluetoothLabelAlbum = _bluetoothLabelAlbumToken;
            _settings.BluetoothLabelComposer = _bluetoothLabelComposerToken;
            _settings.BluetoothLabelGenre = _bluetoothLabelGenreToken;

            _savedAuthServerBaseUrl = _settings.AuthServerBaseUrl ?? string.Empty;
            _savedUseDefaultConnection = UseDefaultConnection;

            _savedWindowsAutoConnectOnStart = _windowsAutoConnectOnStart;
            _savedWindowsStartWithWindows = _windowsStartWithWindows;
            _savedMobileAutoConnectOnStart = _mobileAutoConnectOnStart;

            _savedBluetoothLabelArtistToken = _bluetoothLabelArtistToken;
            _savedBluetoothLabelTitleToken = _bluetoothLabelTitleToken;
            _savedBluetoothLabelAlbumToken = _bluetoothLabelAlbumToken;
            _savedBluetoothLabelComposerToken = _bluetoothLabelComposerToken;
            _savedBluetoothLabelGenreToken = _bluetoothLabelGenreToken;

            HasChanges = false;

            OnPropertyChanged(nameof(HasUnsavedSettings));
            OnPropertyChanged(nameof(ConnectionNeedsValidation));

            UpdateSubscriptionSummaryFromSettings();
        }

        private void ResetServerUrl()
        {
            ServerUrl = DefaultServerUrl;
            UseDefaultConnection = true;

            BasicAuthUsername = string.Empty;
            BasicAuthPassword = string.Empty;
        }

        private async Task SaveThenValidateServerUrlAsync()
        {
            // If the user is changing the TR server URL, we must stop playback/monitoring
            // after the new server has been validated. The user will explicitly press Play
            // again from the queue when they are ready.
            var previousServerUrlSnapshot = _savedServerUrl ?? string.Empty;

            await SaveSettingsAsync();
            await ValidateServerUrlAsync();

            if (!ServerValidationIsError)
            {
                _savedServerUrl = _settings.ServerUrl ?? string.Empty;
                _savedBasicAuthUsername = _settings.BasicAuthUsername ?? string.Empty;
                _savedBasicAuthPassword = _settings.BasicAuthPassword ?? string.Empty;

                HasChanges = false;
                OnPropertyChanged(nameof(ConnectionNeedsValidation));
            }

            // Only stop when validation succeeded and the server actually changed.
            // Normalize by trimming whitespace and trailing slashes.
            if (!ServerValidationIsError)
            {
                var newServerUrl = _settings.ServerUrl ?? string.Empty;

                if (!string.Equals(
                        NormalizeUrlForCompare(previousServerUrlSnapshot),
                        NormalizeUrlForCompare(newServerUrl),
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _mainViewModel.StopMonitoringAsync();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string NormalizeUrlForCompare(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('/');
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
                var hasAuth = !string.IsNullOrWhiteSpace(BasicAuthUsername);
                AppLog.Add(() => $"Validate: url={url}, isDefault={isDefaultServer}, hasBasicAuth={hasAuth}");
            }
            catch
            {
            }


            try
            {
                if (isDefaultServer)
                {
                    var accountUsername = (BasicAuthUsername ?? string.Empty).Trim();
                    var accountPassword = (BasicAuthPassword ?? string.Empty).Trim();

                    if (string.IsNullOrWhiteSpace(accountUsername) || string.IsNullOrWhiteSpace(accountPassword))
                    {
                        // Hosted default server requires a valid Joe's Scanner account subscription.
                        // Do NOT allow a "validated" state without user-entered account credentials.
                        ServerValidationMessage =
                            "Enter your Joe's Scanner username and password, then tap Validate.";
                        ServerValidationIsError = true;

                        // Do not show the "validated" prefix in the header.
                        SetShowValidationPrefix(false);

                        _settings.SubscriptionLastStatusOk = false;
                        _settings.AuthSessionToken = string.Empty;
                        _settings.SubscriptionLastMessage = ServerValidationMessage;
                        _settings.SubscriptionLastCheckUtc = DateTime.UtcNow;

                        try { _settings.ClearServerCredentials(DefaultServerUrl); } catch { }

                        UpdateSubscriptionSummaryFromSettings();
                        IsValidatingServer = false;
                        return;
                    }

                    var authUrl = BuildAuthServerUrl("/wp-json/joes-scanner/v1/auth");

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

                        try { _settings.ClearServerCredentials(DefaultServerUrl); } catch { }

                        try { _settings.ClearServerCredentials(DefaultServerUrl); } catch { }

                        try { _settings.ClearServerCredentials(DefaultServerUrl); } catch { }

                        try { _settings.ClearServerCredentials(DefaultServerUrl); } catch { }

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
                    await _telemetryService.AdoptSessionTokenAsync(authResponse.SessionToken ?? string.Empty, "settings_validate_auth_success", CancellationToken.None);

                    

                    // Persist active server and credentials only after a successful account validation.
                    _settings.ServerUrl = DefaultServerUrl;
                    _settings.BasicAuthUsername = accountUsername;
                    _settings.BasicAuthPassword = accountPassword;
                    _settings.SetServerCredentials(DefaultServerUrl, accountUsername, accountPassword);
                    _settings.LastAuthUsername = accountUsername;

                    _mainViewModel.ServerUrl = DefaultServerUrl;

                    SetShowValidationPrefix(true);
                    UpdateSubscriptionSummaryFromSettings();
                }
                else
                {
                    var userProvidedAnyAuth =
                        !string.IsNullOrWhiteSpace(BasicAuthUsername) || !string.IsNullOrWhiteSpace(BasicAuthPassword);

                    async Task<HttpResponseMessage> SendHeadAsync(bool includeAuth)
                    {
                        var req = new HttpRequestMessage(HttpMethod.Head, url);

                        try
                        {
                            AppLog.Add(() => $"Validate: sending HEAD to {url} (auth={includeAuth})");
                        }
                        catch
                        {
                        }

                        if (includeAuth)
                        {
                            var u = (BasicAuthUsername ?? string.Empty).Trim();
                            var p = (BasicAuthPassword ?? string.Empty).Trim();

                            if (!string.IsNullOrWhiteSpace(u) || !string.IsNullOrWhiteSpace(p))
                            {
                                var raw = $"{u}:{p}";
                                var bytes = Encoding.ASCII.GetBytes(raw);
                                var base64 = Convert.ToBase64String(bytes);

                                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);
                            }
                        }

                        return await _httpClient.SendAsync(req);
                    }

                    // For custom servers, always try without basic auth first.
                    using var noAuthResponse = await SendHeadAsync(includeAuth: false);

                    try
                    {
                        AppLog.Add(() => $"Validate: response (no auth) HTTP {(int)noAuthResponse.StatusCode} {noAuthResponse.ReasonPhrase}");
                    }
                    catch
                    {
                    }

                    var statusCode = noAuthResponse.StatusCode;
                    var statusInt = (int)statusCode;

                    if (noAuthResponse.IsSuccessStatusCode || statusCode == HttpStatusCode.NotImplemented)
                    {
                        if (statusCode == HttpStatusCode.NotImplemented)
                        {
                            ServerValidationMessage = "Server reachable. Connection looks good.";
                        }
                        else
                        {
                            ServerValidationMessage =
                                $"Server reachable (HTTP {statusInt} {noAuthResponse.ReasonPhrase}).";
                        }

                        // If the server is reachable without auth, clear any user-entered credentials.
                        if (userProvidedAnyAuth)
                        {
                            BasicAuthUsername = string.Empty;
                            BasicAuthPassword = string.Empty;

                            try
                            {
                                await UiDialogs.AlertAsync(
                                    "Credentials cleared",
                                    "This server responded without requiring basic auth. Username and password were cleared.",
                                    "OK");
                            }
                            catch
                            {
                            }
                        }

                        ServerValidationIsError = false;

                        var finalUrl = url.Trim();
                        _settings.ServerUrl = finalUrl;

                        // Persist what we actually used (blank for no-auth success).
                        var effectiveUser = string.Empty;
                        var effectivePass = string.Empty;

                        _settings.BasicAuthUsername = effectiveUser;
                        _settings.BasicAuthPassword = effectivePass;
                        _settings.SetServerCredentials(finalUrl, effectiveUser, effectivePass);

                        _mainViewModel.ServerUrl = finalUrl;
                    }
                    else if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
                    {
                        // If auth is required and the user provided credentials, retry with basic auth.
                        if (!userProvidedAnyAuth)
                        {
                            ServerValidationMessage =
                                $"Authentication required (HTTP {statusInt} {noAuthResponse.ReasonPhrase}). " +
                                "Enter a basic auth username and password, then tap Validate.";
                            ServerValidationIsError = true;
                        }
                        else
                        {
                            // Retry with auth.
                            using var authResponse = await SendHeadAsync(includeAuth: true);

                            try
                            {
                                AppLog.Add(() => $"Validate: response (with auth) HTTP {(int)authResponse.StatusCode} {authResponse.ReasonPhrase}");
                            }
                            catch
                            {
                            }

                            var authStatus = authResponse.StatusCode;
                            var authStatusInt = (int)authStatus;

                            if (authResponse.IsSuccessStatusCode || authStatus == HttpStatusCode.NotImplemented)
                            {
                                if (authStatus == HttpStatusCode.NotImplemented)
                                {
                                    ServerValidationMessage = "Server reachable. Connection looks good.";
                                }
                                else
                                {
                                    ServerValidationMessage =
                                        $"Server reachable (HTTP {authStatusInt} {authResponse.ReasonPhrase}).";
                                }

                                ServerValidationIsError = false;

                                var finalUrl = url.Trim();
                                _settings.ServerUrl = finalUrl;

                                var effectiveUser = (BasicAuthUsername ?? string.Empty).Trim();
                                var effectivePass = (BasicAuthPassword ?? string.Empty).Trim();

                                _settings.BasicAuthUsername = effectiveUser;
                                _settings.BasicAuthPassword = effectivePass;
                                _settings.SetServerCredentials(finalUrl, effectiveUser, effectivePass);

                                _mainViewModel.ServerUrl = finalUrl;
                            }
                            else if (authStatus == HttpStatusCode.Unauthorized || authStatus == HttpStatusCode.Forbidden)
                            {
                                ServerValidationMessage =
                                    $"Authentication failed (HTTP {authStatusInt} {authResponse.ReasonPhrase}). " +
                                    "Check basic auth username and password and that the server or firewall is configured to allow this client.";
                                ServerValidationIsError = true;
                            }
                            else
                            {
                                ServerValidationMessage =
                                    $"Server responded with HTTP {authStatusInt} {authResponse.ReasonPhrase}.";
                                ServerValidationIsError = true;
                            }
                        }
                    }
                    else
                    {
                        ServerValidationMessage =
                            $"Server responded with HTTP {statusInt} {noAuthResponse.ReasonPhrase}.";
                        ServerValidationIsError = true;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                try
                {
                    AppLog.Add(() => $"Validate: HttpRequestException: {ex.Message}");
                    AppLog.Add(() => ex.ToString());
                    if (ex.InnerException != null)
                        AppLog.Add(() => $"Validate: Inner: {ex.InnerException}");
                }
                catch
                {
                }

                ServerValidationMessage = $"Could not reach server: {ex.Message}";
                ServerValidationIsError = true;
            }
            catch (TaskCanceledException ex)
            {
                try
                {
                    AppLog.Add(() => "Validate: timed out.");
                    AppLog.Add(() => ex.ToString());
                }
                catch
                {
                }

                ServerValidationMessage = "Server did not respond in time (validation timed out).";
                ServerValidationIsError = true;
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.Add(() => $"Validate: unexpected exception: {ex.Message}");
                    AppLog.Add(() => ex.ToString());
                }
                catch
                {
                }

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
                || !string.Equals(_authServerBaseUrl, _savedAuthServerBaseUrl, StringComparison.Ordinal)
                || _useDefaultConnection != _savedUseDefaultConnection
                || !string.Equals(_basicAuthUsername, _savedBasicAuthUsername, StringComparison.Ordinal)
                || !string.Equals(_basicAuthPassword, _savedBasicAuthPassword, StringComparison.Ordinal)
                || _autoPlay != _savedAutoPlay
                || _windowsAutoConnectOnStart != _savedWindowsAutoConnectOnStart
                || _windowsStartWithWindows != _savedWindowsStartWithWindows
                || _mobileAutoConnectOnStart != _savedMobileAutoConnectOnStart
                || !string.Equals(_bluetoothLabelArtistToken, _savedBluetoothLabelArtistToken, StringComparison.Ordinal)
                || !string.Equals(_bluetoothLabelTitleToken, _savedBluetoothLabelTitleToken, StringComparison.Ordinal)
                || !string.Equals(_bluetoothLabelAlbumToken, _savedBluetoothLabelAlbumToken, StringComparison.Ordinal)
                || !string.Equals(_bluetoothLabelComposerToken, _savedBluetoothLabelComposerToken, StringComparison.Ordinal)
                || !string.Equals(_bluetoothLabelGenreToken, _savedBluetoothLabelGenreToken, StringComparison.Ordinal);

            HasChanges = has;
        }

        public void DiscardConnectionChanges()
        {
            ServerUrl = _savedServerUrl;
            AuthServerBaseUrl = _savedAuthServerBaseUrl;
            UseDefaultConnection = _savedUseDefaultConnection;
            BasicAuthUsername = _savedBasicAuthUsername;
            BasicAuthPassword = _savedBasicAuthPassword;
            WindowsAutoConnectOnStart = _savedWindowsAutoConnectOnStart;
            WindowsStartWithWindows = _savedWindowsStartWithWindows;

            _bluetoothLabelArtistToken = _savedBluetoothLabelArtistToken;
            _bluetoothLabelTitleToken = _savedBluetoothLabelTitleToken;
            _bluetoothLabelAlbumToken = _savedBluetoothLabelAlbumToken;
            _bluetoothLabelComposerToken = _savedBluetoothLabelComposerToken;
            _bluetoothLabelGenreToken = _savedBluetoothLabelGenreToken;

            _bluetoothLabelArtistOption = GetBluetoothOptionOrDefault(_bluetoothLabelArtistToken, BluetoothLabelMapping.TokenAppName);
            _bluetoothLabelTitleOption = GetBluetoothOptionOrDefault(_bluetoothLabelTitleToken, BluetoothLabelMapping.TokenTranscription);
            _bluetoothLabelAlbumOption = GetBluetoothOptionOrDefault(_bluetoothLabelAlbumToken, BluetoothLabelMapping.TokenTalkgroup);
            _bluetoothLabelComposerOption = GetBluetoothOptionOrDefault(_bluetoothLabelComposerToken, BluetoothLabelMapping.TokenSite);
            _bluetoothLabelGenreOption = GetBluetoothOptionOrDefault(_bluetoothLabelGenreToken, BluetoothLabelMapping.TokenReceiver);

            OnPropertyChanged(nameof(BluetoothLabelArtistOption));
            OnPropertyChanged(nameof(BluetoothLabelTitleOption));
            OnPropertyChanged(nameof(BluetoothLabelAlbumOption));
            OnPropertyChanged(nameof(BluetoothLabelComposerOption));
            OnPropertyChanged(nameof(BluetoothLabelGenreOption));
            HasChanges = false;
        }


        private string BuildAuthServerUrl(string path)
        {
            var baseUrl = (AuthServerBaseUrl ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "https://joesscanner.com";

            baseUrl = baseUrl.TrimEnd('/');

            var cleanPath = (path ?? string.Empty).Trim();
            if (!cleanPath.StartsWith("/", StringComparison.Ordinal))
                cleanPath = "/" + cleanPath;

            return baseUrl + cleanPath;
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