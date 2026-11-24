namespace JoesScanner.Services;

internal enum AndroidClipFocusStatus
{
    Pending,
    Granted,
    Denied,
    TransientLoss,
    DuckableLoss,
    PermanentLoss
}

// One state object per request. Its callbacks and its release belong to that
// request, even when a newer recording has already selected another one.
internal sealed class AndroidClipFocusState
{
    private TaskCompletionSource _changed = NewSignal();
    private bool _registered;
    private bool _released;

    public AndroidClipFocusStatus Status { get; private set; }
    public Task Changed => _changed.Task;

    public void Requested(bool registered, bool delayed = false)
    {
        _registered = registered;
        if (!delayed)
            SetStatus(registered ? AndroidClipFocusStatus.Granted : AndroidClipFocusStatus.Denied);
    }

    public void ReadyWithoutFocus() => SetStatus(AndroidClipFocusStatus.Granted);

    public bool OnChange(AndroidClipFocusStatus status)
    {
        if (!_registered || _released || Status == AndroidClipFocusStatus.PermanentLoss || Status == status)
            return false;
        SetStatus(status);
        return true;
    }

    // Android delivers focus change callbacks on the main looper. Pulse on each
    // transition so a later gain is observed even if the loss already woke a waiter.
    private void SetStatus(AndroidClipFocusStatus status)
    {
        if (Status == status)
            return;
        Status = status;
        var prior = _changed;
        _changed = NewSignal();
        prior.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Release()
    {
        if (_released)
            return false;
        _released = true;
        return _registered;
    }
}
