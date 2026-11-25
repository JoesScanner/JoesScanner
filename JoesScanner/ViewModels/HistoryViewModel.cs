using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using JoesScanner.Helpers;

namespace JoesScanner.ViewModels
{
    // View model for the History tab.
    // History is an explicit, fixed result set returned by search and never ingests new calls.
    public sealed partial class HistoryViewModel : BindableObject
    {
        private readonly ICallHistoryService _callHistoryService;
        private readonly IAudioPlaybackService _audioPlaybackService;
        private readonly ISettingsService _settingsService;
        private readonly IAddressDetectionService _addressDetectionService;
        private readonly IWhat3WordsService _what3WordsService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly ICallStreamService _callStreamService;
        private readonly IToneAlertService _toneAlertService;
        private readonly HttpClient _audioHttpClient;
        private readonly PlaybackAudioCache _audioCache;

        private bool _isDownloadRangeSelecting;
        private string _downloadRangePrompt = string.Empty;

        private DateTime _selectedDateFrom = DateTime.Today;
        private DateTime _selectedDateTo = DateTime.Today;
        private bool _enforceHistory24HourLimit;
        private bool _isLoading;
        private bool _isQueuePlaybackRunning;
        private CallItem? _activeCall;
        private string _statusText = string.Empty;

        private HistoryLookupItem? _selectedReceiver;
        private HistoryLookupItem? _selectedSite;
        private int _selectedHour;
        private int _selectedMinute;
        private int _selectedSecond;
        private bool _suppressTimeCascade;
        private bool _isTimePickerOpen;

        private bool _what3WordsLinksEnabled = true;

        // 0 = 1x, 1 = 1.25x, 2 = 1.5x, 3 = 1.75x, 4 = 2x
        private double _historyPlaybackSpeedStep;

        // Service auth used on app.joesscanner.com, consistent with CallStreamService.

        public event Action<CallItem, ScrollToPosition>? ScrollRequested;

        public HistoryViewModel(
            ICallHistoryService callHistoryService,
            IAudioPlaybackService audioPlaybackService,
            ISettingsService settingsService,
            IAddressDetectionService addressDetectionService,
            IWhat3WordsService what3WordsService,
            ISubscriptionService subscriptionService,
            ICallStreamService callStreamService,
            IToneAlertService toneAlertService,
            PlaybackOwnership playbackOwnership,
            MainViewModel mainViewModel,
            HistorySearchPreferences historyPreferences,
            PlaybackAudioCache audioCache)
        {
            _callHistoryService = callHistoryService ?? throw new ArgumentNullException(nameof(callHistoryService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));
            _playbackOwnership = playbackOwnership;
            _mainViewModel = mainViewModel;
            _historyPreferences = historyPreferences;
            _audioCache = audioCache;
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _settingsService.AccountIdentityChanged += OnHistoryAccountChanged;
            HostedEntitlement.For(_settingsService).Changed += OnHistoryEntitlementChanged;
            _addressDetectionService = addressDetectionService ?? throw new ArgumentNullException(nameof(addressDetectionService));
            _what3WordsService = what3WordsService ?? throw new ArgumentNullException(nameof(what3WordsService));

            try { What3WordsLinksEnabled = _settingsService.What3WordsLinksEnabled; } catch { }

            try
            {
                if (settingsService is SettingsService concrete)
                {
                    concrete.AddressDetectionSettingsChanged += OnAddressDetectionSettingsChanged;
                    concrete.What3WordsSettingsChanged += (_, __) =>
                    {
                        try { What3WordsLinksEnabled = _settingsService.What3WordsLinksEnabled; } catch { }
                    };
                }
            }
            catch
            {
            }

            
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
            _callStreamService = callStreamService;
			_toneAlertService = toneAlertService ?? throw new ArgumentNullException(nameof(toneAlertService));

			_toneAlertService.ToneDetected += OnToneDetected;
			_toneAlertService.HotTalkgroupsChanged += OnHotTalkgroupsChanged;

            Calls = new ObservableCollection<CallItem>();
            AddressAlerts.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasAddressAlerts));
            Receivers = new ObservableCollection<HistoryLookupItem>();
            Sites = new ObservableCollection<HistoryLookupItem>();
            Talkgroups = new ObservableCollection<HistoryLookupItem>();

            Calls.CollectionChanged += OnHistoryCallsChanged;

            HourOptions = Enumerable.Range(0, 24).ToList();
            MinuteOptions = Enumerable.Range(0, 60).ToList();
            SecondOptions = Enumerable.Range(0, 60).ToList();

            _audioHttpClient = ServerAuthenticationHandler.CreateClient(_settingsService);
            _audioHttpClient.Timeout = Timeout.InfiniteTimeSpan;

            var savedSpeed = AppStateStore.GetDouble("history_playback_speed_step", 0.0);
            HistoryPlaybackSpeedStep = savedSpeed;

            SearchCommand = new Command(async () => await SafeRunAsync(SearchAsync, "History.Search"), () => CanSearch);
            LoadMoreOlderCommand = new Command(async () => await SafeRunAsync(LoadMoreOlderAsync, "History.LoadMoreOlder"), () => CanLoadMoreOlder);
            PlayFromCallCommand = new Command<CallItem>(async c => await SafeRunAsync(() => PlayFromCallAsync(c), "History.PlayFromCall"), c => !IsLoading && c != null);
            PlayCommand = new Command(async () => await SafeRunAsync(PlayAsync, "History.Play"), () => !IsLoading && IsPlayEnabled);
            StopCommand = new Command(async () => await SafeRunAsync(StopAsync, "History.Stop"), () => true);
#if ANDROID
            JoesScanner.Platforms.Android.Services.AndroidAudioEnvironment.ResumeStateChanged += OnMobileResumeStateChanged;
#elif IOS
            JoesScanner.Platforms.iOS.Services.IosAudioEnvironment.ResumeStateChanged += OnMobileResumeStateChanged;
#endif
            // Keep transport available during an older-page request.
            NextCommand = new Command(async () => await SafeRunAsync(SkipNextAsync, "History.Next"), () => CanGoHistoryNext);
            PreviousCommand = new Command(async () => await SafeRunAsync(SkipPreviousAsync, "History.Previous"), () => CanGoHistoryPrevious);

            PlaybackSpeedDownCommand = new Command(DecreasePlaybackSpeedStep, () => !IsLoading && (Calls.Count > 0 || _isQueuePlaybackRunning));
            PlaybackSpeedUpCommand = new Command(IncreasePlaybackSpeedStep, () => !IsLoading && (Calls.Count > 0 || _isQueuePlaybackRunning));

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

        public ObservableCollection<CallItem> AddressAlerts { get; } = new();
        public bool HasAddressAlerts => AddressAlerts.Count > 0;

        public bool IsDownloadRangeSelecting
        {
            get => _isDownloadRangeSelecting;
            set
            {
                if (_isDownloadRangeSelecting == value)
                    return;

                _isDownloadRangeSelecting = value;
                OnPropertyChanged();
            }
        }

        public string DownloadRangePrompt
        {
            get => _downloadRangePrompt;
            set
            {
                var newValue = value ?? string.Empty;
                if (_downloadRangePrompt == newValue)
                    return;

                _downloadRangePrompt = newValue;
                OnPropertyChanged();
            }
        }

        // We only show up to 3 alerts at once, but we keep older ones queued until dismissed.
        private readonly List<CallItem> _hiddenAddressAlerts = new();

		public CallItem? ResolveCallForAddressAlert(CallItem alertItem)
		{
			try
			{
				if (Calls.Contains(alertItem))
					return alertItem;

				var id = alertItem.BackendId;
				if (!string.IsNullOrWhiteSpace(id))
					return Calls.FirstOrDefault(c => c.ServerId == alertItem.ServerId && c.BackendId == id);
			}
			catch { }
			return null;
		}

        public IReadOnlyList<int> HourOptions { get; }
        public IReadOnlyList<int> MinuteOptions { get; }
        public IReadOnlyList<int> SecondOptions { get; }

        public Command SearchCommand { get; }
        public Command LoadMoreOlderCommand { get; }
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
                HistoryFiltersChanged();
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
                HistoryFiltersChanged();
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
                HistoryFiltersChanged();
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
                HistoryFiltersChanged();
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
                HistoryFiltersChanged();
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
                HistoryFiltersChanged();
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

        public bool What3WordsLinksEnabled
        {
            get => _what3WordsLinksEnabled;
            private set
            {
                if (_what3WordsLinksEnabled == value)
                    return;

                _what3WordsLinksEnabled = value;
                OnPropertyChanged();
            }
        }

        // Keep transport/speed buttons usable while audio is playing even if the list is paged/cleared.
        public bool IsHistoryMediaButtonsEnabled => _isQueuePlaybackRunning || Calls.Count > 0;
        public bool IsStopEnabled => _isQueuePlaybackRunning || _historySelectionRequests.HasManualSelection;

        private bool CanResumeMobilePlayback =>
#if ANDROID
            _isQueuePlaybackRunning && JoesScanner.Platforms.Android.Services.AndroidAudioEnvironment.RequiresPlay;
#elif IOS
            _isQueuePlaybackRunning && JoesScanner.Platforms.iOS.Services.IosAudioEnvironment.RequiresPlay;
#else
            false;
#endif

        public bool IsPlayEnabled => CanResumeMobilePlayback || (!IsStopEnabled && CanNavigateHistory);

        public bool ShowHistoryPlayButton => !IsStopEnabled || CanResumeMobilePlayback;

        public bool ShowHistoryStopButton => IsStopEnabled;

#if ANDROID || IOS
        private void OnMobileResumeStateChanged()
        {
            var lease = Volatile.Read(ref _historyOwnership);
            _ = RunOnMainThreadAsync(() =>
            {
                if (!OwnsHistoryPage(lease) || !_isQueuePlaybackRunning)
                    return;
                OnPropertyChanged(nameof(ShowHistoryPlayButton));
                OnPropertyChanged(nameof(IsPlayEnabled));
                PlayCommand.ChangeCanExecute();
            });
        }
#endif

        public bool IsHistoryPlaybackRunning => _isQueuePlaybackRunning;

        public CallItem? ActiveCall => _activeCall;

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
            OnPropertyChanged(nameof(CanSearch));
            OnPropertyChanged(nameof(CanLoadMoreOlder));
            OnPropertyChanged(nameof(HasHistoryResults));
        }

        private async Task SafeRunAsync(Func<Task> action, string operation)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.Add(() => $"{operation} failed. ex={DiagnosticRedaction.Describe(ex)}");
                }
                catch
                {
                }
            }
        }

        private Task PlayFromCallAsync(CallItem call) => SelectHistoryRecordingAsync(HistoryNavigation.Recording, call);

        private Task PlayAsync()
        {
#if ANDROID || IOS
            if (CanResumeMobilePlayback)
            {
#if ANDROID
                SystemMediaService.PrepareAndroidPlaybackService();
                JoesScanner.Platforms.Android.Services.AndroidAudioEnvironment.RequestPlay();
#else
                JoesScanner.Platforms.iOS.Services.IosAudioEnvironment.RequestPlay();
#endif
                return Task.CompletedTask;
            }
#endif
            return SelectHistoryRecordingAsync(HistoryNavigation.Play);
        }

        private Task SkipNextAsync() => SelectHistoryRecordingAsync(HistoryNavigation.Next);

        private Task SkipPreviousAsync() => SelectHistoryRecordingAsync(HistoryNavigation.Previous);

        private double GetPlaybackRate() => _historyPlaybackSpeedStep switch
        {
            1 => 1.25,
            2 => 1.5,
            3 => 1.75,
            4 => 2.0,
            _ => 1.0
        };

        private bool HasHistoryRecorderAccess(HistoryRecorderAccess access)
        {
            var recorder = access.Recorder;
            var protectedServer = HostedServerRules.RequiresApiFirewallCredentials(_settingsService, recorder.Endpoint);
            if (protectedServer != access.Protected)
                return false;
            if (protectedServer)
                return _settingsService.ReadAccountSession().AccountVersion == access.AccountVersion &&
                    RecordingPlaybackAccess.AccountKey(_settingsService, recorder.Endpoint, independent: true) == access.Account;
            return RecordingPlaybackAccess.RecorderAccountKey(_settingsService, recorder.Id, recorder.Endpoint) == access.Account;
        }

        private void RequireHistoryRecordingAccess(HistorySearchSnapshot snapshot, CallItem item)
        {
            var access = snapshot.Access;
            if (item.ServerId != access.Recorder.Id || item.RecordingServer != access.Recorder.Endpoint ||
                item.RecordingAccess is not { Origin: RecordingOrigin.History } context ||
                context.Protected != access.Protected || context.Account != access.Account)
                throw new RecordingAccessException("This recording belongs to a different History search.");

            if (!HasHistoryRecorderAccess(access))
                throw new RecordingAccessException("The recorder sign-in changed. Search again.");
        }

        private async Task AuthorizeHistoryRecordingAsync(HistorySearchSnapshot snapshot, CallItem item, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RequireHistoryRecordingAccess(snapshot, item);
            await RecordingPlaybackAccess.VerifyAsync(_settingsService, _subscriptionService, snapshot.Access.Recorder.Endpoint,
                item.RecordingTimeUtc, item.RecordingAccess!, currentLive: false, requireAccount: snapshot.Access.Protected,
                token, independent: true).ConfigureAwait(false);
            RequireHistoryRecordingAccess(snapshot, item);
        }

        private void RequireCachedHistoryEntitlement(HistorySearchSnapshot snapshot, CallItem item)
        {
            RequireHistoryRecordingAccess(snapshot, item);
            if (snapshot.Access.Protected)
                RecordingPlaybackAccess.RequireCachedEntitlement(_settingsService, snapshot.Access.Recorder.Endpoint,
                    snapshot.Access.Account, item.RecordingTimeUtc, currentLive: false, independent: true);
        }

        private async Task RunAuthorizedHistoryAudioAsync(HistorySearchSnapshot snapshot, CallItem item,
            CancellationToken token, Func<CancellationToken, Task> play)
        {
            using var authorization = ProtectHistoryRecording(snapshot, item, token);
            try
            {
                await play(authorization?.Token ?? token).ConfigureAwait(false);
                authorization?.Validate();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && authorization?.Token.IsCancellationRequested == true)
            {
                throw RecordingPlaybackAccess.VerificationRequired();
            }
        }

        private HostedEntitlement.Access? ProtectHistoryRecording(HistorySearchSnapshot snapshot, CallItem item,
            CancellationToken token)
        {
            RequireHistoryRecordingAccess(snapshot, item);
            if (!snapshot.Access.Protected)
                return null;
            var authorization = HostedEntitlement.For(_settingsService).Protect(token,
                recordedAt: item.RecordingTimeUtc, history: true, accountVersion: snapshot.Access.AccountVersion);
            try
            {
                RequireHistoryRecordingAccess(snapshot, item);
                authorization.Validate();
                return authorization;
            }
            catch
            {
                authorization.Dispose();
                throw;
            }
        }

        private async Task<PlaybackAudioLease> AcquireHistoryRecordingAsync(HistorySearchSnapshot snapshot, CallItem item,
            CancellationToken token)
        {
            var recorder = snapshot.Access.Recorder;
            var authority = snapshot.Access.Protected ? _settingsService.ReadAccountSession().BaseUrl : null;
            var key = RecordingCacheKey.Create(recorder.Id, recorder.Endpoint, item.BackendId, snapshot.Access.Account, authority);
            return await _audioCache.AcquireAsync(key,
                (cached, acquireToken) =>
                {
                    if (cached)
                    {
                        RequireCachedHistoryEntitlement(snapshot, item);
                        return Task.CompletedTask;
                    }
                    RequireNetwork();
                    return AuthorizeHistoryRecordingAsync(snapshot, item, acquireToken);
                },
                async (transfer, acquireToken) =>
                {
                    using var authorization = ProtectHistoryRecording(snapshot, item, acquireToken);
                    using var stream = _callStreamService.ForRecorder(recorder.Endpoint, recorder.Id);
                    try
                    {
                        var path = await RecordingAudioRecovery.AcquireAsync(item.AudioUrl,
                            refreshToken =>
                            {
                                RequireNetwork();
                                return stream.RefreshRecordingAudioAsync(item, recorder.Endpoint, item.RecordingTimeUtc, refreshToken);
                            },
                            (url, downloadToken) =>
                            {
                                RequireNetwork();
                                // Normalizing legacy encoded spaces must leave signed URLs intact.
                                var normalized = url.Contains('?') ? url : Regex.Replace(url, "(?i)%25", "%");
                                return RecordingAudioRecovery.DownloadAsync(_audioHttpClient, normalized, transfer, downloadToken,
                                    request => request.Options.Set(ServerAuthenticationHandler.Recorder, recorder.RequestScope));
                            }, authorization?.Token ?? acquireToken).ConfigureAwait(false);
                        authorization?.Validate();
                        RequireHistoryRecordingAccess(snapshot, item);
                        return path;
                    }
                    catch (OperationCanceledException) when (!acquireToken.IsCancellationRequested && authorization?.Token.IsCancellationRequested == true)
                    {
                        throw RecordingPlaybackAccess.VerificationRequired();
                    }
                }, token).ConfigureAwait(false) ?? throw new RecordingAudioUnavailableException();

            void RequireNetwork()
            {
                RequireHistoryRecordingAccess(snapshot, item);
                RecordingPlaybackAccess.RequireRecorderNetwork(_settingsService, recorder.Endpoint);
            }
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
        
        private void StartAddressDetectionForHistoryCalls(IReadOnlyList<CallItem> calls)
        {
            var lease = Volatile.Read(ref _historyOwnership);
            if (lease == null)
                return;

            try
            {
                if (calls == null || calls.Count == 0)
                {
                    try { AppLog.Add(() => "AddrDetect(Hist): skipped. calls empty"); } catch { }
                    return;
                }

                try
                {
                    var withTx = calls.Count(c => c != null && !string.IsNullOrWhiteSpace(c.Transcription));
                    AppLog.Add(() => $"AddrDetect(Hist): start calls={calls.Count} withTx={withTx}");
                }
                catch { }

                _ = Task.Run(async () =>
                {
                    var applied = 0;
                    var detected = 0;

                    foreach (var call in calls)
                    {
                        if (lease.Token.IsCancellationRequested)
                            break;

                        try
                        {
                            if (call == null)
                                continue;

                            if (string.IsNullOrWhiteSpace(call.Transcription))
                                continue;

                            var wasApplied = await ReadHistoryUiAsync(() =>
                            {
                                if (!OwnsHistoryPage(lease) || !Calls.Contains(call))
                                    return false;
                                _addressDetectionService.Apply(call);
                                if (call.HasDetectedAddress)
                                    AddAddressAlertIfNew(call);
                                return true;
                            }).ConfigureAwait(false);
                            if (!wasApplied)
                                break;

                            try { _ = _what3WordsService.ResolveCoordinatesIfNeededAsync(call, lease.Token); } catch { }

                            applied++;
                            if (!string.IsNullOrWhiteSpace(call.DetectedAddress))
                                detected++;
                        }
                        catch (Exception ex)
                        {
                            try { AppLog.Add(() => $"AddrDetect(Hist): apply failed call={call?.BackendId}. {DiagnosticRedaction.Describe(ex)}"); } catch { }
                        }
                    }

                    try
                    {
                        AppLog.Add(() => $"AddrDetect(Hist): done applied={applied} detected={detected}");
                    }
                    catch { }
                });
            }
            catch { }
        }


        private void AddAddressAlertIfNew(CallItem call)
{
    try
    {
        if (call == null || !call.HasDetectedAddress)
        {
            try
            {
                if (call == null)
                    AppLog.Add("AddrAlert(Hist): Add skipped. call is null");
                else
                    AppLog.Add(() => $"AddrAlert(Hist): Add skipped. HasDetectedAddress=false callId={call.BackendId ?? "(no id)"}");
            }
            catch
            {
            }
            return;
        }

        try
        {
            AppLog.Add(() => $"AddrAlert(Hist): Add attempt. callId={call.BackendId ?? "(no id)"} addr='{call.DetectedAddress ?? ""}' conf={call.DetectedAddressConfidencePercent:0.##}% beforeVisible={AddressAlerts.Count} beforeHidden={_hiddenAddressAlerts.Count} hasVisibleFlag={HasAddressAlerts}");
        }
        catch
        {
        }

        var id = call.BackendId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (AddressAlerts.Any(a => !string.IsNullOrWhiteSpace(a.BackendId) &&
                                      string.Equals(a.BackendId, id, StringComparison.Ordinal)))
            {
                try { AppLog.Add(() => $"AddrAlert(Hist): Add skipped. Duplicate visible. callId={id}"); } catch { }
                return;
            }

            if (_hiddenAddressAlerts.Any(a => !string.IsNullOrWhiteSpace(a.BackendId) &&
                                             string.Equals(a.BackendId, id, StringComparison.Ordinal)))
            {
                try { AppLog.Add(() => $"AddrAlert(Hist): Add skipped. Duplicate hidden. callId={id}"); } catch { }
                return;
            }
        }
        else
        {
            if (AddressAlerts.Any(a => ReferenceEquals(a, call)) || _hiddenAddressAlerts.Any(a => ReferenceEquals(a, call)))
            {
                try { AppLog.Add("AddrAlert(Hist): Add skipped. Duplicate by reference."); } catch { }
                return;
            }
        }

        AddressAlerts.Insert(0, call);

        try
        {
            AppLog.Add(() => $"AddrAlert(Hist): Added. nowVisible={AddressAlerts.Count} nowHidden={_hiddenAddressAlerts.Count} hasVisibleFlag={HasAddressAlerts}");
        }
        catch
        {
        }

        // Keep only 3 visible; queue the oldest visible alert instead of discarding it.
        if (AddressAlerts.Count > 3)
        {
            var oldestVisible = AddressAlerts[AddressAlerts.Count - 1];
            AddressAlerts.RemoveAt(AddressAlerts.Count - 1);
            _hiddenAddressAlerts.Add(oldestVisible);

            try
            {
                AppLog.Add(() => $"AddrAlert(Hist): Moved oldest to hidden. nowVisible={AddressAlerts.Count} nowHidden={_hiddenAddressAlerts.Count}");
            }
            catch
            {
            }

            if (_hiddenAddressAlerts.Count > 25)
                _hiddenAddressAlerts.RemoveRange(0, _hiddenAddressAlerts.Count - 25);
        }
    }
    catch
    {
        try { AppLog.Add("AddrAlert(Hist): Add failed (exception swallowed)"); } catch { }
    }
}

public void DismissAddressAlert(CallItem call)
{
    try
    {
        if (!MainThread.IsMainThread)
        {
            try
            {
                AppLog.Add(() => $"AddrAlert(Hist): Dismiss request off UI thread. Scheduling on UI thread. callId={call?.BackendId ?? "(null)"}");
                MainThread.BeginInvokeOnMainThread(() => DismissAddressAlert(call));
            }
            catch
            {
            }
            return;
        }

        if (call == null)
            return;

        try
        {
            AppLog.Add(() => $"AddrAlert(Hist): Dismiss attempt. callId={call.BackendId ?? "(no id)"} beforeVisible={AddressAlerts.Count} beforeHidden={_hiddenAddressAlerts.Count}");
        }
        catch
        {
        }

        var existing = AddressAlerts.FirstOrDefault(a =>
            (!string.IsNullOrWhiteSpace(a.BackendId) && !string.IsNullOrWhiteSpace(call.BackendId) &&
             string.Equals(a.BackendId, call.BackendId, StringComparison.Ordinal)) ||
            ReferenceEquals(a, call));

        if (existing != null)
            AddressAlerts.Remove(existing);
        else
        {
            var hiddenExisting = _hiddenAddressAlerts.FirstOrDefault(a =>
                (!string.IsNullOrWhiteSpace(a.BackendId) && !string.IsNullOrWhiteSpace(call.BackendId) &&
                 string.Equals(a.BackendId, call.BackendId, StringComparison.Ordinal)) ||
                ReferenceEquals(a, call));

            if (hiddenExisting != null)
                _hiddenAddressAlerts.Remove(hiddenExisting);
        }

        while (AddressAlerts.Count < 3 && _hiddenAddressAlerts.Count > 0)
        {
            var idx = _hiddenAddressAlerts.Count - 1;
            var next = _hiddenAddressAlerts[idx];
            _hiddenAddressAlerts.RemoveAt(idx);
            AddressAlerts.Add(next);
        }

        try
        {
            AppLog.Add(() => $"AddrAlert(Hist): Dismiss done. afterVisible={AddressAlerts.Count} afterHidden={_hiddenAddressAlerts.Count} hasVisibleFlag={HasAddressAlerts}");
        }
        catch
        {
        }
    }
    catch
    {
        try { AppLog.Add("AddrAlert(Hist): Dismiss failed (exception swallowed)"); } catch { }
    }
}

private void OnAddressDetectionSettingsChanged(object? sender, EventArgs e)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        if (Calls == null || Calls.Count == 0)
                            return;

                        foreach (var item in Calls)
                        {
                            _addressDetectionService.Apply(item);
							try { _ = _what3WordsService.ResolveCoordinatesIfNeededAsync(item, CancellationToken.None); } catch { }
                        }
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

		private void OnToneDetected(string audioUrl)
		{
			if (string.IsNullOrWhiteSpace(audioUrl))
				return;

			try
			{
				MainThread.BeginInvokeOnMainThread(() =>
				{
					try
					{
						var match = Calls.FirstOrDefault(c =>
							!string.IsNullOrWhiteSpace(c.AudioUrl)
							&& string.Equals(c.AudioUrl, audioUrl, StringComparison.OrdinalIgnoreCase));

						if (match == null)
							return;

						var key = match.ToneHotKey;
						var minutes = Math.Clamp(_settingsService.AudioToneHighlightMinutes, 1, 99);
						_toneAlertService.SetTalkgroupHot(key, TimeSpan.FromMinutes(minutes));
						RefreshToneHotFlags();

						try { AppLog.Add(() => $"AudioTone: talkgroup hot for {minutes} minutes. key={key}"); } catch { }
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

		private void OnHotTalkgroupsChanged()
		{
			try
			{
				MainThread.BeginInvokeOnMainThread(RefreshToneHotFlags);
			}
			catch
			{
			}
		}

		private void RefreshToneHotFlags()
		{
			try
			{
				foreach (var c in Calls)
					c.IsToneHot = _toneAlertService.IsTalkgroupHot(c.ToneHotKey);
			}
			catch
			{
			}
		}

    }
}
