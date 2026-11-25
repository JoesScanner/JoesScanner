namespace JoesScanner.Services;

public sealed class MonitoringDeviceService
{
    private readonly MonitoringDevicePlatform _platform = new();
    private readonly MonitoringNotificationPolicy _policy = new(TimeProvider.System);
    private readonly MonitoringNotificationDelivery _delivery;
    private readonly ScreenAwakeController _screen = new();
    private readonly Dictionary<string, CancellationTokenSource> _failureAlerts = new();
    private Task<bool>? _permissionRequest;
    private Window? _foregroundWindow;
    private bool _observingPower;
    private volatile bool _monitoring;
    private volatile bool _viewingLive;
    private volatile bool _foreground;
    private int _transmissionRevision;

    internal MonitoringDeviceOptions Options { get; private set; } = new();
    internal double WindowDim { get; private set; }
    internal event Action<double>? WindowDimChanged;

    public MonitoringDeviceService()
    {
        _delivery = new(() => _platform.IsAllowedAsync(request: false), _platform.ShowAsync, _platform.RemoveAsync);
        _ = Task.Run(_delivery.RunAsync);
    }

    internal void LoadOptions() => Options = MonitoringDeviceOptions.Read();

    internal void SetOptions(MonitoringDeviceOptions options)
    {
        var previous = Options;
        options.Save();
        Options = options;
        if (previous.TransmissionNotifications != options.TransmissionNotifications ||
            previous.Scope != options.Scope || previous.GroupByTalkgroup != options.GroupByTalkgroup ||
            previous.Audible != options.Audible || !previous.Talkgroups.SequenceEqual(options.Talkgroups))
        {
            Interlocked.Increment(ref _transmissionRevision);
            _policy.ClearTransmissionGroups();
        }
        if (!options.ConnectionNotifications)
            EndFailures();
        RefreshScreen();
    }

    internal void Attach(Window window)
    {
        window.Activated += (_, _) =>
        {
            if (_foregroundWindow != window)
                ReleaseScreen();
            _foregroundWindow = window;
            _foreground = true;
            RefreshScreen();
        };
        window.Deactivated += (_, _) => Leave(window);
        window.Stopped += (_, _) => Leave(window);
        window.Destroying += (_, _) => Leave(window);
    }

    private void Leave(Window window)
    {
        if (_foregroundWindow != window)
            return;
        _foreground = false;
        _foregroundWindow = null;
        RefreshScreen();
    }

    internal void SetLiveVisible(bool visible) => _viewingLive = visible;

    internal void SetMonitoring(bool monitoring)
    {
        _monitoring = monitoring;
        if (!monitoring)
        {
            Interlocked.Increment(ref _transmissionRevision);
            _policy.ClearTransmissionGroups();
            EndFailures();
        }
        RefreshScreen();
    }

    internal void OnRecording(NotificationRecording recording, bool sourceAllowed, CancellationToken connection)
    {
        if (_policy.Transmission(recording, Options, _monitoring, _foreground && _viewingLive, sourceAllowed) is not { } alert)
            return;
        var revision = Volatile.Read(ref _transmissionRevision);
        _delivery.Post(alert, () => !connection.IsCancellationRequested && _monitoring &&
            !(_foreground && _viewingLive) && Volatile.Read(ref _transmissionRevision) == revision);
    }

    internal void OnRecorderStatus(Guid recorder, long generation, ServerConnectionStatus status, CancellationToken connection)
    {
        if (status is ServerConnectionStatus.Monitoring or ServerConnectionStatus.Disabled or ServerConnectionStatus.Stopped)
        {
            EndRecorderFailure(recorder, generation);
            return;
        }
        if (!_monitoring || _policy.FailureStatus(recorder, generation, status, Options, out var superseded) is not { } alert)
            return;
        if (superseded != null)
            RemoveFailure(superseded);

        var lifetime = new CancellationTokenSource();
        _failureAlerts.Add(alert.Id, lifetime);
        var token = lifetime.Token;
        _delivery.Post(alert, () => !token.IsCancellationRequested && !connection.IsCancellationRequested);
    }

    internal void EndRecorderFailure(Guid recorder, long generation)
    {
        if (_policy.EndFailure(recorder, generation) is { } id)
            RemoveFailure(id);
    }

    private void EndFailures()
    {
        foreach (var id in _policy.EndFailures())
            RemoveFailure(id);
    }

    private void RemoveFailure(string id)
    {
        if (_failureAlerts.Remove(id, out var lifetime))
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
        _delivery.Remove(id);
    }

    internal Task<bool> GetNotificationAccessAsync() => _platform.IsAllowedAsync(request: false);

    internal Task<bool> RequestNotificationAccessAsync() =>
        _permissionRequest is { IsCompleted: false } current ? current :
            _permissionRequest = _platform.IsAllowedAsync(request: true);

    private void RefreshScreen()
    {
        try
        {
            var options = Options;
            var observe = options.ScreenAwake && options.ChargingOnly && _monitoring && _foreground;
            if (_observingPower != observe)
            {
                _platform.SetPowerListener(observe ? OnBatteryChanged : null);
                _observingPower = observe;
            }

            var powered = !observe || _platform.ReadExternalPower();
            _screen.Update(options, _monitoring, _foreground, powered, () => _foregroundWindow is { } window
                ? _platform.AcquireScreen(window, UpdateWindowDim) : null);
        }
        catch (Exception ex)
        {
            ReleaseScreen();
            AppLog.Basic(() => $"Could not update the monitoring display ({DiagnosticRedaction.Describe(ex)}).");
        }
    }

    private void ReleaseScreen()
    {
        try { _screen.Release(); }
        catch (Exception ex) { AppLog.Basic(() => $"Could not restore the display ({DiagnosticRedaction.Describe(ex)})."); }
    }

    private void OnBatteryChanged() => MainThread.BeginInvokeOnMainThread(RefreshScreen);

    private void UpdateWindowDim(double value)
    {
        WindowDim = value;
        WindowDimChanged?.Invoke(value);
    }
}

internal sealed partial class MonitoringDevicePlatform
{
    internal partial Task<bool> IsAllowedAsync(bool request);
    internal partial Task ShowAsync(MonitoringAlert alert);
    internal partial Task RemoveAsync(string id);
    internal partial IMonitoringScreenLease? AcquireScreen(Window window, Action<double> setWindowDim);
    internal partial bool ReadExternalPower();
    internal partial void SetPowerListener(Action? listener);
}
