using System.Collections.ObjectModel;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.ViewModels;

public sealed partial class HistoryViewModel
{
    private readonly MainViewModel _mainViewModel;
    private readonly HistorySearchPreferences _historyPreferences;
    private readonly LivePlaybackRequests _historySearchRequests = new();
    private readonly LivePlaybackRequests _historyLookupRequests = new();
    private ScannerConnectionRow? _selectedRecorder;
    private HistoryRecorder? _historyRecorder;
    private HistorySearchResult? _historyResults;
    private Task? _lookupTask;
    private bool _lookupsLoaded;
    private bool _restoringHistoryFilters;
    private bool _isPaging;
    private IReadOnlyList<HistoryLookupItem> _selectedTalkgroups = [];

    public ObservableCollection<ScannerConnectionRow> HistoryRecorders => _mainViewModel.ScannerConnections;
    public ScannerConnectionRow? SelectedRecorder
    {
        get => _selectedRecorder;
        set => SelectRecorder(value);
    }

    internal HistoryRecorder? CurrentHistoryRecorder => _historyRecorder;

    public bool CanSearch => OwnsHistoryPage(_historyOwnership) && !IsLoading && !_isPaging &&
        _historyRecorder is { } recorder && CurrentFilters().IsValidFor(recorder.Id);
    public bool HasHistoryResults => _historyResults != null && Calls.Count != 0;
    public bool CanLoadMoreOlder => HasHistoryResults && _historyResults!.HasMore && !IsLoading && !_isPaging &&
        OwnsHistoryPage(_historyOwnership);

    public HistoryLookupItem? SelectedReceiver => _selectedReceiver;
    public HistoryLookupItem? SelectedSite => _selectedSite;
    public IReadOnlyList<HistoryLookupItem> SelectedTalkgroups => _selectedTalkgroups;
    public string SelectedReceiverDisplay => _selectedReceiver?.Label ?? "All";
    public string SelectedSiteDisplay => _selectedSite?.Label ?? "All";
    public string SelectedTalkgroupDisplay => _selectedTalkgroups.Count switch
    {
        0 => "All",
        1 => _selectedTalkgroups[0].Label,
        _ => $"{_selectedTalkgroups.Count} selected"
    };

    public async Task OnPageOpenedAsync()
    {
        await RunOnMainThreadAsync(() =>
        {
            TakeHistoryPlaybackOwnership();
            _mainViewModel.RefreshScannerConnectionRows();
            Guid? last = null;
            try { last = _historyPreferences.LastRecorder; }
            catch (Exception ex) { AppLog.Add(() => $"History recorder preference could not be read ({ex.GetType().Name})."); }

            var profile = HistorySearchPreferences.ChooseRecorder(HistoryRecorders.Select(row => row.Profile).ToArray(),
                last, _mainViewModel.VisibleLiveRecorderId, _settingsService.ServerUrl);
            SelectRecorder(profile == null ? null : HistoryRecorders.FirstOrDefault(row => row.Id == profile.Id));
            RefreshHistoryDateAccess();
            RefreshCommandStates();
        });
        await EnsureLookupsLoadedAsync();
    }

    private void SelectRecorder(ScannerConnectionRow? row)
    {
        var recorder = row == null ? null : new HistoryRecorder(row.Id, row.Endpoint);
        if (ReferenceEquals(_selectedRecorder, row) && recorder == _historyRecorder)
        {
            if (recorder == null)
                ClearSearchResults();
            return;
        }

        RetireHistorySearchRequests();
        _selectedRecorder = row;
        _historyRecorder = recorder;
        _lookupsLoaded = false;
        _lookupTask = null;
        Receivers.Clear();
        Sites.Clear();
        Talkgroups.Clear();

        SavedHistoryFilters? stored = null;
        if (recorder != null)
        {
            try
            {
                _historyPreferences.LastRecorder = recorder.Id;
                stored = _historyPreferences.ReadFilters(recorder.Id);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"History filters could not be restored ({ex.GetType().Name}).");
            }
        }

        _restoringHistoryFilters = true;
        try
        {
            var defaultStart = DateTime.Now.AddMinutes(-2);
            var today = DateOnly.FromDateTime(defaultStart);
            var dates = stored?.Dates ?? new HistoryDateRange(today, today, defaultStart.TimeOfDay);
            _selectedDateFrom = dates.From.ToDateTime(TimeOnly.MinValue);
            _selectedDateTo = dates.To.ToDateTime(TimeOnly.MinValue);
            var selections = recorder != null ? stored?.ForRecorder(recorder.Id) : null;
            _selectedReceiver = selections?.Receiver;
            _selectedSite = selections?.Site;
            _selectedTalkgroups = selections?.Talkgroups ?? [];
            SelectedTime = dates.StartTime;
            OnPropertyChanged(nameof(SelectedDateFrom));
            OnPropertyChanged(nameof(SelectedDateTo));
            OnPropertyChanged(nameof(SelectedDateFromDisplay));
            OnPropertyChanged(nameof(SelectedDateToDisplay));
        }
        finally { _restoringHistoryFilters = false; }

        OnPropertyChanged(nameof(SelectedRecorder));
        NotifyLookupSelections();
        RefreshHistoryDateAccess();
        ClearSearchResults();
        if (recorder != null && OwnsHistoryPage(_historyOwnership))
            _ = EnsureLookupsLoadedAsync();
    }

    private void RefreshHistoryDateAccess()
    {
        var restrict = _historyRecorder is { } recorder &&
            HostedServerRules.RequiresApiFirewallCredentials(_settingsService, recorder.Endpoint) &&
            _settingsService.ReadHostedSubscription().Tier < 2;
        if (_enforceHistory24HourLimit == restrict)
            return;
        _enforceHistory24HourLimit = restrict;
        OnPropertyChanged(nameof(CanPickDateRange));
    }

    private void OnHistoryAccountChanged(object? sender, EventArgs args) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_historyRecorder is not { } recorder ||
            !HostedServerRules.RequiresApiFirewallCredentials(_settingsService, recorder.Endpoint))
            return;
        RetireHistorySearchRequests();
        Receivers.Clear();
        Sites.Clear();
        Talkgroups.Clear();
        _lookupsLoaded = false;
        _lookupTask = null;
        RefreshHistoryDateAccess();
        ClearSearchResults();
        if (OwnsHistoryPage(_historyOwnership))
            _ = EnsureLookupsLoadedAsync();
    });

    internal void SelectReceiverFilter(HistoryLookupItem item)
    {
        if (!IsLookupFromCurrentRecorder(item) || (_selectedReceiver?.Value ?? "") == item.Value)
            return;
        _selectedReceiver = item.Value.Length == 0 ? null : item;
        _selectedSite = null;
        _selectedTalkgroups = [];
        NotifyLookupSelections();
        HistoryFiltersChanged();
    }

    internal void SelectSiteFilter(HistoryLookupItem item)
    {
        if (!IsLookupFromCurrentRecorder(item) || (_selectedSite?.Value ?? "") == item.Value)
            return;
        _selectedSite = item.Value.Length == 0 ? null : item;
        _selectedTalkgroups = [];
        NotifyLookupSelections();
        HistoryFiltersChanged();
    }

    internal void SelectTalkgroupFilters(HistoryRecorder recorder, IReadOnlyList<HistoryLookupItem> items)
    {
        if (recorder != _historyRecorder || items.Any(item => !IsLookupFromCurrentRecorder(item) ||
            string.IsNullOrWhiteSpace(item.Value) || item.Value.Contains(',')))
            return;
        var groups = items.DistinctBy(item => item.Value, StringComparer.Ordinal).ToArray();
        if (_selectedTalkgroups.Select(item => item.Value).SequenceEqual(groups.Select(item => item.Value)))
            return;
        _selectedTalkgroups = groups;
        NotifyLookupSelections();
        HistoryFiltersChanged();
    }

    private bool IsLookupFromCurrentRecorder(HistoryLookupItem item) =>
        _historyRecorder is { } recorder && item.RecorderId == recorder.Id;

    private HistorySearchFilters CurrentFilters() => new()
    {
        Receiver = _selectedReceiver,
        Site = _selectedSite,
        Talkgroups = _selectedTalkgroups
    };

    private HistoryDateRange CurrentDates() => new(DateOnly.FromDateTime(SelectedDateFrom),
        DateOnly.FromDateTime(SelectedDateTo), SelectedTime);

    private void HistoryFiltersChanged()
    {
        if (_restoringHistoryFilters || _historyRecorder is not { } recorder)
            return;

        _historySearchRequests.RetireCurrent();
        try
        {
            _historyPreferences.SaveFilters(recorder.Id, new(CurrentDates(), SavedHistoryChoice.From(_selectedReceiver),
                SavedHistoryChoice.From(_selectedSite), _selectedTalkgroups.Select(SavedHistoryChoice.From).OfType<SavedHistoryChoice>().ToArray()));
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"History filters could not be saved ({ex.GetType().Name}).");
        }
        ClearSearchResults();
    }

    private void NotifyLookupSelections()
    {
        OnPropertyChanged(nameof(SelectedReceiver));
        OnPropertyChanged(nameof(SelectedSite));
        OnPropertyChanged(nameof(SelectedTalkgroups));
        OnPropertyChanged(nameof(SelectedReceiverDisplay));
        OnPropertyChanged(nameof(SelectedSiteDisplay));
        OnPropertyChanged(nameof(SelectedTalkgroupDisplay));
    }

    public Task EnsureLookupsLoadedAsync(bool forceReload = false)
    {
        if (_historyRecorder is not { } recorder || _historyOwnership is not { } lease ||
            !OwnsHistoryPage(lease) || _lookupsLoaded && !forceReload)
            return Task.CompletedTask;

        if (_lookupTask is { IsCompleted: false } pending)
            return pending;
        return _lookupTask = LoadRecorderLookupsAsync(recorder, lease, forceReload);
    }

    private async Task LoadRecorderLookupsAsync(HistoryRecorder recorder, PlaybackOwnership.Lease lease, bool force)
    {
        var request = _historyLookupRequests.Begin(manual: false, lease.Token);
        try
        {
            var data = await _callHistoryService.GetRecorderLookupsAsync(recorder, force, request.Token).ConfigureAwait(false);
            await RunOnMainThreadAsync(() =>
            {
                if (!request.CanContinue || !OwnsHistoryPage(lease) || recorder != _historyRecorder)
                    return;

                ReplaceLookups(Receivers, data.Receivers, _selectedReceiver is { } receiver ? [receiver] : []);
                ReplaceLookups(Sites, data.Sites, _selectedSite is { } site ? [site] : []);
                ReplaceLookups(Talkgroups, data.Talkgroups, _selectedTalkgroups);
                _selectedReceiver = MatchLookup(Receivers, _selectedReceiver);
                _selectedSite = MatchLookup(Sites, _selectedSite);
                _selectedTalkgroups = _selectedTalkgroups.Select(item => MatchLookup(Talkgroups, item)!).ToArray();
                _lookupsLoaded = data.Receivers.Any(item => item.Value.Length != 0) ||
                    data.Sites.Any(item => item.Value.Length != 0) || data.Talkgroups.Any(item => item.Value.Length != 0);
                NotifyLookupSelections();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Add(() => $"History lookups failed ({ex.GetType().Name})."); }
        finally { await RunOnMainThreadAsync(request.Dispose).ConfigureAwait(false); }
    }

    private static void ReplaceLookups(ObservableCollection<HistoryLookupItem> target, IReadOnlyList<HistoryLookupItem> incoming,
        IReadOnlyList<HistoryLookupItem> selected)
    {
        target.Clear();
        foreach (var item in incoming.Concat(selected).DistinctBy(item => item.Value, StringComparer.Ordinal))
            target.Add(item);
    }

    private static HistoryLookupItem? MatchLookup(IEnumerable<HistoryLookupItem> items, HistoryLookupItem? selected) =>
        selected == null ? null : items.FirstOrDefault(item => item.Value == selected.Value) ?? selected;

    internal static bool LookupHasOnlyDefault(IEnumerable<HistoryLookupItem> items) =>
        !items.Any(item => !string.IsNullOrWhiteSpace(item.Value));

    private void RetireHistorySearchRequests()
    {
        _historySearchRequests.RetireCurrent();
        _historyLookupRequests.RetireCurrent();
        _lookupTask = null;
        _isPaging = false;
        IsLoading = false;
    }

    private void ClearSearchResults()
    {
        if (OwnsHistoryPage(_historyOwnership))
            StopHistoryPlayback();
        _historyResults = null;
        _activeCall = null;
        OnPropertyChanged(nameof(ActiveCall));
        Calls.Clear();
        AddressAlerts.Clear();
        _hiddenAddressAlerts.Clear();
        IsDownloadRangeSelecting = false;
        DownloadRangePrompt = string.Empty;
        IsLoading = false;
        _isPaging = false;
        var recorder = _historyRecorder;
        StatusText = recorder == null ? "Add a recorder in Settings to search History." :
            CurrentFilters().IsValidFor(recorder.Id) ? string.Empty : "Select a site, receiver, or talkgroup to search.";
        RefreshCommandStates();
    }

    private Task SearchAsync() => ReadHistoryPageAsync(loadMore: false);

    private Task LoadMoreOlderAsync() => ReadHistoryPageAsync(loadMore: true);

    private async Task ReadHistoryPageAsync(bool loadMore)
    {
        if (_historyOwnership is not { } lease || _historyRecorder is not { } recorder ||
            !(loadMore ? CanLoadMoreOlder : CanSearch))
            return;

        var previous = _historyResults;
        var filters = CurrentFilters();
        var dates = CurrentDates();
        if (!loadMore)
            ClearSearchResults();

        var request = _historySearchRequests.Begin(manual: false, lease.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        bool OwnsRequest() => request.CanContinue && OwnsHistoryPage(lease) && recorder == _historyRecorder;

        _isPaging = loadMore;
        IsLoading = !loadMore;
        if (!_isQueuePlaybackRunning)
            StatusText = loadMore ? "Loading older recordings…" : "Searching…";
        RefreshCommandStates();

        try
        {
            var page = await (loadMore ? _callHistoryService.LoadMoreAsync(previous!, deadline.Token)
                : _callHistoryService.SearchAsync(recorder, filters, dates, deadline.Token)).WaitAsync(deadline.Token)
                .ConfigureAwait(false);

            await RunOnMainThreadAsync(() =>
            {
                if (!OwnsRequest() || deadline.IsCancellationRequested)
                    return;
                _historyResults = page;
                HistoryResultOrder.Add(Calls, page.Calls);
                StartAddressDetectionForHistoryCalls(page.Calls);
                RefreshHistoryDateAccess();
                if (!_isQueuePlaybackRunning)
                    StatusText = HistoryResultStatus();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!OwnsRequest()) { }
        catch (Exception ex)
        {
            await RunOnMainThreadAsync(() =>
            {
                if (!OwnsRequest())
                    return;
                if (ex is HistoryResultsChangedException && previous != null)
                    _historyResults = new(previous.Snapshot, previous.Cursor, [], false);
                StatusText = ex switch
                {
                    OperationCanceledException => "History request timed out.",
                    ArgumentException or RecordingAccessException or FirewallAuthenticationException or HistoryResultsChangedException => ex.Message,
                    _ => "History request failed. Try again."
                };
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnMainThreadAsync(() =>
            {
                if (OwnsRequest())
                {
                    IsLoading = false;
                    _isPaging = false;
                    RefreshCommandStates();
                }
                request.Dispose();
            }).ConfigureAwait(false);
        }
    }

    private string HistoryResultStatus() => Calls.Count == 0 ? "No recordings match these filters." :
        $"{Calls.Count} of {_historyResults?.TotalMatches ?? Calls.Count} recordings" +
        (_historyResults?.LimitedToLast24Hours == true ? " · Last 24 hours" : string.Empty);
}
