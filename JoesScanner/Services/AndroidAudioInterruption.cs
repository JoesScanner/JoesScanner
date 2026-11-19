using JoesScanner.Models;

namespace JoesScanner.Services;

// All transitions run on the Android UI dispatcher. A route pause survives the end
// of a clip, while a call can only release the suspension it owns.
internal sealed class AndroidAudioInterruption
{
    private bool _callActive;
    private bool _routePaused;
    private bool _focusBlocked;
    private bool _waitingForCallEnd;

    public bool RequiresPlay => _routePaused || _focusBlocked && !_waitingForCallEnd;
    public bool IsBlocked => _callActive || _routePaused || _focusBlocked;

    public void SetCallActive(bool active)
    {
        if (active == _callActive)
            return;

        _callActive = active;
        if (!active && _waitingForCallEnd)
        {
            _focusBlocked = false;
            _waitingForCallEnd = false;
        }
    }

    public void BlockForFocus()
    {
        _focusBlocked = true;
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
