namespace JoesScanner.Helpers;

// Owned by the Settings view model and accessed on the UI thread.
internal sealed class SettingsStartupPreparation<T>
{
    private string? _serverKey;
    private Task<T>? _pending;

    public Task<T> GetOrStart(string serverKey, Func<string, Task<T>> read)
    {
        if (!string.Equals(_serverKey, serverKey, StringComparison.OrdinalIgnoreCase) ||
            _pending is not { IsFaulted: false, IsCanceled: false })
        {
            _serverKey = serverKey;
            _pending = read(serverKey);
        }

        return _pending;
    }

    public void Release(Task<T> read)
    {
        if (ReferenceEquals(_pending, read))
            _pending = null;
    }
}
