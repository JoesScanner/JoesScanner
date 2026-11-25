namespace JoesScanner.Services;

internal interface IMonitoringScreenLease : IDisposable
{
    double Brightness { get; set; }
}

internal sealed class ScreenAwakeController
{
    private IMonitoringScreenLease? _lease;
    private double? _originalBrightness;

    internal void Update(MonitoringDeviceOptions options, bool monitoring, bool foreground, bool externalPower,
        Func<IMonitoringScreenLease?> acquire)
    {
        if (!options.ScreenAwake || !monitoring || !foreground || (options.ChargingOnly && !externalPower))
        {
            Release();
            return;
        }

        _lease ??= acquire();
        if (_lease == null)
            return;
        if (options.LowLight)
        {
            _originalBrightness ??= _lease.Brightness;
            var dim = options.Brightness / 100.0;
            if (Math.Abs(_lease.Brightness - dim) > 0.001)
                _lease.Brightness = dim;
        }
        else
        {
            RestoreBrightness(_lease);
        }
    }

    internal void Release()
    {
        if (_lease is not { } lease)
            return;
        _lease = null;
        try { RestoreBrightness(lease); }
        finally { lease.Dispose(); }
    }

    private void RestoreBrightness(IMonitoringScreenLease lease)
    {
        if (_originalBrightness is not { } brightness)
            return;
        _originalBrightness = null;
        lease.Brightness = brightness;
    }
}
