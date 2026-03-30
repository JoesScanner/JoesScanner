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
        private readonly ISystemMediaService _systemMediaService;
        private readonly IHistoryLookupsCacheService _historyLookupsCacheService;
        private readonly IAuthObservedTriplesSyncService _authObservedTriplesSyncService;
        private readonly HttpClient _httpClient;
        private readonly FilterService _filterService = FilterService.Instance;

        private readonly IFilterProfileStore _filterProfileStore;

        private readonly ObservableCollection<FilterProfile> _settingsFilterProfiles = new();
        private readonly ObservableCollection<string> _settingsFilterProfileNameOptions = new();
        // One-time guard to avoid redoing expensive initialization work each time the user
        // visits Settings. (Profiles can still be refreshed via explicit actions.)
        private int _pageOpenStarted;

        private int _filterUiRefreshQueued;


        private bool _deferredUiLoaded;
        private bool _filtersBodyLoaded;
        private bool _audioFiltersBodyLoaded;
        private bool _addressDetectionBodyLoaded;
        private bool _bluetoothBodyLoaded;
        private bool _themeBodyLoaded;
        private bool _telemetryBodyLoaded;
        private bool _logBodyLoaded;

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

        public ObservableCollection<FilterRule> FilterRules => _filterService.Rules;


public ObservableCollection<FilterRule> FilterRulesUi => _filterService.Rules;

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
        private bool _mobileMixAudioWithOtherApps = true;
        private bool _savedMobileMixAudioWithOtherApps = true;

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

        // Telemetry (phone-home) - only configurable for custom servers.
        private bool _telemetryEnabled = true;
        private bool _savedTelemetryEnabled = true;

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
        private volatile bool _telemetryCardOpen;
        private volatile bool _logCardOpen;

        private int _autoSaveInProgress = 0;
        private int _autoSaveQueued = 0;

        private bool _hasUnsavedConnectionChanges;
        private bool _hasUnsavedNonConnectionChanges;

        public void SetSettingsCardOpenState(string cardKey, bool isOpen)
        {
            var key = (cardKey ?? string.Empty).Trim();

            if (string.Equals(key, "Autoplay", StringComparison.OrdinalIgnoreCase))
                _autoplayCardOpen = isOpen;
            else if (string.Equals(key, "Filters", StringComparison.OrdinalIgnoreCase))
                _filtersCardOpen = isOpen;
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
            else if (string.Equals(key, "Telemetry", StringComparison.OrdinalIgnoreCase))
                _telemetryCardOpen = isOpen;
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

            if (string.Equals(key, "Telemetry", StringComparison.OrdinalIgnoreCase))
                return _telemetryCardOpen;

            if (string.Equals(key, "Log", StringComparison.OrdinalIgnoreCase))
                return _logCardOpen;

            return false;
        }

        private void QueueAutosaveNonConnection(string cardKey)
        {
            // Only autosave when the relevant card is open.
            if (!IsCardOpenForAutosave(cardKey))
                return;

            // Debounce/serialize autosaves to avoid overlapping saves and excessive IO.
            if (Interlocked.Exchange(ref _autoSaveQueued, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Small debounce window to coalesce rapid toggles/picker changes.
                    await Task.Delay(150);

                    Interlocked.Exchange(ref _autoSaveQueued, 0);

                    if (Interlocked.CompareExchange(ref _autoSaveInProgress, 1, 0) != 0)
                        return;

                    try
                    {
                        await SaveNonConnectionSettingsAsync();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _autoSaveInProgress, 0);
                    }
                }
                catch
                {
                    // Autosave is best-effort; don't surface UI errors for it.
                }
            });
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
                OnPropertyChanged(nameof(EffectiveTelemetryEnabled));
                OnPropertyChanged(nameof(CanSyncTalkgroupFilters));
            }
        }

        // Manual sync is user-initiated and should always be allowed.
        // Telemetry settings only affect whether we can report/seed via the Auth API.
        public bool CanSyncTalkgroupFilters => !IsSyncingTalkgroupFilters;

        public SettingsViewModel(
            ISettingsService settingsService,
            MainViewModel mainViewModel,
            ITelemetryService telemetryService,
            ISystemMediaService systemMediaService,
            IFilterProfileStore filterProfileStore,
            IHistoryLookupsCacheService historyLookupsCacheService,
            IAuthObservedTriplesSyncService authObservedTriplesSyncService)
        {
            _settings = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
            _systemMediaService = systemMediaService ?? throw new ArgumentNullException(nameof(systemMediaService));

            _filterProfileStore = filterProfileStore ?? throw new ArgumentNullException(nameof(filterProfileStore));


try
{
    _filterService.RulesChanged += (_, __) => QueueFilterUiRefresh();
}
catch
{
}

            _historyLookupsCacheService = historyLookupsCacheService ?? throw new ArgumentNullException(nameof(historyLookupsCacheService));
            _authObservedTriplesSyncService = authObservedTriplesSyncService ?? throw new ArgumentNullException(nameof(authObservedTriplesSyncService));

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

            SaveCommand = new Command(async () => await SaveNonConnectionSettingsAsync());
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
                AppLog.Add(() => $"Settings: manual lookup sync requested. server='{_settings.ServerUrl}'");

                // Ensure the filter engine is in the correct per-server context before we mutate rules.
                try
                {
                    _filterService.SetServerUrl(_settings.ServerUrl);
                }
                catch
                {
                }

                // First seed from the local calls database so the filter tree can immediately reflect
                // anything the user has already observed on this device, even before the server responds.
                var localSeeded = 0;
                try
                {
                    var localObserved = await _authObservedTriplesSyncService.GetLocalObservedAsync(_settings.ServerUrl, DateTime.MinValue, CancellationToken.None);
                    localSeeded = localObserved?.Count ?? 0;
                    if (localObserved != null && localObserved.Count > 0)
                        _filterService.SeedFromObservedTriples(localObserved);
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
                    var triples = await _authObservedTriplesSyncService.TrySyncExchangeAsync(_settings.ServerUrl, force: true, CancellationToken.None);
                    received = triples?.Count ?? 0;
                    if (triples != null)
                        replaced = _filterService.ReplaceFromObservedTriples(triples);
                }
                catch
                {
                }

                // Manual sync should be silent. Log the results for diagnostics, but do not show UI dialogs.
                AppLog.Add(() => $"Settings: manual lookup sync complete. server='{_settings.ServerUrl}' receivers={r} sites={s} talkgroups={t} localObservedSeeded={localSeeded} observedReceived={received} rulesReplaced={replaced}");
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Settings: manual lookup sync failed. ex={ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                IsSyncingTalkgroupFilters = false;
            }
        }


        public Task OnPageOpenedAsync()
        {
            if (Interlocked.Exchange(ref _pageOpenStarted, 1) == 1)
                return Task.CompletedTask;


            try
            {
                _filterService.SetServerUrl(_settings.ServerUrl);
            }
            catch
            {
            }

            try
            {
                // IMPORTANT:
                // The Settings page must feel instant. Do not block the UI thread waiting for
                // SQLite reads or any other IO during page open.
                //
                // Kick off the expensive work and let the UI render immediately.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LoadSettingsFilterProfilesAsync(applySelectedProfile: true);
                    }
                    catch
                    {
                    }
                });

                // Refresh server directory list (best effort) without blocking page open.
                // Use the cached list immediately, then refresh only if stale.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(450);
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            try
                            {
                                await EnsureDirectoryServersFreshAsync(force: false);
                            }
                            catch
                            {
                            }
                        });
                    }
                    catch
                    {
                    }
                });

                // Let the page render first, then build the heavier UI sections.
                _ = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(50);
                        // Do not build or populate heavy card bodies here.
                        // Cards are lazy loaded on demand when expanded.
                    }
                    catch
                    {
                    }
                });
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.DebugWriteLine(() => $"SettingsViewModel.OnPageOpenedAsync failed: {ex}");
                }
                catch
                {
                }
            }
            finally
            {
                // Intentionally do not reset _pageOpenStarted.
                // This work is designed to be one-time per process so Settings stays snappy.
            }

            return Task.CompletedTask;
        }

        public bool FiltersBodyLoaded
        {
            get => _filtersBodyLoaded;
            set
            {
                if (_filtersBodyLoaded == value)
                    return;
                _filtersBodyLoaded = value;
                OnPropertyChanged();
            }
        }

        public bool AudioFiltersBodyLoaded
        {
            get => _audioFiltersBodyLoaded;
            set
            {
                if (_audioFiltersBodyLoaded == value)
                    return;
                _audioFiltersBodyLoaded = value;
                OnPropertyChanged();
            }
        }

        public bool AddressDetectionBodyLoaded
        {
            get => _addressDetectionBodyLoaded;
            set
            {
                if (_addressDetectionBodyLoaded == value)
                    return;
                _addressDetectionBodyLoaded = value;
                OnPropertyChanged();
            }
        }

        public bool BluetoothBodyLoaded
        {
            get => _bluetoothBodyLoaded;
            set
            {
                if (_bluetoothBodyLoaded == value)
                    return;
                _bluetoothBodyLoaded = value;
                OnPropertyChanged();
            }
        }

        public bool ThemeBodyLoaded
        {
            get => _themeBodyLoaded;
            set
            {
                if (_themeBodyLoaded == value)
                    return;
                _themeBodyLoaded = value;
                OnPropertyChanged();
            }
        }

        public bool TelemetryBodyLoaded
        {
            get => _telemetryBodyLoaded;
            set
            {
                if (_telemetryBodyLoaded == value)
                    return;
                _telemetryBodyLoaded = value;
                OnPropertyChanged();
            }
        }

        public bool LogBodyLoaded
        {
            get => _logBodyLoaded;
            set
            {
                if (_logBodyLoaded == value)
                    return;
                _logBodyLoaded = value;
                OnPropertyChanged();
            }
        }

        public async Task EnsureFiltersReadyAsync()
        {
            FiltersBodyLoaded = true;

            // Always switch the filter engine to the currently selected server in the UI,
            // not only the last saved settings value. This avoids stale or empty-state
            // mismatches right after the user changes servers and immediately opens Filters.
            try
            {
                _filterService.SetServerUrl(ServerUrl);
            }
            catch
            {
            }

            // First rebuild from the current live queue snapshot. This is immediate, local,
            // and avoids the first-open empty state when the user already has calls loaded.
            RebuildFilterRulesFromCurrentCalls_NoThrow();

            // If we already have rules after switching context/rebuilding locally, stop here.
            if (_filterService.Rules.Count > 0)
            {
                RefreshFilterRulesUi();
                return;
            }

            await SeedFiltersFromObservedAsync(ServerUrl, forceNetwork: false);
            RefreshFilterRulesUi();
        }

        public void EnsureSectionLoaded(string sectionKey)
        {
            if (string.Equals(sectionKey, "AudioFilters", StringComparison.OrdinalIgnoreCase)) AudioFiltersBodyLoaded = true;
            if (string.Equals(sectionKey, "AddressDetection", StringComparison.OrdinalIgnoreCase)) AddressDetectionBodyLoaded = true;
            if (string.Equals(sectionKey, "Bluetooth", StringComparison.OrdinalIgnoreCase)) BluetoothBodyLoaded = true;
            if (string.Equals(sectionKey, "Theme", StringComparison.OrdinalIgnoreCase)) ThemeBodyLoaded = true;
            if (string.Equals(sectionKey, "Telemetry", StringComparison.OrdinalIgnoreCase)) TelemetryBodyLoaded = true;
            if (string.Equals(sectionKey, "Log", StringComparison.OrdinalIgnoreCase)) LogBodyLoaded = true;
        }

        private async Task SeedFiltersFromObservedAsync(string? serverUrl, bool forceNetwork)
        {
            try
            {
                _ = forceNetwork;

                var serverKey = ServerKeyHelper.Normalize(serverUrl);
                if (string.IsNullOrWhiteSpace(serverKey))
                    return;

                // Always seed from the local calls database first so the card can populate immediately from
                // what this device has already discovered, even when the auth server is unavailable.
                try
                {
                    var localObserved = await _authObservedTriplesSyncService.GetLocalObservedAsync(serverKey, DateTime.MinValue, CancellationToken.None);
                    if (localObserved != null && localObserved.Count > 0)
                        _filterService.SeedFromObservedTriples(localObserved);
                }
                catch
                {
                }

                // Then fetch the canonical server view and merge it in. This keeps both sides converging
                // without making the settings page wait on network success.
                var triples = await _authObservedTriplesSyncService.TryFetchSeedAsync(serverKey, CancellationToken.None);
                if (triples == null || triples.Count == 0)
                    return;

                _filterService.SeedFromObservedTriples(triples);
            }
            catch
            {
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
            // NOTE:
            // On iOS (especially when autoplay/monitoring is active), doing synchronous file IO and JSON parsing
            // on the UI thread during navigation can cause hangs or watchdog kills. The underlying store is sync,
            // even though it exposes an async signature (Task.FromResult).
            //
            // Run the store call on a background thread, then marshal collection updates back to the UI thread.
	            var serverKey = ServerKeyHelper.Normalize(_settings.ServerUrl);
            var profiles = await Task.Run(async () => await _filterProfileStore.GetProfilesForServerAsync(serverKey, CancellationToken.None));

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

                var selected = _settingsFilterProfiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase));
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
	                ServerKey = ServerKeyHelper.Normalize(_settings.ServerUrl),
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

            if (!syncServerUrl || value == null)
                return;

            var url = (value.Url ?? string.Empty).Trim();
            if (url.Length > 0)
                ServerUrl = url;

            if (value != null && !value.IsCustom)
                _ = EnsureServerFirewallCredentialsAsync(value, false);
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

                OnPropertyChanged(nameof(ShowTelemetryCard));
                OnPropertyChanged(nameof(EffectiveTelemetryEnabled));
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


        public bool MobileMixAudioWithOtherApps
        {
            get => _mobileMixAudioWithOtherApps;
            set
            {
                if (_mobileMixAudioWithOtherApps == value)
                    return;

                _mobileMixAudioWithOtherApps = value;
                AppLog.Add(() => $"Settings: MobileMixAudioWithOtherApps changed to {_mobileMixAudioWithOtherApps}. Autosave queued.");
                OnPropertyChanged();
                UpdateHasChanges();
                QueueAutosaveNonConnection("Audio");
            }
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

        public bool ShowTelemetryCard => !HostedServerRules.IsProvidedDefaultServerUrl(_serverUrl);

        public bool EffectiveTelemetryEnabled => HostedServerRules.IsProvidedDefaultServerUrl(_serverUrl) || _telemetryEnabled;

        public bool TelemetryEnabled
        {
            get => _telemetryEnabled;
            set
            {
                if (_telemetryEnabled == value)
                    return;

                _telemetryEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectiveTelemetryEnabled));
                OnPropertyChanged(nameof(CanSyncTalkgroupFilters));
                UpdateHasChanges();
                QueueAutosaveNonConnection("Telemetry");
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

            var persistedServerUrl = (_settings.ServerUrl ?? string.Empty).Trim();
            _serverUrl = string.IsNullOrWhiteSpace(persistedServerUrl)
                ? DefaultServerUrl
                : persistedServerUrl;

            _basicAuthUsername = _settings.BasicAuthUsername ?? string.Empty;
            _basicAuthPassword = _settings.BasicAuthPassword ?? string.Empty;

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
            ApplyTheme(_themeMode);

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

            _mobileMixAudioWithOtherApps = _settings.MobileMixAudioWithOtherApps;
            _savedMobileMixAudioWithOtherApps = _mobileMixAudioWithOtherApps;
            OnPropertyChanged(nameof(MobileMixAudioWithOtherApps));

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

            _telemetryEnabled = _settings.TelemetryEnabled;
            _savedTelemetryEnabled = _telemetryEnabled;

            _logEnabled = AppLog.IsEnabled;

_addressDetectionEnabled = _settings.AddressDetectionEnabled;
            _savedAddressDetectionEnabled = _addressDetectionEnabled;


            _addressDetectionOpenMapsOnTap = _settings.AddressDetectionOpenMapsOnTap;
            _savedAddressDetectionOpenMapsOnTap = _addressDetectionOpenMapsOnTap;

            LoadDirectoryServersFromCache();

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
            OnPropertyChanged(nameof(ShowTelemetryCard));
                OnPropertyChanged(nameof(EffectiveTelemetryEnabled));
                OnPropertyChanged(nameof(CanSyncTalkgroupFilters));
                OnPropertyChanged(nameof(TelemetryEnabled));
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

            // Restore renewal info in the header summary when the last message is plan-only.
            // The auth check caches the renewal timestamp separately; include it here so users can see the next renewal at a glance.
            var renewalUtc = _settings.SubscriptionRenewalUtc;
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

            var priceText = _settings.SubscriptionPriceId ?? string.Empty;
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

        private async Task SaveNonConnectionSettingsAsync()
        {
            // Autosave target: non-connection settings only.
            // Connection credentials and active server selection are persisted only by Verify/Validate.

            _settings.AutoPlay = AutoPlay;

            _settings.WindowsAutoConnectOnStart = WindowsAutoConnectOnStart;
            _settings.WindowsStartWithWindows = WindowsStartWithWindows;
            _settings.MobileAutoConnectOnStart = MobileAutoConnectOnStart;

#if WINDOWS
            try
            {
                // Best effort. If this fails (policy restrictions, permissions, etc.),
                // the setting remains saved and the user can toggle again.
                var ok = await WindowsStartupManager.TrySetRunOnLoginAsync(_settings.WindowsStartWithWindows);
                if (!ok)
                {
                    AppLog.Add(() => $"Startup: Settings save attempted to set StartWithWindows={_settings.WindowsStartWithWindows} but it reported failure.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Startup: Settings save threw while applying StartWithWindows={_settings.WindowsStartWithWindows}. {ex.GetType().Name}: {ex.Message}");
            }
#endif

            _settings.ThemeMode = ThemeMode;

            _settings.BluetoothLabelArtist = _bluetoothLabelArtistToken;
            _settings.BluetoothLabelTitle = _bluetoothLabelTitleToken;
            _settings.BluetoothLabelAlbum = _bluetoothLabelAlbumToken;
            _settings.BluetoothLabelComposer = _bluetoothLabelComposerToken;
            _settings.BluetoothLabelGenre = _bluetoothLabelGenreToken;
            _settings.MobileMixAudioWithOtherApps = _mobileMixAudioWithOtherApps;
            AppLog.Add(() => $"Settings: MobileMixAudioWithOtherApps saved as {_settings.MobileMixAudioWithOtherApps}.");
            try
            {
                await _systemMediaService.RefreshAudioSessionAsync(_mainViewModel.AudioEnabled, "SettingsSave:MobileMixAudioWithOtherApps");
                AppLog.Add("Settings: requested live iOS audio-session refresh after MobileMixAudioWithOtherApps save.");
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Settings: live audio-session refresh failed after MobileMixAudioWithOtherApps save. {ex.GetType().Name}: {ex.Message}");
            }

            _settings.What3WordsLinksEnabled = _what3WordsLinksEnabled;
            _settings.What3WordsApiKey = _what3WordsApiKey;

            
            _settings.AudioStaticFilterEnabled = _audioStaticFilterEnabled;
            _settings.AudioStaticAttenuatorVolume = _audioStaticAttenuatorVolume;
            _settings.AudioToneFilterEnabled = _audioToneFilterEnabled;
            _settings.AudioToneStrength = _audioToneStrength;
            _settings.AudioToneSensitivity = _audioToneSensitivity;
            _settings.AudioToneHighlightMinutes = _audioToneHighlightMinutes;

            _settings.TelemetryEnabled = _telemetryEnabled;

_settings.AddressDetectionEnabled = _addressDetectionEnabled;
            _settings.AddressDetectionOpenMapsOnTap = _addressDetectionOpenMapsOnTap;

            _settings.AddressDetectionMinConfidencePercent = _addressDetectionMinConfidencePercent;
            _settings.AddressDetectionMinAddressChars = _addressDetectionMinAddressChars;
            _settings.AddressDetectionMaxCandidatesPerCall = _addressDetectionMaxCandidatesPerCall;
            // Update non-connection saved snapshots so only connection changes drive the Verify indicator.
            _savedAutoPlay = _autoPlay;
            _savedWindowsAutoConnectOnStart = _windowsAutoConnectOnStart;
            _savedWindowsStartWithWindows = _windowsStartWithWindows;
            _savedMobileAutoConnectOnStart = _mobileAutoConnectOnStart;

            _savedThemeMode = _themeMode;

            _savedBluetoothLabelArtistToken = _bluetoothLabelArtistToken;
            _savedBluetoothLabelTitleToken = _bluetoothLabelTitleToken;
            _savedBluetoothLabelAlbumToken = _bluetoothLabelAlbumToken;
            _savedBluetoothLabelComposerToken = _bluetoothLabelComposerToken;
            _savedBluetoothLabelGenreToken = _bluetoothLabelGenreToken;
            _savedMobileMixAudioWithOtherApps = _mobileMixAudioWithOtherApps;

            _savedWhat3WordsLinksEnabled = _what3WordsLinksEnabled;
            _savedWhat3WordsApiKey = _what3WordsApiKey;
            _savedAudioStaticFilterEnabled = _audioStaticFilterEnabled;
            _savedAudioStaticAttenuatorVolume = _audioStaticAttenuatorVolume;
            _savedAudioToneFilterEnabled = _audioToneFilterEnabled;
            _savedAudioToneStrength = _audioToneStrength;
            _savedAudioToneSensitivity = _audioToneSensitivity;
            _savedAudioToneHighlightMinutes = _audioToneHighlightMinutes;

            _savedTelemetryEnabled = _telemetryEnabled;



            _savedAddressDetectionEnabled = _addressDetectionEnabled;
            _savedAddressDetectionOpenMapsOnTap = _addressDetectionOpenMapsOnTap;

            _savedAddressDetectionMinConfidencePercent = _addressDetectionMinConfidencePercent;
            _savedAddressDetectionMinAddressChars = _addressDetectionMinAddressChars;
            _savedAddressDetectionMaxCandidatesPerCall = _addressDetectionMaxCandidatesPerCall;
            UpdateHasChanges();

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
            // Validation can change connection/auth state even when the selected server URL is the same.
            // Always stop the active connection after a successful validation so the next play/connect
            // starts from a clean validated state.
            var previousServerUrlSnapshot = _savedServerUrl ?? string.Empty;

            await SaveNonConnectionSettingsAsync();
            await ValidateServerUrlAsync();

            if (!ServerValidationIsError)
            {
                // IMPORTANT:
                // After a successful validation, mark the *current* connection state as the saved baseline.
                // The Validate button's red/blue state is driven by ConnectionNeedsValidation, which is
                // computed from _hasUnsavedConnectionChanges. If we don't refresh the saved snapshots and
                // recompute, custom server validations (especially no-auth ones) can leave the Validate
                // button stuck in a "needs validation" state.
                _savedAuthServerBaseUrl = _authServerBaseUrl;
                _savedServerUrl = _settings.ServerUrl ?? string.Empty;
                _savedUseDefaultConnection = _useDefaultConnection;
                _savedBasicAuthUsername = _settings.BasicAuthUsername ?? string.Empty;
                _savedBasicAuthPassword = _settings.BasicAuthPassword ?? string.Empty;

                UpdateHasChanges();

                try
                {
                    await _mainViewModel.StopMonitoringAsync();
                }
                catch
                {
                }

                var newServerUrl = _settings.ServerUrl ?? string.Empty;

                // When switching servers, also clear any existing main queue items and address alerts so
                // the user never falls back into calls that belong to a different server.
                if (!string.Equals(
                        NormalizeUrlForCompare(previousServerUrlSnapshot),
                        NormalizeUrlForCompare(newServerUrl),
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _mainViewModel.ClearQueueForServerSwitchAsync();
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
            var selectedDirectoryServerAtStart = _selectedDirectoryServer;
            var selectedDirectoryServerUrlAtStart = (selectedDirectoryServerAtStart?.Url ?? string.Empty).Trim().TrimEnd('/');
            var selectedCustomAtStart = selectedDirectoryServerAtStart?.IsCustom == true;

            Interlocked.Increment(ref _suppressDirectorySelectionSync);

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
                    var accountPassword = TextNormalizationHelper.NormalizeSmartQuotes((BasicAuthPassword ?? string.Empty).Trim());

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

                        session_token = _settings.AuthSessionToken
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

                    try
                    {
                        await EnsureServerFirewallCredentialsAsync(new ServerDirectoryEntry
                        {
                            DirectoryId = -1,
                            Name = "Joe's Scanner Default",
                            Url = DefaultServerUrl,
                            UsesApiFirewallCredentials = true,
                            IsOfficial = true
                        }, true);
                    }
                    catch
                    {
                    }

                    _mainViewModel.ServerUrl = DefaultServerUrl;


                    // Mark connection as saved so plan and renewal info appears immediately after the first successful verify.
                    Interlocked.Exchange(ref _suppressDirectorySelectionSync, 1);
                    try
                    {
                        UseDefaultConnection = true;
                        ServerUrl = DefaultServerUrl;
                        BasicAuthUsername = accountUsername;
                        BasicAuthPassword = accountPassword;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _suppressDirectorySelectionSync, 0);
                    }

                    _savedAuthServerBaseUrl = _authServerBaseUrl;
                    _savedServerUrl = _serverUrl;
                    _savedUseDefaultConnection = _useDefaultConnection;
                    _savedBasicAuthUsername = _basicAuthUsername;
                    _savedBasicAuthPassword = _basicAuthPassword;

                    UpdateHasChanges();

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
                            var p = TextNormalizationHelper.NormalizeSmartQuotes((BasicAuthPassword ?? string.Empty).Trim());

                            if (!string.IsNullOrWhiteSpace(u) || !string.IsNullOrWhiteSpace(p))
                            {
                                var raw = $"{u}:{p}";
                                var bytes = Encoding.UTF8.GetBytes(raw);
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
                                var effectivePass = TextNormalizationHelper.NormalizeSmartQuotes((BasicAuthPassword ?? string.Empty).Trim());

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
                try
                {
                    // Keep the picker locked to the user-selected listed server during Validate.
                    // Only fall back to URL-based sync when Custom server was actually selected.
                    if (!selectedCustomAtStart && selectedDirectoryServerAtStart != null)
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
                    else
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

                // If validation succeeded, treat the current connection settings as saved
                // so the Validate button returns to its normal (blue) state.
                if (!ServerValidationIsError)
                {
                    _savedAuthServerBaseUrl = _authServerBaseUrl;
                    _savedServerUrl = _serverUrl;
                    _savedUseDefaultConnection = _useDefaultConnection;
                    _savedBasicAuthUsername = _basicAuthUsername;
                    _savedBasicAuthPassword = _basicAuthPassword;

                    UpdateHasChanges();
                }

                IsValidatingServer = false;
            }
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
                || _mobileMixAudioWithOtherApps != _savedMobileMixAudioWithOtherApps
                || _what3WordsLinksEnabled != _savedWhat3WordsLinksEnabled
                || !string.Equals(_what3WordsApiKey ?? string.Empty, _savedWhat3WordsApiKey ?? string.Empty, StringComparison.Ordinal)
                || _audioStaticFilterEnabled != _savedAudioStaticFilterEnabled
                || _audioStaticAttenuatorVolume != _savedAudioStaticAttenuatorVolume
                || _audioToneFilterEnabled != _savedAudioToneFilterEnabled
                || _audioToneStrength != _savedAudioToneStrength
                || _audioToneSensitivity != _savedAudioToneSensitivity
                || _audioToneHighlightMinutes != _savedAudioToneHighlightMinutes
                || _telemetryEnabled != _savedTelemetryEnabled
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



        private ServerDirectoryEntry ReuseOrUpdateDirectoryEntry(ServerDirectoryEntry source)
        {
            if (source == null)
                return new ServerDirectoryEntry();

            if (source.IsCustom)
                return CustomServerDirectoryEntry;

            var normalizedUrl = (source.Url ?? string.Empty).Trim().TrimEnd('/');
            var builtinJoeUrl = DefaultServerUrl.Trim().TrimEnd('/');

            if (string.Equals(normalizedUrl, builtinJoeUrl, StringComparison.OrdinalIgnoreCase))
                return BuiltinJoeDirectoryEntry;

            var existing = _directoryServers.FirstOrDefault(s =>
                s != null &&
                !s.IsCustom &&
                string.Equals((s.Url ?? string.Empty).Trim().TrimEnd('/'), normalizedUrl, StringComparison.OrdinalIgnoreCase));

            var target = existing ?? new ServerDirectoryEntry();
            target.DirectoryId = source.DirectoryId;
            target.Name = source.Name ?? string.Empty;
            target.Url = source.Url ?? string.Empty;
            target.InfoUrl = source.InfoUrl ?? string.Empty;
            target.AreaLabel = source.AreaLabel ?? string.Empty;
            target.MapAnchor = source.MapAnchor ?? string.Empty;
            target.IsOfficial = source.IsOfficial;
            target.IsCustom = false;
            target.Badge = source.Badge ?? string.Empty;
            target.BadgeLabel = source.BadgeLabel ?? string.Empty;
            return target;
        }

        private void LoadDirectoryServersFromCache()
        {
            try
            {
                var existingEntries = _directoryServers.ToList();
                _directoryServers.Clear();
                _directoryServers.Add(BuiltinJoeDirectoryEntry);
                var cached = _settings.GetCachedDirectoryServers();
                foreach (var s in cached)
                {
                    if (s == null)
                        continue;

                    var cachedUrl = (s.Url ?? string.Empty).Trim().TrimEnd('/');
                    var builtinJoeUrl = DefaultServerUrl.Trim().TrimEnd('/');
                    if (string.Equals(cachedUrl, builtinJoeUrl, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var existing = existingEntries.FirstOrDefault(entry =>
                        entry != null &&
                        !entry.IsCustom &&
                        string.Equals((entry.Url ?? string.Empty).Trim().TrimEnd('/'), cachedUrl, StringComparison.OrdinalIgnoreCase));

                    _directoryServers.Add(ReuseOrUpdateDirectoryEntry(existing ?? s));
                }

                _directoryServers.Add(CustomServerDirectoryEntry);

                // Sync picker selection to current ServerUrl.
                SyncSelectedDirectoryServerFromCurrentUrl();

                DirectoryStatusText = _directoryServers.Count <= 2
                    ? "No servers loaded yet."
                    : $"Loaded {_directoryServers.Count - 2} server(s).";
            }
            catch
            {
                // Ignore cache load errors.
            }
        }

        private void SyncSelectedDirectoryServerFromCurrentUrl()
        {
            try
            {
                var current = (ServerUrl ?? string.Empty).Trim().TrimEnd('/');
                var defaultUrl = DefaultServerUrl.Trim().TrimEnd('/');

                ServerDirectoryEntry? match;

                if (string.IsNullOrWhiteSpace(current))
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

        private async Task RefreshDirectoryServersAsync()
        {
            if (Interlocked.Exchange(ref _directoryRefreshInProgress, 1) == 1)
                return;

            try
            {
                IsDirectoryLoading = true;
                DirectoryStatusText = "Loading servers...";

                var url = BuildAuthServerUrl("/wp-json/joes-scanner/v1/servers");

                using var resp = await _httpClient.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    DirectoryStatusText = $"Server list load failed: HTTP {(int)resp.StatusCode}.";
                    return;
                }

                ServerDirectoryResponseDto? parsed = null;
                try
                {
                    parsed = JsonSerializer.Deserialize<ServerDirectoryResponseDto>(
                        body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                }

                if (parsed == null || !parsed.Ok || parsed.Servers == null)
                {
                    DirectoryStatusText = "Server list returned an unexpected response.";
                    return;
                }

                var list = new List<ServerDirectoryEntry>();
                foreach (var s in parsed.Servers)
                {
                    if (s == null)
                        continue;

                    var entry = new ServerDirectoryEntry
                    {
                        DirectoryId = s.Id,
                        Name = s.Name ?? string.Empty,
                        Url = s.Url ?? string.Empty,
                        InfoUrl = s.InfoUrl ?? string.Empty,
                        AreaLabel = s.AreaLabel ?? string.Empty,
                        MapAnchor = s.MapAnchor ?? string.Empty,
                        IsOfficial = s.IsOfficial,
                        Badge = s.Badge ?? string.Empty,
                        BadgeLabel = s.BadgeLabel ?? string.Empty,
                        UsesApiFirewallCredentials = s.UsesApiFirewallCredentials
                    };

                    if (string.IsNullOrWhiteSpace(entry.Url))
                        continue;

                    list.Add(entry);
                }

                // Persist cache first.
                try { _settings.UpsertCachedDirectoryServers(list); } catch { }

                foreach (var entry in list)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Url))
                        continue;

                    if (!entry.UsesApiFirewallCredentials)
                    {
                        try { _settings.ClearServerFirewallCredentials(entry.Url); } catch { }
                    }
                }

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    _directoryServers.Clear();
                    _directoryServers.Add(BuiltinJoeDirectoryEntry);
                    foreach (var s in list)
                    {
                        if (s == null)
                            continue;

                        var listUrl = (s.Url ?? string.Empty).Trim().TrimEnd('/');
                        var builtinJoeUrl = DefaultServerUrl.Trim().TrimEnd('/');
                        if (string.Equals(listUrl, builtinJoeUrl, StringComparison.OrdinalIgnoreCase))
                            continue;

                        _directoryServers.Add(ReuseOrUpdateDirectoryEntry(s));
                    }

                    _directoryServers.Add(CustomServerDirectoryEntry);
                    SyncSelectedDirectoryServerFromCurrentUrl();
                });

                DirectoryStatusText = list.Count == 0
                    ? "No servers are currently listed."
                    : $"Loaded {list.Count} server(s).";

                try
                {
                    var selected = _selectedDirectoryServer;
                    if (selected != null && !selected.IsCustom)
                        await EnsureServerFirewallCredentialsAsync(selected, false);
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                DirectoryStatusText = "Directory load failed.";
                try { AppLog.Add(() => $"Directory: load failed. {ex.Message}"); } catch { }
            }
            finally
            {
                _lastDirectoryRefreshUtc = DateTime.UtcNow;
                IsDirectoryLoading = false;
                Interlocked.Exchange(ref _directoryRefreshInProgress, 0);
            }
        }


        private async Task EnsureServerFirewallCredentialsAsync(ServerDirectoryEntry entry, bool forceRefresh)
        {
            if (entry == null || entry.IsCustom)
                return;

            var serverUrl = (entry.Url ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(serverUrl))
                return;

            if (!entry.UsesApiFirewallCredentials)
            {
                try { _settings.ClearServerFirewallCredentials(serverUrl); } catch { }
                return;
            }

            var sessionToken = (_settings.AuthSessionToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sessionToken))
                return;

            if (!forceRefresh && _settings.TryGetServerFirewallCredentials(serverUrl, out _, out _))
                return;

            try
            {
                var payload = new
                {
                    session_token = sessionToken,
                    directory_id = entry.DirectoryId > 0 ? entry.DirectoryId : (int?)null,
                    server_url = serverUrl,
                    device_id = _settings.DeviceInstallId ?? string.Empty
                };

                using var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                using var response = await _httpClient.PostAsync(
                    BuildAuthServerUrl("/wp-json/joes-scanner/v1/server-firewall-credentials"),
                    content);

                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    try { AppLog.Add(() => $"Directory: firewall credentials load failed for {serverUrl}. HTTP {(int)response.StatusCode}."); } catch { }
                    return;
                }

                ServerFirewallCredentialsResponseDto? parsed = null;
                try
                {
                    parsed = JsonSerializer.Deserialize<ServerFirewallCredentialsResponseDto>(
                        body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                }

                if (parsed == null || !parsed.Ok)
                    return;

                var username = (parsed.Username ?? string.Empty).Trim();
                var password = (parsed.Password ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    return;

                _settings.SetServerFirewallCredentials(serverUrl, username, password);
            }
            catch (Exception ex)
            {
                try { AppLog.Add(() => $"Directory: firewall credentials load exception for {serverUrl}. {ex.Message}"); } catch { }
            }
        }

        private sealed class ServerDirectoryResponseDto
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("servers")]
            public List<ServerDirectoryServerDto>? Servers { get; set; }
        }

        private sealed class ServerFirewallCredentialsResponseDto
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("password")]
            public string? Password { get; set; }
        }

        private sealed class ServerDirectoryServerDto
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("info_url")]
            public string? InfoUrl { get; set; }

            [JsonPropertyName("area_label")]
            public string? AreaLabel { get; set; }

            [JsonPropertyName("map_anchor")]
            public string? MapAnchor { get; set; }

            [JsonPropertyName("is_official")]
            public bool IsOfficial { get; set; }

            [JsonPropertyName("badge")]
            public string? Badge { get; set; }

            [JsonPropertyName("badge_label")]
            public string? BadgeLabel { get; set; }

            [JsonPropertyName("uses_api_firewall_credentials")]
            public bool UsesApiFirewallCredentials { get; set; }
        }

        private string BuildAuthServerUrl(string path)
        {
            var baseUrl = (AuthServerBaseUrl ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "https://joesscanner.com";

            baseUrl = baseUrl.TrimEnd('/');

            var cleanPath = (path ?? string.Empty).Trim();
            if (!cleanPath.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                cleanPath = "/" + cleanPath;

            return baseUrl + cleanPath;
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


private void RebuildFilterRulesFromCurrentCalls_NoThrow()
{
    try
    {
        // Settings expects the filter list to be populated even if the user opens Settings
        // before the next live call arrives. Rebuild from the current queue snapshot.
        var snapshot = _mainViewModel?.Calls?.ToList();
        if (snapshot == null || snapshot.Count == 0)
            return;

        // Keep it bounded to avoid doing too much work on page open.
        const int MaxCalls = 250;
        if (snapshot.Count > MaxCalls)
            snapshot = snapshot.Skip(snapshot.Count - MaxCalls).ToList();

        foreach (var call in snapshot)
        {
            try
            {
                _filterService.EnsureRulesForCall(call);
            }
            catch
            {
            }
        }
    }
    catch
    {
    }
}

private void QueueFilterUiRefresh()
{
    if (Interlocked.Exchange(ref _filterUiRefreshQueued, 1) == 1)
        return;

    _ = MainThread.InvokeOnMainThreadAsync(async () =>
    {
        try
        {
            await Task.Delay(75);
        }
        catch
        {
        }

        try
        {
            RefreshFilterRulesUi();
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _filterUiRefreshQueued, 0);
        }
    });
}

private void RefreshFilterRulesUi()
{
}


    }
}