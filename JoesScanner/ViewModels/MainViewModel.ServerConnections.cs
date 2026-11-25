using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    private sealed record RecorderRun(ServerConnectionManager.Connection Connection,
        DurableLiveQueue.Run Queue, LiveStreamRecovery Recovery, long AccountIdentity);

    private readonly ServerConnectionManager _connections = new(TimeProvider.System);
    private readonly ConcurrentDictionary<long, RecorderRun> _recorderRuns = new();
    private readonly ScannerConnectionStore _connectionStore;
    private readonly PrivacyNetworkPolicy _privacyNetwork;
    internal FavoritesService Favorites { get; }

    private bool MayUseNetwork(SavedScannerConnection profile) => _privacyNetwork.Allows(new Uri(profile.Endpoint));

    private void OnPrivacyNetworkChanged(object? sender, EventArgs e)
    {
        if (!_privacyNetwork.AllowsCloud)
            _connections.Retire(profile => !MayUseNetwork(profile));
        MainThread.BeginInvokeOnMainThread(() => ReconcileScannerConnections());
    }

    internal bool AllowsExternalLink(Uri uri) => _privacyNetwork.Allows(uri);
    private volatile IReadOnlyList<SavedScannerConnection> _profiles = Array.Empty<SavedScannerConnection>();

    public ObservableCollection<ScannerConnectionRow> ScannerConnections { get; } = [];
    internal Guid? VisibleLiveRecorderId => _visibleLiveServer;
    internal event Action? ScannerConnectionLayoutChanged;

    private bool HasEnabledScannerConnections() => LoadScannerConnections().Any(profile => profile.Enabled);

    private IReadOnlyList<SavedScannerConnection> LoadScannerConnections(string? legacyEndpoint = null)
    {
        try
        {
            var selected = ScannerConnectionStore.Normalize(legacyEndpoint ?? _settingsService.ServerUrl);
            ServerDirectoryEntry? listed = null;
            if (selected != null)
            {
                try
                {
                    listed = _settingsService.GetCachedDirectoryServers()
                        .FirstOrDefault(entry => entry.DirectoryId > 0 && ScannerConnectionStore.Normalize(entry.Url) == selected);
                }
                catch (Exception ex) { AppLog.Add(() => $"Cached directory could not be read ({ex.GetType().Name})."); }
            }
            _profiles = _connectionStore.Load(selected, listed?.DirectoryId, listed?.Name);
            _filterService.ConfigureRecorderScopes(_profiles.Select(profile => (profile.Id, profile.Endpoint, profile.FilterKey)),
                _settingsService.ServerUrl);
            foreach (var profile in _profiles.Where(profile => !profile.CredentialsMigrated))
            {
                try
                {
                    if (!RequiresHostedAccount(profile.Endpoint) &&
                        _settingsService.TryGetServerCredentials(profile.Endpoint, out var user, out var password))
                        _settingsService.SetRecorderCredentials(profile.Id, profile.Endpoint, user, password);
                    _connectionStore.MarkCredentialsMigrated(profile.Id);
                }
                catch (Exception ex)
                {
                    AppLog.Add(() => $"Saved recorder credentials could not be migrated ({ex.GetType().Name}).");
                }
            }
            Favorites.SetProfiles(_profiles);
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Could not load saved scanner connections ({ex.GetType().Name}).");
        }
        return _profiles;
    }

    internal void RefreshScannerConnectionRows()
    {
        LoadScannerConnections();
        PublishScannerConnectionRows();
    }

    internal void SaveScannerConnection(string endpoint, string? name, int? directoryId, Guid? editId,
        (string Username, string Password)? customCredentials = null, bool credentialsChanged = false)
    {
        var saved = _connectionStore.Save(endpoint, name, directoryId, editId, profile =>
            {
                if (customCredentials is { } credentials)
                {
                    credentialsChanged = !_settingsService.TryGetRecorderCredentials(profile.Id, profile.Endpoint, out var user, out var password)
                        ? credentials.Username.Length != 0 : (user, password) != credentials;
                    _settingsService.SetRecorderCredentials(profile.Id, profile.Endpoint, credentials.Username, credentials.Password);
                }
            });
        LoadScannerConnections();
        try { SelectScannerForLegacyViews(saved); }
        finally { ReconcileScannerConnections(credentialsChanged ? saved.Id : null); }
    }

    internal bool TryGetConfiguredRecorderCredentials(string endpoint, out string username, out string password)
    {
        username = password = string.Empty;
        var key = ScannerConnectionStore.Normalize(endpoint);
        if (key != null && RequiresHostedAccount(key))
            return _settingsService.TryGetServerCredentials(endpoint, out username, out password);
        var profile = _profiles.FirstOrDefault(profile => profile.Endpoint == key);
        return profile != null && _settingsService.TryGetRecorderCredentials(profile.Id, profile.Endpoint, out username, out password);
    }

    internal void SetScannerConnectionEnabled(ScannerConnectionRow row, bool enabled)
    {
        try
        {
            _connectionStore.SetEnabled(row.Id, enabled);
            LoadScannerConnections();
            ReconcileScannerConnections();
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Could not save recorder connection settings ({ex.GetType().Name}).");
        }
        finally { PublishScannerConnectionRows(); }
    }

    internal string? ScannerConnectionEditError(ScannerConnectionRow? row, string endpoint)
    {
        if (row == null)
            return null;
        var proposed = ScannerConnectionStore.Normalize(endpoint);
        var existing = _profiles.FirstOrDefault(profile => profile.Id == row.Id);
        if (existing == null)
            return "This server has already been removed.";
        if (existing.Managed && existing.Endpoint != proposed)
            return "The directory manages this server's address.";
        return _profiles.Any(profile => profile.Id != row.Id && profile.Endpoint == proposed)
            ? "This address is already configured as another server." : null;
    }

    internal void MoveScannerConnection(ScannerConnectionRow row, int direction)
    {
        _connectionStore.Move(row.Id, direction);
        RefreshScannerConnectionRows();
    }

    internal void RemoveScannerConnection(ScannerConnectionRow row, string? deleteFavoritesFor = null)
    {
        var profile = _profiles.FirstOrDefault(candidate => candidate.Id == row.Id);
        if (profile == null)
            return;

        // Joe's account is shared by the directory and other recorders. Remove only this recorder's logins.
        foreach (var origin in _connectionStore.CredentialOrigins(profile))
        {
            _settingsService.SetRecorderCredentials(profile.Id, origin, string.Empty, string.Empty);
            if (!HostedServerRules.IsHostedStreamServerUrl(origin) &&
                !_profiles.Any(other => other.Id != profile.Id && SavedConnectionUrl.Scope(other.Endpoint) == origin))
                _settingsService.ClearServerCredentials(origin);
        }
        if (deleteFavoritesFor != null)
            Favorites.DeleteServerFavorites(deleteFavoritesFor, profile);
        _connectionStore.Remove(profile.Id);

        var wasSelected = ScannerConnectionStore.Normalize(_settingsService.ServerUrl) == profile.Endpoint;
        LoadScannerConnections();
        try
        {
            if (wasSelected)
            {
                var replacement = _profiles.FirstOrDefault(candidate => candidate.Enabled) ?? _profiles.FirstOrDefault();
                SelectScannerForLegacyViews(replacement);
            }
        }
        catch (CredentialStorageException)
        {
            ServerUrl = string.Empty;
        }
        finally { ReconcileScannerConnections(); }
    }

    private void SelectScannerForLegacyViews(SavedScannerConnection? selected)
    {
        // History still reads the selected endpoint's legacy credentials. Copy only the
        // selected profile's own pair, including clearing that slot for a public recorder.
        if (selected != null && !RequiresHostedAccount(selected.Endpoint))
        {
            _settingsService.TryGetRecorderCredentials(selected.Id, selected.Endpoint, out var username, out var password);
            _settingsService.SetServerCredentials(selected.Endpoint, username, password);
        }
        ServerUrl = selected?.Endpoint ?? string.Empty;
    }

    private void ReconcileScannerConnections(Guid? restart = null)
    {
        if (_monitoring.CanRecover(_monitoring.Current))
        {
            var profiles = _profiles.Where(profile => MayUseNetwork(profile) &&
                (!_changingHostedAccount || !RequiresHostedAccount(profile.Endpoint))).ToArray();
            if (!_profiles.Any(profile => profile.Enabled))
            {
                _ = StopMonitoringAsync();
                return;
            }

            _connections.Synchronize(profiles, _monitoring.Current!.Token, RunRecorderAsync, restart);
            if (_retryIsAutomatic && _profiles.Count(profile => profile.Enabled) > 1)
                SetPlaybackRetry(null);
            RequestQueueForLiveArrival("RecorderConfigurationChanged");
        }
        PublishScannerConnectionRows();
    }

    private bool RequiresHostedAccount(string server) =>
        HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server);

    private void OnRecorderStatusChanged(ServerConnectionManager.Connection connection) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_connections.IsCurrent(connection) && _monitoring.CanRecover(_monitoring.Current))
            {
                PublishScannerConnectionRows();
                if (connection.Status == ServerConnectionStatus.Monitoring)
                {
                    RequestQueueForLiveArrival("RecorderConnected");
                    if (connection.Profile.Endpoint == ScannerConnectionStore.Normalize(_settingsService.ServerUrl))
                        _ = _historyLookupsCacheService.PreloadAsync(false, "connect", connection.Token);
                }
            }
        });

    private void PublishScannerConnectionRows()
    {
        var statuses = _connections.Snapshot().ToDictionary(connection => connection.Profile.Id, connection => connection.Status);
        var current = ScannerConnections.Select(row => (row.Id, row.Enabled)).ToArray();
        var saved = _profiles;
        var ids = saved.Select(profile => profile.Id).ToHashSet();
        for (var i = ScannerConnections.Count - 1; i >= 0; i--)
            if (!ids.Contains(ScannerConnections[i].Id))
                ScannerConnections.RemoveAt(i);

        for (var i = 0; i < saved.Count; i++)
        {
            var profile = saved[i];
            var status = GetScannerRowStatus(profile, statuses.GetValueOrDefault(profile.Id));
            var row = ScannerConnections.FirstOrDefault(candidate => candidate.Id == profile.Id);
            if (row == null)
            {
                row = new ScannerConnectionRow(profile, status);
                ScannerConnections.Insert(i, row);
            }
            else
            {
                row.Update(profile, status);
                var oldIndex = ScannerConnections.IndexOf(row);
                if (oldIndex != i)
                    ScannerConnections.Move(oldIndex, i);
            }
            row.UpdateOrder(i > 0, i < saved.Count - 1);
        }

        if (!current.SequenceEqual(saved.Select(profile => (profile.Id, profile.Enabled))))
            ScannerConnectionLayoutChanged?.Invoke();

        if (!_monitoring.CanRecover(_monitoring.Current))
            return;
        var aggregate = statuses.Values.Contains(ServerConnectionStatus.Monitoring) ? ConnectionStatus.Connected :
            statuses.Values.Any(status => status is ServerConnectionStatus.Connecting or ServerConnectionStatus.Reconnecting) ?
                ConnectionStatus.Connecting :
            statuses.Values.Contains(ServerConnectionStatus.SetupRequired) ? ConnectionStatus.AuthFailed :
                ConnectionStatus.ServerUnreachable;
        SetConnectionStatus(aggregate);
    }

    private ServerConnectionStatus GetScannerRowStatus(SavedScannerConnection profile, ServerConnectionStatus status)
    {
        if (!profile.Enabled)
            return ServerConnectionStatus.Disabled;
        if (!MayUseNetwork(profile))
            return ServerConnectionStatus.Unavailable;
        if (status != ServerConnectionStatus.Stopped || !profile.Managed)
            return status;
        try
        {
            if (RequiresHostedAccount(profile.Endpoint) &&
                (!_settingsService.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out var user, out var password) ||
                 string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password)))
                return ServerConnectionStatus.SetupRequired;
        }
        catch (CredentialStorageException) { return ServerConnectionStatus.SetupRequired; }
        return status;
    }

    private void RetireRecorder(ServerConnectionManager.Connection connection)
    {
        if (_recorderRuns.TryRemove(connection.Generation, out var retired))
            _ = SaveRecorderStopAsync(retired.Queue);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_retryPlaybackCall?.LivePlaybackOwner != connection.Generation)
                return;
            SetPlaybackRetry(null);
            RequestQueueForLiveArrival("RecorderRetired");
        });
    }

    private async Task SaveRecorderStopAsync(DurableLiveQueue.Run scope)
    {
        try
        {
            await _playbackOwnership.WaitForLiveReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            await FlushLiveCompletionAsync().ConfigureAwait(false);
            await _durableQueue.StopAsync(scope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Recorder queue retirement could not be saved ({ex.GetType().Name}).");
        }
    }

    private DurableLiveQueue.Run[] LiveQueueScopes() => _connections.Snapshot()
        .Select(connection => _recorderRuns.GetValueOrDefault(connection.Generation)?.Queue)
        .OfType<DurableLiveQueue.Run>()
        .Where(IsCurrentLiveScope)
        .ToArray();

    private DurableLiveQueue.Run? ScopeOf(CallItem item) =>
        _recorderRuns.TryGetValue(item.LivePlaybackOwner, out var recorder) && IsCurrentLiveScope(recorder.Queue) &&
        TryGetDurableRecording(item, recorder.Queue, out _) ? recorder.Queue : null;

    private bool IsCurrentLiveScope(DurableLiveQueue.Run scope)
    {
        if (!_recorderRuns.TryGetValue(scope.Owner, out var recorder) || recorder.Queue != scope ||
            !_connections.IsCurrent(recorder.Connection))
            return false;

        try { return RecorderAccountIsCurrent(recorder.Connection, scope.Protected, scope.Account, recorder.AccountIdentity); }
        catch (CredentialStorageException) { return false; }
    }

    private bool RecorderAccountIsCurrent(ServerConnectionManager.Connection connection, bool hosted, string? account, long identity)
    {
        var endpoint = connection.Profile.Endpoint;
        return RequiresHostedAccount(endpoint) == hosted && (hosted
            ? !_changingHostedAccount && identity == _settingsService.ReadAccountSession().AccountVersion &&
                account == RecordingPlaybackAccess.AccountKey(_settingsService, endpoint, independent: true)
            : account == CustomRecorderAccount(connection.Profile.Id, endpoint));
    }

    private string? CustomRecorderAccount(Guid id, string endpoint)
    {
        if (!_settingsService.TryGetRecorderCredentials(id, endpoint, out var user, out _))
            return null;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user)));
    }

    private bool CanUseRecorder(CallItem item, string server) =>
        item.ServerId is not { } id ? RecordingPlaybackAccess.IsSelected(_settingsService, server) :
        _profiles.Any(profile => profile.Id == id && profile.Endpoint == ScannerConnectionStore.Normalize(server));

    private async Task RunRecorderAsync(ServerConnectionManager.Connection connection)
    {
        var server = connection.Profile.Endpoint;
        var token = connection.Token;
        if (!_recorderRuns.TryGetValue(connection.Generation, out var recorder))
        {
            token.ThrowIfCancellationRequested();
            var hosted = RequiresHostedAccount(server);
            var account = hosted ? RecordingPlaybackAccess.AccountKey(_settingsService, server, independent: true) :
                CustomRecorderAccount(connection.Profile.Id, server);
            var identity = hosted ? _settingsService.ReadAccountSession().AccountVersion : 0;
            int? tier = null;
            if (hosted)
            {
                if (account == null)
                {
                    _connections.Report(connection, ServerConnectionStatus.SetupRequired);
                    return;
                }
                var result = await _subscriptionService.EnsureSubscriptionForRecorderAsync(server, token).WaitAsync(token);
                if (!result.IsAllowed)
                {
                    if (result.ErrorCode is "missing_credentials" or "credential_storage_unavailable" or "http_401" or "http_403" ||
                        _settingsService.SubscriptionExpiresUtc <= DateTime.UtcNow)
                        _connections.Report(connection, ServerConnectionStatus.SetupRequired);
                    return;
                }
                tier = result.VerifiedOnlineTier;
            }
            token.ThrowIfCancellationRequested();
            if (!_connections.IsCurrent(connection) || !RecorderAccountIsCurrent(connection, hosted, account, identity))
                return;

            var scope = new DurableLiveQueue.Run(server, connection.Generation, tier, hosted, account, connection.Profile.Id);
            var recovery = await LiveStreamRecovery.StartListeningAsync(_durableQueue, scope, DateTimeOffset.UtcNow, token);
            recorder = new RecorderRun(connection, scope, recovery, identity);
            _recorderRuns[connection.Generation] = recorder;
            if (!IsCurrentLiveScope(scope))
            {
                RetireRecorder(connection);
                return;
            }
        }

        if (!IsCurrentLiveScope(recorder.Queue))
        {
            RetireRecorder(connection);
            return;
        }

        using var stream = _callStreamService.ForRecorder(server, connection.Profile.Id);
        var initial = await MainThread.InvokeOnMainThreadAsync(() => SnapshotLiveCalls()
            .Where(item => item.ServerId == connection.Profile.Id && item.LivePlaybackOwner == connection.Generation &&
                item.RecordingServer == server &&
                recorder.Queue.Owns(item.RecordingAccess)).ToArray());
        await foreach (var call in stream.GetCallStreamAsync(initial, token, recorder.Recovery).WithCancellation(token))
        {
            if (!IsCurrentLiveScope(recorder.Queue))
                break;
            if (call.LiveState == null && !call.IsRecordingNotification)
            {
                if (call.IsConnectionActionRequired)
                {
                    _connections.Report(connection, ServerConnectionStatus.SetupRequired);
                    return;
                }
                _connections.Report(connection, call.Talkgroup == "HEARTBEAT"
                    ? ServerConnectionStatus.Monitoring : ServerConnectionStatus.Reconnecting);
                continue;
            }
            _connections.Report(connection, ServerConnectionStatus.Monitoring);
            await ReceiveRecorderCallAsync(recorder.Queue, call, token);
        }
    }

    private async Task<string?> RefreshRecorderAudioAsync(CallItem item, string server, DateTimeOffset? recordedAt,
        CancellationToken token)
    {
        if (item.ServerId == null)
            return await _callStreamService.RefreshRecordingAudioAsync(item, server, recordedAt, token);
        if (!CanUseRecorder(item, server))
            return null;
        using var stream = _callStreamService.ForRecorder(server, item.ServerId);
        return await stream.RefreshRecordingAudioAsync(item, server, recordedAt, token);
    }

    private async Task RefreshRecorderMetadataAsync(DurableLiveQueue.Run scope, CallItem[] candidates)
    {
        if (!_recorderRuns.TryGetValue(scope.Owner, out var recorder) || !IsCurrentLiveScope(scope))
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(recorder.Connection.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var stream = _callStreamService.ForRecorder(scope.Server, scope.ServerId);
            var previousAudio = candidates.Select(card => card.LiveState?.Read().AudioUrl).ToArray();
            await stream.RefreshCallMetadataAsync(candidates, timeout.Token);
            for (var i = 0; i < candidates.Length; i++)
            {
                var card = candidates[i];
                if (card.LiveState is not { } state || state.Read() is not { AudioUrl: { } audio } snapshot ||
                    previousAudio[i] == audio)
                    continue;
                await SaveLiveObservationAsync(scope, state.Id, card.RecordingTimeUtc, snapshot, RecordingOrigin.Unknown,
                    existingOnly: true, timeout.Token, skipAutomatic: !AudioEnabled || _filterService.ShouldMute(card),
                    audioReferenceChanged: true);
            }
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (timeout.IsCancellationRequested || !IsCurrentLiveScope(scope))
                    return;
                foreach (var card in candidates)
                    if (card.LiveState is { } state)
                        ApplyLiveUpdate(state, existing: card);
                RequestQueueForLiveArrival("RefreshedRecorderAudio");
            });
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Recorder metadata refresh failed ({ex.GetType().Name}).");
        }
    }

    private void RequestRecorderRecovery(DateTimeOffset since)
    {
        foreach (var recorder in _recorderRuns.Values)
            if (IsCurrentLiveScope(recorder.Queue))
                recorder.Recovery.Request(since);
    }
}
