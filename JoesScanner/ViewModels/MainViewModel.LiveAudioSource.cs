using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    private LiveAudioSource _selectedAudioSource;
    private bool _loadedAudioSourcePreference;
    private LiveAudioSource _requestedAudioSource = LiveAudioSource.All;
    private LiveAudioSource _automaticAudioSource = LiveAudioSource.All;
    private LiveAutomaticAudioFilter? _automaticAudioFilter;
    private (Guid Id, string Name, bool Enabled)[] _audioSourceRecorders = [];
    private readonly LivePlaybackRequests _audioSourceTransitions = new();
    private readonly LivePlaybackRequests _favoriteAudioUpdates = new();
    private Task? _audioSourceBoundary;
    private int _automaticAudioRevision;
    private int _audioSourceGeneration;
    private LivePlaybackRequests.Request? _nativeLiveRequest;

    public bool IsSelectedLiveAudioMode => _livePlaybackPreferences.SourceMode == LiveAudioSourceMode.SelectedOnly;
    public string LiveAudioSourceText
    {
        get
        {
            if (!IsSelectedLiveAudioMode)
                return "Automatic audio · All enabled servers";
            var name = AudioSourceName(_selectedAudioSource);
            if (_audioSourceBoundary != null)
                return _requestedAudioSource.Kind == LiveAudioSourceKind.None
                    ? "Automatic audio pauses after this call"
                    : $"Next automatic audio · {name}";
            return _requestedAudioSource.Kind == LiveAudioSourceKind.None || _automaticAudioSource != _requestedAudioSource
                ? $"Automatic audio paused · {name}"
                : $"Automatic audio · {name}";
        }
    }

    private string AudioSourceName(LiveAudioSource source)
    {
        if (source.Kind == LiveAudioSourceKind.Favorites)
            return "Favorites";
        if (source.RecorderId is not { } id)
            return "Choose a recorder or Favorites";

        var recorder = _audioSourceRecorders.FirstOrDefault(item => item.Id == id);
        return recorder.Name == null ? "Selected recorder removed" :
            recorder.Enabled ? recorder.Name : $"{recorder.Name} is disabled";
    }

    internal void ChooseLiveAudioSource(LiveAudioSource source)
    {
        if (source == _selectedAudioSource || (source.Kind != LiveAudioSourceKind.Favorites &&
            (source.Kind != LiveAudioSourceKind.Recorder || !_audioSourceRecorders.Any(item => item.Id == source.RecorderId && item.Enabled))))
            return;

        SaveSelectedAudioSource(source);
        RefreshAutomaticAudioSource();
    }

    internal void SetLiveAudioRecorders(IEnumerable<(Guid Id, string Name, bool Enabled)> recorders)
    {
        _audioSourceRecorders = recorders.ToArray();
        if (!_loadedAudioSourcePreference)
        {
            _selectedAudioSource = LiveAudioSource.ReadSelection();
            _loadedAudioSourcePreference = true;
        }
        if (_selectedAudioSource.Kind == LiveAudioSourceKind.None &&
            _audioSourceRecorders.FirstOrDefault(item => item.Enabled) is { Enabled: true } first)
            SaveSelectedAudioSource(LiveAudioSource.Recorder(first.Id));
        RefreshAutomaticAudioSource();
    }

    private void SaveSelectedAudioSource(LiveAudioSource source)
    {
        _selectedAudioSource = source;
        _loadedAudioSourcePreference = true;
        try { source.SaveSelection(); }
        catch (Exception ex) { AppLog.Add(() => $"Live audio source could not be saved ({ex.GetType().Name})."); }
    }

    private void RefreshAutomaticAudioSource()
    {
        var requested = !IsSelectedLiveAudioMode ? LiveAudioSource.All :
            _selectedAudioSource.Kind == LiveAudioSourceKind.Favorites ||
            _audioSourceRecorders.Any(item => item.Id == _selectedAudioSource.RecorderId && item.Enabled)
                ? _selectedAudioSource : default;
        if (requested != _requestedAudioSource)
        {
            _requestedAudioSource = requested;
            _audioSourceTransitions.RetireCurrent();
            _favoriteAudioUpdates.RetireCurrent();
            _audioSourceBoundary = null;
            ++_automaticAudioRevision;
            ++_audioSourceGeneration;
            _manualLiveTraversal = null;
            _transportLookups.RetireCurrent();
            CancelSourceRecoveryJump();
            SetPlaybackRetry(null);
            ResetScheduler();

            // Once a request has entered the native owner, changing the source
            // leaves that recording alone. A download can be retired immediately.
            if (_nativeLiveRequest?.CanContinue != true)
                _playbackRequests.CancelCurrent();

            if (AudioEnabled && _monitoring.Current is { } run && _monitoring.CanRecover(run) &&
                _playbackOwnership.Live is { } live)
            {
                var request = _audioSourceTransitions.Begin(manual: false, live.Token);
                var boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _audioSourceBoundary = boundary.Task;
                _ = ApplyAutomaticAudioSourceAtEdgeAsync(requested, run, request, boundary);
            }
            else
                InstallAutomaticAudioSource(requested);
        }

        OnPropertyChanged(nameof(IsSelectedLiveAudioMode));
        OnPropertyChanged(nameof(LiveAudioSourceText));
    }

    private async Task ApplyAutomaticAudioSourceAtEdgeAsync(LiveAudioSource source, MonitoringLifecycle.Run run,
        LivePlaybackRequests.Request request, TaskCompletionSource boundary)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.Token, run.Token);
        var token = cancellation.Token;
        using var releaseWaiter = token.Register(() => boundary.TrySetResult());
        var applied = false;
        try
        {
            await FlushLiveCompletionAsync(token);
            var edge = await ReadAutomaticAudioEdgeAsync(token);
            if (request.CanContinue && _monitoring.CanRecover(run))
            {
                InstallAutomaticAudioSource(source, edge.Arrival, edge.Ticks);
                applied = true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Could not change the automatic audio source ({ex.GetType().Name}).");
        }
        finally
        {
            var current = request.IsCurrent;
            if (current)
                _audioSourceBoundary = null;
            request.Dispose();
            try
            {
                if (current)
                {
                    OnPropertyChanged(nameof(LiveAudioSourceText));
                    if (applied)
                        RequestQueueForLiveArrival("AutomaticAudioSourceChanged");
                }
            }
            finally { boundary.TrySetResult(); }
        }
    }

    private async Task<bool> WaitForAutomaticAudioSourceAsync(CancellationToken token)
    {
        while (_audioSourceBoundary is { } boundary)
        {
            await boundary.WaitAsync(token);
            token.ThrowIfCancellationRequested();
        }
        return _automaticAudioSource == _requestedAudioSource;
    }

    private void ResetAutomaticAudioSource()
    {
        _audioSourceTransitions.RetireCurrent();
        _favoriteAudioUpdates.RetireCurrent();
        _audioSourceBoundary = null;
        InstallAutomaticAudioSource(_requestedAudioSource);
        OnPropertyChanged(nameof(LiveAudioSourceText));
    }

    private void InstallAutomaticAudioSource(LiveAudioSource source, long after = 0, long recoveredSince = 0)
    {
        _automaticPreloads.Retire();
        _automaticAudioSource = source;
        _automaticAudioFilter = source.Kind == LiveAudioSourceKind.All && after == 0 && recoveredSince == 0 ? null :
            new(after, recoveredSince, source.Kind == LiveAudioSourceKind.Favorites
                ? ReadFavoriteAudioMembership().ToDictionary(identity => identity, _ => after) : null);
        ++_automaticAudioRevision;
    }

    private DurableLiveQueue.Run[] AutomaticQueueScopes() =>
        _audioSourceBoundary != null || _automaticAudioSource != _requestedAudioSource ||
        _automaticAudioFilter?.FavoriteEdges is { Count: 0 }
        ? [] : LiveQueueScopes().Where(scope => _automaticAudioSource.IncludesRecorder(scope.ServerId)).ToArray();

    private LiveAutomaticAudioFilter? AutomaticJumpFilter =>
        _automaticAudioSource.Kind != LiveAudioSourceKind.All || _automaticAudioFilter is { AfterArrival: > 0 }
            ? _automaticAudioFilter : null;

    private bool AutomaticAudioIncludes(CallItem item) =>
        _audioSourceBoundary == null && _automaticAudioSource == _requestedAudioSource &&
        _automaticAudioSource.IncludesRecorder(item.ServerId) &&
        (_automaticAudioFilter?.Includes(item) ?? true);

    private async Task<(long Arrival, long Ticks)> ReadAutomaticAudioEdgeAsync(CancellationToken token)
    {
        // Use the journal's sequence for observed calls. A recovered row also
        // needs to fall after the boundary, since it can be admitted much later.
        var since = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1).UtcTicks;
        var arrival = await DurableLiveQueue.PersistObservationAsync(() => _durableQueue.ReadAutomaticAudioEdgeAsync(token),
            ex => AppLog.Add(() => $"Could not read the automatic audio edge ({ex.GetType().Name})."), token);
        return (arrival, since);
    }

    private void AdvanceAutomaticAudioEdge(long arrival, long ticks)
    {
        _automaticPreloads.Retire();
        _automaticAudioFilter = (_automaticAudioFilter ?? new()) with { AfterArrival = arrival, RecoveredSinceUtcTicks = ticks };
        ++_automaticAudioRevision;
    }

    private async Task<bool> SkipAutomaticAudioBacklogAsync(DurableLiveQueue.Run[] scopes,
        LivePlaybackRequests.Request request, int sourceRevision)
    {
        try
        {
            if (_automaticAudioSource.Kind == LiveAudioSourceKind.All)
            {
                var floor = await _durableQueue.SetPlaybackFloorAtLiveEdgeAsync(scopes, request.Token, skipOutstandingRecovery: true);
                if (!request.CanContinue || sourceRevision != _automaticAudioRevision)
                    return false;
                _livePlaybackFloor = floor;
            }
            else
            {
                var edge = await ReadAutomaticAudioEdgeAsync(request.Token);
                if (!request.CanContinue || sourceRevision != _automaticAudioRevision)
                    return false;
                AdvanceAutomaticAudioEdge(edge.Arrival, edge.Ticks);
            }
            return true;
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RefreshFavoriteAudioSourceAsync()
    {
        _automaticPreloads.Retire();
        if (_audioSourceBoundary != null || _automaticAudioSource.Kind != LiveAudioSourceKind.Favorites ||
            _automaticAudioFilter?.FavoriteEdges is not { } previous)
        {
            RequestQueueForLiveArrival("FavoritesChanged");
            return;
        }

        var membership = ReadFavoriteAudioMembership();
        if (previous.Count == membership.Count && previous.Keys.All(membership.Contains))
        {
            RequestQueueForLiveArrival("FavoritesChanged");
            return;
        }

        _favoriteAudioUpdates.RetireCurrent();
        var retained = previous.Where(pair => membership.Contains(pair.Key)).ToDictionary();
        var added = membership.Where(key => !previous.ContainsKey(key)).ToArray();
        ReplaceFavoriteAudioEdges(retained);

        if (added.Length == 0)
            return;
        if (_monitoring.Current is not { } run || !_monitoring.CanRecover(run) ||
            _playbackOwnership.Live is not { } live || !AudioEnabled)
        {
            var edges = retained.ToDictionary();
            foreach (var key in added)
                edges[key] = 0;
            ReplaceFavoriteAudioEdges(edges);
            return;
        }

        using var update = _favoriteAudioUpdates.Begin(manual: false, live.Token);
        try
        {
            var edge = await ReadAutomaticAudioEdgeAsync(update.Token);
            if (!update.CanContinue || !_monitoring.CanRecover(run))
                return;

            // Existing favorites keep their waiting recordings. A newly
            // starred talkgroup becomes eligible for subsequent admissions.
            var edges = retained.ToDictionary();
            foreach (var key in added)
                edges[key] = edge.Arrival;
            ReplaceFavoriteAudioEdges(edges);
        }
        catch (OperationCanceledException) when (update.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Could not update the Favorites audio source ({ex.GetType().Name}).");
        }
    }

    private void ReplaceFavoriteAudioEdges(IReadOnlyDictionary<(Guid, FavoriteTalkgroupIdentity), long> edges)
    {
        _automaticPreloads.Retire();
        _automaticAudioFilter = _automaticAudioFilter! with { FavoriteEdges = edges };
        ++_automaticAudioRevision;
        _backlog.Reset();
        if (_currentPlayingCall is { } selected && !_playbackRequests.HasManualSelection &&
            _manualLiveTraversal == null && _nativeLiveRequest?.CanContinue != true && !AutomaticAudioIncludes(selected))
            _playbackRequests.CancelCurrent();
        RequestQueueForLiveArrival("FavoritesChanged");
    }
}
