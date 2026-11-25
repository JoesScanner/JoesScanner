using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public sealed class ScannerConnectionRow : BindableObject
{
    private SavedScannerConnection _profile;
    private ServerConnectionStatus _status;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private bool _directoryActionsAllowed;

    internal ScannerConnectionRow(SavedScannerConnection profile, ServerConnectionStatus status)
    {
        _profile = profile;
        _status = status;
    }

    internal SavedScannerConnection Profile => _profile;
    internal Guid Id => _profile.Id;
    internal bool Managed => _profile.Managed;
    internal int? DirectoryId => _profile.DirectoryId;
    public bool CanMoveUp => _canMoveUp;
    public bool CanMoveDown => _canMoveDown;
    public string Name => _profile.Name;
    public string Endpoint => _profile.Endpoint;
    public bool Enabled => _profile.Enabled;
    public bool CanShare => _directoryActionsAllowed && !Managed;
    public bool HasDirectoryUpdate => _directoryActionsAllowed && _profile.DirectoryCandidate != null;

    public string StatusText => ServerConnectionPresentation.Text(_status);
    public Color StatusColor => Color.FromArgb(ServerConnectionPresentation.Color(_status));

    internal void UpdateOrder(bool canMoveUp, bool canMoveDown)
    {
        if (_canMoveUp != canMoveUp)
        {
            _canMoveUp = canMoveUp;
            OnPropertyChanged(nameof(CanMoveUp));
        }
        if (_canMoveDown != canMoveDown)
        {
            _canMoveDown = canMoveDown;
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }

    internal void Update(SavedScannerConnection profile, ServerConnectionStatus status)
    {
        if (_profile != profile)
        {
            _profile = profile;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Endpoint));
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(CanShare));
            OnPropertyChanged(nameof(HasDirectoryUpdate));
        }
        if (_status != status)
        {
            _status = status;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    internal void UpdateDirectoryActions(bool allowed)
    {
        if (_directoryActionsAllowed == allowed)
            return;
        _directoryActionsAllowed = allowed;
        OnPropertyChanged(nameof(CanShare));
        OnPropertyChanged(nameof(HasDirectoryUpdate));
    }
}
