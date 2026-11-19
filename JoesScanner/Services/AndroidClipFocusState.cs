namespace JoesScanner.Services;

// One state object per request. Its callbacks and its release belong to that
// request, even when a newer recording has already selected another one.
internal sealed class AndroidClipFocusState
{
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _registered;
    private bool _released;

    public Task<bool> Ready => _ready.Task;
    public Task Lost => _lost.Task;

    public void Requested(bool registered, bool delayed = false)
    {
        _registered = registered;
        if (!delayed)
            _ready.TrySetResult(registered);
    }

    public void ReadyWithoutFocus() => _ready.TrySetResult(true);

    public void OnGain()
    {
        if (!_released)
            _ready.TrySetResult(true);
    }

    public void OnLoss()
    {
        if (!_released)
            _lost.TrySetResult();
    }

    public bool Release()
    {
        if (_released)
            return false;
        _released = true;
        return _registered;
    }
}
