using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Input;

namespace JoesScanner.ViewModels
{
    // View model for the History tab.
    // History is an explicit, fixed result set returned by search and never ingests new calls.
    // Playback and media controls are independent from the Main tab.
    public sealed class HistoryViewModel : BindableObject
    {
        private readonly ICallHistoryService _callHistoryService;
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly ISettingsService _settingsService;
        private readonly IFilterProfileStore _filterProfileStore;
        private readonly HttpClient _audioHttpClient;

        private CancellationTokenSource? _audioCts;
        private CancellationTokenSource? _queuePlaybackCts;
        private readonly SemaphoreSlim _playbackLock = new SemaphoreSlim(1, 1);

        private HistorySearchFilters? _activeSearchFilters;
        private int _activeWindowSize = 35;
        private int _activeStartIndex;
        private int _activeTotalMatches;
        private bool _isPaging;

        private DateTime _nextAllowedNewerLoadUtc = DateTime.MinValue;
        private DateTime _nextAllowedOlderLoadUtc = DateTime.MinValue;

        // History tab is limited to the last 24 hours. Older content belongs in Archive.
        private DateTime _historyCutoffLocal = DateTime.MinValue;
        private bool _historyCutoffReached;
        private bool _historyCutoffNotified;

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
        private bool _isLoading;
        private bool _isQueuePlaybackRunning;
        private string _statusText = string.Empty;

        private bool _suppressLookupReload;

        private HistoryLookupItem? _selectedReceiver;
        private HistoryLookupItem? _selectedSite;
        private HistoryLookupItem? _selectedTalkgroup;

        private readonly ObservableCollection<FilterProfile> _filterProfiles;
        private FilterProfile? _selectedFilterProfile;

        private string _filterProfileNameDraft = string.Empty;

        private readonly ObservableCollection<string> _filterProfileNameOptions = new();
        private string _selectedFilterProfileNameOption = NoneProfileNameOption;
        private bool _isCustomFilterProfileName;


        private const string SelectedFilterProfileIdPreferenceKey = "HistorySelectedFilterProfileId";

        private const string CustomProfileNameOption = "New";
        
        private const string NoneProfileNameOption = "None";

        private DateTime _selectedDate = DateTime.Today;
        private int _selectedHour;
        private int _selectedMinute;
        private int _selectedSecond;
        private bool _isTimePickerOpen;

        private int _currentIndex = -1;

        // 0 = 1x, 1 = 1.5x, 2 = 2x
        private double _historyPlaybackSpeedStep;
        private const string HistoryPlaybackSpeedStepPreferenceKey = "HistoryPlaybackSpeedStep";

        // Service auth used on app.joesscanner.com, consistent with CallStreamService.
        private const string ServiceAuthUsername = "secappass";
        private const string ServiceAuthPassword = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

        public event Action<CallItem, ScrollToPosition>? ScrollRequested;

        public HistoryViewModel(
            ICallHistoryService callHistoryService,
            IAudioPlaybackService audioPlaybackService,
            ISettingsService settingsService,
            IFilterProfileStore filterProfileStore)
        {
            _callHistoryService = callHistoryService ?? throw new ArgumentNullException(nameof(callHistoryService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _filterProfileStore = filterProfileStore ?? throw new ArgumentNullException(nameof(filterProfileStore));

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

            var savedSpeed = Preferences.Get(HistoryPlaybackSpeedStepPreferenceKey, 0.0);
            HistoryPlaybackSpeedStep = savedSpeed;

            // Default time to now.
            var now = DateTime.Now;
            _selectedHour = now.Hour;
            _selectedMinute = now.Minute;
            _selectedSecond = now.Second;

            SearchCommand = new Command(async () => await SearchAsync(), () => !IsLoading);
            LoadMoreOlderCommand = new Command(async () => await LoadMoreOlderAsync(), () => CanLoadMoreOlder);
            PlayFromCallCommand = new Command<CallItem>(async c => await PlayFromCallAsync(c), c => !IsLoading && c != null);
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
        public ICommand PlayFromCallCommand { get; }
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
            }
        }

        public HistoryLookupItem? SelectedSite
        {
            get => _selectedSite;
            set
            {
                if (ReferenceEquals(_selectedSite, value))
                    return;

                _selectedSite = value;
                OnPropertyChanged();
            }
        }

        public HistoryLookupItem? SelectedTalkgroup
        {
            get => _selectedTalkgroup;
            set
            {
                if (ReferenceEquals(_selectedTalkgroup, value))
                    return;

                _selectedTalkgroup = value;
                OnPropertyChanged();
            }
        }

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

                // Use property setters to keep all dependent properties in sync.
                SelectedHour = h;
                SelectedMinute = m;
                SelectedSecond = s;

                OnPropertyChanged();
            }
        }

        public string SelectedTimeText =>
            string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", SelectedHour, SelectedMinute, SelectedSecond);

        public string SelectedTimeDisplay
        {
            get
            {
                // Display as a compact 12-hour time like 7:19 PM.
                // Include seconds only if the user has explicitly set them.
                var dt = DateTime.Today.Add(SelectedTime);
                return SelectedSecond != 0
                    ? dt.ToString("h:mm:ss tt", CultureInfo.InvariantCulture)
                    : dt.ToString("h:mm tt", CultureInfo.InvariantCulture);
            }
        }

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
        public bool IsStopEnabled => _isQueuePlaybackRunning || Calls.Count > 0;

        public bool CanLoadMoreOlder =>
            _activeSearchFilters != null &&
            !IsLoading &&
            !_isPaging &&
            !_historyCutoffReached &&
            _activeTotalMatches > 0 &&
            (_activeStartIndex + Calls.Count) < _activeTotalMatches;

        public bool CanLoadMoreNewer =>
            _activeSearchFilters != null &&
            !IsLoading &&
            !_isPaging &&
            _activeTotalMatches > 0 &&
            _activeStartIndex > 0;

        public double HistoryPlaybackSpeedStep
        {
            get => _historyPlaybackSpeedStep;
            set
            {
                var clamped = value;
                if (clamped < 0) clamped = 0;
                if (clamped > 2) clamped = 2;
                clamped = Math.Round(clamped);

                if (Math.Abs(_historyPlaybackSpeedStep - clamped) < 0.001)
                    return;

                _historyPlaybackSpeedStep = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HistoryPlaybackSpeedLabel));
                Preferences.Set(HistoryPlaybackSpeedStepPreferenceKey, _historyPlaybackSpeedStep);
            }
        }

        public string HistoryPlaybackSpeedLabel =>
            _historyPlaybackSpeedStep switch
            {
                1 => "1.5x",
                2 => "2x",
                _ => "1x"
            };

        public async Task OnPageOpenedAsync()
        {
            // Only mute the Main tab audio. Do not stop or disconnect the live queue.
            QueueControlBus.RequestSetMainAudioMuted(true);

            await LoadLookupsAsync();

            try
            {
                await LoadFilterProfilesAsync(applySelectedProfile: true);
            }
            catch
            {
            }
        }

        public async Task LoadFilterProfilesAsync(bool applySelectedProfile)
        {
            var profiles = await _filterProfileStore.GetProfilesAsync(FilterProfileContexts.History, CancellationToken.None);

            _filterProfiles.Clear();
            foreach (var p in profiles)
                _filterProfiles.Add(p);

            
            RefreshFilterProfileNameOptions();
var selectedId = Preferences.Get(SelectedFilterProfileIdPreferenceKey, string.Empty);
            if (string.IsNullOrWhiteSpace(selectedId))
            {
                SelectedFilterProfile = null;
                return;
            }

            var selected = _filterProfiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.Ordinal));
            SelectedFilterProfile = selected;

            if (applySelectedProfile && selected != null)
                ApplyProfileToFilters(selected);
        }

        public async Task SelectFilterProfileAsync(FilterProfile? profile, bool apply)
        {
            SelectedFilterProfile = profile;

            var id = profile?.Id ?? string.Empty;
            Preferences.Set(SelectedFilterProfileIdPreferenceKey, id);

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
                Context = FilterProfileContexts.History,
                Filters = new FilterProfileFilters
                {
                    ReceiverValue = SelectedReceiver?.Value,
                    ReceiverLabel = SelectedReceiver?.Label,
                    SiteValue = SelectedSite?.Value,
                    SiteLabel = SelectedSite?.Label,
                    TalkgroupValue = SelectedTalkgroup?.Value,
                    TalkgroupLabel = SelectedTalkgroup?.Label,
                    SelectedTime = SelectedTime
                }
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

            await _filterProfileStore.RenameAsync(FilterProfileContexts.History, current.Id, newName, CancellationToken.None);
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

            await _filterProfileStore.DeleteAsync(FilterProfileContexts.History, current.Id, CancellationToken.None);
            Preferences.Set(SelectedFilterProfileIdPreferenceKey, string.Empty);
            SelectedFilterProfile = null;

            await LoadFilterProfilesAsync(applySelectedProfile: false);
            return true;
        }

        private void ApplyProfileToFilters(FilterProfile profile)
        {
            if (profile == null)
                return;

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
            try { await StopAsync(); } catch { }
            QueueControlBus.RequestSetMainAudioMuted(false);
        }

        private async Task LoadLookupsAsync()
        {
            IsLoading = true;
            try
            {
                StatusText = "Loading filters";

                var data = await _callHistoryService.GetLookupDataAsync(currentFilters: null, CancellationToken.None);

                _suppressLookupReload = true;
                try
                {
                    ReplaceItems(Receivers, data.Receivers);
                    ReplaceItems(Sites, data.Sites);
                    ReplaceItems(Talkgroups, data.Talkgroups);
                    SelectedReceiver = Receivers.FirstOrDefault();
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
                StatusText = "Error loading filters";
            }
            finally
            {
                IsLoading = false;
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
            var now = DateTime.Now;
            var candidate = new DateTime(
                now.Year,
                now.Month,
                now.Day,
                SelectedHour,
                SelectedMinute,
                SelectedSecond,
                DateTimeKind.Local);

            // If the user picked a time that is later than the current clock time, interpret it as yesterday.
            if (candidate > now)
                candidate = candidate.AddDays(-1);

            return candidate;
        }

        private bool IsWithinHistoryWindow(CallItem call)
        {
            if (call == null)
                return false;

            if (_historyCutoffLocal == DateTime.MinValue)
                return true;

            return call.Timestamp >= _historyCutoffLocal;
        }

        private async Task NotifyHistoryLimitAsync()
        {
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
                            "History is limited to the last 24 hours. Use the Archive tab for older calls.",
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

        private async Task SearchAsync()
        {
            if (IsLoading)
                return;

            IsLoading = true;
            try
            {
                StatusText = "Searching";
                await StopAsync();

                _historyCutoffLocal = DateTime.Now.AddHours(-24);
                _historyCutoffReached = false;
                _historyCutoffNotified = false;

                var target = GetSelectedTargetLocal();

                if (target < _historyCutoffLocal)
                {
                    StatusText = "History is limited to the last 24 hours. Use the Archive tab for older calls.";
                    await NotifyHistoryLimitAsync();
                    RefreshCommandStates();
                    return;
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
                var result = await _callHistoryService.SearchAroundAsync(target, filters, windowSize, CancellationToken.None);

                _activeStartIndex = result.StartIndex;
                _activeTotalMatches = result.TotalMatches;

                var filtered = result.Calls
                    .Where(IsWithinHistoryWindow)
                    .ToList();

                Calls.Clear();
                foreach (var c in filtered)
                    Calls.Add(c);

                _currentIndex = -1;

                if (Calls.Count == 0)
                {
                    StatusText = "No calls found in the last 24 hours. Use the Archive tab for older calls.";
                    RefreshCommandStates();
                    return;
                }

                // Center the closest call immediately after search.
                var anchorCall = Calls
                    .OrderBy(c => Math.Abs((c.Timestamp - target).TotalSeconds))
                    .FirstOrDefault();

                var anchor = anchorCall != null ? Calls.IndexOf(anchorCall) : 0;
                anchor = Math.Clamp(anchor, 0, Calls.Count - 1);
                ScrollToIndex(anchor);

                StatusText = $"{result.TotalMatches} match(es), showing {Calls.Count}. History is limited to the last 24 hours.";

                // Prime the next pages in both directions so scrolling feels continuous.
                StartPrefetchNewerIfNeeded();
                StartPrefetchOlderIfNeeded();
                RefreshCommandStates();
            }
            catch
            {
                StatusText = "Search failed";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void RefreshCommandStates()
        {
            try
            {
                SearchCommand.ChangeCanExecute();
                LoadMoreOlderCommand.ChangeCanExecute();
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
            OnPropertyChanged(nameof(CanLoadMoreOlder));
            OnPropertyChanged(nameof(CanLoadMoreNewer));
        }

        public async Task OnCallsListScrolledAsync(int firstVisibleItemIndex, int lastVisibleItemIndex)
        {
            try
            {
                if (_activeSearchFilters == null)
                    return;

                if (Calls.Count == 0)
                    return;

                if (firstVisibleItemIndex < 0 || lastVisibleItemIndex < 0)
                    return;

                if (_isPaging)
                    return;

                var nowUtc = DateTime.UtcNow;

                // Near the newest end.
                if (firstVisibleItemIndex <= PrefetchThresholdItems)
                {
                    if (nowUtc < _nextAllowedNewerLoadUtc)
                        return;

                    _nextAllowedNewerLoadUtc = nowUtc.AddMilliseconds(700);

                    var keepVisibleIndex = Math.Clamp(firstVisibleItemIndex, 0, Calls.Count - 1);
                    var keepVisibleItem = Calls[keepVisibleIndex];
                    _ = await PrependNewerPageAsync(CancellationToken.None, keepVisibleItem);
                }

                // Near the oldest end.
                if (lastVisibleItemIndex >= Calls.Count - 1 - PrefetchThresholdItems)
                {
                    if (nowUtc < _nextAllowedOlderLoadUtc)
                        return;

                    _nextAllowedOlderLoadUtc = nowUtc.AddMilliseconds(700);

                    _ = await AppendOlderPageAsync(CancellationToken.None);
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

                        var page = await _callHistoryService.GetCallsPageAsync(newStart, length, _activeSearchFilters, CancellationToken.None).ConfigureAwait(false);
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

                        var page = await _callHistoryService.GetCallsPageAsync(start, length, _activeSearchFilters, CancellationToken.None).ConfigureAwait(false);
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
                                StatusText = "End of History. Use the Archive tab for older calls.";
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

        private async Task SkipNextAsync()
        {
            if (Calls.Count == 0)
                return;

            if (_currentIndex <= 0)
            {
                // At the newest loaded call. If more newer matches exist, pull them in and continue.
                var added = await PrependNewerPageAsync(CancellationToken.None);
                if (added > 0)
                {
                    await PlayFromIndexAsync(added - 1);
                }

                return;
            }

            await PlayFromIndexAsync(_currentIndex - 1);
        }

        private async Task SkipPreviousAsync()
        {
            if (Calls.Count == 0)
                return;

            if (_currentIndex < 0)
            {
                // If not playing, start from the centered anchor (middle of current list).
                await PlayFromIndexAsync(Math.Min(Calls.Count - 1, Calls.Count / 2));
                return;
            }

            if (_currentIndex >= Calls.Count - 1)
            {
                // At the oldest loaded call. If more older matches exist, pull them in and continue.
                var added = await AppendOlderPageAsync(CancellationToken.None);
                if (added > 0)
                {
                    await PlayFromIndexAsync(_currentIndex + 1);
                }

                return;
            }

            await PlayFromIndexAsync(_currentIndex + 1);
        }

        private async Task PlayFromIndexAsync(int startIndex)
        {
            CancellationTokenSource? myPlaybackCts = null;
            CancellationToken playbackToken;

            await _playbackLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Stop any existing playback (safe for repeated calls).
                await StopAsyncCore().ConfigureAwait(false);

                if (Calls == null || Calls.Count == 0)
                    return;

                startIndex = Math.Clamp(startIndex, 0, Calls.Count - 1);
                _currentIndex = startIndex;

                // Dedicated CTS for the playback loop so Stop cancels both audio and any filter-side loading.
                _queuePlaybackCts = new CancellationTokenSource();
                myPlaybackCts = _queuePlaybackCts;
                playbackToken = myPlaybackCts.Token;

                _isQueuePlaybackRunning = true;
                StatusText = "Playing";
                RefreshCommandStates();
            }
            finally
            {
                _playbackLock.Release();
            }

            try
            {
                // Play toward newer calls (index decreases). When we reach the top of the list,
                // try to pull in additional newer calls that match the current search filters.
                var i = startIndex;
                while (i >= 0)
                {
                    playbackToken.ThrowIfCancellationRequested();

                    _currentIndex = i;
                    var item = Calls[i];
                    ScrollToIndex(i);

                    await PlayAudioAsync(item, playbackToken);

                    // Stop cancels _queuePlaybackCts.
                    if (playbackToken.IsCancellationRequested)
                        break;

                    if (i == 0)
                    {
                        var added = await PrependNewerPageAsync(playbackToken);
                        if (added > 0)
                        {
                            StatusText = "Playing";
                            // After prepending, the call we just played shifts down.
                            // The next call to play is the one directly newer than it.
                            i = added - 1;
                            continue;
                        }

                        break;
                    }

                    i--;
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
                    // Only tear down if we are still the active playback instance.
                    if (ReferenceEquals(_queuePlaybackCts, myPlaybackCts))
                    {
                        _isQueuePlaybackRunning = false;

                        if (_queuePlaybackCts != null)
                        {
                            try { _queuePlaybackCts.Cancel(); } catch { }
                            _queuePlaybackCts.Dispose();
                            _queuePlaybackCts = null;
                        }

                        if (Calls != null && Calls.Count > 0)
                            StatusText = "Playback finished";

                        RefreshCommandStates();
                    }
                }
                finally
                {
                    _playbackLock.Release();
                }
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
                        var page = await _callHistoryService.GetCallsPageAsync(newStart, length, _activeSearchFilters, token).ConfigureAwait(false);
                        if (page?.Calls == null || page.Calls.Count == 0)
                            return 0;

                        if (page.TotalMatches > 0)
                            _activeTotalMatches = page.TotalMatches;

                        insertCalls = page.Calls.ToList();
                    }

                    _prefetchedNewerCalls = null;

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

                        var page = await _callHistoryService.GetCallsPageAsync(start, length, _activeSearchFilters, token).ConfigureAwait(false);
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
                            StatusText = "End of History. Use the Archive tab for older calls.";
                        }).ConfigureAwait(false);

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
            RefreshCommandStates();
        }

        private double GetPlaybackRate()
        {
            return _historyPlaybackSpeedStep switch
            {
                1 => 1.5,
                2 => 2.0,
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
            if (Calls != null)
            {
                foreach (var c in Calls)
                {
                    if (c != null && c.IsPlaying)
                        c.IsPlaying = false;
                }
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
                if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri))
                    return audioUrl;

                // Remove embedded credentials, always.
                var sanitizedBuilder = new UriBuilder(uri)
                {
                    UserName = string.Empty,
                    Password = string.Empty
                };

                var sanitized = sanitizedBuilder.Uri;

                // If no auth is needed, just return the sanitized URL.
                var needsAuth = sanitized.Host.Equals("app.joesscanner.com", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(_settingsService.BasicAuthUsername);

                if (!needsAuth)
                    return sanitized.ToString();

                // Download to local temp to avoid credentialed URLs.
                var cacheDir = FileSystem.CacheDirectory;
                if (string.IsNullOrWhiteSpace(cacheDir))
                    return sanitized.ToString();

                var fileName = "hist_" + Guid.NewGuid().ToString("N") + ".mp3";
                var path = Path.Combine(cacheDir, fileName);

                using var req = new HttpRequestMessage(HttpMethod.Get, sanitized);
                ApplyAudioAuth(req, sanitized);

                using var resp = await _audioHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                if (!resp.IsSuccessStatusCode)
                    return sanitized.ToString();

                await using (var fs = File.Create(path))
                {
                    await resp.Content.CopyToAsync(fs, token);
                }

                return path;
            }
            catch
            {
                return audioUrl;
            }
        }

        private void ApplyAudioAuth(HttpRequestMessage req, Uri serverUri)
        {
            try
            {
                if (serverUri.Host.Equals("app.joesscanner.com", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = $"{ServiceAuthUsername}:{ServiceAuthPassword}";
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
                    return;
                }

                var u = _settingsService.BasicAuthUsername?.Trim() ?? string.Empty;
                var p = _settingsService.BasicAuthPassword ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(u))
                {
                    var raw = $"{u}:{p}";
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
                }
            }
            catch
            {
            }
        }

        private void DecreasePlaybackSpeedStep()
        {
            if (HistoryPlaybackSpeedStep > 0)
                HistoryPlaybackSpeedStep -= 1;
        }

        private void IncreasePlaybackSpeedStep()
        {
            if (HistoryPlaybackSpeedStep < 2)
                HistoryPlaybackSpeedStep += 1;
        }

        private void ScrollToIndex(int index)
        {
            if (index < 0 || index >= Calls.Count)
                return;

            var call = Calls[index];
            ScrollRequested?.Invoke(call, ScrollToPosition.Center);
        }
    }
}
