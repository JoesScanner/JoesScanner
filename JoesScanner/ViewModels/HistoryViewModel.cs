using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.ViewModels
{
    // View model for the History tab.
    // History is an explicit, fixed result set returned by search and never ingests new calls.
    // Playback and media controls are independent from the Main tab.
    public sealed class HistoryViewModel : BindableObject
    {
        private const string ServiceAuthUsername = "secappass";
        private const string ServiceAuthPassword = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

        private readonly ICallHistoryService _callHistoryService;
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly ISettingsService _settingsService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IFilterProfileStore _filterProfileStore;
        private readonly IHistoryLookupsCacheService _historyLookupsCacheService;
        private readonly HttpClient _audioHttpClient;

        private CancellationTokenSource? _audioCts;
        private CancellationTokenSource? _queuePlaybackCts;
        private CancellationTokenSource? _searchCts;
        private readonly SemaphoreSlim _playbackLock = new SemaphoreSlim(1, 1);


        private readonly SemaphoreSlim _lookupsLock = new SemaphoreSlim(1, 1);
        private bool _lookupsLoaded;
private HistorySearchFilters? _activeSearchFilters;
        private int _activeWindowSize = 35;
        private int _activeStartIndex;
        private int _activeTotalMatches;
        private bool _isPaging;

        private DateTime _nextAllowedNewerLoadUtc = DateTime.MinValue;
        private DateTime _nextAllowedOlderLoadUtc = DateTime.MinValue;

        // History tab is limited to the last 24 hours. Older content belongs in Archive.
        private DateTime _historyCutoffLocal = DateTime.MinValue;
        private DateTime _historyUpperLimitLocal = DateTime.MaxValue;
        private bool _historyCutoffReached;
        private bool _historyUpperLimitReached;
        private bool _historyCutoffNotified;

        // Snapshot time taken when a search is performed.
        // History paging and playback must never include calls that occur after this time.
        private DateTime _searchSnapshotUpperBoundLocal = DateTime.MaxValue;

        private DateTime _selectedDateFrom = DateTime.Today;
        private DateTime _selectedDateTo = DateTime.Today;

        private DateTime? _activeDateFromLocal;
        private DateTime? _activeDateToLocal;
        private bool _enforceHistory24HourLimit;
        private bool _show247Button;

        // Internal scroll buffering so the list feels continuous without explicit pagination UI.
        // Newer calls are "above" index 0, older calls are "below" index Calls.Count - 1.
        private readonly SemaphoreSlim _prefetchSemaphore = new SemaphoreSlim(1, 1);
        private List<CallItem>? _prefetchedNewerCalls;
        private List<CallItem>? _prefetchedOlderCalls;
        private int _prefetchedNewerStartIndex;
        private int _prefetchedOlderStartIndex;
        private Task? _prefetchNewerTask;
        private Task? _prefetchOlderTask;
        private const int PrefetchThresholdItems = 6;
        private int _searchGeneration;
        private int _searchWatchdogToken;
        private bool _isLoading;
        private bool _isQueuePlaybackRunning;

        // Snapshot playback model (History behaves like Main queue but from a frozen result set).
        // We keep a fixed window of calls around an active center index and shift by one item per call.
        private bool _isSnapshotSearchActive;
        private readonly List<CallItem> _snapshotCalls = new List<CallItem>();
        private int _snapshotCenterIndex = -1; // index into _snapshotCalls for the active call
        private int _visibleStartSnapshotIndex; // snapshot index represented by Calls[0]
        private int _visibleWindowSize = 25; // target number of items visible/bound
        private CallItem? _activeCall;

        private string _statusText = string.Empty;

        private bool _suppressLookupReload;
        private IReadOnlyDictionary<string, IReadOnlyList<HistoryLookupItem>> _talkgroupGroups
            = new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);

        private HistoryLookupItem? _selectedReceiver;
        private HistoryLookupItem? _selectedSite;
        private HistoryLookupItem? _selectedTalkgroup;

        private readonly ObservableCollection<FilterProfile> _filterProfiles;
        private FilterProfile? _selectedFilterProfile;

        private string _filterProfileNameDraft = string.Empty;

        private readonly ObservableCollection<string> _filterProfileNameOptions = new();
        private string _selectedFilterProfileNameOption = NoneProfileNameOption;
        private bool _isCustomFilterProfileName;

        private const string CustomProfileNameOption = "New";

        private const string NoneProfileNameOption = "None";

        private DateTime _selectedDate = DateTime.Today;
        private int _selectedHour;
        private int _selectedMinute;
        private int _selectedSecond;
        private bool _suppressTimeCascade;
        private bool _isTimePickerOpen;

        private int _currentIndex = -1;

        // 0 = 1x, 1 = 1.25x, 2 = 1.5x, 3 = 1.75x, 4 = 2x
        private double _historyPlaybackSpeedStep;

        // Service auth used on app.joesscanner.com, consistent with CallStreamService.

        public event Action<CallItem, ScrollToPosition>? ScrollRequested;

        public HistoryViewModel(
            ICallHistoryService callHistoryService,
            IAudioPlaybackService audioPlaybackService,
            ISettingsService settingsService,
            ISubscriptionService subscriptionService,
            IFilterProfileStore filterProfileStore,
            IHistoryLookupsCacheService historyLookupsCacheService)
        {
            _callHistoryService = callHistoryService ?? throw new ArgumentNullException(nameof(callHistoryService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));_filterProfileStore = filterProfileStore ?? throw new ArgumentNullException(nameof(filterProfileStore));
            _historyLookupsCacheService = historyLookupsCacheService ?? throw new ArgumentNullException(nameof(historyLookupsCacheService));

            Calls = new ObservableCollection<CallItem>();
            Receivers = new ObservableCollection<HistoryLookupItem>();
            Sites = new ObservableCollection<HistoryLookupItem>();
            Talkgroups = new ObservableCollection<HistoryLookupItem>();

            _filterProfiles = new ObservableCollection<FilterProfile>();

            _filterProfileNameOptions.Clear();
            _filterProfileNameOptions.Add(NoneProfileNameOption);
            _filterProfileNameOptions.Add(CustomProfileNameOption);
            _selectedFilterProfileNameOption = NoneProfileNameOption;
            _isCustomFilterProfileName = false;

            Calls.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(IsHistoryMediaButtonsEnabled));
                OnPropertyChanged(nameof(IsStopEnabled));
                RefreshCommandStates();
            };

            HourOptions = Enumerable.Range(0, 24).ToList();
            MinuteOptions = Enumerable.Range(0, 60).ToList();
            SecondOptions = Enumerable.Range(0, 60).ToList();

            _audioHttpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            var savedSpeed = AppStateStore.GetDouble("history_playback_speed_step", 0.0);
            HistoryPlaybackSpeedStep = savedSpeed;

            // Default time to two minutes ago.
            // This makes the default "quick search" more likely to return recent calls
            // (current-time searches can land beyond the newest completed call).
            var now = DateTime.Now.AddMinutes(-2);
            _selectedDate = now.Date;
            _selectedDateFrom = now.Date;
            _selectedDateTo = now.Date;
            _selectedHour = now.Hour;
            _selectedMinute = now.Minute;
            _selectedSecond = now.Second;

            SearchCommand = new Command(async () => await SearchAsync(), () => !IsLoading);
            LoadMoreOlderCommand = new Command(async () => await LoadMoreOlderAsync(), () => CanLoadMoreOlder);
            UnlimitedHistoryCommand = new Command(async () => await UnlimitedHistoryAsync(), () => !IsLoading);
            PlayFromCallCommand = new Command<CallItem>(async c => await PlayFromCallAsync(c), c => !IsLoading && c != null);
            PlayCommand = new Command(async () => await PlayAsync(), () => !IsLoading && Calls.Count > 0 && !_isQueuePlaybackRunning);
            StopCommand = new Command(async () => await StopAsync(), () => true);
            NextCommand = new Command(async () => await SkipNextAsync(), () => Calls.Count > 0);
            PreviousCommand = new Command(async () => await SkipPreviousAsync(), () => Calls.Count > 0);

            PlaybackSpeedDownCommand = new Command(DecreasePlaybackSpeedStep, () => Calls.Count > 0);
            PlaybackSpeedUpCommand = new Command(IncreasePlaybackSpeedStep, () => Calls.Count > 0);

            OpenTimePickerCommand = new Command(() => IsTimePickerOpen = true, () => !IsLoading);
            CancelTimePickerCommand = new Command(() => IsTimePickerOpen = false, () => !IsLoading);
            ConfirmTimePickerCommand = new Command(() =>
            {
                IsTimePickerOpen = false;
                OnPropertyChanged(nameof(SelectedTimeText));
            }, () => !IsLoading);
        

            _enforceHistory24HourLimit = ShouldEnforce24HourHistoryLimit();
                OnPropertyChanged(nameof(CanPickDateRange));
            OnPropertyChanged(nameof(CanPickDateRange));
}

        public ObservableCollection<CallItem> Calls { get; }
        public ObservableCollection<HistoryLookupItem> Receivers { get; }
        public ObservableCollection<HistoryLookupItem> Sites { get; }
        public ObservableCollection<HistoryLookupItem> Talkgroups { get; }

        public ObservableCollection<FilterProfile> FilterProfiles => _filterProfiles;


        public ObservableCollection<string> FilterProfileNameOptions => _filterProfileNameOptions;

        public string SelectedFilterProfileNameOption
        {
            get => _selectedFilterProfileNameOption;
            set
            {
                var newValue = string.IsNullOrWhiteSpace(value) ? NoneProfileNameOption : value;
                if (string.Equals(_selectedFilterProfileNameOption, newValue, StringComparison.Ordinal))
                    return;

                _selectedFilterProfileNameOption = newValue;
                if (string.Equals(newValue, NoneProfileNameOption, StringComparison.Ordinal))
                {
                    _isCustomFilterProfileName = false;
                    FilterProfileNameDraft = string.Empty;
                    _ = SelectFilterProfileAsync(null, apply: false);
                }
                else if (string.Equals(newValue, CustomProfileNameOption, StringComparison.Ordinal))
                {
                    _isCustomFilterProfileName = true;
                    FilterProfileNameDraft = string.Empty;
                }
                else
                {
                    _isCustomFilterProfileName = false;
                    FilterProfileNameDraft = newValue;
                    TrySelectFilterProfileFromNameOption(newValue);
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomFilterProfileName));
            }
        }

        public bool IsCustomFilterProfileName => _isCustomFilterProfileName;

        public FilterProfile? SelectedFilterProfile
        {
            get => _selectedFilterProfile;
            private set
            {
                if (ReferenceEquals(_selectedFilterProfile, value))
                    return;

                _selectedFilterProfile = value;
                FilterProfileNameDraft = _selectedFilterProfile?.Name ?? string.Empty;
                SyncProfileNameDropdownFromDraft();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedFilterProfileDisplay));
            }
        }

        public string SelectedFilterProfileDisplay => SelectedFilterProfile?.Name ?? "None";

        public string FilterProfileNameDraft
        {
            get => _filterProfileNameDraft;
            set
            {
                var newValue = value ?? string.Empty;
                if (string.Equals(_filterProfileNameDraft, newValue, StringComparison.Ordinal))
                    return;

                _filterProfileNameDraft = newValue;
                OnPropertyChanged();
            }
        }
        private void RefreshFilterProfileNameOptions()
        {
            _filterProfileNameOptions.Clear();
            _filterProfileNameOptions.Add(NoneProfileNameOption);
            foreach (var name in _filterProfiles.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))
                _filterProfileNameOptions.Add(name);

            _filterProfileNameOptions.Add(CustomProfileNameOption);
            SyncProfileNameDropdownFromDraft();
            OnPropertyChanged(nameof(FilterProfileNameOptions));
        }

        private void SyncProfileNameDropdownFromDraft()
        {
            var draft = (FilterProfileNameDraft ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(draft))
            {
                _selectedFilterProfileNameOption = NoneProfileNameOption;
                _isCustomFilterProfileName = false;
                OnPropertyChanged(nameof(SelectedFilterProfileNameOption));
                OnPropertyChanged(nameof(IsCustomFilterProfileName));
                return;
            }
            if (_filterProfileNameOptions.Any(n => string.Equals(n, draft, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedFilterProfileNameOption = _filterProfileNameOptions.First(n => string.Equals(n, draft, StringComparison.OrdinalIgnoreCase));
                _isCustomFilterProfileName = false;
            }
            else
            {
                _selectedFilterProfileNameOption = CustomProfileNameOption;
                _isCustomFilterProfileName = true;
            }

            OnPropertyChanged(nameof(SelectedFilterProfileNameOption));
            OnPropertyChanged(nameof(IsCustomFilterProfileName));
        }

        private void TrySelectFilterProfileFromNameOption(string nameOption)
        {
            if (string.IsNullOrWhiteSpace(nameOption))
                return;

            if (string.Equals(nameOption, NoneProfileNameOption, StringComparison.Ordinal) ||
                string.Equals(nameOption, CustomProfileNameOption, StringComparison.Ordinal))
                return;

            var match = _filterProfiles.FirstOrDefault(p => string.Equals(p.Name, nameOption, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                return;

            _ = SelectFilterProfileAsync(match, apply: true);
        }


        public IReadOnlyList<int> HourOptions { get; }
        public IReadOnlyList<int> MinuteOptions { get; }
        public IReadOnlyList<int> SecondOptions { get; }

        public Command SearchCommand { get; }
        public Command LoadMoreOlderCommand { get; }
        public Command UnlimitedHistoryCommand { get; }

        public bool Show247Button
        {
            get => _show247Button;
            private set
            {
                if (_show247Button == value)
                    return;
                _show247Button = value;
                OnPropertyChanged();
            }
        }

        public ICommand PlayFromCallCommand { get; }
        public Command PlayCommand { get; }
        public Command StopCommand { get; }
        public Command NextCommand { get; }
        public Command PreviousCommand { get; }

        public Command PlaybackSpeedDownCommand { get; }
        public Command PlaybackSpeedUpCommand { get; }

        public Command OpenTimePickerCommand { get; }
        public Command CancelTimePickerCommand { get; }
        public Command ConfirmTimePickerCommand { get; }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value)
                    return;

                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHistoryMediaButtonsEnabled));
                OnPropertyChanged(nameof(IsStopEnabled));
                RefreshCommandStates();
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (_statusText == value)
                    return;

                _statusText = value;
                OnPropertyChanged();
            }
        }

        public HistoryLookupItem? SelectedReceiver
        {
            get => _selectedReceiver;
            set
            {
                if (ReferenceEquals(_selectedReceiver, value))
                    return;

                _selectedReceiver = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedReceiverDisplay));
            }
        }

        public string SelectedReceiverDisplay =>
            !string.IsNullOrWhiteSpace(_selectedReceiver?.Label) ? _selectedReceiver.Label : "All";

        public HistoryLookupItem? SelectedSite
        {
            get => _selectedSite;
            set
            {
                if (ReferenceEquals(_selectedSite, value))
                    return;

                _selectedSite = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedSiteDisplay));
            }
        }

        public string SelectedSiteDisplay =>
            !string.IsNullOrWhiteSpace(_selectedSite?.Label) ? _selectedSite.Label : "All";

        public HistoryLookupItem? SelectedTalkgroup
        {
            get => _selectedTalkgroup;
            set
            {
                if (ReferenceEquals(_selectedTalkgroup, value))
                    return;

                _selectedTalkgroup = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedTalkgroupDisplay));
            }
        }

        public string SelectedTalkgroupDisplay =>
            !string.IsNullOrWhiteSpace(_selectedTalkgroup?.Label) ? _selectedTalkgroup.Label : "All";

        public DateTime SelectedDate
        {
            get => _selectedDate;
            set
            {
                if (_selectedDate.Date == value.Date)
                    return;

                _selectedDate = value.Date;
                OnPropertyChanged();
            }
        }

        public int SelectedHour
        {
            get => _selectedHour;
            set
            {
                var v = Math.Clamp(value, 0, 23);
                if (_selectedHour == v)
                    return;

                _selectedHour = v;
                OnPropertyChanged();

                if (_suppressTimeCascade)
                    return;

                OnPropertyChanged(nameof(SelectedTimeText));
                OnPropertyChanged(nameof(SelectedTimeDisplay));
                OnPropertyChanged(nameof(SelectedTime));
            }
        }

        public int SelectedMinute
        {
            get => _selectedMinute;
            set
            {
                var v = Math.Clamp(value, 0, 59);
                if (_selectedMinute == v)
                    return;

                _selectedMinute = v;
                OnPropertyChanged();

                if (_suppressTimeCascade)
                    return;

                OnPropertyChanged(nameof(SelectedTimeText));
                OnPropertyChanged(nameof(SelectedTimeDisplay));
                OnPropertyChanged(nameof(SelectedTime));
            }
        }

        public int SelectedSecond
        {
            get => _selectedSecond;
            set
            {
                var v = Math.Clamp(value, 0, 59);
                if (_selectedSecond == v)
                    return;

                _selectedSecond = v;
                OnPropertyChanged();

                if (_suppressTimeCascade)
                    return;

                OnPropertyChanged(nameof(SelectedTimeText));
                OnPropertyChanged(nameof(SelectedTimeDisplay));
                OnPropertyChanged(nameof(SelectedTime));
            }
        }

        public TimeSpan SelectedTime
        {
            get => new TimeSpan(SelectedHour, SelectedMinute, SelectedSecond);
            set
            {
                var h = Math.Clamp(value.Hours, 0, 23);
                var m = Math.Clamp(value.Minutes, 0, 59);
                var s = Math.Clamp(value.Seconds, 0, 59);

                // Avoid re-entrancy/feedback loops with TimePicker bindings.
                // Update backing fields in one shot, then raise the dependent notifications once.
                _suppressTimeCascade = true;
                try
                {
                    var changed = false;

                    if (_selectedHour != h) { _selectedHour = h; changed = true; }
                    if (_selectedMinute != m) { _selectedMinute = m; changed = true; }
                    if (_selectedSecond != s) { _selectedSecond = s; changed = true; }

                    if (!changed)
                        return;
                }
                finally
                {
                    _suppressTimeCascade = false;
                }

                OnPropertyChanged(nameof(SelectedHour));
                OnPropertyChanged(nameof(SelectedMinute));
                OnPropertyChanged(nameof(SelectedSecond));
                OnPropertyChanged(nameof(SelectedTimeText));
                OnPropertyChanged(nameof(SelectedTimeDisplay));
                OnPropertyChanged();
            }
        }

        public string SelectedTimeText =>
            string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", SelectedHour, SelectedMinute, SelectedSecond);

        public string SelectedTimeDisplay
        {
            get
            {
                // Display as a compact 24-hour time (no AM/PM).
                // Include seconds only if the user has explicitly set them.
                var dt = DateTime.Today.Add(SelectedTime);
                return SelectedSecond != 0
                    ? dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : dt.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
        }

        public DateTime SelectedDateFrom
        {
            get => _selectedDateFrom;
            set
            {
                var v = value.Date;
                if (_selectedDateFrom == v)
                    return;

                _selectedDateFrom = v;

                // Keep the range sane by default.
                if (_selectedDateTo < _selectedDateFrom)
                    _selectedDateTo = _selectedDateFrom;

                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedDateFromDisplay));
                OnPropertyChanged(nameof(SelectedDateTo));
                OnPropertyChanged(nameof(SelectedDateToDisplay));
            }
        }

        public DateTime SelectedDateTo
        {
            get => _selectedDateTo;
            set
            {
                var v = value.Date;
                if (_selectedDateTo == v)
                    return;

                _selectedDateTo = v;

                if (_selectedDateTo < _selectedDateFrom)
                    _selectedDateFrom = _selectedDateTo;

                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedDateToDisplay));
                OnPropertyChanged(nameof(SelectedDateFrom));
                OnPropertyChanged(nameof(SelectedDateFromDisplay));
            }
        }

        public string SelectedDateFromDisplay => SelectedDateFrom.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        public string SelectedDateToDisplay => SelectedDateTo.ToString("M/d/yyyy", CultureInfo.InvariantCulture);

        public bool CanPickDateRange => !_enforceHistory24HourLimit;

        public bool IsTimePickerOpen
        {
            get => _isTimePickerOpen;
            set
            {
                if (_isTimePickerOpen == value)
                    return;

                _isTimePickerOpen = value;
                OnPropertyChanged();
            }
        }

        public bool IsHistoryMediaButtonsEnabled => Calls.Count > 0;
        public bool IsStopEnabled => _isQueuePlaybackRunning;

        public bool IsPlayEnabled => !_isQueuePlaybackRunning && Calls.Count > 0;

        public bool ShowHistoryPlayButton => !_isQueuePlaybackRunning;

        public bool ShowHistoryStopButton => _isQueuePlaybackRunning;


        public bool IsHistoryPlaybackRunning => _isQueuePlaybackRunning;

        public CallItem? ActiveCall => _activeCall;

        public bool CanLoadMoreOlder
        {
            get
            {
                if (_isSnapshotSearchActive)
                    return false;

                return
                    _activeSearchFilters != null &&
                    !IsLoading &&
                    !_isPaging &&
                    !_historyCutoffReached &&
                    _activeTotalMatches > 0 &&
                    (_activeStartIndex + Calls.Count) < _activeTotalMatches;
            }
        }

        public bool CanLoadMoreNewer
        {
            get
            {
                if (_isSnapshotSearchActive)
                    return false;

                return
                    _activeSearchFilters != null &&
                    !IsLoading &&
                    !_isPaging &&
                    !_historyUpperLimitReached &&
                    _activeTotalMatches > 0 &&
                    _activeStartIndex > 0;
            }
        }

        public double HistoryPlaybackSpeedStep
        {
            get => _historyPlaybackSpeedStep;
            set
            {
                var clamped = value;
                if (clamped < 0) clamped = 0;
                if (clamped > 4) clamped = 4;
                clamped = Math.Round(clamped);

                if (Math.Abs(_historyPlaybackSpeedStep - clamped) < 0.001)
                    return;

                _historyPlaybackSpeedStep = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HistoryPlaybackSpeedLabel));
                AppStateStore.SetDouble("history_playback_speed_step", _historyPlaybackSpeedStep);
            }
        }

        public string HistoryPlaybackSpeedLabel =>
            _historyPlaybackSpeedStep switch
            {
                1 => "1.25x",
                2 => "1.5x",
                3 => "1.75x",
                4 => "2x",
                _ => "1x"
            };

        public async Task OnPageOpenedAsync()
{
            AppLog.Add(() => "History: OnPageOpenedAsync invoked.");
            RefreshShow247Button();

            // Refresh History gating every time the page opens so switching servers (hosted vs custom)
            // immediately updates the UI (date pickers) without requiring a restart or a Search.
            try
            {
                void RefreshGating()
                {
                    _enforceHistory24HourLimit = ShouldEnforce24HourHistoryLimit();
                    OnPropertyChanged(nameof(CanPickDateRange));

                    var nowLocal = DateTime.Now;
                    _historyCutoffLocal = _enforceHistory24HourLimit ? nowLocal.AddHours(-24) : DateTime.MinValue;
                    _historyUpperLimitLocal = _enforceHistory24HourLimit ? nowLocal : DateTime.MaxValue;
                }

                if (MainThread.IsMainThread)
                    RefreshGating();
                else
                    await MainThread.InvokeOnMainThreadAsync(RefreshGating);
            }
            catch
            {
            }

    // Only mute the Main tab audio. Do not stop or disconnect the live queue.
    try
    {
        QueueControlBus.RequestSetMainAudioMuted(true);
    }
    catch (Exception ex)
    {
        AppLog.Add(() => $"History: RequestSetMainAudioMuted failed. ex={ex.GetType().Name}: {ex.Message}");
    }

    // Load locally stored profiles first so the UI is usable even when the server is unreachable
    // or when lookups would otherwise block on a network timeout.
    try
    {
        await LoadFilterProfilesAsync(applySelectedProfile: true);
    }
    catch
    {
    }

    // Lookups depend on the server and may fail when offline.
    // We WANT them loaded on page open. If the first attempt races startup/connect,
    // retry once after a short delay so the dropdowns are populated without requiring a tap.
    await EnsureLookupsLoadedAsync(forceReload: true);

    if (Receivers.Count == 0 || Sites.Count == 0 || Talkgroups.Count == 0)
    {
        AppLog.Add(() => $"History: lookups incomplete after first load. receivers={Receivers.Count} sites={Sites.Count} talkgroups={Talkgroups.Count}. Retrying once.");
        await Task.Delay(500);
        await EnsureLookupsLoadedAsync(forceReload: true);
    }
}

        public async Task EnsureLookupsLoadedAsync(bool forceReload = false)
        {
            if (!forceReload && _lookupsLoaded)
                return;

            await _lookupsLock.WaitAsync();
            try
            {
                if (!forceReload && _lookupsLoaded)
                    return;

                AppLog.Add(() => $"History: loading lookups. current receiver={SelectedReceiver?.Label ?? ""} site={SelectedSite?.Label ?? ""} talkgroup={SelectedTalkgroup?.Label ?? ""}");
                await LoadLookupsAsyncSafe();
                _lookupsLoaded = (Receivers.Count > 0 && Sites.Count > 0 && Talkgroups.Count > 0);
                AppLog.Add(() => $"History: lookups loaded. receivers={Receivers.Count} sites={Sites.Count} talkgroups={Talkgroups.Count}");
            }
            finally
            {
                _lookupsLock.Release();
            }
        }

private async Task LoadLookupsAsyncSafe()
        {
            try
            {
                await LoadLookupsAsync();
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"History: LoadLookupsAsyncSafe failed. ex={ex.GetType().Name}: {ex.Message}");
            }
        }


public async Task LoadFilterProfilesAsync(bool applySelectedProfile)
        {
            // NOTE:
            // The underlying filter profile store performs synchronous file IO + JSON parsing/migration,
            // even though it exposes an async signature. If we run it on the UI thread during navigation,
            // iOS can appear to "hang" when opening Settings / switching tabs.
            //
            // Run the store call on a background thread, then marshal collection + selection updates back
            // to the UI thread.
            var profiles = await Task.Run(async () => await _filterProfileStore.GetProfilesAsync(CancellationToken.None));

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _filterProfiles.Clear();
                foreach (var p in profiles)
                    _filterProfiles.Add(p);

                RefreshFilterProfileNameOptions();

                var selectedId = AppStateStore.GetString("history_selected_filter_profile_id", string.Empty);
                if (string.IsNullOrWhiteSpace(selectedId))
                {
                    SelectedFilterProfile = null;
                    return;
                }

                var selected = _filterProfiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.Ordinal));
                SelectedFilterProfile = selected;

                if (applySelectedProfile && selected != null)
                    ApplyProfileToFilters(selected);
            });
        }

        public async Task SelectFilterProfileAsync(FilterProfile? profile, bool apply)
        {
            SelectedFilterProfile = profile;

            var id = profile?.Id ?? string.Empty;
            AppStateStore.SetString("history_selected_filter_profile_id", id);

            if (apply && profile != null)
                ApplyProfileToFilters(profile);
        }

        public async Task<FilterProfile?> SaveCurrentFiltersAsync(string name)
        {
            name = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var existing = _filterProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var profileId = existing?.Id;

            var profile = new FilterProfile
            {
                Id = profileId ?? string.Empty,
                Name = name,
                Filters = new FilterProfileFilters
                {
                    ReceiverValue = SelectedReceiver?.Value,
                    ReceiverLabel = SelectedReceiver?.Label,
                    SiteValue = SelectedSite?.Value,
                    SiteLabel = SelectedSite?.Label,
                    TalkgroupValue = SelectedTalkgroup?.Value,
                    TalkgroupLabel = SelectedTalkgroup?.Label,
                    SelectedTime = SelectedTime
                },
                Rules = FilterService.Instance.GetActiveStateRecords()
            };

            await _filterProfileStore.SaveOrUpdateAsync(profile, CancellationToken.None);
            await LoadFilterProfilesAsync(applySelectedProfile: false);

            var saved = _filterProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (saved != null)
                await SelectFilterProfileAsync(saved, apply: false);

            return saved;
        }

        public async Task<bool> RenameSelectedProfileAsync(string newName)
        {
            var current = SelectedFilterProfile;
            if (current == null)
                return false;

            newName = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName))
                return false;

            await _filterProfileStore.RenameAsync(current.Id, newName, CancellationToken.None);
            await LoadFilterProfilesAsync(applySelectedProfile: false);

            var refreshed = _filterProfiles.FirstOrDefault(p => string.Equals(p.Id, current.Id, StringComparison.Ordinal));
            if (refreshed != null)
                await SelectFilterProfileAsync(refreshed, apply: false);

            return true;
        }

        public async Task<bool> DeleteSelectedProfileAsync()
        {
            var current = SelectedFilterProfile;
            if (current == null)
                return false;

            await _filterProfileStore.DeleteAsync(current.Id, CancellationToken.None);
            AppStateStore.SetString("history_selected_filter_profile_id", string.Empty);
            SelectedFilterProfile = null;

            await LoadFilterProfilesAsync(applySelectedProfile: false);
            return true;
        }

        private void ApplyProfileToFilters(FilterProfile profile)
        {
            if (profile == null)
                return;

            // Profiles are shared with Settings. Apply mute/disable snapshot as well.
            try
            {
                FilterService.Instance.ApplyStateRecords(profile.Rules ?? new List<FilterRuleStateRecord>(), resetOthers: true);
            }
            catch
            {
            }

            var f = profile.Filters;
            if (f == null)
                return;

            SelectedReceiver = FindMatch(Receivers, f.ReceiverValue, f.ReceiverLabel);
            SelectedSite = FindMatch(Sites, f.SiteValue, f.SiteLabel);
            SelectedTalkgroup = FindMatch(Talkgroups, f.TalkgroupValue, f.TalkgroupLabel);

            if (f.SelectedTime != null)
                SelectedTime = f.SelectedTime.Value;
        }

        private static HistoryLookupItem? FindMatch(ObservableCollection<HistoryLookupItem> items, string? value, string? label)
        {
            if (items == null || items.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(value))
            {
                var byValue = items.FirstOrDefault(i => string.Equals(i.Value, value, StringComparison.Ordinal));
                if (byValue != null)
                    return byValue;
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                var byLabel = items.FirstOrDefault(i => string.Equals(i.Label, label, StringComparison.Ordinal));
                if (byLabel != null)
                    return byLabel;
            }

            return items.FirstOrDefault();
        }

        public async Task OnPageClosedAsync()
        {
            try { _searchCts?.Cancel(); } catch { }
            try { await StopAsync(); } catch { }
            QueueControlBus.RequestSetMainAudioMuted(false);
        }

        private async Task LoadLookupsAsync()
        {
            // Lookups must load even if other operations are in-flight (search/paging/etc).
            // EnsureLookupsLoadedAsync serializes calls via _lookupsLock.
            AppLog.Add(() => $"History: LoadLookupsAsync start. IsLoading={IsLoading} calls={Calls.Count} receivers={Receivers.Count} sites={Sites.Count} talkgroups={Talkgroups.Count}");

            // 1) Apply cached lookups immediately if available.
            try
            {
                var cached = await _historyLookupsCacheService.GetCachedAsync(CancellationToken.None);
                if (cached != null)
                {
                    await MainThread.InvokeOnMainThreadAsync(() => ApplyLookups(cached, preserveSelections: true));

                    // Best-effort background refresh. Never blocks UI or stream connection.
                    _ = Task.Run(async () =>
                    {
                        await _historyLookupsCacheService.PreloadAsync(
                            forceReload: false,
                            reason: "history_open",
                            cancellationToken: CancellationToken.None);

                        var refreshed = await _historyLookupsCacheService.GetCachedAsync(CancellationToken.None);
                        if (refreshed != null)
                            await MainThread.InvokeOnMainThreadAsync(() => ApplyLookups(refreshed, preserveSelections: true));
                    });

                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"History: lookup cache read failed. ex={ex.GetType().Name}: {ex.Message}");
            }

            // 2) No cache: fetch now (best effort).
            try
            {
                var data = await _callHistoryService.GetLookupDataAsync(currentFilters: null, CancellationToken.None);
                await MainThread.InvokeOnMainThreadAsync(() => ApplyLookups(data, preserveSelections: false));
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"History: LoadLookupsAsync failed. ex={ex.GetType().Name}: {ex.Message}");
                StatusText = "Error loading filters";
            }
        }

        private void ApplyLookups(HistoryLookupData data, bool preserveSelections)
        {
            _suppressLookupReload = true;
            try
            {
                var prevReceiverId = preserveSelections ? SelectedReceiver?.Value : null;
                var prevSiteId = preserveSelections ? SelectedSite?.Value : null;
                var prevTalkgroupId = preserveSelections ? SelectedTalkgroup?.Value : null;

                ReplaceItems(Receivers, data.Receivers);
                ReplaceItems(Sites, data.Sites);
                ReplaceItems(Talkgroups, data.Talkgroups);

                _talkgroupGroups = data.TalkgroupGroups;

                AppLog.Add(() => $"History: lookups applied. receivers={Receivers.Count} sites={Sites.Count} talkgroups={Talkgroups.Count}");

                SelectedReceiver = prevReceiverId != null ? Receivers.FirstOrDefault(x => x.Value == prevReceiverId) : null;
                SelectedSite = prevSiteId != null ? Sites.FirstOrDefault(x => x.Value == prevSiteId) : null;
                SelectedTalkgroup = prevTalkgroupId != null ? Talkgroups.FirstOrDefault(x => x.Value == prevTalkgroupId) : null;

                SelectedReceiver ??= Receivers.FirstOrDefault();
                SelectedSite ??= Sites.FirstOrDefault();
                SelectedTalkgroup ??= Talkgroups.FirstOrDefault();
            }
            finally
            {
                _suppressLookupReload = false;
            }
        }

        private async Task ReloadSitesAndTalkgroupsAsync()
        {
            if (IsLoading)
                return;

            IsLoading = true;
            try
            {
                StatusText = "Loading sites and talkgroups";

                var filters = new HistorySearchFilters
                {
                    Receiver = SelectedReceiver
                };

                var data = await _callHistoryService.GetLookupDataAsync(filters, CancellationToken.None);

                _suppressLookupReload = true;
                try
                {
                    ReplaceItems(Sites, data.Sites);
                    ReplaceItems(Talkgroups, data.Talkgroups);

                    SelectedSite = Sites.FirstOrDefault();
                    SelectedTalkgroup = Talkgroups.FirstOrDefault();
                }
                finally
                {
                    _suppressLookupReload = false;
                }

                StatusText = string.Empty;
            }
            catch
            {
                StatusText = "Error loading sites and talkgroups";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ReloadTalkgroupsAsync()
        {
            if (IsLoading)
                return;

            IsLoading = true;
            try
            {
                StatusText = "Loading talkgroups";

                var filters = new HistorySearchFilters
                {
                    Receiver = SelectedReceiver,
                    Site = SelectedSite
                };

                var data = await _callHistoryService.GetLookupDataAsync(filters, CancellationToken.None);

                _suppressLookupReload = true;
                try
                {
                    ReplaceItems(Talkgroups, data.Talkgroups);
                    SelectedTalkgroup = Talkgroups.FirstOrDefault();
                }
                finally
                {
                    _suppressLookupReload = false;
                }

                StatusText = string.Empty;
            }
            catch
            {
                StatusText = "Error loading talkgroups";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static void ReplaceItems(ObservableCollection<HistoryLookupItem> target, IReadOnlyList<HistoryLookupItem> source)
        {
            target.Clear();
            foreach (var it in source)
            {
                target.Add(it);
            }
        }

        private DateTime GetSelectedTargetLocal()
        {
            var date = SelectedDateFrom.Date;
            return new DateTime(
                date.Year,
                date.Month,
                date.Day,
                SelectedHour,
                SelectedMinute,
                SelectedSecond,
                DateTimeKind.Local);
        }

        private bool IsWithinHistoryWindow(CallItem call)
        {
            if (call == null)
                return false;

            if (_historyCutoffLocal != DateTime.MinValue && call.Timestamp < _historyCutoffLocal)
                return false;

            var effectiveUpper = GetEffectiveHistoryUpperBoundLocal();
            if (effectiveUpper != DateTime.MaxValue && call.Timestamp > effectiveUpper)
                return false;

            return true;
        }

        private DateTime GetEffectiveHistoryUpperBoundLocal()
        {
            try
            {
                // Always cap history at the time the search was performed.
                // This prevents the History tab from turning into a live monitoring page.
                var upper = _historyUpperLimitLocal;
                if (_searchSnapshotUpperBoundLocal != DateTime.MaxValue && _searchSnapshotUpperBoundLocal < upper)
                    upper = _searchSnapshotUpperBoundLocal;

                return upper;
            }
            catch
            {
                return _historyUpperLimitLocal;
            }
        }

        private bool ShouldEnforce24HourHistoryLimit()
        {
            // Only gate Joe's hosted server. Custom servers are never gated.
            if (!IsHostedJoeServerSelected())
                return false;

            // Tier rules:
            //   0 = no access (enforced elsewhere by hosted auth gating)
            //   1 = access, but history/archive is limited to the last 24 hours
            //   2 = access, no 24 hour limit
            return _settingsService.SubscriptionTierLevel < 2;
        }

        void RefreshShow247Button()
        {
            try
            {
                // Custom servers are always full access.
                // Joe's hosted server only shows this for premium (tier >= 2).
                Show247Button = !IsHostedJoeServerSelected() || _settingsService.SubscriptionTierLevel >= 2;
            }
            catch
            {
                Show247Button = false;
            }
        }


        private bool IsHostedJoeServerSelected()
        {
            try
            {
                var raw = (_settingsService.ServerUrl ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    return false;

                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                    return false;

                return string.Equals(uri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async Task NotifyHistoryLimitAsync()
        {
            if (!_enforceHistory24HourLimit)
                return;

            if (_historyCutoffNotified)
                return;

            _historyCutoffNotified = true;

            try
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        var page = Application.Current?.MainPage;
                        if (page == null)
                            return;

                        await page.DisplayAlert(
                            "History limit",
                            "Your account is limited to the last 24 hours. Upgrade to a premium account for full access.",
                            "OK");
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
        }

        
        private async Task<bool> EnsureHostedSubscriptionFreshAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!IsHostedJoeServerSelected())
                    return true;

                var result = await _subscriptionService.EnsureSubscriptionAsync(cancellationToken);
                if (result == null || !result.IsAllowed)
                {
                    var msg = (result?.Message ?? "Subscription not active").Trim();
                    if (string.IsNullOrWhiteSpace(msg))
                        msg = "Subscription not active";

                    StatusText = msg;
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                StatusText = "Subscription check timed out";
                return false;
            }
            catch
            {
                StatusText = "Subscription check failed";
                return false;
            }
        }

private async Task SearchAsync()
        {
            if (IsLoading)
                return;

            IsLoading = true;
            try
            {
                StatusText = "Searching";

                // UI watchdog: Android can occasionally hang inside HttpClient despite cancellation.
                // This guarantees History is never stuck in a permanent Searching state.
                var watchdogToken = Interlocked.Increment(ref _searchWatchdogToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        if (IsLoading && _searchWatchdogToken == watchdogToken)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                if (IsLoading && _searchWatchdogToken == watchdogToken)
                                {
                                    StatusText = "Search timed out";
                                    IsLoading = false;
                                    RefreshCommandStates();
                                }
                            });
                        }
                    }
                    catch
                    {
                    }
                });

                // Cancel any prior in-flight search (best effort).
                try { _searchCts?.Cancel(); } catch { }
                try { _searchCts?.Dispose(); } catch { }
                _searchCts = new CancellationTokenSource();
                // Hard safety timeout so the UI never gets stuck in a perpetual "Searching" state.
                _searchCts.CancelAfter(TimeSpan.FromSeconds(25));
                var searchToken = _searchCts.Token;

                // A History search intentionally takes ownership of playback.
                // Force the Main tab into a true stopped state (same effect as tapping Stop)
                // so returning to the Main tab requires an explicit Play to resume.
                QueueControlBus.RequestStopMainQueue();

                await StopAsync();


                // Make sure the local tier level reflects the current server state before applying History gating.
                if (!await EnsureHostedSubscriptionFreshAsync(searchToken))
                {
                    RefreshCommandStates();
                    return;
                }

                RefreshShow247Button();

                _enforceHistory24HourLimit = ShouldEnforce24HourHistoryLimit();
                OnPropertyChanged(nameof(CanPickDateRange));
                var nowLocal = DateTime.Now;
                _searchSnapshotUpperBoundLocal = nowLocal;
                _historyCutoffLocal = _enforceHistory24HourLimit ? nowLocal.AddHours(-24) : DateTime.MinValue;
                _historyUpperLimitLocal = _enforceHistory24HourLimit ? nowLocal : DateTime.MaxValue;
                _historyCutoffReached = false;
                _historyUpperLimitReached = false;
                _historyCutoffNotified = false;

                // Premium/custom servers can also narrow History by date or date range.
                // This is applied client-side as a soft window over the server's calljson stream.
                if (!_enforceHistory24HourLimit)
                {
                    var from = SelectedDateFrom.Date;
                    var to = SelectedDateTo.Date;
                    if (to < from)
                    {
                        var tmp = from;
                        from = to;
                        to = tmp;
                    }

                    _historyCutoffLocal = from;
                    _historyUpperLimitLocal = to.AddDays(1).AddTicks(-1);
                }

                var target = GetSelectedTargetLocal();

                if (_enforceHistory24HourLimit && target < _historyCutoffLocal)
                {
                    StatusText = "Your account is limited to the last 24 hours. Upgrade to a premium account for full access.";
                    await NotifyHistoryLimitAsync();
                    RefreshCommandStates();
                    return;
                }

                if (!_enforceHistory24HourLimit)
                {
                    if (target < _historyCutoffLocal || target > _historyUpperLimitLocal)
                    {
                        StatusText = "Selected date/time is outside the chosen date range.";
                        RefreshCommandStates();
                        return;
                    }
                }

                var filters = new HistorySearchFilters
                {
                    Receiver = (SelectedReceiver != null && !string.Equals(SelectedReceiver.Label, "All", StringComparison.OrdinalIgnoreCase))
                        ? SelectedReceiver
                        : null,
                    Site = (SelectedSite != null && !string.Equals(SelectedSite.Label, "All", StringComparison.OrdinalIgnoreCase))
                        ? SelectedSite
                        : null,
                    Talkgroup = (SelectedTalkgroup != null && !string.Equals(SelectedTalkgroup.Label, "All", StringComparison.OrdinalIgnoreCase))
                        ? SelectedTalkgroup
                        : null
                };

                _activeSearchFilters = filters;

                // Reset scroll buffers for a new search so prefetch does not reuse stale pages.
                _searchGeneration++;
                _prefetchedNewerCalls = null;
                _prefetchedOlderCalls = null;
                _prefetchNewerTask = null;
                _prefetchOlderTask = null;

                const int windowSize = 35;
                _activeWindowSize = windowSize;
                // Persist the date range used for this search so paging stays constrained.
                _activeDateFromLocal = _historyCutoffLocal == DateTime.MinValue ? (DateTime?)null : _historyCutoffLocal;
                var effectiveUpper = GetEffectiveHistoryUpperBoundLocal();
                _activeDateToLocal = effectiveUpper == DateTime.MaxValue ? (DateTime?)null : effectiveUpper;

                var result = await _callHistoryService.SearchAroundAsync(target, filters, windowSize, _activeDateFromLocal, _activeDateToLocal, searchToken);

                _activeStartIndex = result.StartIndex;
                _activeTotalMatches = result.TotalMatches;

                var filtered = result.Calls
                    .Where(IsWithinHistoryWindow)
                    .ToList();

                // History should start at the call closest to the selected time (top of list),
                // and proceed downward through newer calls.
                var ordered = filtered
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                var closest = ordered
                    .Select((c, idx) => new { Call = c, Index = idx })
                    .OrderBy(x => Math.Abs((x.Call.Timestamp - target).TotalSeconds))
                    .FirstOrDefault();

                var anchorIndex = closest?.Index ?? 0;
                anchorIndex = Math.Clamp(anchorIndex, 0, Math.Max(0, ordered.Count - 1));

                var linear = ordered
                    .Skip(anchorIndex)
                    .ToList();

                Calls.Clear();
                foreach (var c in linear)
                    Calls.Add(c);

                _currentIndex = -1;

                if (Calls.Count == 0)
                {
                    StatusText = _enforceHistory24HourLimit
                        ? "No calls found in the last 24 hours. Upgrade to a premium account for full access."
                        : "No calls found for the selected date range.";
                    RefreshCommandStates();
                    return;
                }

				// History must be able to continue paging while playing.
				// Do not enter snapshot mode, which freezes the list and stops paging.
				_isSnapshotSearchActive = false;
				_snapshotCalls.Clear();

				await RunOnMainThreadAsync(() =>
				{
					StatusText = _enforceHistory24HourLimit
						? $"{result.TotalMatches} match(es), showing {Calls.Count}. History is limited to the last 24 hours for your account. Upgrade to premium for full access."
						: $"{result.TotalMatches} match(es), showing {Calls.Count}. Showing {SelectedDateFromDisplay} to {SelectedDateToDisplay}.";

					RefreshCommandStates();
				}).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await RunOnMainThreadAsync(() =>
                {
                    StatusText = "Search timed out";
                    RefreshCommandStates();
                }).ConfigureAwait(false);
            }
            catch
            {
                await RunOnMainThreadAsync(() =>
                {
                    StatusText = "Search failed";
                    RefreshCommandStates();
                }).ConfigureAwait(false);
            }
            finally
            {
                // iOS is sensitive to PropertyChanged / ChangeCanExecute calls happening off the UI thread.
                // Ensure IsLoading (and the command state it drives) is always cleared on the main thread.
                await RunOnMainThreadAsync(() =>
                {
                    IsLoading = false;
                    RefreshCommandStates();
                }).ConfigureAwait(false);
            }
        }

        private void RefreshCommandStates()
        {
            try
            {
                SearchCommand.ChangeCanExecute();
                LoadMoreOlderCommand.ChangeCanExecute();
                PlayCommand.ChangeCanExecute();
                StopCommand.ChangeCanExecute();
                NextCommand.ChangeCanExecute();
                PreviousCommand.ChangeCanExecute();
                PlaybackSpeedDownCommand.ChangeCanExecute();
                PlaybackSpeedUpCommand.ChangeCanExecute();
                OpenTimePickerCommand.ChangeCanExecute();
                CancelTimePickerCommand.ChangeCanExecute();
                ConfirmTimePickerCommand.ChangeCanExecute();
            }
            catch
            {
            }

            OnPropertyChanged(nameof(IsHistoryMediaButtonsEnabled));
            OnPropertyChanged(nameof(IsStopEnabled));
            OnPropertyChanged(nameof(IsPlayEnabled));
            OnPropertyChanged(nameof(CanLoadMoreOlder));
            OnPropertyChanged(nameof(CanLoadMoreNewer));
        }

        public async Task OnCallsListScrolledAsync(int firstVisibleItemIndex, int lastVisibleItemIndex)
        {
            try
            {
                if (_isSnapshotSearchActive)
                {
                    if (_isQueuePlaybackRunning && _activeCall != null)
                    {
                        ScrollRequested?.Invoke(_activeCall, ScrollToPosition.Center);
                    }

                    return;
                }

                // Legacy paging mode (kept for compatibility, but snapshot searches should not use it).
                if (_activeSearchFilters == null)
                    return;

                if (Calls.Count == 0)
                    return;

                if (firstVisibleItemIndex < 0 || lastVisibleItemIndex < 0)
                    return;

                if (_isPaging)
                    return;

                var nowUtc = DateTime.UtcNow;

                // In History, we play "down" toward newer calls. As the user approaches the bottom, append more newer calls.
                if (lastVisibleItemIndex >= Calls.Count - 1 - PrefetchThresholdItems)
                {
                    if (nowUtc < _nextAllowedNewerLoadUtc)
                        return;

                    _nextAllowedNewerLoadUtc = nowUtc.AddMilliseconds(700);

                    _ = await AppendNewerPageAsync(CancellationToken.None);
                }
            }
            catch
            {
            }
        }

        private static Task RunOnMainThreadAsync(Action action)
        {
            if (MainThread.IsMainThread)
            {
                action();
                return Task.CompletedTask;
            }

            return MainThread.InvokeOnMainThreadAsync(action);
        }

        private void StartPrefetchNewerIfNeeded()
        {
            try
            {
                if (_activeSearchFilters == null)
                    return;

                if (!CanLoadMoreNewer)
                    return;

                if (_prefetchedNewerCalls != null && _prefetchedNewerCalls.Count > 0)
                    return;

                if (_prefetchNewerTask != null && !_prefetchNewerTask.IsCompleted)
                    return;

                var gen = _searchGeneration;

                _prefetchNewerTask = Task.Run(async () =>
                {
                    await _prefetchSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (gen != _searchGeneration)
                            return;

                        if (_activeSearchFilters == null)
                            return;

                        if (!CanLoadMoreNewer)
                            return;

                        if (_prefetchedNewerCalls != null && _prefetchedNewerCalls.Count > 0)
                            return;

                        var newStart = Math.Max(0, _activeStartIndex - _activeWindowSize);
                        var length = _activeStartIndex - newStart;
                        if (length <= 0)
                            return;

                        _prefetchedNewerStartIndex = newStart;

                        var page = await _callHistoryService.GetCallsPageAsync(newStart, length, _activeSearchFilters, _activeDateFromLocal, _activeDateToLocal, CancellationToken.None).ConfigureAwait(false);
                        if (gen != _searchGeneration)
                            return;

                        if (page?.Calls == null || page.Calls.Count == 0)
                            return;

                        _prefetchedNewerCalls = page.Calls.ToList();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        _prefetchSemaphore.Release();
                    }
                });
            }
            catch
            {
            }
        }

        private void StartPrefetchOlderIfNeeded()
        {
            try
            {
                if (_activeSearchFilters == null)
                    return;

                if (!CanLoadMoreOlder)
                    return;

                if (_prefetchedOlderCalls != null && _prefetchedOlderCalls.Count > 0)
                    return;

                if (_prefetchOlderTask != null && !_prefetchOlderTask.IsCompleted)
                    return;

                var gen = _searchGeneration;

                _prefetchOlderTask = Task.Run(async () =>
                {
                    await _prefetchSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (gen != _searchGeneration)
                            return;

                        if (_activeSearchFilters == null)
                            return;

                        if (_prefetchedOlderCalls != null && _prefetchedOlderCalls.Count > 0)
                            return;

                        var callsCount = await MainThread.InvokeOnMainThreadAsync(() => Calls.Count).ConfigureAwait(false);
                        var start = _activeStartIndex + callsCount;
                        var remaining = _activeTotalMatches - start;
                        if (remaining <= 0)
                            return;

                        var length = Math.Min(_activeWindowSize, remaining);

                        _prefetchedOlderStartIndex = start;

                        var page = await _callHistoryService.GetCallsPageAsync(start, length, _activeSearchFilters, _activeDateFromLocal, _activeDateToLocal, CancellationToken.None).ConfigureAwait(false);
                        if (gen != _searchGeneration)
                            return;

                        if (page?.Calls == null || page.Calls.Count == 0)
                            return;

                        var filtered = page.Calls
                            .Where(IsWithinHistoryWindow)
                            .ToList();

                        if (filtered.Count == 0)
                        {
                            _historyCutoffReached = true;
                            await MainThread.InvokeOnMainThreadAsync(() =>
                            {
                                StatusText = "End of History. Upgrade to a premium account for full access.";
                                RefreshCommandStates();
                            }).ConfigureAwait(false);

                            await NotifyHistoryLimitAsync().ConfigureAwait(false);
                            return;
                        }

                        _prefetchedOlderCalls = filtered;
                    }
                    catch
                    {
                    }
                    finally
                    {
                        _prefetchSemaphore.Release();
                    }
                });
            }
            catch
            {
            }
        }

        private async Task PlayFromCallAsync(CallItem call)
        {
            if (call == null || Calls.Count == 0)
                return;

            var index = Calls.IndexOf(call);
            if (index < 0)
                return;

            await PlayFromIndexAsync(index);
        }

        private async Task PlayAsync()
        {
            if (Calls.Count == 0)
                return;

            if (_isQueuePlaybackRunning)
                return;

            var startIndex = _currentIndex;
            if (startIndex < 0)
                startIndex = 0;
            if (startIndex >= Calls.Count)
                startIndex = Math.Max(0, Calls.Count - 1);

            await PlayFromIndexAsync(startIndex);
        }

        private async Task SkipNextAsync()
        {
            if (Calls.Count == 0)
                return;

            if (_currentIndex < 0)
            {
                await PlayFromIndexAsync(0);
                return;
            }

            if (_currentIndex >= Calls.Count - 1)
                return;

            await PlayFromIndexAsync(_currentIndex + 1);
        }

        private async Task SkipPreviousAsync()
        {
            if (Calls.Count == 0)
                return;

            if (_currentIndex < 0)
            {
                await PlayFromIndexAsync(0);
                return;
            }

            if (_currentIndex <= 0)
                return;

            await PlayFromIndexAsync(_currentIndex - 1);
        }

        private async Task PlayFromIndexAsync(int startIndex)
        {
            CancellationTokenSource? myPlaybackCts = null;
            CancellationToken playbackToken;

            await _playbackLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopAsyncCore().ConfigureAwait(false);

                if (Calls == null || Calls.Count == 0)
                    return;

                _queuePlaybackCts = new CancellationTokenSource();
                myPlaybackCts = _queuePlaybackCts;
                playbackToken = myPlaybackCts.Token;

                _isQueuePlaybackRunning = true;
                OnPropertyChanged(nameof(IsHistoryPlaybackRunning));
                OnPropertyChanged(nameof(IsPlayEnabled));
                OnPropertyChanged(nameof(IsStopEnabled));
                OnPropertyChanged(nameof(ShowHistoryPlayButton));
                OnPropertyChanged(nameof(ShowHistoryStopButton));
                StatusText = "Playing";
                RefreshCommandStates();
            }
            finally
            {
                _playbackLock.Release();
            }

            try
            {
                // Frozen linear archive mode (no paging, no list mutations during playback).
                if (_isSnapshotSearchActive)
                {
                    startIndex = Math.Clamp(startIndex, 0, Calls.Count - 1);
                    _currentIndex = startIndex;

					for (var i = startIndex; i < Calls.Count; i++)
                    {
                        playbackToken.ThrowIfCancellationRequested();

						_currentIndex = i;
						var item = Calls[i];
                        _activeCall = item;
                        OnPropertyChanged(nameof(ActiveCall));

                        await PlayAudioAsync(item, playbackToken);

                        if (playbackToken.IsCancellationRequested)
                            break;
                    }

                    return;
                }

                // Legacy paging mode.
                startIndex = Math.Clamp(startIndex, 0, Calls.Count - 1);
                _currentIndex = startIndex;

                var index = startIndex;
                while (index < Calls.Count)
                {
                    playbackToken.ThrowIfCancellationRequested();

                    _currentIndex = index;
                    var item = Calls[index];
                    _activeCall = item;
                    OnPropertyChanged(nameof(ActiveCall));

                    ScrollToIndex(index, ScrollToPosition.Start);

                    await PlayAudioAsync(item, playbackToken);

                    if (playbackToken.IsCancellationRequested)
                        break;

                    // When we reach the end of the loaded window, load more newer calls and keep going.
                    if (index >= Calls.Count - 1)
                    {
                        var added = await AppendNewerPageAsync(playbackToken);
                        if (added > 0)
                        {
                            StatusText = "Playing";
                            // Continue with the next item (the list grew).
                            index++;
                            continue;
                        }

                        // No more calls available within the selected range.
                        break;
                    }

                    index++;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
            finally
            {
                await _playbackLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_queuePlaybackCts, myPlaybackCts))
                    {
                        await StopAsyncCore().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _playbackLock.Release();
                }
            }
        }

        
        private async Task UnlimitedHistoryAsync()
        {
            try
            {
                // Convenience action for premium: jump to "now" and run a search.
                SelectedTime = DateTime.Now.TimeOfDay;
                await SearchAsync();
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"History: UnlimitedHistoryAsync failed. ex={ex.GetType().Name}: {ex.Message}");
            }
        }

private async Task LoadMoreOlderAsync()
        {
            try
            {
                await AppendOlderPageAsync(CancellationToken.None);
            }
            catch
            {
            }
        }

        private static string GetDedupKey(CallItem c)
        {
            if (c == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(c.BackendId))
                return c.BackendId;

            if (!string.IsNullOrWhiteSpace(c.AudioUrl))
                return c.AudioUrl;

            return string.Create(CultureInfo.InvariantCulture, $"{c.Timestamp:O}|{c.Talkgroup}|{c.Site}|{c.CallDurationSeconds:0.###}");
        }
        private async Task<int> AppendNewerPageAsync(CancellationToken token)
        {
            var acquired = false;
            try
            {
                if (_activeSearchFilters == null)
                    return 0;

                if (!CanLoadMoreNewer)
                    return 0;

                if (Calls.Count == 0)
                    return 0;

                await _prefetchSemaphore.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                try
                {
                    if (_activeSearchFilters == null)
                        return 0;

                    if (!CanLoadMoreNewer)
                        return 0;

                    if (_isPaging)
                        return 0;

                    _isPaging = true;
                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);

                    // Prefer the prefetched buffer. If it is not ready yet, wait for it.
                    await RunOnMainThreadAsync(StartPrefetchNewerIfNeeded).ConfigureAwait(false);

                    if ((_prefetchedNewerCalls == null || _prefetchedNewerCalls.Count == 0) && _prefetchNewerTask != null && !_prefetchNewerTask.IsCompleted)
                    {
                        var completed = await Task.WhenAny(_prefetchNewerTask, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
                        if (!ReferenceEquals(completed, _prefetchNewerTask))
                            token.ThrowIfCancellationRequested();
                    }

                    var newCalls = _prefetchedNewerCalls;
                    var newStartIndex = _prefetchedNewerStartIndex;

                    // If prefetch did not populate, fall back to a direct fetch.
                    if (newCalls == null || newCalls.Count == 0)
                    {
                        var newStart = Math.Max(0, _activeStartIndex - _activeWindowSize);
                        var length = _activeStartIndex - newStart;
                        if (length <= 0)
                            return 0;

                        newStartIndex = newStart;
                        var page = await _callHistoryService.GetCallsPageAsync(newStart, length, _activeSearchFilters, _activeDateFromLocal, _activeDateToLocal, token).ConfigureAwait(false);
                        if (page?.Calls == null || page.Calls.Count == 0)
                            return 0;

                        if (page.TotalMatches > 0)
                            _activeTotalMatches = page.TotalMatches;

                        newCalls = page.Calls.ToList();
                    }

                    _prefetchedNewerCalls = null;

                    newCalls = newCalls
                        .Where(IsWithinHistoryWindow)
                        .OrderBy(c => c.Timestamp)
                        .ToList();

                    if (newCalls.Count == 0)
                    {
                        _historyUpperLimitReached = true;
                        await RunOnMainThreadAsync(() =>
                        {
                            StatusText = "Reached the end of the selected date range.";
                        }).ConfigureAwait(false);

                        await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                        return 0;
                    }

                    // Only append calls that are strictly newer than what we already have at the end.
                    var lastTs = Calls.LastOrDefault()?.Timestamp;
                    if (lastTs.HasValue)
                        newCalls = newCalls.Where(c => c.Timestamp > lastTs.Value).ToList();

                    if (newCalls.Count == 0)
                    {
                        await RunOnMainThreadAsync(StartPrefetchNewerIfNeeded).ConfigureAwait(false);
                        await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                        return 0;
                    }

                    var added = 0;
                    await RunOnMainThreadAsync(() =>
                    {
                        foreach (var c in newCalls)
                        {
                            Calls.Add(c);
                            added++;
                        }
                    }).ConfigureAwait(false);

                    if (added > 0)
                    {
                        _activeStartIndex = newStartIndex;
                        await RunOnMainThreadAsync(() =>
                        {
                            StatusText = $"{_activeTotalMatches} match(es), showing {Calls.Count}";
                        }).ConfigureAwait(false);

                        await RunOnMainThreadAsync(StartPrefetchNewerIfNeeded).ConfigureAwait(false);
                    }

                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                    return added;
                }
                finally
                {
                    _isPaging = false;
                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                    if (acquired)
                        _prefetchSemaphore.Release();
                }
            }
            catch
            {
                _isPaging = false;
                try { await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false); } catch { }
                return 0;
            }
        }


        private async Task<int> PrependNewerPageAsync(CancellationToken token, CallItem? keepVisible = null)
        {
            var acquired = false;
            try
            {
                if (_activeSearchFilters == null)
                    return 0;

                if (!CanLoadMoreNewer)
                    return 0;

                if (Calls.Count == 0)
                    return 0;

                await _prefetchSemaphore.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                try
                {
                    if (_activeSearchFilters == null)
                        return 0;

                    if (!CanLoadMoreNewer)
                        return 0;

                    if (_isPaging)
                        return 0;

                    _isPaging = true;
                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);

                    // Prefer the prefetched buffer. If it is not ready yet, wait briefly for it.
                    await RunOnMainThreadAsync(StartPrefetchNewerIfNeeded).ConfigureAwait(false);

                    if ((_prefetchedNewerCalls == null || _prefetchedNewerCalls.Count == 0) && _prefetchNewerTask != null && !_prefetchNewerTask.IsCompleted)
                    {
                        var completed = await Task.WhenAny(_prefetchNewerTask, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
                        if (!ReferenceEquals(completed, _prefetchNewerTask))
                            token.ThrowIfCancellationRequested();
                    }

                    var insertCalls = _prefetchedNewerCalls;
                    var newStartIndex = _prefetchedNewerStartIndex;

                    // If prefetch was not able to populate, fall back to a direct fetch.
                    if (insertCalls == null || insertCalls.Count == 0)
                    {
                        var newStart = Math.Max(0, _activeStartIndex - _activeWindowSize);
                        var length = _activeStartIndex - newStart;
                        if (length <= 0)
                            return 0;

                        newStartIndex = newStart;
                        var page = await _callHistoryService.GetCallsPageAsync(newStart, length, _activeSearchFilters, _activeDateFromLocal, _activeDateToLocal, token).ConfigureAwait(false);
                        if (page?.Calls == null || page.Calls.Count == 0)
                            return 0;

                        if (page.TotalMatches > 0)
                            _activeTotalMatches = page.TotalMatches;

                        insertCalls = page.Calls.ToList();
                    }

                    _prefetchedNewerCalls = null;

                    insertCalls = insertCalls
                        .Where(IsWithinHistoryWindow)
                        .ToList();

                    if (insertCalls.Count == 0)
                    {
                        _historyUpperLimitReached = true;
                        await RunOnMainThreadAsync(() =>
                        {
                            StatusText = "Reached the end of the selected date range.";
                        }).ConfigureAwait(false);

                        await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                        return 0;
                    }

                    var added = 0;
                    await RunOnMainThreadAsync(() =>
                    {
                        for (var k = insertCalls.Count - 1; k >= 0; k--)
                        {
                            Calls.Insert(0, insertCalls[k]);
                            added++;
                        }
                    }).ConfigureAwait(false);

                    if (added > 0)
                    {
                        _activeStartIndex = newStartIndex;
                        await RunOnMainThreadAsync(() =>
                        {
                            StatusText = $"{_activeTotalMatches} match(es), showing {Calls.Count}";
                        }).ConfigureAwait(false);

                        // Keep the user's view stable when we prepend while scrolling.
                        if (keepVisible != null)
                            ScrollRequested?.Invoke(keepVisible, ScrollToPosition.MakeVisible);

                        // Prime the next newer buffer.
                        await RunOnMainThreadAsync(StartPrefetchNewerIfNeeded).ConfigureAwait(false);
                    }

                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                    return added;
                }
                finally
                {
                    _isPaging = false;
                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                    if (acquired)
                        _prefetchSemaphore.Release();
                }
            }
            catch
            {
                _isPaging = false;
                try { await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false); } catch { }
                return 0;
            }
        }

        private async Task<int> AppendOlderPageAsync(CancellationToken token)
        {
            var acquired = false;
            try
            {
                if (_activeSearchFilters == null)
                    return 0;

                if (!CanLoadMoreOlder)
                    return 0;

                await _prefetchSemaphore.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                try
                {
                    if (_activeSearchFilters == null)
                        return 0;

                    if (!CanLoadMoreOlder)
                        return 0;

                    if (_isPaging)
                        return 0;

                    _isPaging = true;
                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);

                    await RunOnMainThreadAsync(StartPrefetchOlderIfNeeded).ConfigureAwait(false);

                    if ((_prefetchedOlderCalls == null || _prefetchedOlderCalls.Count == 0) && _prefetchOlderTask != null && !_prefetchOlderTask.IsCompleted)
                    {
                        var completed = await Task.WhenAny(_prefetchOlderTask, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
                        if (!ReferenceEquals(completed, _prefetchOlderTask))
                            token.ThrowIfCancellationRequested();
                    }

                    var appendCalls = _prefetchedOlderCalls;
                    if (appendCalls == null || appendCalls.Count == 0)
                    {
                        var start = _activeStartIndex + Calls.Count;
                        var remaining = _activeTotalMatches - start;
                        if (remaining <= 0)
                            return 0;

                        var length = Math.Min(_activeWindowSize, remaining);

                        var page = await _callHistoryService.GetCallsPageAsync(start, length, _activeSearchFilters, _activeDateFromLocal, _activeDateToLocal, token).ConfigureAwait(false);
                        if (page?.Calls == null || page.Calls.Count == 0)
                            return 0;

                        if (page.TotalMatches > 0)
                            _activeTotalMatches = page.TotalMatches;

                        appendCalls = page.Calls.ToList();
                    }

                    _prefetchedOlderCalls = null;

                    // Enforce the 24 hour History limit.
                    appendCalls = appendCalls
                        .Where(IsWithinHistoryWindow)
                        .ToList();

                    if (appendCalls.Count == 0)
                    {
                        _historyCutoffReached = true;
                        await RunOnMainThreadAsync(() =>
                        {
                            StatusText = _enforceHistory24HourLimit
                                ? "End of History. Upgrade to a premium account for full access."
                                : "Reached the start of the selected date range.";
                        }).ConfigureAwait(false);

                        if (_enforceHistory24HourLimit)
                            await NotifyHistoryLimitAsync().ConfigureAwait(false);
                        await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                        return 0;
                    }

                    var added = 0;
                    await RunOnMainThreadAsync(() =>
                    {
                        foreach (var c in appendCalls)
                        {
                            Calls.Add(c);
                            added++;
                        }
                    }).ConfigureAwait(false);

                    if (added > 0)
                    {
                        await RunOnMainThreadAsync(() =>
                        {
                            StatusText = $"{_activeTotalMatches} match(es), showing {Calls.Count}";
                        }).ConfigureAwait(false);

                        await RunOnMainThreadAsync(StartPrefetchOlderIfNeeded).ConfigureAwait(false);
                    }

                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                    return added;
                }
                finally
                {
                    _isPaging = false;
                    await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false);
                    if (acquired)
                        _prefetchSemaphore.Release();
                }
            }
            catch
            {
                _isPaging = false;
                try { await RunOnMainThreadAsync(RefreshCommandStates).ConfigureAwait(false); } catch { }
                return 0;
            }
        }

        private async Task StopAsync()
        {
            await _playbackLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopAsyncCore().ConfigureAwait(false);
            }
            finally
            {
                _playbackLock.Release();
            }
        }

        private async Task StopAsyncCore()
        {
            if (_queuePlaybackCts != null)
            {
                try { _queuePlaybackCts.Cancel(); } catch { }
                try { _queuePlaybackCts.Dispose(); } catch { }
                _queuePlaybackCts = null;
            }

            if (_audioCts != null)
            {
                try { _audioCts.Cancel(); } catch { }
                try { _audioCts.Dispose(); } catch { }
                _audioCts = null;
            }

            try { await _audioPlaybackService.StopAsync(); } catch { }

            if (Calls != null)
            {
                foreach (var c in Calls)
                {
                    if (c != null && c.IsPlaying)
                        c.IsPlaying = false;
                }
            }

            _isQueuePlaybackRunning = false;
            OnPropertyChanged(nameof(IsHistoryPlaybackRunning));
            OnPropertyChanged(nameof(IsPlayEnabled));
            OnPropertyChanged(nameof(IsStopEnabled));
            OnPropertyChanged(nameof(ShowHistoryPlayButton));
            OnPropertyChanged(nameof(ShowHistoryStopButton));
            RefreshCommandStates();
        }

        private double GetPlaybackRate()
        {
            return _historyPlaybackSpeedStep switch
{
    1 => 1.25,
    2 => 1.5,
    3 => 1.75,
    4 => 2.0,
    _ => 1.0
};
        }

        private async Task PlayAudioAsync(CallItem item, CancellationToken playbackToken)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.AudioUrl))
                return;

            if (_audioCts != null)
            {
                try { _audioCts.Cancel(); } catch { }
                _audioCts.Dispose();
                _audioCts = null;
            }

            _audioCts = CancellationTokenSource.CreateLinkedTokenSource(playbackToken);
            var token = _audioCts.Token;

            // Avoid toggling IsPlaying on every item (causes full list redraw / flashing).
            // Only clear the previous active item.
            if (_activeCall != null && !ReferenceEquals(_activeCall, item))
            {
                try { _activeCall.IsPlaying = false; } catch { }
            }

            item.IsPlaying = true;

            try
            {
                var rate = GetPlaybackRate();
                var url = await GetPlayableAudioUrlAsync(item.AudioUrl, token);
                if (string.IsNullOrWhiteSpace(url))
                    return;

                await _audioPlaybackService.PlayAsync(url, rate, token);
            }
            catch
            {
            }
            finally
            {
                item.IsPlaying = false;
            }
        }

                private async Task<string?> GetPlayableAudioUrlAsync(string audioUrl, CancellationToken token)
        {
            try
            {
                // Some servers (including hosted history items) may send media URLs where
                // percent signs have been percent-encoded (e.g. %2520 instead of %20). That
                // produces 404s when requesting the file. Normalize '%25' back to '%'.
                var normalizedAudioUrl = Regex.Replace(audioUrl, "(?i)%25", "%");
                if (!string.Equals(normalizedAudioUrl, audioUrl, StringComparison.Ordinal))
                    AppLog.Add(() => $"History: normalized media url encoding. before='{audioUrl}' after='{normalizedAudioUrl}'");

                if (!Uri.TryCreate(normalizedAudioUrl, UriKind.Absolute, out var uri))
                    return normalizedAudioUrl;


                // Remove embedded credentials, always.
                var sanitizedBuilder = new UriBuilder(uri)
                {
                    UserName = string.Empty,
                    Password = string.Empty
                };

                var sanitized = sanitizedBuilder.Uri;

                // Determine whether we can and should apply authorization for this host.
                var authHeader = BuildBasicAuthHeaderValueForAudio(sanitized);

                // Hosted audio requires an active validated account. If we cannot produce the
                // service authorization header, do not attempt playback.
                if (string.Equals(sanitized.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(authHeader))
                {
                    return null;
                }

                // Custom servers: if the user did not configure Basic Auth, just return the sanitized URL.
                if (string.IsNullOrWhiteSpace(authHeader))
                    return sanitized.ToString();

                // Download to local temp to avoid credentialed URLs.
                var cacheDir = FileSystem.CacheDirectory;
                if (string.IsNullOrWhiteSpace(cacheDir))
                {
                    // Without cache, we cannot safely play hosted audio (would require auth on the stream).
                    return string.Equals(sanitized.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : sanitized.ToString();
                }

                var fileName = "hist_" + Guid.NewGuid().ToString("N") + ".mp3";
                var path = Path.Combine(cacheDir, fileName);

                using var req = new HttpRequestMessage(HttpMethod.Get, sanitized);
                ApplyAudioAuth(req, sanitized);

                using var resp = await _audioHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                if (!resp.IsSuccessStatusCode)
                {
                    return string.Equals(sanitized.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : sanitized.ToString();
                }

                await using (var fs = File.Create(path))
                {
                    await resp.Content.CopyToAsync(fs, token);
                }

                // Return a file URI so Apple AVPlayer can resolve it reliably.
                // Other platforms also accept file:// URIs.
                return new Uri(path).AbsoluteUri;
            }
            catch
            {
                try
                {
                    // If we already have a local file path, normalize it to a file URI.
                    if (!string.IsNullOrWhiteSpace(audioUrl) && File.Exists(audioUrl))
                        return new Uri(audioUrl).AbsoluteUri;
                }
                catch
                {
                }

                return audioUrl;
            }
        }

        private void ApplyAudioAuth(HttpRequestMessage req, Uri serverUri)
        {
            try
            {
                var header = BuildBasicAuthHeaderValueForAudio(serverUri);
                if (string.IsNullOrWhiteSpace(header))
                    return;

                var token = header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                    ? header.Substring("Basic ".Length).Trim()
                    : header.Trim();

                if (!string.IsNullOrWhiteSpace(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
            catch
            {
            }
        }

        private string? BuildBasicAuthHeaderValueForAudio(Uri serverUri)
        {
            try
            {
                // Hosted Joe's Scanner server: the Trunking Recorder endpoints use a service account,
                // but ONLY after the user has a currently valid authorization via the Auth API.
                if (string.Equals(serverUri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase))
                {
                    var user = (_settingsService.BasicAuthUsername ?? string.Empty).Trim();
                    var pass = (_settingsService.BasicAuthPassword ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                        return null;

                    if (!_settingsService.TryGetServerCredentials("https://app.joesscanner.com", out var savedUser, out var savedPass))
                        return null;

                    if (!string.Equals(savedUser?.Trim() ?? string.Empty, user, StringComparison.Ordinal) ||
                        !string.Equals(savedPass?.Trim() ?? string.Empty, pass, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var expires = _settingsService.SubscriptionExpiresUtc;
                    var isAllowed =
                        _settingsService.SubscriptionLastStatusOk &&
                        expires.HasValue &&
                        expires.Value > DateTime.UtcNow;

                    if (!isAllowed)
                        return null;

                    var rawHosted = $"{ServiceAuthUsername}:{ServiceAuthPassword}";
                    var bytesHosted = Encoding.ASCII.GetBytes(rawHosted);
                    var base64Hosted = Convert.ToBase64String(bytesHosted);
                    return $"Basic {base64Hosted}";
                }

                // Custom servers: only apply Basic Auth if the user provided a username.
                var usernameCustom = _settingsService.BasicAuthUsername?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(usernameCustom))
                    return null;

                var passwordCustom = _settingsService.BasicAuthPassword ?? string.Empty;
                var raw = $"{usernameCustom}:{passwordCustom}";
                var bytes = Encoding.ASCII.GetBytes(raw);
                var base64 = Convert.ToBase64String(bytes);
                return $"Basic {base64}";
            }
            catch
            {
                return null;
            }
        }


        private int FindSnapshotIndex(CallItem call)
        {
            if (call == null)
                return -1;

            var key = GetDedupKey(call);
            for (var i = 0; i < _snapshotCalls.Count; i++)
            {
                if (GetDedupKey(_snapshotCalls[i]) == key)
                    return i;
            }

            return -1;
        }

        private void BuildVisibleWindowAroundCenter(int centerSnapshotIndex, bool forceRebuild)
        {
            if (_snapshotCalls.Count == 0)
                return;

            centerSnapshotIndex = Math.Clamp(centerSnapshotIndex, 0, _snapshotCalls.Count - 1);

            var windowSize = Math.Min(_visibleWindowSize, _snapshotCalls.Count);
            var halfAbove = windowSize / 2;
            var start = centerSnapshotIndex - halfAbove;
            if (start < 0)
                start = 0;

            var maxStart = Math.Max(0, _snapshotCalls.Count - windowSize);
            if (start > maxStart)
                start = maxStart;

            var desiredStart = start;

            // Active call is always the centerSnapshotIndex (may not be visually centered near edges).
            _activeCall = _snapshotCalls[centerSnapshotIndex];
            OnPropertyChanged(nameof(ActiveCall));

            if (forceRebuild || Calls.Count == 0)
            {
                Calls.Clear();
                for (var k = 0; k < windowSize; k++)
                {
                    Calls.Add(_snapshotCalls[desiredStart + k]);
                }

                _visibleStartSnapshotIndex = desiredStart;
                return;
            }

            // Common path during playback: shift by one item (prevents full relayout flash).
            if (desiredStart == _visibleStartSnapshotIndex - 1 && Calls.Count == windowSize)
            {
                Calls.Insert(0, _snapshotCalls[desiredStart]);
                while (Calls.Count > windowSize)
                    Calls.RemoveAt(Calls.Count - 1);

                _visibleStartSnapshotIndex = desiredStart;
                return;
            }

            if (desiredStart == _visibleStartSnapshotIndex + 1 && Calls.Count == windowSize)
            {
                if (Calls.Count > 0)
                    Calls.RemoveAt(0);

                var newLastSnapshotIndex = desiredStart + windowSize - 1;
                if (newLastSnapshotIndex >= 0 && newLastSnapshotIndex < _snapshotCalls.Count)
                    Calls.Add(_snapshotCalls[newLastSnapshotIndex]);

                _visibleStartSnapshotIndex = desiredStart;
                ScrollRequested?.Invoke(_activeCall, ScrollToPosition.Center);
                return;
            }

            // Fallback: rebuild (only happens on first play, big jumps, or near list edges).
            Calls.Clear();
            for (var k = 0; k < windowSize; k++)
            {
                Calls.Add(_snapshotCalls[desiredStart + k]);
            }

            _visibleStartSnapshotIndex = desiredStart;
            ScrollRequested?.Invoke(_activeCall, ScrollToPosition.Center);
        }


private void DecreasePlaybackSpeedStep()
        {
            if (HistoryPlaybackSpeedStep > 0)
                HistoryPlaybackSpeedStep -= 1;
        }

        private void IncreasePlaybackSpeedStep()
        {
            if (HistoryPlaybackSpeedStep < 4)
                HistoryPlaybackSpeedStep += 1;
        }

        private void ScrollToIndex(int index, ScrollToPosition position)
        {
            if (index < 0 || index >= Calls.Count)
                return;

            var call = Calls[index];
            ScrollRequested?.Invoke(call, position);
        }
    }
}
