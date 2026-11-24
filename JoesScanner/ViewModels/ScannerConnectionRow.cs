using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public sealed class ScannerConnectionRow : BindableObject
{
    private SavedScannerConnection _profile;
    private ServerConnectionStatus _status;

    internal ScannerConnectionRow(SavedScannerConnection profile, ServerConnectionStatus status)
    {
        _profile = profile;
        _status = status;
    }

    internal Guid Id => _profile.Id;
    public string Name => _profile.Name;
    public string Endpoint => _profile.Endpoint;
    public bool Enabled => _profile.Enabled;

    public string StatusText => _status switch
    {
        ServerConnectionStatus.Monitoring => "Connected",
        ServerConnectionStatus.Reconnecting => "Reconnecting",
        ServerConnectionStatus.Unavailable => "Unavailable",
        ServerConnectionStatus.SetupRequired => "Setup required",
        _ => _status.ToString()
    };

    public Color StatusColor => _status switch
    {
        ServerConnectionStatus.Monitoring => Color.FromArgb("#15803d"),
        ServerConnectionStatus.Connecting or ServerConnectionStatus.Reconnecting => Colors.Goldenrod,
        ServerConnectionStatus.Unavailable or ServerConnectionStatus.SetupRequired => Colors.Red,
        _ => Color.FromArgb("#6b7280")
    };

    internal void Update(SavedScannerConnection profile, ServerConnectionStatus status)
    {
        if (_profile != profile)
        {
            _profile = profile;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Endpoint));
            OnPropertyChanged(nameof(Enabled));
        }
        if (_status != status)
        {
            _status = status;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }
}
