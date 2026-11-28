using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public sealed partial class HistoryViewModel
{
    private const int HistoryPrefetchThreshold = 25;
    private readonly LivePlaybackRequests _historyPageRequests = new();
    private HistorySearchResult? _historyResults;
    private Task<bool>? _olderHistoryPage;
    private string? _historyPageError;
    private bool _isPaging;
    private sealed record HistoryOlderPrefetch(HistorySearchResult Results, Task<bool>? LastRequest, int StopRevision);

    internal event Action? OlderHistoryResultsAdding;
    internal event Action? OlderHistoryResultsAdded;

    public bool CanSearch => OwnsHistoryPage(_historyOwnership) && !IsLoading &&
        _selectedAccess is { Retired: false } && _historyRecorder is { } recorder && CurrentFilters().IsValidFor(recorder.Id);
    public bool HasHistoryResults => _historyResults != null && Calls.Count != 0;
    public bool CanLoadMoreOlder => HasHistoryResults && _historyResults!.HasMore && !IsLoading && !_isPaging &&
        OwnsHistoryPage(_historyOwnership);

    private void RetireHistoryPageRequest()
    {
        var wasReading = _isPaging || IsLoading;
        _historyPageRequests.RetireCurrent();
        _olderHistoryPage = null;
        _isPaging = false;
        IsLoading = false;
        if (wasReading)
            StatusText = _historyPageError ?? (_historyResults == null ? string.Empty : HistoryResultStatus());
    }

    private void ClearSearchResults()
    {
        RetireHistoryPageRequest();
        StopHistoryPlayback();
        _historyResults = null;
        _historyPageError = null;
        _activeCall = null;
        OnPropertyChanged(nameof(ActiveCall));
        Calls.Clear();
        ClearHistoryResultAnnotations();
        var recorder = _historyRecorder;
        StatusText = recorder == null ? "Add a recorder in Settings to search History." :
            CurrentFilters().IsValidFor(recorder.Id) ? string.Empty : "Select a site, receiver, or talkgroup to search.";
        RefreshCommandStates();
    }

    private Task SearchAsync()
    {
        if (!CanSearch || _historyOwnership is not { } lease || _historyRecorder is not { } recorder)
            return Task.CompletedTask;

        var filters = CurrentFilters();
        var dates = CurrentDates();
        ClearSearchResults();
        return ReadHistoryPageAsync(lease, recorder, null,
            token => _callHistoryService.SearchAsync(recorder, filters, dates, token));
    }

    private Task LoadMoreOlderAsync() => ReadOlderHistoryPageAsync();

    private HistoryOlderPrefetch? CaptureHistoryPrefetch(int index)
    {
        if (Calls.Count - index > HistoryPrefetchThreshold ||
            _historyResults is not { HasMore: true } results || _olderHistoryPage is { IsCompleted: false })
            return null;
        return new(results, _olderHistoryPage, _historyStopRevision);
    }

    private async Task PrefetchOlderHistoryPageAsync(HistoryPlayback playback, HistoryOlderPrefetch prefetch)
    {
        try
        {
            // Invoked by the native start signal. Return to audio immediately
            // even when the dispatcher and the service can run synchronously.
            await Task.Yield();
            var page = await ReadHistoryUiAsync<Task>(() =>
            {
                if (playback.Request.Token.IsCancellationRequested || prefetch.StopRevision != _historyStopRevision ||
                    !OwnsHistoryPage(playback.Lease) || _historyRecorder != prefetch.Results.Snapshot.Access.Recorder ||
                    !ReferenceEquals(_historyResults, prefetch.Results))
                    return Task.CompletedTask;

                // A manual read started since selection may already have finished.
                // Treat that read as this traversal's page, even if it failed.
                if (_olderHistoryPage is { IsCompleted: false } pending)
                    return pending;
                return ReferenceEquals(_olderHistoryPage, prefetch.LastRequest)
                    ? ReadOlderHistoryPageAsync() : Task.CompletedTask;
            }).ConfigureAwait(false);
            await page.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.DebugWriteLine(() => $"History prefetch failed ({ex.GetType().Name}).");
        }
    }

    // Load More, Previous at the boundary, and automatic prefetch share a read.
    // A newer playback choice can abandon its wait without altering the page.
    private Task<bool> ReadOlderHistoryPageAsync()
    {
        if (_olderHistoryPage is { IsCompleted: false } pending)
            return pending;
        if (!CanLoadMoreOlder || _historyOwnership is not { } lease || _historyRecorder is not { } recorder ||
            _historyResults is not { } previous)
            return Task.FromResult(false);

        return _olderHistoryPage = ReadHistoryPageAsync(lease, recorder, previous,
            token => _callHistoryService.LoadMoreAsync(previous, token));
    }

    private async Task<bool> ReadHistoryPageAsync(PlaybackOwnership.Lease lease, HistoryRecorder recorder,
        HistorySearchResult? previous, Func<CancellationToken, Task<HistorySearchResult>> fetch)
    {
        var request = _historyPageRequests.Begin(manual: false, lease.Token);
        using var deadline = _selectedAccess!.Link(request.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        bool OwnsRequest() => request.CanContinue && OwnsHistoryPage(lease) && recorder == _historyRecorder;

        _isPaging = previous != null;
        IsLoading = previous == null;
        StatusText = _isPaging ? "Loading older recordings…" : "Searching…";
        NotifyHistoryToolbarState();

        Task<HistorySearchResult>? fetchTask = null;
        try
        {
            fetchTask = fetch(deadline.Token);
            var page = await fetchTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            return await ReadHistoryUiAsync(() =>
            {
                if (!OwnsRequest())
                    return false;
                deadline.Token.ThrowIfCancellationRequested();
                if (page.Snapshot.Access.Retired || page.Snapshot.Access != _selectedAccess || page.Snapshot.Access.Recorder != recorder ||
                    previous != null && !ReferenceEquals(previous.Snapshot, page.Snapshot))
                    throw new HistoryResultsChangedException();

                _historyResults = page;
                _historyPageError = null;
                if (previous != null)
                    OlderHistoryResultsAdding?.Invoke();
                HistoryResultOrder.Add(Calls, page.Calls);
                if (previous != null)
                    OlderHistoryResultsAdded?.Invoke();
                StartAddressDetectionForHistoryCalls(page.Calls);
                RefreshHistoryDateAccess();
                StatusText = _isQueuePlaybackRunning ? HistoryPlayingStatus : HistoryResultStatus();
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
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
                    HistoryResultsChangedException => "Archive changed. Start a new search.",
                    OperationCanceledException when previous != null => "Loading older recordings timed out. Try again.",
                    OperationCanceledException => "History request timed out.",
                    ArgumentException or RecordingAccessException or FirewallAuthenticationException => ex.Message,
                    _ => previous != null ? "Could not load older recordings. Try again." : "History request failed. Try again."
                };
                if (previous != null)
                    _historyPageError = StatusText;
            }).ConfigureAwait(false);
        }
        finally
        {
            // A canceled waiter releases the UI even if the transport is slow
            // to cancel. Observe any fault the abandoned fetch reports later.
            if (deadline.IsCancellationRequested && fetchTask != null)
                _ = fetchTask.ContinueWith(static late => { _ = late.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await RunOnMainThreadAsync(() =>
            {
                if (OwnsRequest())
                {
                    IsLoading = false;
                    _isPaging = false;
                    NotifyHistoryToolbarState();
                }
                request.Dispose();
            }).ConfigureAwait(false);
        }
        return false;
    }

    private string HistoryResultStatus() => Calls.Count == 0 ? "No recordings match these filters." :
        $"{Calls.Count} of {_historyResults?.TotalMatches ?? Calls.Count} recordings" +
        (_historyResults?.LimitedToLast24Hours == true ? " · Last 24 hours" : string.Empty);
}
