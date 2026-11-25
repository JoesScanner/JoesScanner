#if !ANDROID
using Microsoft.Maui.Devices;

namespace JoesScanner.Services;

internal sealed partial class MonitoringDevicePlatform
{
    private Action? _powerListener;

    internal partial bool ReadExternalPower() => Battery.Default.PowerSource switch
    {
        BatteryPowerSource.AC or BatteryPowerSource.Usb or BatteryPowerSource.Wireless => true,
        BatteryPowerSource.Battery => false,
        _ => Battery.Default.State is BatteryState.Charging or BatteryState.Full
    };

    internal partial void SetPowerListener(Action? listener)
    {
        if (_powerListener != null)
            Battery.Default.BatteryInfoChanged -= OnBatteryChanged;
        _powerListener = listener;
        if (listener != null)
            Battery.Default.BatteryInfoChanged += OnBatteryChanged;
    }

    private void OnBatteryChanged(object? sender, BatteryInfoChangedEventArgs args) => _powerListener?.Invoke();
}
#endif
