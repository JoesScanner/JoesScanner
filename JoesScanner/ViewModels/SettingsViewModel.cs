using System.Diagnostics;
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
using JoesScanner.Helpers;
using Microsoft.Data.Sqlite;

namespace JoesScanner.ViewModels
{
    // View model for the Settings page.
    // Controls connection settings, call list behavior, theme,
    // and the unified filter list that is populated from live calls.
    public partial class SettingsViewModel : BindableObject
    {
        private readonly ISettingsService _settings;
        private readonly MainViewModel _mainViewModel;
        private readonly IHistoryLookupsCacheService _historyLookupsCacheService;
        private readonly IAuthObservedTriplesSyncService _authObservedTriplesSyncService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly CallCardOptionsService _callCardOptionsService;
        private CallCardOptionsViewModel? _callCardOptions;
        public CallCardOptionsViewModel CallCardOptions => _callCardOptions ??= new(_callCardOptionsService);
        private readonly HttpClient _httpClient;
        private readonly HttpClient _scannerValidationClient;
        private readonly FilterService _filterService = FilterService.Instance;

        private readonly IFilterProfileStore _filterProfileStore;

        private readonly ObservableCollection<FilterProfile> _settingsFilterProfiles = new();
        private readonly ObservableCollection<string> _settingsFilterProfileNameOptions = new();
        // One-time guard to avoid redoing expensive initialization work each time the user
        // visits Settings. (Profiles can still be refreshed via explicit actions.)
        private int _pageOpenStarted;
        private readonly SettingsStartupPreparation<StartupData> _startupData = new();

        private sealed record StartupData(
            IReadOnlyList<ServerDirectoryEntry> Directory,
            IReadOnlyList<FilterProfile> Profiles,
            string SelectedProfileId);

        private const int FilterPageSize = 64;
        private IReadOnlyList<FilterCatalogRow> _filterCatalog = [];
        private FilterCatalogRow[] _visibleFilterRows = [];
        private string _filterCatalogServerUrl = string.Empty;
        private int _filterPageIndex;
        private int _filterCardVersion;
        private int _filterRefreshPending;
        private bool _filterRefreshRunning;
        private bool _tryFilterSeed;
        private Task? _filterSeedTask;


        private bool _deferredUiLoaded;

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
                if (string.Equals(_selectedSettingsFilterProfileNameOption, newValue, StringComparison.OrdinalIgnoreCase))
                    return;

                _selectedSettingsFilterProfileNameOption = newValue;

                if (string.Equals(newValue, NoneSettingsProfileNameOption, StringComparison.OrdinalIgnoreCase))
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
                if (string.Equals(_settingsFilterProfileNameDraft, newValue, StringComparison.OrdinalIgnoreCase))
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

            if (string.Equals(nameOption, NoneSettingsProfileNameOption, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var profile = _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Name, nameOption, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
                _ = SelectSettingsFilterProfileAsync(profile, apply: true);
        }

        public IReadOnlyList<FilterRule> FilterRules { get; private set; } = [];
        public bool HasFilterPages => _filterCatalog.Count > FilterPageSize;
        public bool CanShowPreviousFilterPage => _filterPageIndex > 0;
        public bool CanShowNextFilterPage => (_filterPageIndex + 1) * FilterPageSize < _filterCatalog.Count;
        public string FilterPageSummary => _filterCatalog.Count == 0 ? "No filters discovered yet."
            : $"{_filterPageIndex * FilterPageSize + 1}?{_filterPageIndex * FilterPageSize + FilterRules.Count} of {_filterCatalog.Count}";

private double _filterTextHorizontalOffset;
public double FilterTextHorizontalOffset
{
    get => _filterTextHorizontalOffset;
    set
    {
        var newValue = value < 0 ? 0 : value;
        if (Math.Abs(_filterTextHorizontalOffset - newValue) < 0.5)
            return;

        _filterTextHorizontalOffset = newValue;
        OnPropertyChanged();
        OnPropertyChanged(nameof(FilterTextHorizontalTranslation));
        OnPropertyChanged(nameof(FilterTextColumnWidth));
    }
}

public double FilterTextHorizontalTranslation => -_filterTextHorizontalOffset;

public double FilterTextColumnWidth => 140 + _filterTextHorizontalOffset;


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

        private readonly ObservableCollection<ServerDirectoryEntry> _directoryServers = new();
        
        private static readonly ServerDirectoryEntry BuiltinJoeDirectoryEntry = new ServerDirectoryEntry
        {
            DirectoryId = -1,
            Name = "Joe's Scanner",
            Url = DefaultServerUrl,
            IsOfficial = true,
            UsesApiFirewallCredentials = true,
            Badge = "Official",
            BadgeLabel = "Official"
        };

        private static readonly ServerDirectoryEntry CustomServerDirectoryEntry = new ServerDirectoryEntry
        {
            DirectoryId = 0,
            Name = "Custom server",
            Url = string.Empty,
            IsCustom = true
        };

        private ServerDirectoryEntry? _selectedDirectoryServer;
        private bool _isDirectoryLoading;
        private string _directoryStatusText = string.Empty;
        private int _directoryRefreshInProgress;
        private bool _suppressDirectorySelection;
        private bool _showCustomServerUrlRow;

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
        private string _savedThemeMode = "System";

        // Bluetooth label mapping
        private string _bluetoothLabelArtistToken = BluetoothLabelMapping.TokenAppName;
        private string _bluetoothLabelTitleToken = BluetoothLabelMapping.TokenTranscription;
        private string _bluetoothLabelAlbumToken = BluetoothLabelMapping.TokenTalkgroup;
        private string _bluetoothLabelComposerToken = BluetoothLabelMapping.TokenSite;
        private string _bluetoothLabelGenreToken = BluetoothLabelMapping.TokenReceiver;
        private MobileAudioPreferences _mobileAudio = MobileAudioPreferences.Default;
        private MobileAudioPreferences _savedMobileAudio = MobileAudioPreferences.Default;

        // Audio filters (Phase 1 settings only; audio pipeline wiring in later phases)
        private bool _audioStaticFilterEnabled;
        private bool _savedAudioStaticFilterEnabled;

        private int _audioStaticAttenuatorVolume = 50;
        private int _savedAudioStaticAttenuatorVolume = 50;

        private bool _audioToneFilterEnabled;
        private bool _savedAudioToneFilterEnabled;

        private int _audioToneStrength = 50;
        private int _savedAudioToneStrength = 50;

        private int _audioToneSensitivity = 50;
        private int _savedAudioToneSensitivity = 50;

        private int _audioToneHighlightMinutes = 5;
        private int _savedAudioToneHighlightMinutes = 5;


        private bool _logEnabled;

        // Address detection
        private bool _addressDetectionEnabled;

        // what3words
        private bool _what3WordsLinksEnabled = true;
        private bool _savedWhat3WordsLinksEnabled = true;
        private string _what3WordsApiKey = string.Empty;
        private string _savedWhat3WordsApiKey = string.Empty;

        private bool _savedAddressDetectionEnabled;
        private bool _addressDetectionOpenMapsOnTap = true;
        private bool _savedAddressDetectionOpenMapsOnTap = true;

        private int _addressDetectionMinConfidencePercent = 70;
        private int _savedAddressDetectionMinConfidencePercent = 70;

        private int _addressDetectionMinAddressChars = 8;
        private int _savedAddressDetectionMinAddressChars = 8;

        private int _addressDetectionMaxCandidatesPerCall = 3;
        private int _savedAddressDetectionMaxCandidatesPerCall = 3;
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
        // Autosave gating: only fire autosave when the user is actively interacting with an open card.
        // Server Connections remains manual (Verify button).
        private volatile bool _autoplayCardOpen;
        private volatile bool _filtersCardOpen;
        private volatile bool _audioFiltersCardOpen;
        private volatile bool _audioCardOpen;
        private volatile bool _addressDetectionCardOpen;
        private volatile bool _bluetoothCardOpen;
        private volatile bool _themeCardOpen;
        private volatile bool _logCardOpen;

        private readonly SemaphoreSlim _settingsSaveGate = new(1);
        private bool _autoSaveInProgress;
        private bool _autoSaveQueued;

        private bool _hasUnsavedConnectionChanges;
        private bool _hasUnsavedNonConnectionChanges;

        public void SetSettingsCardOpenState(string cardKey, bool isOpen)
        {
            var key = (cardKey ?? string.Empty).Trim();

            if (string.Equals(key, "Autoplay", StringComparison.OrdinalIgnoreCase))
                _autoplayCardOpen = isOpen;
            else if (string.Equals(key, "Filters", StringComparison.OrdinalIgnoreCase))
                SetFiltersCardOpen(isOpen);
            else if (string.Equals(key, "AudioFilters", StringComparison.OrdinalIgnoreCase))
                _audioFiltersCardOpen = isOpen;
            else if (string.Equals(key, "Audio", StringComparison.OrdinalIgnoreCase))
                _audioCardOpen = isOpen;
            else if (string.Equals(key, "AddressDetection", StringComparison.OrdinalIgnoreCase))
                _addressDetectionCardOpen = isOpen;
            else if (string.Equals(key, "Bluetooth", StringComparison.OrdinalIgnoreCase))
                _bluetoothCardOpen = isOpen;
            else if (string.Equals(key, "Theme", StringComparison.OrdinalIgnoreCase))
                _themeCardOpen = isOpen;

            else if (string.Equals(key, "Log", StringComparison.OrdinalIgnoreCase))
                _logCardOpen = isOpen;
        }

        private bool IsCardOpenForAutosave(string cardKey)
        {
            var key = (cardKey ?? string.Empty).Trim();

            if (string.Equals(key, "Autoplay", StringComparison.OrdinalIgnoreCase))
                return _autoplayCardOpen;

            if (string.Equals(key, "Filters", StringComparison.OrdinalIgnoreCase))
                return _filtersCardOpen;

            if (string.Equals(key, "AudioFilters", StringComparison.OrdinalIgnoreCase))
                return _audioFiltersCardOpen;

            if (string.Equals(key, "Audio", StringComparison.OrdinalIgnoreCase))
                return _audioCardOpen;

            if (string.Equals(key, "AddressDetection", StringComparison.OrdinalIgnoreCase))
                return _addressDetectionCardOpen;

            if (string.Equals(key, "Bluetooth", StringComparison.OrdinalIgnoreCase))
                return _bluetoothCardOpen;

            if (string.Equals(key, "Theme", StringComparison.OrdinalIgnoreCase))
                return _themeCardOpen;


            if (string.Equals(key, "Log", StringComparison.OrdinalIgnoreCase))
                return _logCardOpen;

            return false;
        }

        private void QueueAutosaveNonConnection(string cardKey)
        {
            if (!IsCardOpenForAutosave(cardKey))
                return;

            _autoSaveQueued = true;
            if (!_autoSaveInProgress)
                _ = ProcessAutosavesAsync();
        }

        private async Task ProcessAutosavesAsync()
        {
            _autoSaveInProgress = true;
            try
            {
                while (_autoSaveQueued)
                {
                    // Coalesce edits within the existing autosave debounce window.
                    await Task.Delay(150);
                    _autoSaveQueued = false;
                    await TrySaveNonConnectionSettingsAsync();
                }
            }
            finally
            {
                _autoSaveInProgress = false;
            }
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }


        // Commands
        public ICommand ToggleMuteFilterCommand { get; }
        public ICommand ToggleDisableFilterCommand { get; }
        public ICommand ClearFilterCommand { get; }
        public ICommand SyncTalkgroupFiltersCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ResetServerCommand { get; }
        public ICommand ValidateServerCommand { get; }

        public ICommand RefreshDirectoryServersCommand { get; }

        // Password visibility command
        public ICommand ToggleBasicAuthPasswordVisibilityCommand { get; }

        private const string DefaultServerUrl = HostedServerRules.DefaultServerUrl;

        private bool _isSyncingTalkgroupFilters;
        public bool IsSyncingTalkgroupFilters
        {
            get => _isSyncingTalkgroupFilters;
            private set
            {
                if (_isSyncingTalkgroupFilters == value)
                    return;

                _isSyncingTalkgroupFilters = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSyncTalkgroupFilters));
            }
        }

        // Manual sync is user-initiated and should always be allowed.
        // Telemetry settings only affect whether we can report/seed via the Auth API.
        public bool CanSyncTalkgroupFilters => !LocalOnlyMode && !IsSyncingTalkgroupFilters;

        public SettingsViewModel(
            ISettingsService settingsService,
            MainViewModel mainViewModel,
            IFilterProfileStore filterProfileStore,
            IHistoryLookupsCacheService historyLookupsCacheService,
            IAuthObservedTriplesSyncService authObservedTriplesSyncService,
            ISubscriptionService subscriptionService,
            CallCardOptionsService callCardOptionsService,
            PlaybackAudioCache audioCache)
        {
            _settings = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

            _filterProfileStore = filterProfileStore ?? throw new ArgumentNullException(nameof(filterProfileStore));


            _historyLookupsCacheService = historyLookupsCacheService ?? throw new ArgumentNullException(nameof(historyLookupsCacheService));
            _authObservedTriplesSyncService = authObservedTriplesSyncService ?? throw new ArgumentNullException(nameof(authObservedTriplesSyncService));
            _subscriptionService = subscriptionService;
            _callCardOptionsService = callCardOptionsService;
            InitializeAudioCache(audioCache);
            _filterService.CatalogChanged += (_, _) => QueueFilterCatalogRefresh();

            _httpClient = WordPressSession.CreateClient(_settings);
            _httpClient.Timeout = TimeSpan.FromSeconds(3);
            _serverDirectory = new ServerDirectoryClient(_settings);
            _settings.LocalOnlyModeChanged += (_, _) => MainThread.BeginInvokeOnMainThread(OnDirectoryPrivacyChanged);
            Favorites.Changed += OnFavoritesSettingsChanged;

            // Use managed networking for custom HTTP server validation on Apple.
            _scannerValidationClient = ServerAuthenticationHandler.CreateClient(_settings,
                new SocketsHttpHandler { AllowAutoRedirect = false });
            _scannerValidationClient.Timeout = TimeSpan.FromSeconds(15);

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

            var initializationStarted = Stopwatch.GetTimestamp();
            InitializeFromSettings();
            AppLog.Add(() => $"Settings timing: view model initialization={Stopwatch.GetElapsedTime(initializationStarted).TotalMilliseconds:F1} ms.");

            SaveCommand = new Command(async () => await TrySaveNonConnectionSettingsAsync());
            ResetServerCommand = new Command(ResetServerUrl);

            // Validate always saves first, then validates.
            ValidateServerCommand = new Command(async () => await SaveThenValidateServerUrlAsync());

            RefreshDirectoryServersCommand = new Command(async () => await RefreshDirectoryServersAsync());

            ToggleMuteFilterCommand = new Command<FilterRule>(OnToggleMuteFilter);
            ToggleDisableFilterCommand = new Command<FilterRule>(OnToggleDisableFilter);
            ClearFilterCommand = new Command<FilterRule>(OnClearFilter);

            SyncTalkgroupFiltersCommand = new Command(async () => await SyncTalkgroupFiltersWithServerAsync());
        }

        private async Task SyncTalkgroupFiltersWithServerAsync()
        {
            if (IsSyncingTalkgroupFilters)
                return;

            try
            {
                IsSyncingTalkgroupFilters = true;
                var serverUrl = _settings.ServerUrl;
                AppLog.Add("Settings: manual lookup sync requested.");

                // First seed from the local calls database so the filter tree can immediately reflect
                // anything the user has already observed on this device, even before the server responds.
                var localSeeded = 0;
                try
                {
                    var localObserved = await Task.Run(() => _authObservedTriplesSyncService.GetLocalObservedAsync(serverUrl, DateTime.MinValue, CancellationToken.None));
                    localSeeded = localObserved?.Count ?? 0;
                    if (localObserved != null && localObserved.Count > 0)
                    {
                        _filterService.SeedFromObservedTriples(localObserved, serverUrl);
                    }
                    else
                    {
                        var localDiscovery = _filterService.GetDiscoveredTriplesSnapshot();
                        localSeeded = localDiscovery?.Count ?? 0;
                        if (localDiscovery != null && localDiscovery.Count > 0)
                        {
                            AppLog.Add(() => $"Settings: manual lookup sync using local discovery, count={localSeeded}.");
                        }
                    }
                }
                catch
                {
                }

                // Force a lookup refresh from the stream server. Lookup catalogs are now independent from
                // talkgroup discovery, but we still refresh them here for the rest of the history/settings UI.
                await _historyLookupsCacheService.PreloadAsync(forceReload: true, reason: "manual_filters_sync", CancellationToken.None);

                var cached = await _historyLookupsCacheService.GetCachedAsync(CancellationToken.None);
                var r = cached?.Receivers?.Count ?? 0;
                var s = cached?.Sites?.Count ?? 0;
                var t = cached?.Talkgroups?.Count ?? 0;

                // Canonical discovery exchange:
                // report local discoveries and then mirror the authoritative server result exactly.
                var replaced = 0;
                var received = 0;
                try
                {
                    var accountSession = WordPressSession.Capture(_settings);
                    var triples = await _authObservedTriplesSyncService.TrySyncExchangeAsync(serverUrl, force: true, CancellationToken.None);
                    received = triples?.Count ?? 0;
                    if (triples != null)
                        replaced = await Task.Run(() =>
                        {
                            var count = 0;
                            accountSession.TryApply(() => count = _filterService.ReplaceFromObservedTriples(triples, serverUrl));
                            return count;
                        });
                }
                catch
                {
                }

                // Manual sync should be silent. Log the results for diagnostics, but do not show UI dialogs.
                AppLog.Add(() => $"Settings: manual lookup sync complete. receivers={r} sites={s} talkgroups={t} localObservedSeeded={localSeeded} observedReceived={received} rulesReplaced={replaced}");
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Settings: manual lookup sync failed ({ex.GetType().Name}).");
            }
            finally
            {
                IsSyncingTalkgroupFilters = false;
            }
        }


        public async Task PrepareLocalDataAsync()
        {
            if (_pageOpenStarted != 0)
                return;

            try
            {
                var serverKey = _filterService.ScopeKey(_settings.ServerUrl);
                await _startupData.GetOrStart(serverKey, ReadStartupDataAsync);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Settings timing: local preload failed ({ex.GetType().Name}).");
            }
        }

        private Task<StartupData> ReadStartupDataAsync(string serverKey) =>
            Task.Run(async () =>
            {
                var started = Stopwatch.GetTimestamp();
                var stage = "directory cache";
                try
                {
                    var directory = _settings.GetCachedDirectoryServers();
                    var directoryTime = Stopwatch.GetElapsedTime(started);
                    stage = "filter profiles";
                    var (profiles, selectedId) = await ReadFilterProfilesAsync(serverKey).ConfigureAwait(false);
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    AppLog.Add(() => $"Settings timing: local reads directory={directoryTime.TotalMilliseconds:F1} ms, profiles={elapsed.TotalMilliseconds - directoryTime.TotalMilliseconds:F1} ms.");
                    return new StartupData(directory, profiles, selectedId);
                }
                catch (SqliteException ex)
                {
                    // SQLite messages can include filesystem paths or data. Log only known error categories.
                    var detail = ex.SqliteErrorCode switch
                    {
                        1 when stage == "filter profiles" && ex.Message.Contains("no such column: server_key", StringComparison.OrdinalIgnoreCase) => "missing filter profile server_key column",
                        1 => "SQL or schema error",
                        5 => "database busy",
                        6 => "database locked",
                        8 => "database read-only",
                        10 => "database I/O error",
                        11 => "database corrupt",
                        14 => "cannot open database",
                        19 => "constraint violation",
                        26 => "invalid database format",
                        _ => "SQLite operation failed"
                    };
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    AppLog.Add(() => $"Settings timing: local read failed, stage={stage}, elapsed={elapsed.TotalMilliseconds:F1} ms, sqlite={ex.SqliteErrorCode}, extended={ex.SqliteExtendedErrorCode}, detail={detail}.");
                    throw;
                }
            });

        public async Task OnPageOpenedAsync()
        {
            if (Interlocked.Exchange(ref _pageOpenStarted, 1) == 1)
                return;

            var started = Stopwatch.GetTimestamp();
            try
            {
                var serverKey = _filterService.ScopeKey(_settings.ServerUrl);
                var localData = _startupData.GetOrStart(serverKey, ReadStartupDataAsync);
                var wasReady = localData.IsCompletedSuccessfully;
                var data = await localData;
                _startupData.Release(localData);
                var waited = Stopwatch.GetElapsedTime(started);

                if (!string.Equals(serverKey, _filterService.ScopeKey(_settings.ServerUrl), StringComparison.OrdinalIgnoreCase))
                    await LoadSettingsFilterProfilesAsync(applySelectedProfile: true);
                else
                    PublishFilterProfiles(data.Profiles, data.SelectedProfileId, applySelectedProfile: true);

                if (_lastDirectoryRefreshUtc == DateTime.MinValue && !IsDirectoryLoading)
                    PublishDirectoryServers(data.Directory);

                AppLog.Add(() => $"Settings timing: local data ready before open={wasReady}, wait={waited.TotalMilliseconds:F1} ms, publish={Stopwatch.GetElapsedTime(started).TotalMilliseconds - waited.TotalMilliseconds:F1} ms.");
                var refreshStarted = Stopwatch.GetTimestamp();
                await EnsureDirectoryServersFreshAsync(force: false);
                AppLog.Add(() => $"Settings timing: directory refresh check={Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds:F1} ms.");
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _pageOpenStarted, 0);
                AppLog.Add(() => $"Settings: initial data load failed ({ex.GetType().Name}).");
            }
        }

        private void SetFiltersCardOpen(bool isOpen)
        {
            if (_filtersCardOpen == isOpen)
                return;

            _filtersCardOpen = isOpen;
            _filterCardVersion++;
            if (isOpen)
            {
                _tryFilterSeed = true;
                QueueFilterCatalogRefresh();
            }
            else
            {
                _filterCatalog = [];
                _filterCatalogServerUrl = string.Empty;
                _filterPageIndex = 0;
                PublishFilterPage();
            }
        }

        private void QueueFilterCatalogRefresh()
        {
            if (!_filtersCardOpen || Interlocked.Exchange(ref _filterRefreshPending, 1) != 0)
                return;

            MainThread.BeginInvokeOnMainThread(() => _ = RefreshFilterCatalogAsync());
        }

        private async Task RefreshFilterCatalogAsync()
        {
            if (_filterRefreshRunning)
                return;

            _filterRefreshRunning = true;
            try
            {
                while (Interlocked.Exchange(ref _filterRefreshPending, 0) != 0 && _filtersCardOpen)
                {
                    var start = Stopwatch.GetTimestamp();
                    var version = _filterCardVersion;
                    var serverUrl = _settings.ServerUrl;
                    var catalog = await Task.Run(_filterService.GetCatalogSnapshot);
                    var buildTime = Stopwatch.GetElapsedTime(start);
                    if (!_filtersCardOpen || _filterCardVersion != version ||
                        !string.Equals(serverUrl, _settings.ServerUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var publishStart = Stopwatch.GetTimestamp();
                    _filterCatalog = catalog;
                    _filterCatalogServerUrl = serverUrl;
                    PublishFilterPage();
                    AppLog.Add(() => $"Settings: filter catalog rows={catalog.Count}, visible={FilterRules.Count}, build={buildTime.TotalMilliseconds:F0} ms, publish={Stopwatch.GetElapsedTime(publishStart).TotalMilliseconds:F0} ms.");

                    if (_tryFilterSeed)
                    {
                        _tryFilterSeed = false;
                        if (catalog.Count == 0 && _filterSeedTask?.IsCompleted != false)
                            _filterSeedTask = Task.Run(() => SeedFiltersFromObservedAsync(serverUrl));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Settings: filter catalog refresh failed ({ex.GetType().Name}).");
            }
            finally
            {
                _filterRefreshRunning = false;
            }
        }

        public void MoveFilterPage(int offset)
        {
            _filterPageIndex += offset;
            PublishFilterPage();
        }

        private void PublishFilterPage()
        {
            _filterPageIndex = Math.Clamp(_filterPageIndex, 0, Math.Max(0, (_filterCatalog.Count - 1) / FilterPageSize));
            var rows = _filterCatalog.Skip(_filterPageIndex * FilterPageSize).Take(FilterPageSize).ToArray();
            if (!rows.SequenceEqual(_visibleFilterRows))
            {
                _visibleFilterRows = rows;
                FilterRules = rows.Select(row => new FilterRule
                {
                    Level = row.Level,
                    Receiver = row.Receiver,
                    Site = row.Site,
                    Talkgroup = row.Talkgroup,
                    IsMuted = row.IsMuted,
                    IsDisabled = row.IsDisabled,
                    LastSeenUtc = row.LastSeenUtc
                }).ToArray();
                OnPropertyChanged(nameof(FilterRules));
            }
            OnPropertyChanged(nameof(HasFilterPages));
            OnPropertyChanged(nameof(CanShowPreviousFilterPage));
            OnPropertyChanged(nameof(CanShowNextFilterPage));
            OnPropertyChanged(nameof(FilterPageSummary));
        }

        private async Task SeedFiltersFromObservedAsync(string? serverUrl)
        {
            try
            {
                var serverKey = ServerKeyHelper.Normalize(serverUrl);
                if (string.IsNullOrWhiteSpace(serverKey))
                    return;

                // Always seed from the local calls database first so the card can populate immediately from
                // what this device has already discovered, even when the auth server is unavailable.
                try
                {
                    var localObserved = await _authObservedTriplesSyncService.GetLocalObservedAsync(serverKey, DateTime.MinValue, CancellationToken.None);
                    if (localObserved != null && localObserved.Count > 0)
                    {
                        _filterService.SeedFromObservedTriples(localObserved, serverUrl);
                    }

                }
                catch
                {
                }

                // Then fetch the canonical server view and merge it in. This keeps both sides converging
                // without making the settings page wait on network success.
                var accountSession = WordPressSession.Capture(_settings);
                var triples = await _authObservedTriplesSyncService.TryFetchSeedAsync(serverKey, CancellationToken.None);
                if (triples == null || triples.Count == 0)
                    return;

                accountSession.TryApply(() => _filterService.SeedFromObservedTriples(triples, serverUrl));
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Settings: filter seed failed ({ex.GetType().Name}).");
            }
        }

        public bool DeferredUiLoaded
        {
            get => _deferredUiLoaded;
            set
            {
                if (_deferredUiLoaded == value)
                    return;

                _deferredUiLoaded = value;
                OnPropertyChanged();
            }
        }

        public async Task LoadSettingsFilterProfilesAsync(bool applySelectedProfile)
        {
            var serverKey = _filterService.ScopeKey(_settings.ServerUrl);
            // The SQLite store does synchronous work even though its API returns a Task.
            var (profiles, selectedId) = await Task.Run(() => ReadFilterProfilesAsync(serverKey));

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (string.Equals(serverKey, _filterService.ScopeKey(_settings.ServerUrl), StringComparison.OrdinalIgnoreCase))
                    PublishFilterProfiles(profiles, selectedId, applySelectedProfile);
            });
        }

        private async Task<(IReadOnlyList<FilterProfile> Profiles, string SelectedId)> ReadFilterProfilesAsync(string serverKey)
        {
            var profiles = await _filterProfileStore.GetProfilesForServerAsync(serverKey, CancellationToken.None)
                .ConfigureAwait(false);
            return (profiles, AppStateStore.GetString("settings_selected_filter_profile_id", string.Empty));
        }

        private void PublishFilterProfiles(IReadOnlyList<FilterProfile> profiles, string selectedId, bool applySelectedProfile)
        {
            _settingsFilterProfiles.Clear();
            foreach (var profile in profiles)
                _settingsFilterProfiles.Add(profile);

            RefreshSettingsProfileNameOptions();
            var selected = string.IsNullOrWhiteSpace(selectedId) ? null :
                _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            SelectedSettingsFilterProfile = selected;
            if (applySelectedProfile && selected != null)
                ApplySettingsProfile(selected);
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
	                ServerKey = _filterService.ScopeKey(_settings.ServerUrl),
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

            var refreshed = _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Id, current.Id, StringComparison.OrdinalIgnoreCase));
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
        public bool HasUnsavedSettings => _hasUnsavedNonConnectionChanges;

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

        public bool ConnectionNeedsValidation => _hasUnsavedConnectionChanges;

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

        public ObservableCollection<ServerDirectoryEntry> DirectoryServers => _directoryServers;

        public ServerDirectoryEntry? SelectedDirectoryServer
        {
            get => _selectedDirectoryServer;
            set
            {
                ApplySelectedDirectoryServer(value, syncServerUrl: !_suppressDirectorySelection);
            }
        }

        public bool IsCustomServerSelected => _showCustomServerUrlRow;

        private DateTime _lastDirectoryRefreshUtc = DateTime.MinValue;

        // When we are applying connection settings programmatically (for example, during Validate),
        // we do not want the act of assigning ServerUrl to re-sync the server picker selection.
        // The picker should remain on what the user selected.
        private int _suppressDirectorySelectionSync;

        private void ApplySelectedDirectoryServer(ServerDirectoryEntry? value, bool syncServerUrl, bool forceNotify = false)
        {
            var selectionChanged = !ReferenceEquals(_selectedDirectoryServer, value);
            _selectedDirectoryServer = value;

            var showCustom = value?.IsCustom == true;
            var customVisibilityChanged = _showCustomServerUrlRow != showCustom;
            _showCustomServerUrlRow = showCustom;

            if (selectionChanged || forceNotify)
                OnPropertyChanged(nameof(SelectedDirectoryServer));

            if (selectionChanged || customVisibilityChanged)
                OnPropertyChanged(nameof(IsCustomServerSelected));
            OnPropertyChanged(nameof(CanEditScannerAddress));
            OnPropertyChanged(nameof(CanSaveDirectoryConnection));

            if (!syncServerUrl || value == null)
                return;

            var url = (value.Url ?? string.Empty).Trim();
            if (url.Length > 0)
                ServerUrl = url;

        }

        public async Task EnsureDirectoryServersFreshAsync(bool force)
        {
            // If we have only the Custom placeholder, or if it's been a while, refresh.
            var hasAnyDirectoryServers = _directoryServers.Count > 1;
            var age = DateTime.UtcNow - _lastDirectoryRefreshUtc;

            if (!force && hasAnyDirectoryServers && age < TimeSpan.FromMinutes(5))
                return;

            await RefreshDirectoryServersAsync();
        }
public bool IsDirectoryLoading
        {
            get => _isDirectoryLoading;
            private set
            {
                if (_isDirectoryLoading == value)
                    return;
                _isDirectoryLoading = value;
                OnPropertyChanged(nameof(IsDirectoryLoading));
            }
        }

        public string DirectoryStatusText
        {
            get => _directoryStatusText;
            private set
            {
                if (string.Equals(_directoryStatusText, value, StringComparison.OrdinalIgnoreCase))
                    return;
                _directoryStatusText = value ?? string.Empty;
                OnPropertyChanged(nameof(DirectoryStatusText));
            }
        }

        public string ServerUrl
        {
            get => _serverUrl;
            set
            {
                var newValue = value ?? string.Empty;
                if (string.Equals(_serverUrl, newValue, StringComparison.OrdinalIgnoreCase))
                    return;

                _serverUrl = newValue;
                OnPropertyChanged();

                var isDefault = HostedServerRules.IsProvidedDefaultServerUrl(_serverUrl);

                if (_useDefaultConnection != isDefault)
                {
                    _useDefaultConnection = isDefault;
                    OnPropertyChanged(nameof(UseDefaultConnection));
                }

                OnPropertyChanged(nameof(CanSyncTalkgroupFilters));
                // Changing the server URL must clear user/pass in the UI immediately.
                // If this URL was used before, pull cached credentials for convenience.
                var normalized = (newValue ?? string.Empty).Trim();

                _basicAuthUsername = string.Empty;
                _basicAuthPassword = string.Empty;

                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    try
                    {
                        if (_mainViewModel.TryGetConfiguredRecorderCredentials(normalized, out var cachedUser, out var cachedPass))
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

                // Keep the user's current picker selection stable when we are applying server
                // state programmatically (for example, during Validate).
                if (Volatile.Read(ref _suppressDirectorySelectionSync) == 0)
                    SyncSelectedDirectoryServerFromCurrentUrl();

                UpdateHasChanges();
            }
        }


        public string AuthServerBaseUrl
        {
            get => _authServerBaseUrl;
            set
            {
                var newValue = value ?? string.Empty;
                if (string.Equals(_authServerBaseUrl, newValue, StringComparison.OrdinalIgnoreCase))
                    return;

                _authServerBaseUrl = newValue;
                if (HostedServerRules.IsHostedStreamServerUrl(_serverUrl))
                {
                    BasicAuthUsername = string.Empty;
                    BasicAuthPassword = string.Empty;
                }
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        public string BasicAuthUsername
        {
            get => _basicAuthUsername;
            set
            {
                if (string.Equals(_basicAuthUsername, value, StringComparison.OrdinalIgnoreCase))
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


        public int MobileAudioModeIndex
        {
            get => (int)_mobileAudio.Mode;
            set
            {
                if (Enum.IsDefined((MobileAudioMode)value))
                    SetMobileAudio(_mobileAudio with { Mode = (MobileAudioMode)value }, nameof(MobileAudioModeIndex));
            }
        }

        public int AudioDisconnectionIndex
        {
            get => (int)_mobileAudio.OnDisconnect;
            set
            {
                if (Enum.IsDefined((AudioDisconnectionBehavior)value))
                    SetMobileAudio(_mobileAudio with { OnDisconnect = (AudioDisconnectionBehavior)value }, nameof(AudioDisconnectionIndex));
            }
        }

        private string _customRecoveryMinutes = "5";

        public int InterruptionRecoveryIndex
        {
            get => (int)_mobileAudio.InterruptionRecovery.Option;
            set
            {
                if (!Enum.IsDefined((InterruptionRecoveryOption)value))
                    return;
                var option = (InterruptionRecoveryOption)value;
                SetMobileAudio(_mobileAudio with { InterruptionRecovery = _mobileAudio.InterruptionRecovery with { Option = option } },
                    nameof(InterruptionRecoveryIndex));
                CustomRecoveryMinutes = _mobileAudio.InterruptionRecovery.CustomMinutes.ToString();
                OnPropertyChanged(nameof(ShowsCustomRecoveryMinutes));
            }
        }

        public bool ShowsCustomRecoveryMinutes => _mobileAudio.InterruptionRecovery.Option == InterruptionRecoveryOption.Custom;
        public bool HasInvalidCustomRecoveryMinutes => !int.TryParse(CustomRecoveryMinutes, out var minutes) ||
            !InterruptionRecoveryPreference.AllowsCustomMinutes(minutes);

        public string CustomRecoveryMinutes
        {
            get => _customRecoveryMinutes;
            set
            {
                _customRecoveryMinutes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasInvalidCustomRecoveryMinutes));
                if (int.TryParse(value, out var minutes) && InterruptionRecoveryPreference.AllowsCustomMinutes(minutes))
                    SetMobileAudio(_mobileAudio with { InterruptionRecovery = _mobileAudio.InterruptionRecovery with { CustomMinutes = minutes } },
                        nameof(CustomRecoveryMinutes));
            }
        }

        private void SetMobileAudio(MobileAudioPreferences preferences, string propertyName)
        {
            if (_mobileAudio == preferences)
                return;

            _mobileAudio = preferences;
            if (preferences == _savedMobileAudio)
                SetMobileAudioSaveError(false);
            OnPropertyChanged(propertyName);
            UpdateHasChanges();
            QueueAutosaveNonConnection("Audio");
        }

        private bool _hasMobileAudioSaveError;
        public bool HasMobileAudioSaveError => _hasMobileAudioSaveError;

        private void SetMobileAudioSaveError(bool value)
        {
            if (_hasMobileAudioSaveError == value)
                return;
            _hasMobileAudioSaveError = value;
            OnPropertyChanged(nameof(HasMobileAudioSaveError));
        }


        public bool What3WordsLinksEnabled
        {
            get => _what3WordsLinksEnabled;
            set
            {
                if (_what3WordsLinksEnabled == value)
                    return;

                _what3WordsLinksEnabled = value;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("AddressDetection");
            }
        }

        public string What3WordsApiKey
        {
            get => _what3WordsApiKey;
            set
            {
                var cleaned = (value ?? string.Empty).Trim();
                if (_what3WordsApiKey == cleaned)
                    return;

                _what3WordsApiKey = cleaned;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("AddressDetection");
            }
        }


        public bool AudioStaticFilterEnabled
        {
            get => _audioStaticFilterEnabled;
            set
            {
                if (_audioStaticFilterEnabled == value)
                    return;

                _audioStaticFilterEnabled = value;
                OnPropertyChanged();
                QueueAutosaveNonConnection("AudioFilters");
            }
        }

        public int AudioStaticAttenuatorVolume
        {
            get => _audioStaticAttenuatorVolume;
            set
            {
                var cleaned = ClampInt(value, 0, 100);
                if (_audioStaticAttenuatorVolume == cleaned)
                    return;

                _audioStaticAttenuatorVolume = cleaned;
                OnPropertyChanged();
                QueueAutosaveNonConnection("AudioFilters");
            }
        }

        public bool AudioToneFilterEnabled
        {
            get => _audioToneFilterEnabled;
            set
            {
                if (_audioToneFilterEnabled == value)
                    return;

                _audioToneFilterEnabled = value;
                OnPropertyChanged();
                QueueAutosaveNonConnection("AudioFilters");
            }
        }

        public int AudioToneStrength
        {
            get => _audioToneStrength;
            set
            {
                var cleaned = ClampInt(value, 0, 100);
                if (_audioToneStrength == cleaned)
                    return;

                _audioToneStrength = cleaned;
                OnPropertyChanged();
                QueueAutosaveNonConnection("AudioFilters");
            }
        }

        public int AudioToneSensitivity
        {
            get => _audioToneSensitivity;
            set
            {
                var cleaned = ClampInt(value, 0, 100);
                if (_audioToneSensitivity == cleaned)
                    return;

                _audioToneSensitivity = cleaned;
                OnPropertyChanged();
                QueueAutosaveNonConnection("AudioFilters");
            }
        }

        public int AudioToneHighlightMinutes
        {
            get => _audioToneHighlightMinutes;
            set
            {
                var cleaned = ClampInt(value, 1, 99);
                if (_audioToneHighlightMinutes == cleaned)
                    return;

                _audioToneHighlightMinutes = cleaned;
                OnPropertyChanged();
                QueueAutosaveNonConnection("AudioFilters");
            }
        }


        public bool AddressDetectionEnabled
        {
            get => _addressDetectionEnabled;
            set
            {
                if (_addressDetectionEnabled == value)
                    return;

                _addressDetectionEnabled = value;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("AddressDetection");
            }
        }

        public bool AddressDetectionOpenMapsOnTap
        {
            get => _addressDetectionOpenMapsOnTap;
            set
            {
                if (_addressDetectionOpenMapsOnTap == value)
                    return;

                _addressDetectionOpenMapsOnTap = value;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("AddressDetection");
            }
        }

        public int AddressDetectionMinConfidencePercent
        {
            get => _addressDetectionMinConfidencePercent;
            set
            {
                var cleaned = ClampInt(value, 0, 100);
                if (_addressDetectionMinConfidencePercent == cleaned)
                    return;

                _addressDetectionMinConfidencePercent = cleaned;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("AddressDetection");
            }
        }

        public int AddressDetectionMinAddressChars
        {
            get => _addressDetectionMinAddressChars;
            set
            {
                var cleaned = ClampInt(value, 0, 200);
                if (_addressDetectionMinAddressChars == cleaned)
                    return;

                _addressDetectionMinAddressChars = cleaned;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("AddressDetection");
            }
        }

        public int AddressDetectionMaxCandidatesPerCall
        {
            get => _addressDetectionMaxCandidatesPerCall;
            set
            {
                var cleaned = ClampInt(value, 1, 10);
                if (_addressDetectionMaxCandidatesPerCall == cleaned)
                    return;

                _addressDetectionMaxCandidatesPerCall = cleaned;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("AddressDetection");
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

        public bool LogEnabled
        {
            get => _logEnabled;
            set
            {
                if (_logEnabled == value && AppLog.IsEnabled == value)
                    return;

                AppLog.SetEnabled(value);
                _logEnabled = AppLog.IsEnabled;
                OnPropertyChanged();
            }
        }

        public void RefreshLogEnabledFromRuntime()
        {
            var enabled = AppLog.IsEnabled;
            if (_logEnabled == enabled)
                return;

            _logEnabled = enabled;
            OnPropertyChanged(nameof(LogEnabled));
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

                ApplyTheme(_themeMode);
                UpdateHasChanges();
                QueueAutosaveNonConnection("Theme");
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

            var opt = BluetoothLabelOptions.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
            if (opt != null)
                return opt;

            // Fall back to default token if stored token is unknown (corrupt/old settings)
            opt = BluetoothLabelOptions.FirstOrDefault(o => string.Equals(o.Key, defaultToken, StringComparison.OrdinalIgnoreCase));
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
                if (value == null || string.Equals(_bluetoothLabelArtistOption?.Key, value.Key, StringComparison.OrdinalIgnoreCase))
                    return;

                _bluetoothLabelArtistOption = value;
                _bluetoothLabelArtistToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("Bluetooth");
            }
        }

        public BluetoothLabelMapping.Option BluetoothLabelTitleOption
        {
            get => _bluetoothLabelTitleOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelTitleOption?.Key, value.Key, StringComparison.OrdinalIgnoreCase))
                    return;

                _bluetoothLabelTitleOption = value;
                _bluetoothLabelTitleToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("Bluetooth");
            }
        }

        public BluetoothLabelMapping.Option BluetoothLabelAlbumOption
        {
            get => _bluetoothLabelAlbumOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelAlbumOption?.Key, value.Key, StringComparison.OrdinalIgnoreCase))
                    return;

                _bluetoothLabelAlbumOption = value;
                _bluetoothLabelAlbumToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("Bluetooth");
            }
        }

        public BluetoothLabelMapping.Option BluetoothLabelComposerOption
        {
            get => _bluetoothLabelComposerOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelComposerOption?.Key, value.Key, StringComparison.OrdinalIgnoreCase))
                    return;

                _bluetoothLabelComposerOption = value;
                _bluetoothLabelComposerToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("Bluetooth");
            }
        }

        public BluetoothLabelMapping.Option BluetoothLabelGenreOption
        {
            get => _bluetoothLabelGenreOption;
            set
            {
                if (value == null || string.Equals(_bluetoothLabelGenreOption?.Key, value.Key, StringComparison.OrdinalIgnoreCase))
                    return;

                _bluetoothLabelGenreOption = value;
                _bluetoothLabelGenreToken = value.Key;
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("Bluetooth");
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
            if (rule == null || !IsCurrentFilterCatalog())
                return;

            _filterService.ToggleMute(rule);
        }

        private void OnToggleDisableFilter(FilterRule? rule)
        {
            if (rule == null || !IsCurrentFilterCatalog())
                return;

            _filterService.ToggleDisable(rule);
        }

        private void OnClearFilter(FilterRule? rule)
        {
            if (rule == null || !IsCurrentFilterCatalog())
                return;

            _filterService.ClearRule(rule);
        }

        private bool IsCurrentFilterCatalog() =>
            _filtersCardOpen && string.Equals(_filterCatalogServerUrl, _settings.ServerUrl, StringComparison.OrdinalIgnoreCase);

        private void InitializeFromSettings()
        {
            // Load persisted settings into local view model fields.
            // This keeps the Settings UI in sync with the DB (single source of truth).
            _authServerBaseUrl = _settings.AuthServerBaseUrl ?? string.Empty;

            var persistedServerUrl = (_settings.ServerUrl ?? string.Empty).Trim();
            _serverUrl = string.IsNullOrWhiteSpace(persistedServerUrl)
                ? DefaultServerUrl
                : persistedServerUrl;

            _basicAuthUsername = _settings.BasicAuthUsername ?? string.Empty;
            _basicAuthPassword = _settings.BasicAuthPassword ?? string.Empty;
            if (_settings.SavedCredentialWarning is { } credentialWarning)
            {
                ServerValidationMessage = credentialWarning;
                ServerValidationIsError = true;
                HasServerValidationResult = true;
            }

            // Derived flag: treat built-in server(s) as the default connection.
            _useDefaultConnection = HostedServerRules.IsProvidedDefaultServerUrl(_serverUrl);


            _autoPlay = _settings.AutoPlay;
            _savedAutoPlay = _autoPlay;

            _windowsAutoConnectOnStart = _settings.WindowsAutoConnectOnStart;
            _savedWindowsAutoConnectOnStart = _windowsAutoConnectOnStart;

            _mobileAutoConnectOnStart = _settings.MobileAutoConnectOnStart;
            _savedMobileAutoConnectOnStart = _mobileAutoConnectOnStart;

            _windowsStartWithWindows = _settings.WindowsStartWithWindows;
            _savedWindowsStartWithWindows = _windowsStartWithWindows;

            _themeMode = string.IsNullOrWhiteSpace(_settings.ThemeMode) ? "System" : _settings.ThemeMode;
            _savedThemeMode = _themeMode;

            _bluetoothLabelArtistToken = BluetoothLabelMapping.NormalizeToken(_settings.BluetoothLabelArtist, BluetoothLabelMapping.TokenAppName);
            _savedBluetoothLabelArtistToken = _bluetoothLabelArtistToken;

            _bluetoothLabelTitleToken = BluetoothLabelMapping.NormalizeToken(_settings.BluetoothLabelTitle, BluetoothLabelMapping.TokenTranscription);
            _savedBluetoothLabelTitleToken = _bluetoothLabelTitleToken;

            _bluetoothLabelAlbumToken = BluetoothLabelMapping.NormalizeToken(_settings.BluetoothLabelAlbum, BluetoothLabelMapping.TokenTalkgroup);
            _savedBluetoothLabelAlbumToken = _bluetoothLabelAlbumToken;

            _bluetoothLabelComposerToken = BluetoothLabelMapping.NormalizeToken(_settings.BluetoothLabelComposer, BluetoothLabelMapping.TokenSite);
            _savedBluetoothLabelComposerToken = _bluetoothLabelComposerToken;

            _bluetoothLabelGenreToken = BluetoothLabelMapping.NormalizeToken(_settings.BluetoothLabelGenre, BluetoothLabelMapping.TokenReceiver);
            _savedBluetoothLabelGenreToken = _bluetoothLabelGenreToken;

            _mobileAudio = _settings.MobileAudio;
            _savedMobileAudio = _mobileAudio;
            _customRecoveryMinutes = _mobileAudio.InterruptionRecovery.CustomMinutes.ToString();
            OnPropertyChanged(nameof(MobileAudioModeIndex));
            OnPropertyChanged(nameof(AudioDisconnectionIndex));
            OnPropertyChanged(nameof(InterruptionRecoveryIndex));
            OnPropertyChanged(nameof(ShowsCustomRecoveryMinutes));
            OnPropertyChanged(nameof(CustomRecoveryMinutes));
            OnPropertyChanged(nameof(HasInvalidCustomRecoveryMinutes));

            // Ensure the pickers show the persisted selections when the Settings page opens.
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

            _what3WordsLinksEnabled = _settings.What3WordsLinksEnabled;
            _savedWhat3WordsLinksEnabled = _what3WordsLinksEnabled;

            _what3WordsApiKey = _settings.What3WordsApiKey;
            _savedWhat3WordsApiKey = _what3WordsApiKey;

            
            _audioStaticFilterEnabled = _settings.AudioStaticFilterEnabled;
            _savedAudioStaticFilterEnabled = _audioStaticFilterEnabled;

            _audioStaticAttenuatorVolume = _settings.AudioStaticAttenuatorVolume;
            _savedAudioStaticAttenuatorVolume = _audioStaticAttenuatorVolume;

            _audioToneFilterEnabled = _settings.AudioToneFilterEnabled;
            _savedAudioToneFilterEnabled = _audioToneFilterEnabled;

            _audioToneStrength = _settings.AudioToneStrength;
            _savedAudioToneStrength = _audioToneStrength;

            _audioToneSensitivity = _settings.AudioToneSensitivity;
            _savedAudioToneSensitivity = _audioToneSensitivity;

            _audioToneHighlightMinutes = _settings.AudioToneHighlightMinutes;
            _savedAudioToneHighlightMinutes = _audioToneHighlightMinutes;


            _logEnabled = AppLog.IsEnabled;

_addressDetectionEnabled = _settings.AddressDetectionEnabled;
            _savedAddressDetectionEnabled = _addressDetectionEnabled;


            _addressDetectionOpenMapsOnTap = _settings.AddressDetectionOpenMapsOnTap;
            _savedAddressDetectionOpenMapsOnTap = _addressDetectionOpenMapsOnTap;

            PublishDirectoryServers([]);

            _addressDetectionMinConfidencePercent = _settings.AddressDetectionMinConfidencePercent;
            _savedAddressDetectionMinConfidencePercent = _addressDetectionMinConfidencePercent;

            _addressDetectionMinAddressChars = _settings.AddressDetectionMinAddressChars;
            _savedAddressDetectionMinAddressChars = _addressDetectionMinAddressChars;

            _addressDetectionMaxCandidatesPerCall = _settings.AddressDetectionMaxCandidatesPerCall;
            _savedAddressDetectionMaxCandidatesPerCall = _addressDetectionMaxCandidatesPerCall;
            // Snapshots used by HasUnsavedSettings comparisons.
            _savedAuthServerBaseUrl = _authServerBaseUrl;
            _savedServerUrl = _serverUrl;

            _savedBasicAuthUsername = _basicAuthUsername;
            _savedBasicAuthPassword = _basicAuthPassword;

            _savedUseDefaultConnection = _useDefaultConnection;

            HasChanges = false;

            OnPropertyChanged(nameof(AuthServerBaseUrl));
            OnPropertyChanged(nameof(ServerUrl));
            OnPropertyChanged(nameof(UseDefaultConnection));
                OnPropertyChanged(nameof(CanSyncTalkgroupFilters));
            OnPropertyChanged(nameof(LogEnabled));
            OnPropertyChanged(nameof(BasicAuthUsername));
            OnPropertyChanged(nameof(BasicAuthPassword));
            OnPropertyChanged(nameof(AutoPlay));

            OnPropertyChanged(nameof(WindowsAutoConnectOnStart));
            OnPropertyChanged(nameof(MobileAutoConnectOnStart));
            OnPropertyChanged(nameof(WindowsStartWithWindows));

            OnPropertyChanged(nameof(ThemeMode));
            OnPropertyChanged(nameof(IsThemeSystem));
            OnPropertyChanged(nameof(IsThemeLight));
            OnPropertyChanged(nameof(IsThemeDark));

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

        private int _subscriptionSummaryRevision;

        private void UpdateSubscriptionSummaryFromSettings() => _ = RefreshSubscriptionSummaryAsync();

        private async Task RefreshSubscriptionSummaryAsync()
        {
            var revision = ++_subscriptionSummaryRevision;
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

            (bool IsActive, string Message, DateTime? RenewalUtc, string PriceId) summary;
            try
            {
                summary = await Task.Run(() => (_settings.SubscriptionLastStatusOk, _settings.SubscriptionLastMessage,
                    _settings.SubscriptionRenewalUtc, _settings.SubscriptionPriceId));
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Settings: subscription summary read failed ({ex.GetType().Name}).");
                return;
            }

            if (revision != _subscriptionSummaryRevision)
                return;

            if (!summary.IsActive)
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

            var planSummary = (summary.Message ?? string.Empty).Trim();
            var renewalUtc = summary.RenewalUtc;
            if (renewalUtc.HasValue &&
                planSummary.IndexOf("renewal", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var renewalDate = DateTime.SpecifyKind(renewalUtc.Value, DateTimeKind.Utc)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd");

                if (!string.IsNullOrWhiteSpace(renewalDate))
                    planSummary = $"{planSummary} - Renewal date: {renewalDate}";
            }


            if (string.IsNullOrEmpty(planSummary))
            {
                ShowSubscriptionSummary = false;
                SubscriptionSummary = string.Empty;
                return;
            }

            var priceText = summary.PriceId ?? string.Empty;
            priceText = priceText.Trim();

            if (!string.IsNullOrEmpty(priceText))
            {
                var looksLikeFriendlyPrice =
                    priceText.Contains(" ", StringComparison.Ordinal) ||
                    priceText.Contains("$", StringComparison.Ordinal);

                var planLabelPrefix = "Plan:";
                var planLabelText = string.Empty;
                if (planSummary.StartsWith(planLabelPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var afterPlanPrefix = planSummary.Substring(planLabelPrefix.Length).Trim();
                    var idxSeparator = afterPlanPrefix.IndexOf(" - ", StringComparison.Ordinal);
                    planLabelText = idxSeparator >= 0
                        ? afterPlanPrefix.Substring(0, idxSeparator).Trim()
                        : afterPlanPrefix.Trim();
                }

                var priceDuplicatesPlan =
                    !string.IsNullOrEmpty(planLabelText) &&
                    (string.Equals(planLabelText, priceText, StringComparison.OrdinalIgnoreCase) ||
                     planLabelText.Contains(priceText, StringComparison.OrdinalIgnoreCase) ||
                     priceText.Contains(planLabelText, StringComparison.OrdinalIgnoreCase));

                if (looksLikeFriendlyPrice &&
                    !priceDuplicatesPlan &&
                    !planSummary.Contains(priceText, StringComparison.OrdinalIgnoreCase))
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

        private sealed record NonConnectionSettings(
            bool AutoPlay,
            bool WindowsAutoConnectOnStart,
            bool WindowsStartWithWindows,
            bool MobileAutoConnectOnStart,
            string ThemeMode,
            string BluetoothLabelArtist,
            string BluetoothLabelTitle,
            string BluetoothLabelAlbum,
            string BluetoothLabelComposer,
            string BluetoothLabelGenre,
            MobileAudioPreferences MobileAudio,
            bool What3WordsLinksEnabled,
            string What3WordsApiKey,
            bool AudioStaticFilterEnabled,
            int AudioStaticAttenuatorVolume,
            bool AudioToneFilterEnabled,
            int AudioToneStrength,
            int AudioToneSensitivity,
            int AudioToneHighlightMinutes,
            bool AddressDetectionEnabled,
            bool AddressDetectionOpenMapsOnTap,
            int AddressDetectionMinConfidencePercent,
            int AddressDetectionMinAddressChars,
            int AddressDetectionMaxCandidatesPerCall);

        private async Task<bool> TrySaveNonConnectionSettingsAsync()
        {
            await _settingsSaveGate.WaitAsync();
            try
            {
                // Capture UI values together so a change during persistence stays dirty for the next save.
                var snapshot = new NonConnectionSettings(
                    _autoPlay,
                    _windowsAutoConnectOnStart,
                    _windowsStartWithWindows,
                    _mobileAutoConnectOnStart,
                    _themeMode,
                    _bluetoothLabelArtistToken,
                    _bluetoothLabelTitleToken,
                    _bluetoothLabelAlbumToken,
                    _bluetoothLabelComposerToken,
                    _bluetoothLabelGenreToken,
                    _mobileAudio,
                    _what3WordsLinksEnabled,
                    _what3WordsApiKey,
                    _audioStaticFilterEnabled,
                    _audioStaticAttenuatorVolume,
                    _audioToneFilterEnabled,
                    _audioToneStrength,
                    _audioToneSensitivity,
                    _audioToneHighlightMinutes,
                    _addressDetectionEnabled,
                    _addressDetectionOpenMapsOnTap,
                    _addressDetectionMinConfidencePercent,
                    _addressDetectionMinAddressChars,
                    _addressDetectionMaxCandidatesPerCall);
#if WINDOWS
                var updateStartup = snapshot.WindowsStartWithWindows != _savedWindowsStartWithWindows;
#endif
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await Task.Run(() => PersistNonConnectionSettings(snapshot));
                }
                catch (Exception ex)
                {
                    if (_mobileAudio != _savedMobileAudio)
                        SetMobileAudioSaveError(true);
                    if (ex is CredentialStorageException)
                    {
                        ServerValidationMessage = ex.Message;
                        ServerValidationIsError = true;
                        HasServerValidationResult = true;
                    }
                    AppLog.Add(() => $"Settings: save failed ({ex.GetType().Name}).");
                    return false;
                }

#if WINDOWS
                if (updateStartup)
                {
                    try
                    {
                        if (!await WindowsStartupManager.TrySetRunOnLoginAsync(snapshot.WindowsStartWithWindows))
                            AppLog.Add("Settings: applying the Windows startup setting failed.");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Add(() => $"Settings: applying the Windows startup setting failed ({ex.GetType().Name}).");
                    }
                }
#endif
                _savedAutoPlay = snapshot.AutoPlay;
                _savedWindowsAutoConnectOnStart = snapshot.WindowsAutoConnectOnStart;
                _savedWindowsStartWithWindows = snapshot.WindowsStartWithWindows;
                _savedMobileAutoConnectOnStart = snapshot.MobileAutoConnectOnStart;
                _savedThemeMode = snapshot.ThemeMode;
                _savedBluetoothLabelArtistToken = snapshot.BluetoothLabelArtist;
                _savedBluetoothLabelTitleToken = snapshot.BluetoothLabelTitle;
                _savedBluetoothLabelAlbumToken = snapshot.BluetoothLabelAlbum;
                _savedBluetoothLabelComposerToken = snapshot.BluetoothLabelComposer;
                _savedBluetoothLabelGenreToken = snapshot.BluetoothLabelGenre;
                _savedMobileAudio = snapshot.MobileAudio;
                SetMobileAudioSaveError(false);
                _savedWhat3WordsLinksEnabled = snapshot.What3WordsLinksEnabled;
                _savedWhat3WordsApiKey = snapshot.What3WordsApiKey;
                _savedAudioStaticFilterEnabled = snapshot.AudioStaticFilterEnabled;
                _savedAudioStaticAttenuatorVolume = snapshot.AudioStaticAttenuatorVolume;
                _savedAudioToneFilterEnabled = snapshot.AudioToneFilterEnabled;
                _savedAudioToneStrength = snapshot.AudioToneStrength;
                _savedAudioToneSensitivity = snapshot.AudioToneSensitivity;
                _savedAudioToneHighlightMinutes = snapshot.AudioToneHighlightMinutes;
                _savedAddressDetectionEnabled = snapshot.AddressDetectionEnabled;
                _savedAddressDetectionOpenMapsOnTap = snapshot.AddressDetectionOpenMapsOnTap;
                _savedAddressDetectionMinConfidencePercent = snapshot.AddressDetectionMinConfidencePercent;
                _savedAddressDetectionMinAddressChars = snapshot.AddressDetectionMinAddressChars;
                _savedAddressDetectionMaxCandidatesPerCall = snapshot.AddressDetectionMaxCandidatesPerCall;
                UpdateHasChanges();
                OnPropertyChanged(nameof(HasUnsavedSettings));
                OnPropertyChanged(nameof(ConnectionNeedsValidation));
                UpdateSubscriptionSummaryFromSettings();
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed.TotalMilliseconds >= 100)
                    AppLog.Add(() => $"Settings: save completed in {elapsed.TotalMilliseconds:F0} ms.");
                return true;
            }
            finally
            {
                _settingsSaveGate.Release();
            }
        }

        private void PersistNonConnectionSettings(NonConnectionSettings snapshot)
        {
            _settings.AutoPlay = snapshot.AutoPlay;
            _settings.WindowsAutoConnectOnStart = snapshot.WindowsAutoConnectOnStart;
            _settings.WindowsStartWithWindows = snapshot.WindowsStartWithWindows;
            _settings.MobileAutoConnectOnStart = snapshot.MobileAutoConnectOnStart;
            _settings.ThemeMode = snapshot.ThemeMode;
            _settings.BluetoothLabelArtist = snapshot.BluetoothLabelArtist;
            _settings.BluetoothLabelTitle = snapshot.BluetoothLabelTitle;
            _settings.BluetoothLabelAlbum = snapshot.BluetoothLabelAlbum;
            _settings.BluetoothLabelComposer = snapshot.BluetoothLabelComposer;
            _settings.BluetoothLabelGenre = snapshot.BluetoothLabelGenre;
            _settings.What3WordsLinksEnabled = snapshot.What3WordsLinksEnabled;
            if (snapshot.What3WordsApiKey != _savedWhat3WordsApiKey)
                _settings.What3WordsApiKey = snapshot.What3WordsApiKey;
            _settings.AudioStaticFilterEnabled = snapshot.AudioStaticFilterEnabled;
            _settings.AudioStaticAttenuatorVolume = snapshot.AudioStaticAttenuatorVolume;
            _settings.AudioToneFilterEnabled = snapshot.AudioToneFilterEnabled;
            _settings.AudioToneStrength = snapshot.AudioToneStrength;
            _settings.AudioToneSensitivity = snapshot.AudioToneSensitivity;
            _settings.AudioToneHighlightMinutes = snapshot.AudioToneHighlightMinutes;
            _settings.AddressDetectionEnabled = snapshot.AddressDetectionEnabled;
            _settings.AddressDetectionOpenMapsOnTap = snapshot.AddressDetectionOpenMapsOnTap;
            _settings.AddressDetectionMinConfidencePercent = snapshot.AddressDetectionMinConfidencePercent;
            _settings.AddressDetectionMinAddressChars = snapshot.AddressDetectionMinAddressChars;
            _settings.AddressDetectionMaxCandidatesPerCall = snapshot.AddressDetectionMaxCandidatesPerCall;
            if (snapshot.MobileAudio != _savedMobileAudio)
                _settings.MobileAudio = snapshot.MobileAudio;
        }

        private void ResetServerUrl()
        {
            ServerUrl = DefaultServerUrl;
            UseDefaultConnection = true;

            BasicAuthUsername = string.Empty;
            BasicAuthPassword = string.Empty;
        }

        private readonly ConnectionValidationRequests _connectionValidations = new();

        public ObservableCollection<ScannerConnectionRow> ScannerConnections => _mainViewModel.ScannerConnections;

        private bool _scannerEditorOpen;
        private ScannerConnectionRow? _editingScannerConnection;
        private string _scannerName = string.Empty;

        public bool ScannerEditorOpen => _scannerEditorOpen;
        public bool CanChooseScannerSource => _editingScannerConnection == null;
        public bool CanEditScannerAddress => _editingScannerConnection is { } row ? !row.Managed : IsCustomServerSelected;
        public bool CanSaveDirectoryConnection => CanChooseScannerSource &&
            _selectedDirectoryServer is { IsCustom: false, Url.Length: > 0 };
        public string ScannerEditorTitle => _editingScannerConnection == null ? "Add server" : "Edit " + _editingScannerConnection.Name;
        public string ScannerName
        {
            get => _scannerName;
            set
            {
                if (_scannerName != value)
                {
                    _scannerName = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        internal void RefreshScannerConnectionRows() => _mainViewModel.RefreshScannerConnectionRows();

        internal void OpenNewScannerConnection()
        {
            BeginScannerEditor(null);
            ApplySelectedDirectoryServer(CustomServerDirectoryEntry, syncServerUrl: false);
            ServerUrl = string.Empty;
            BasicAuthUsername = string.Empty;
            BasicAuthPassword = string.Empty;
        }

        internal void EditScannerConnection(ScannerConnectionRow row)
        {
            BeginScannerEditor(row);
            ServerUrl = row.Endpoint;
            BasicAuthUsername = BasicAuthPassword = string.Empty;
            try
            {
                if (_mainViewModel.TryGetConfiguredRecorderCredentials(row.Endpoint, out var username, out var password))
                {
                    BasicAuthUsername = username;
                    BasicAuthPassword = password;
                }
            }
            catch (CredentialStorageException) { }
        }

        private void BeginScannerEditor(ScannerConnectionRow? row)
        {
            _connectionValidations.Begin();
            _editingScannerConnection = row;
            _scannerEditorOpen = true;
            ScannerName = row?.Name ?? string.Empty;
            ClearValidationUiForDirtyConnection();
            NotifyScannerEditor();
        }

        internal void CloseScannerEditor()
        {
            _connectionValidations.Begin();
            _scannerEditorOpen = false;
            _editingScannerConnection = null;
            ServerUrl = _savedServerUrl;
            AuthServerBaseUrl = _savedAuthServerBaseUrl;
            BasicAuthUsername = _savedBasicAuthUsername;
            BasicAuthPassword = _savedBasicAuthPassword;
            IsValidatingServer = false;
            NotifyScannerEditor();
        }

        private void NotifyScannerEditor()
        {
            OnPropertyChanged(nameof(ScannerEditorOpen));
            OnPropertyChanged(nameof(CanChooseScannerSource));
            OnPropertyChanged(nameof(CanEditScannerAddress));
            OnPropertyChanged(nameof(CanSaveDirectoryConnection));
            OnPropertyChanged(nameof(ScannerEditorTitle));
        }

        internal void SetScannerConnectionEnabled(ScannerConnectionRow row, bool enabled) =>
            _mainViewModel.SetScannerConnectionEnabled(row, enabled);

        internal void MoveScannerConnection(ScannerConnectionRow row, int direction) =>
            _mainViewModel.MoveScannerConnection(row, direction);

        internal void RemoveScannerConnection(ScannerConnectionRow row, string? deleteFavoritesFor = null)
        {
            _mainViewModel.RemoveScannerConnection(row, deleteFavoritesFor);
            RecordSavedScannerSelection();
            CloseScannerEditor();
        }

        internal void SaveDirectoryConnection()
        {
            if (!CanSaveDirectoryConnection || _selectedDirectoryServer is not { } entry)
                return;

            _connectionValidations.Begin();
            _mainViewModel.SaveScannerConnection(entry.Url, entry.Name, entry.DirectoryId > 0 ? entry.DirectoryId : null, null,
                directorySource: entry.Source, directoryFirewall: entry.UsesApiFirewallCredentials);
            RecordSavedScannerSelection();
            CloseScannerEditor();
        }

        private void RecordSavedScannerSelection()
        {
            _savedAuthServerBaseUrl = _authServerBaseUrl;
            _savedServerUrl = _settings.ServerUrl ?? string.Empty;
            _savedUseDefaultConnection = HostedServerRules.IsProvidedDefaultServerUrl(_savedServerUrl);
            _savedBasicAuthUsername = _settings.BasicAuthUsername ?? string.Empty;
            _savedBasicAuthPassword = _settings.BasicAuthPassword ?? string.Empty;
            UpdateHasChanges();
        }

        private async Task SaveThenValidateServerUrlAsync()
        {
            _mainViewModel.RefreshScannerConnectionRows();
            var editor = _editingScannerConnection;
            if (_mainViewModel.ScannerConnectionEditError(editor, ServerUrl) is { } error)
            {
                ServerValidationMessage = error;
                ServerValidationIsError = HasServerValidationResult = true;
                return;
            }

            var request = _connectionValidations.Begin();
            var inputs = (ServerUrl, AuthServerBaseUrl, BasicAuthUsername, BasicAuthPassword,
                _selectedDirectoryServer?.Url, ScannerName, _editingScannerConnection);
            bool IsCurrent() => _connectionValidations.IsCurrent(request);
            bool OwnsInputs() => IsCurrent() &&
                inputs == (ServerUrl, AuthServerBaseUrl, BasicAuthUsername, BasicAuthPassword,
                    _selectedDirectoryServer?.Url, ScannerName, _editingScannerConnection);
            var previouslySaved = (_savedBasicAuthUsername, _savedBasicAuthPassword);
            var directory = editor == null && _selectedDirectoryServer is { IsCustom: false } entry ? entry : null;
            (string Endpoint, string Username, string Password)? validatedCustom = null;

            if (!await TrySaveNonConnectionSettingsAsync() || !OwnsInputs())
            {
                if (IsCurrent())
                    IsValidatingServer = false;
                return;
            }
            if (!await ValidateServerUrlAsync(IsCurrent, OwnsInputs,
                    (session, apply) => _connectionValidations.TryApply(request, session, OwnsInputs, apply),
                    (endpoint, user, password) => validatedCustom = (endpoint, user, password)) ||
                !IsCurrent() || ServerValidationIsError)
                return;

            try
            {
                var savedEndpoint = validatedCustom?.Endpoint ?? _settings.ServerUrl ?? string.Empty;
                (string Username, string Password)? customCredentials = validatedCustom is { } recorder
                    ? (recorder.Username, recorder.Password)
                    : null;
                var credentialsChanged = previouslySaved != (_settings.BasicAuthUsername, _settings.BasicAuthPassword);
                var newCustom = _mainViewModel.SaveScannerConnection(savedEndpoint, directory?.Name ?? (editor?.Managed == true ? editor.Name : ScannerName),
                    directory is { DirectoryId: > 0 } ? directory.DirectoryId : editor?.DirectoryId, editor?.Id,
                    customCredentials, credentialsChanged, directory?.Source, directory?.UsesApiFirewallCredentials);
                RecordSavedScannerSelection();
                CloseScannerEditor();
                if (newCustom != null)
                    await OfferDirectorySharingAsync(newCustom, newlySaved: true);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Could not save the scanner connection ({ex.GetType().Name}).");
                ServerValidationIsError = true;
                ServerValidationMessage = "The recorder responded, but its connection could not be saved.";
            }
        }

        private async Task<bool> ValidateServerUrlAsync(Func<bool> isCurrent, Func<bool> ownsInputs,
            Func<WordPressSession, Action, bool> publish, Action<string, string, string> recordCustomCredentials)
        {
            var selectedDirectoryServerAtStart = _selectedDirectoryServer;
            var selectedDirectoryServerUrlAtStart = (selectedDirectoryServerAtStart?.Url ?? string.Empty).Trim().TrimEnd('/');
            var selectedCustomAtStart = selectedDirectoryServerAtStart?.IsCustom == true;

            Interlocked.Increment(ref _suppressDirectorySelectionSync);

            var url = ServerUrl?.Trim();

            if (!string.IsNullOrEmpty(url))
            {
                var isDefaultServerForHeader = HostedServerRules.IsHostedStreamServerUrl(url);

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
                Interlocked.Decrement(ref _suppressDirectorySelectionSync);
                return false;
            }

            var isDefaultServer = HostedServerRules.IsHostedStreamServerUrl(url);
            var independentProbe = !isDefaultServer &&
                (_editingScannerConnection is { Managed: false } || _editingScannerConnection == null && selectedCustomAtStart);

            try
            {
                var hasAuth = !string.IsNullOrWhiteSpace(BasicAuthUsername);
                AppLog.Add(() => $"Validate: isDefault={isDefaultServer}, hasBasicAuth={hasAuth}");
            }
            catch
            {
            }


            try
            {
                if (!SavedConnectionUrl.TryParse(url, out _, out var safeUrl, out var embedded))
                    throw new HttpRequestException("Enter a valid HTTP or HTTPS scanner server address.");
                url = safeUrl;
                var destination = new Uri(safeUrl);
                if (!_mainViewModel.AllowsRecorderProbe(destination, independentProbe))
                    throw new HttpRequestException("Turn off Local Only Mode to connect to this server.");
                if (embedded != null && !independentProbe && HostedServerRules.RequiresApiFirewallCredentials(_settings, destination))
                    throw new FirewallAuthenticationException("Hosted server addresses must not contain embedded credentials.");
                var probeUsername = embedded?.Username ?? BasicAuthUsername.Trim();
                var probePassword = embedded?.Password ?? BasicAuthPassword;

                if (isDefaultServer)
                {
                    if (!HostedServerRules.SameOrigin(destination, new Uri(DefaultServerUrl)))
                        throw new FirewallAuthenticationException("Use the default HTTPS address for Joe's Scanner.");

                    var accountUsername = (BasicAuthUsername ?? string.Empty).Trim();
                    var accountPassword = TextNormalizationHelper.NormalizeSmartQuotes((BasicAuthPassword ?? string.Empty).Trim());

                    var accountBase = AuthServerBaseUrl;
                    var authUrl = SavedConnectionUrl.AccountEndpoint(accountBase);
                    var savedSession = WordPressSession.Capture(_settings);
                    var matchesSavedEndpoint = savedSession.Endpoint("auth") == authUrl;
                    var hasSavedAccount = matchesSavedEndpoint &&
                        _settings.TryGetServerCredentials(DefaultServerUrl, out _, out _);

                    if (string.IsNullOrWhiteSpace(accountUsername) || string.IsNullOrWhiteSpace(accountPassword))
                    {
                        if (accountUsername.Length == 0 && accountPassword.Length == 0 && hasSavedAccount &&
                            HostedServerRules.IsHostedStreamServerUrl(_settings.ServerUrl))
                        {
                            await _mainViewModel.ApplyHostedAccountAsync(string.Empty, false,
                                () => ownsInputs() && savedSession.IsCurrentAccount, () =>
                            {
                                publish(savedSession, () => _settings.ClearServerCredentials(DefaultServerUrl));
                            });
                            if (!ownsInputs())
                                return false;
                        }
                        var notice = !hasSavedAccount && matchesSavedEndpoint ? _settings.SavedCredentialWarning : null;
                        ServerValidationMessage = notice ?? "Enter your Joe's Scanner username and password, then tap Validate.";
                        ServerValidationIsError = true;
                        SetShowValidationPrefix(false);
                        UpdateSubscriptionSummaryFromSettings();
                        return false;
                    }

                    var session = matchesSavedEndpoint && savedSession.IsCurrent &&
                        (!_settings.TryGetServerCredentials(DefaultServerUrl, out var savedUser, out _) ||
                         string.Equals(savedUser, accountUsername, StringComparison.OrdinalIgnoreCase))
                        ? savedSession.Token : string.Empty;

                    var appVersion = AppInfo.Current.VersionString ?? string.Empty;
                    var appBuild = AppInfo.Current.BuildString ?? string.Empty;

                    var platform = DeviceInfo.Platform.ToString();
                    var type = DeviceInfo.Idiom.ToString();
                    var model = DeviceInfoHelper.CombineManufacturerAndModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
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

                        session_token = session
                    };

                    // Use UnsafeRelaxedJsonEscaping so special characters like apostrophes in
                    // passwords are sent as literal characters (e.g. ') rather than Unicode escapes
                    // (e.g. '). The default JavaScriptEncoder escapes ' which causes
                    // authentication failures for passwords containing apostrophes.
                    var authJsonOptions = new JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var json = JsonSerializer.Serialize(payload, authJsonOptions);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");

                    if (!ownsInputs() || !savedSession.IsCurrentAccount)
                        return false;
                    using var authResponseMessage = await _httpClient.PostAsync(authUrl, content);
                    var responseBody = await authResponseMessage.Content.ReadAsStringAsync();
                    if (!ownsInputs() || !savedSession.IsCurrentAccount)
                        return false;

                    if (!authResponseMessage.IsSuccessStatusCode)
                    {
                        var statusInt = (int)authResponseMessage.StatusCode;
                        ServerValidationMessage =
                            $"Auth server responded with HTTP {statusInt}.";
                        ServerValidationIsError = true;

                        UpdateSubscriptionSummaryFromSettings();
                        return false;
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

                        UpdateSubscriptionSummaryFromSettings();
                        return false;
                    }

                    if (!authResponse.Ok)
                    {
                        ServerValidationMessage = DiagnosticRedaction.SanitizeAuthenticationMessage(
                            authResponse.Message ?? "Account validation failed.", accountUsername, accountPassword, session);
                        ServerValidationIsError = true;

                        UpdateSubscriptionSummaryFromSettings();
                        return false;
                    }

                    var sub = authResponse.Subscription;
                    if (sub == null || !sub.Active)
                    {
                        ServerValidationMessage = "Account validated but subscription is not active.";
                        ServerValidationIsError = true;

                        UpdateSubscriptionSummaryFromSettings();
                        return false;
                    }

                    var planLabelRaw = sub.LevelLabel ?? sub.Level ?? string.Empty;
                    var periodEndRaw = sub.PeriodEndAt ?? sub.TrialEndsAt ?? string.Empty;
                    var statusRaw = sub.Status ?? string.Empty;
                    var priceIdRaw = sub.PriceId ?? string.Empty;

                    var planLabel = planLabelRaw.Trim();
                    var priceText = priceIdRaw.Trim();
                    var statusText = statusRaw.Trim().ToLowerInvariant();
                    var periodEnd = periodEndRaw.Trim();

                    var includePriceText =
                        !string.IsNullOrEmpty(priceText) &&
                        !string.Equals(planLabel, priceText, StringComparison.OrdinalIgnoreCase) &&
                        !planLabel.Contains(priceText, StringComparison.OrdinalIgnoreCase) &&
                        !priceText.Contains(planLabel, StringComparison.OrdinalIgnoreCase);

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
                        includePriceText &&
                        !string.IsNullOrEmpty(formattedDate))
                    {
                        planSummary = $"Plan: {planLabel} - {priceText} - {dateLabel} {formattedDate}";
                    }
                    else if (!string.IsNullOrEmpty(planLabel) &&
                             includePriceText)
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

                    var committed = false;
                    await _mainViewModel.ApplyHostedAccountAsync(accountUsername, !matchesSavedEndpoint,
                        () => ownsInputs() && savedSession.IsCurrentAccount, () =>
                    {
                        committed = publish(savedSession, () =>
                        {
                            _settings.AuthServerBaseUrl = accountBase;
                            _settings.SetServerCredentials(DefaultServerUrl, accountUsername, accountPassword);
                            _settings.ServerUrl = DefaultServerUrl;
                            string Safe(string? value) => DiagnosticRedaction.SanitizeAuthenticationMessage(
                                value, accountUsername, accountPassword, session, authResponse.SessionToken ?? "");
                            _settings.SaveHostedSubscription(new(true, true, sub.TierLevel, DateTime.UtcNow,
                                DateParseHelper.TryParseUtc(sub.ExpiresAt), null, Safe(planLabel), Safe(priceIdRaw), Safe(planSummary)));
                            _settings.AuthSessionToken = authResponse.SessionToken ?? string.Empty;
                        });
                        if (committed)
                            _mainViewModel.ServerUrl = DefaultServerUrl;
                    });
                    if (!committed || !ownsInputs())
                        return false;
                    ServerValidationMessage = "Joe's Scanner account validated.";
                    ServerValidationIsError = false;

                    // Mark connection as saved so plan and renewal info appears immediately after the first successful verify.
                    Interlocked.Increment(ref _suppressDirectorySelectionSync);
                    try
                    {
                        UseDefaultConnection = true;
                        ServerUrl = DefaultServerUrl;
                        BasicAuthUsername = accountUsername;
                        BasicAuthPassword = accountPassword;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _suppressDirectorySelectionSync);
                    }

                    _savedAuthServerBaseUrl = _authServerBaseUrl;
                    _savedServerUrl = _serverUrl;
                    _savedUseDefaultConnection = _useDefaultConnection;
                    _savedBasicAuthUsername = _basicAuthUsername;
                    _savedBasicAuthPassword = _basicAuthPassword;

                    UpdateHasChanges();

                    SetShowValidationPrefix(true);
                    UpdateSubscriptionSummaryFromSettings();
                    await ServerAuthentication.For(_settings).GetFirewallAuthorizationAsync(
                        new Uri(DefaultServerUrl), CancellationToken.None);
                }
                else if (!independentProbe && (HostedServerRules.RequiresApiFirewallCredentials(_settings, destination) ||
                         (selectedDirectoryServerAtStart?.UsesApiFirewallCredentials == true &&
                          Uri.TryCreate(selectedDirectoryServerUrlAtStart, UriKind.Absolute, out var selectedUri) &&
                          HostedServerRules.SameOrigin(destination, selectedUri))))
                {
                    if (!HostedServerRules.RequiresApiFirewallCredentials(_settings, destination))
                        throw new FirewallAuthenticationException("Could not load the server's firewall configuration. Refresh the server directory and try again.");

                    var subscription = await _subscriptionService.EnsureSubscriptionForRecorderAsync(url, CancellationToken.None);
                    if (!ownsInputs())
                        return false;
                    if (!subscription.IsAllowed)
                        throw new FirewallAuthenticationException(subscription.Message ?? "Verify your Joe's Scanner account in Settings.");

                    var directorySession = WordPressSession.Capture(_settings);
                    using var request = new HttpRequestMessage(HttpMethod.Head, destination);
                    using var response = await _scannerValidationClient.SendAsync(request);
                    if (!ownsInputs() || !directorySession.IsCurrentAccount)
                        return false;
                    if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotImplemented)
                        throw new HttpRequestException($"Scanner server verification failed (HTTP {(int)response.StatusCode}).");

                    if (!publish(directorySession, () =>
                        {
                            _settings.ClearServerCredentials(url);
                            _settings.ServerUrl = url;
                        }))
                        return false;
                    BasicAuthUsername = string.Empty;
                    BasicAuthPassword = string.Empty;
                    _mainViewModel.ServerUrl = url;
                    ServerValidationMessage = "Server verified with API-issued firewall credentials.";
                    ServerValidationIsError = false;
                }
                else
                {
                    var userProvidedAnyAuth =
                        !string.IsNullOrWhiteSpace(probeUsername) || !string.IsNullOrWhiteSpace(probePassword);

                    async Task<HttpResponseMessage> SendHeadAsync(bool includeAuth)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Head, destination);
                        req.Options.Set(ServerAuthenticationHandler.CustomProbe, true);
                        req.Options.Set(ServerAuthenticationHandler.IndependentProbe, independentProbe);

                        try
                        {
                            AppLog.Add(() => $"Validate: sending HEAD to {url} (auth={includeAuth})");
                        }
                        catch
                        {
                        }

                        if (includeAuth)
                        {
                            var u = probeUsername;
                            var p = probePassword;

                            if (!string.IsNullOrWhiteSpace(u) || !string.IsNullOrWhiteSpace(p))
                            {
                                var raw = $"{u}:{p}";
                                var bytes = Encoding.UTF8.GetBytes(raw);
                                var base64 = Convert.ToBase64String(bytes);

                                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);
                            }
                        }

                        return await _scannerValidationClient.SendAsync(req);
                    }

                    // For custom servers, always try without basic auth first.
                    using var noAuthResponse = await SendHeadAsync(includeAuth: false);
                    if (!ownsInputs())
                        return false;

                    try
                    {
                        AppLog.Add(() => $"Validate: response (no auth) HTTP {(int)noAuthResponse.StatusCode}");
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
                                $"Server reachable (HTTP {statusInt}).";
                        }

                        if (userProvidedAnyAuth)
                        {
                            await UiDialogs.AlertAsync("Credentials",
                                "This server responded without requiring basic auth. The connection will be saved without a username or password.",
                                "OK");
                            if (!ownsInputs())
                                return false;
                        }

                        ServerValidationIsError = false;
                        recordCustomCredentials(url, string.Empty, string.Empty);
                    }
                    else if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
                    {
                        // If auth is required and the user provided credentials, retry with basic auth.
                        if (!userProvidedAnyAuth)
                        {
                            var notice = string.Equals(SavedConnectionUrl.Scope(url),
                                SavedConnectionUrl.Scope(_settings.ServerUrl), StringComparison.Ordinal)
                                ? _settings.SavedCredentialWarning : null;
                            ServerValidationMessage = notice ??
                                $"Authentication required (HTTP {statusInt}). " +
                                "Enter a basic auth username and password, then tap Validate.";
                            ServerValidationIsError = true;
                        }
                        else
                        {
                            // Retry with auth.
                            using var authResponse = await SendHeadAsync(includeAuth: true);
                            if (!ownsInputs())
                                return false;

                            try
                            {
                                AppLog.Add(() => $"Validate: response (with auth) HTTP {(int)authResponse.StatusCode}");
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
                                        $"Server reachable (HTTP {authStatusInt}).";
                                }

                                ServerValidationIsError = false;
                                recordCustomCredentials(url, probeUsername, probePassword);
                            }
                            else if (authStatus == HttpStatusCode.Unauthorized || authStatus == HttpStatusCode.Forbidden)
                            {
                                ServerValidationMessage =
                                    $"Authentication failed (HTTP {authStatusInt}). " +
                                    "Check basic auth username and password and that the server or firewall is configured to allow this client.";
                                ServerValidationIsError = true;
                            }
                            else
                            {
                                ServerValidationMessage =
                                    $"Server responded with HTTP {authStatusInt}.";
                                ServerValidationIsError = true;
                            }
                        }
                    }
                    else
                    {
                        ServerValidationMessage =
                            $"Server responded with HTTP {statusInt}.";
                        ServerValidationIsError = true;
                    }
                }
            }
            catch (CredentialStorageException ex)
            {
                if (!isCurrent())
                    return false;
                ServerValidationMessage = ex.Message;
                ServerValidationIsError = true;
            }
            catch (FirewallAuthenticationException ex)
            {
                if (!isCurrent())
                    return false;
                ServerValidationMessage = ex.Message;
                ServerValidationIsError = true;
            }
            catch (HttpRequestException ex)
            {
                if (!isCurrent())
                    return false;
                try
                {
                    AppLog.Add(() => $"Validate: HTTP request failed ({ex.StatusCode}).");
                }
                catch
                {
                }

                ServerValidationMessage = $"Could not reach server: {DiagnosticRedaction.Sanitize(ex.Message)}";
                ServerValidationIsError = true;
            }
            catch (TaskCanceledException)
            {
                if (!isCurrent())
                    return false;
                try
                {
                    AppLog.Add(() => "Validate: timed out.");
                }
                catch
                {
                }

                ServerValidationMessage = "Server did not respond in time (validation timed out).";
                ServerValidationIsError = true;
            }
            catch (Exception ex)
            {
                if (!isCurrent())
                    return false;
                try
                {
                    AppLog.Add(() => $"Validate: unexpected exception ({ex.GetType().Name}).");
                }
                catch
                {
                }

                ServerValidationMessage = $"Unexpected error validating server: {DiagnosticRedaction.Sanitize(ex.Message)}";
                ServerValidationIsError = true;
            }
            finally
            {
                try
                {
                    // Keep the picker locked to the user-selected listed server during Validate.
                    // Only fall back to URL-based sync when Custom server was actually selected.
                    if (isCurrent() && !selectedCustomAtStart && selectedDirectoryServerAtStart != null)
                    {
                        var liveMatch = _directoryServers.FirstOrDefault(s =>
                            !s.IsCustom &&
                            string.Equals((s.Url ?? string.Empty).Trim().TrimEnd('/'), selectedDirectoryServerUrlAtStart, StringComparison.OrdinalIgnoreCase));

                        if (liveMatch != null)
                        {
                            _suppressDirectorySelection = true;
                            try
                            {
                                ApplySelectedDirectoryServer(liveMatch, syncServerUrl: false);
                            }
                            finally
                            {
                                _suppressDirectorySelection = false;
                            }
                        }
                        else
                        {
                            SyncSelectedDirectoryServerFromCurrentUrl();
                        }
                    }
                    else if (isCurrent())
                    {
                        SyncSelectedDirectoryServerFromCurrentUrl();
                    }
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Decrement(ref _suppressDirectorySelectionSync);
                }

                if (isCurrent())
                    IsValidatingServer = false;
            }
            return isCurrent() && !ServerValidationIsError;
        }

        private void UpdateHasChanges()
        {
            // Connection changes (manual Verify) vs non-connection changes (autosave).
            _hasUnsavedConnectionChanges =
                !string.Equals(_serverUrl, _savedServerUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_authServerBaseUrl, _savedAuthServerBaseUrl, StringComparison.Ordinal)
                || _useDefaultConnection != _savedUseDefaultConnection
                || !string.Equals(_basicAuthUsername, _savedBasicAuthUsername, StringComparison.Ordinal)
                || !string.Equals(_basicAuthPassword, _savedBasicAuthPassword, StringComparison.Ordinal);

            _hasUnsavedNonConnectionChanges =
                _autoPlay != _savedAutoPlay
                || _windowsAutoConnectOnStart != _savedWindowsAutoConnectOnStart
                || _windowsStartWithWindows != _savedWindowsStartWithWindows
                || _mobileAutoConnectOnStart != _savedMobileAutoConnectOnStart
                || !string.Equals(_themeMode, _savedThemeMode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_bluetoothLabelArtistToken, _savedBluetoothLabelArtistToken, StringComparison.Ordinal)
                || !string.Equals(_bluetoothLabelTitleToken, _savedBluetoothLabelTitleToken, StringComparison.Ordinal)
                || !string.Equals(_bluetoothLabelAlbumToken, _savedBluetoothLabelAlbumToken, StringComparison.Ordinal)
                || !string.Equals(_bluetoothLabelComposerToken, _savedBluetoothLabelComposerToken, StringComparison.Ordinal)
                || !string.Equals(_bluetoothLabelGenreToken, _savedBluetoothLabelGenreToken, StringComparison.Ordinal)
                || _mobileAudio != _savedMobileAudio
                || _what3WordsLinksEnabled != _savedWhat3WordsLinksEnabled
                || !string.Equals(_what3WordsApiKey ?? string.Empty, _savedWhat3WordsApiKey ?? string.Empty, StringComparison.Ordinal)
                || _audioStaticFilterEnabled != _savedAudioStaticFilterEnabled
                || _audioStaticAttenuatorVolume != _savedAudioStaticAttenuatorVolume
                || _audioToneFilterEnabled != _savedAudioToneFilterEnabled
                || _audioToneStrength != _savedAudioToneStrength
                || _audioToneSensitivity != _savedAudioToneSensitivity
                || _audioToneHighlightMinutes != _savedAudioToneHighlightMinutes
                || _addressDetectionEnabled != _savedAddressDetectionEnabled
                || _addressDetectionOpenMapsOnTap != _savedAddressDetectionOpenMapsOnTap
                || _addressDetectionMinConfidencePercent != _savedAddressDetectionMinConfidencePercent
                || _addressDetectionMinAddressChars != _savedAddressDetectionMinAddressChars
                || _addressDetectionMaxCandidatesPerCall != _savedAddressDetectionMaxCandidatesPerCall
;

            HasChanges = _hasUnsavedConnectionChanges || _hasUnsavedNonConnectionChanges;

            OnPropertyChanged(nameof(HasUnsavedSettings));
            OnPropertyChanged(nameof(ConnectionNeedsValidation));
        }

        public void DiscardConnectionChanges()
        {
            // Only discard connection changes. Other settings are autosaved.
            ServerUrl = _savedServerUrl;
            AuthServerBaseUrl = _savedAuthServerBaseUrl;
            UseDefaultConnection = _savedUseDefaultConnection;
            BasicAuthUsername = _savedBasicAuthUsername;
            BasicAuthPassword = _savedBasicAuthPassword;

            ClearValidationUiForDirtyConnection();

            UpdateHasChanges();
        }



        private void PublishDirectoryServers(IReadOnlyList<ServerDirectoryEntry> entries)
        {
            _suppressDirectorySelection = true;
            try
            {
                _directoryServers.Clear();
                var source = ServerDirectoryClient.Source(WordPressSession.Capture(_settings));
                var blocked = ScannerConnections.Where(row => row.Profile.Hold == DirectoryHold.Suspended &&
                    row.Profile.DirectoryCandidate == null && row.DirectoryId.HasValue).Select(row => row.DirectoryId!.Value).ToHashSet();
                var current = entries.Where(entry => (entry.Source == null || entry.Source == source) && !blocked.Contains(entry.DirectoryId)).ToArray();
                var archive = current.FirstOrDefault(entry => entry.IsOfficial && HostedServerRules.IsProvidedDefaultServerUrl(entry.Url));
                if (archive != null)
                    _directoryServers.Add(archive);
                else if (!_settings.IsCurrentDirectorySnapshotCached(source) &&
                    !ScannerConnections.Any(row => row.Managed && row.Profile.DirectoryBlocked && HostedServerRules.IsProvidedDefaultServerUrl(row.Endpoint)))
                    _directoryServers.Add(BuiltinJoeDirectoryEntry);
                foreach (var entry in current)
                {
                    if (!HostedServerRules.IsProvidedDefaultServerUrl(entry.Url))
                        _directoryServers.Add(entry);
                }
                _directoryServers.Add(CustomServerDirectoryEntry);
                SyncSelectedDirectoryServerFromCurrentUrl();
                DirectoryStatusText = current.Length == 0
                    ? "No public listings are available."
                    : $"Loaded {current.Length} server(s).";
            }
            finally
            {
                _suppressDirectorySelection = false;
            }
        }

        private void SyncSelectedDirectoryServerFromCurrentUrl()
        {
            try
            {
                var current = (ServerUrl ?? string.Empty).Trim().TrimEnd('/');
                var defaultUrl = DefaultServerUrl.Trim().TrimEnd('/');

                ServerDirectoryEntry? match;

                if (ScannerEditorOpen && (_editingScannerConnection is { Managed: false } ||
                    CanChooseScannerSource && (_selectedDirectoryServer?.IsCustom == true || string.IsNullOrWhiteSpace(current))))
                {
                    match = CustomServerDirectoryEntry;
                }
                else if (string.IsNullOrWhiteSpace(current))
                {
                    match = _directoryServers.FirstOrDefault(s =>
                        string.Equals((s.Url ?? string.Empty).Trim().TrimEnd('/'), defaultUrl, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    match = _directoryServers.FirstOrDefault(s =>
                        !s.IsCustom &&
                        string.Equals((s.Url ?? string.Empty).Trim().TrimEnd('/'), current, StringComparison.OrdinalIgnoreCase));
                }

                match ??= _directoryServers.FirstOrDefault(s => s.IsCustom) ?? CustomServerDirectoryEntry;

                _suppressDirectorySelection = true;
                try
                {
                    // Force a SelectedDirectoryServer property notification even when the matched
                    // object reference has not changed. On some platforms the Picker can render
                    // blank after the card becomes visible or after the ItemsSource is rebuilt
                    // unless the selected value is re-published to the binding layer.
                    ApplySelectedDirectoryServer(match, syncServerUrl: false, forceNotify: true);
                }
                finally
                {
                    _suppressDirectorySelection = false;
                }
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

            [JsonPropertyName("tier_level")]
            [JsonConverter(typeof(SubscriptionService.FlexibleIntConverter))]
            public int TierLevel { get; set; }

            [JsonPropertyName("expires_at")]
            public string? ExpiresAt { get; set; }

            [JsonPropertyName("period_end_at")]
            public string? PeriodEndAt { get; set; }

            [JsonPropertyName("trial_ends_at")]
            public string? TrialEndsAt { get; set; }
        }


    }
}
