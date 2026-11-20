using JoesScanner.Models;

namespace JoesScanner.Services;

internal enum AndroidFocusAttemptAction { Play, Wait, Renew, Block }

// All transitions run on the Android UI dispatcher. A route pause survives the end
// of a clip, while a call can only release the suspension it owns.
internal sealed class AndroidAudioInterruption
{
    private bool _callActive;
    private bool _routePaused;
    private bool _focusBlocked;
    private bool _waitingForCallEnd;

    public long CallRevision { get; private set; }
    public bool CallActive => _callActive;
    public bool RoutePaused => _routePaused;
    public bool FocusBlocked => _focusBlocked;
    public bool RequiresPlay => _routePaused || _focusBlocked && !_waitingForCallEnd;
    public bool IsBlocked => _callActive || _routePaused || _focusBlocked;

    public AndroidFocusAttemptAction GetFocusAction(AndroidClipFocusStatus status, long requestCallRevision) =>
        status switch
        {
            AndroidClipFocusStatus.Denied or AndroidClipFocusStatus.PermanentLoss => AndroidFocusAttemptAction.Block,
            AndroidClipFocusStatus.Granted => _callActive ? AndroidFocusAttemptAction.Renew : AndroidFocusAttemptAction.Play,
            AndroidClipFocusStatus.Pending or AndroidClipFocusStatus.DuckableLoss or AndroidClipFocusStatus.TransientLoss
                when !_callActive && requestCallRevision != CallRevision => AndroidFocusAttemptAction.Renew,
            _ => AndroidFocusAttemptAction.Wait
        };

    public void SetCallActive(bool active)
    {
        if (active == _callActive)
            return;

        _callActive = active;
        if (active)
        {
            ++CallRevision;
            // Focus denial/loss can arrive before Telecom changes the audio mode.
            _waitingForCallEnd = _focusBlocked;
        }
        else if (_waitingForCallEnd)
        {
            _focusBlocked = false;
            _waitingForCallEnd = false;
        }
    }

    public void BlockForFocus(long requestCallRevision)
    {
        // If both call-mode callbacks already ran during this focus request,
        // allow one fresh request. A subsequent denial has the new revision.
        _focusBlocked = _callActive || requestCallRevision == CallRevision;
        _waitingForCallEnd = _callActive;
    }

    public void RouteDisconnected(AudioDisconnectionBehavior preference)
    {
        if (preference == AudioDisconnectionBehavior.PausePlayback)
            _routePaused = true;
    }

    public void RequestPlay()
    {
        _routePaused = false;
        _focusBlocked = false;
        _waitingForCallEnd = false;
    }
}
