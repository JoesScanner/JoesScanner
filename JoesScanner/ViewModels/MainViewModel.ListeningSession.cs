using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    private bool _handledMonitoringLaunch;

    internal Task StartMonitoringWithAutoplayAsync(Func<Task>? waitForWindow = null) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (_handledMonitoringLaunch)
                return;

            _handledMonitoringLaunch = true;
            if (!_settingsService.AutoPlay || !HasEnabledScannerConnections())
                return;

            var stopRevision = _monitoring.StopRevision;
            if (waitForWindow != null)
                await waitForWindow();
            await StartMonitoringCoreAsync(driveQueue: true, automaticStopRevision: stopRevision);
        });

    public Task StartMonitoringAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () => { await StartMonitoringCoreAsync(driveQueue: true); });

    public Task StopMonitoringAsync() =>
        MainThread.InvokeOnMainThreadAsync(StopMonitoringCoreAsync);

    private Task ToggleConnectionAsync() =>
        _monitoring.Current == null ? StartMonitoringAsync() : StopMonitoringAsync();

    private bool ActivateFreshListeningSession(MonitoringLifecycle.Run run)
    {
        if (!_monitoring.Activate(run))
            return false;

        Interlocked.Increment(ref _audioToggleSerial);
        Calls.Clear();
        AddressAlerts.Clear();
        _hiddenAddressAlerts.Clear();
        OnPropertyChanged(nameof(HasAddressAlerts));
        _lastPlayedCall = null;
        _lastTransportCall = null;
        SetPlaybackRetry(null);
        return true;
    }

    private Task PlayVisibleCallAsync(CallItem? item) =>
        item != null && Calls.Contains(item) ? OnCallTappedAsync(item) : Task.CompletedTask;

    private Task SystemPlayAsync()
    {
        var run = _monitoring.Current;
        var stopRevision = _monitoring.StopRevision;
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (_playbackOwnership.Live == null || _monitoring.Current != run ||
                _monitoring.StopRevision != stopRevision || run is { IsActive: false })
                return;

            if (run == null)
            {
                await StartMonitoringAsync();
                return;
            }

            if (CanResumePlayback)
            {
                await ResumeMobilePlaybackAsync();
                return;
            }

            if (_monitoring.CanRecover(run) && AudioEnabled)
            {
                _userRunShouldDriveQueue = true;
                _ = RequestQueuePlaybackAsync("UserPlay", true);
            }
        });
    }

    private Task SystemStopAsync()
    {
        var run = _monitoring.Current;
        return MainThread.InvokeOnMainThreadAsync(() =>
            run != null && _monitoring.Current == run ? StopMonitoringAsync() : Task.CompletedTask);
    }

    private Task SystemNavigateAsync(int direction)
    {
        var run = _monitoring.Current;
        return MainThread.InvokeOnMainThreadAsync(() =>
            _monitoring.CanRecover(run) ? NavigateToAdjacentCallAsync(direction) : Task.CompletedTask);
    }

    public async Task RefreshStaleCallMetadataAsync(int maxToRefresh = 20)
    {
        if (Interlocked.Exchange(ref _refreshingCallMetadata, 1) != 0)
            return;

        try
        {
            var groups = await MainThread.InvokeOnMainThreadAsync(() => LiveQueueScopes()
                .Select(scope => (Scope: scope, Calls: SnapshotLiveCalls()
                    .Where(call => call.ServerId == scope.ServerId && call.LivePlaybackOwner == scope.Owner && call.RecordingServer == scope.Server &&
                        call.LiveState?.NeedsNotificationLookup() == true)
                    .Take(maxToRefresh).ToArray()))
                .Where(group => group.Calls.Length > 0).ToArray());
            await Task.WhenAll(groups.Select(group => RefreshRecorderMetadataAsync(group.Scope, group.Calls)));
        }
        finally { Interlocked.Exchange(ref _refreshingCallMetadata, 0); }
    }
}
