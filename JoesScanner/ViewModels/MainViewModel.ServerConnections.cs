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
    private volatile IReadOnlyList<SavedScannerConnection> _profiles = Array.Empty<SavedScannerConnection>();

    public ObservableCollection<ScannerConnectionRow> ScannerConnections { get; } = [];

    private bool HasEnabledScannerConnections() => LoadScannerConnections().Any(profile => profile.Enabled);

    private IReadOnlyList<SavedScannerConnection> LoadScannerConnections(string? legacyEndpoint = null)
    {
        try
        {
            _profiles = _connectionStore.Load(legacyEndpoint ?? _settingsService.ServerUrl);
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

    internal void SaveValidatedScannerConnection(string previousEndpoint, string endpoint, ServerDirectoryEntry? directory,
        bool addAnother, Guid? editId, (string Username, string Password)? customCredentials, bool credentialsChanged)
    {
        var previous = LoadScannerConnections(previousEndpoint)
            .FirstOrDefault(profile => profile.Endpoint == ScannerConnectionStore.Normalize(previousEndpoint));
        var saved = _connectionStore.Save(endpoint, directory?.Name,
            directory is { DirectoryId: > 0 } ? directory.DirectoryId : null,
            addAnother ? null : editId ?? previous?.Id, profile =>
            {
                if (customCredentials is { } credentials)
                {
                    credentialsChanged = !_settingsService.TryGetRecorderCredentials(profile.Id, profile.Endpoint, out var user, out var password)
                        ? credentials.Username.Length != 0 : (user, password) != credentials;
                    _settingsService.SetRecorderCredentials(profile.Id, profile.Endpoint, credentials.Username, credentials.Password);
                }
            });
        LoadScannerConnections();
        ReconcileScannerConnections(credentialsChanged ? saved.Id : null);
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

    private void ReconcileScannerConnections(Guid? restart = null)
    {
        if (_monitoring.CanRecover(_monitoring.Current))
        {
            var profiles = _changingHostedAccount
                ? _profiles.Where(profile => !RequiresHostedAccount(profile.Endpoint)).ToArray()
                : _profiles;
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
        foreach (var profile in _profiles)
        {
            var status = profile.Enabled ? statuses.GetValueOrDefault(profile.Id) : ServerConnectionStatus.Disabled;
            var row = ScannerConnections.FirstOrDefault(candidate => candidate.Id == profile.Id);
            if (row == null)
                ScannerConnections.Add(new ScannerConnectionRow(profile, status));
            else
                row.Update(profile, status);
        }

        if (!_monitoring.CanRecover(_monitoring.Current))
            return;
        var aggregate = statuses.Values.Contains(ServerConnectionStatus.Monitoring) ? ConnectionStatus.Connected :
            statuses.Values.Any(status => status is ServerConnectionStatus.Connecting or ServerConnectionStatus.Reconnecting) ?
                ConnectionStatus.Connecting :
            statuses.Values.Contains(ServerConnectionStatus.SetupRequired) ? ConnectionStatus.AuthFailed :
                ConnectionStatus.ServerUnreachable;
        SetConnectionStatus(aggregate);
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
            await stream.RefreshCallMetadataAsync(candidates, timeout.Token);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (timeout.IsCancellationRequested || !IsCurrentLiveScope(scope))
                    return;
                foreach (var card in candidates)
                    if (card.LiveState is { } state)
                        ApplyLiveUpdate(state, existing: card);
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
