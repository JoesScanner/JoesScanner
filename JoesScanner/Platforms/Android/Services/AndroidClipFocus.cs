using Android.Media;
using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.Platforms.Android.Services;

// A focus request and its listener belong to one audible attempt. Disposal never
// consults a shared "current request" that could have been replaced by a newer clip.
internal sealed class AndroidClipFocus(AudioManager manager) : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
{
    private AudioFocusRequestClass? _request;
    private readonly AndroidClipFocusState _state = new();

    public AndroidClipFocusStatus Status => _state.Status;
    public Task Changed => _state.Changed;

    public event Action? LostAudibility;
    private bool _interruptionReported;

    private void ReportInterruption(bool released = false)
    {
        var interrupted = !released && Status is (AndroidClipFocusStatus.Pending or AndroidClipFocusStatus.TransientLoss or AndroidClipFocusStatus.DuckableLoss);
        if (interrupted == _interruptionReported)
            return;
        _interruptionReported = interrupted;
        AndroidAudioEnvironment.ReportClipFocusInterruption(interrupted, released);
    }

    public void Request(MobileAudioMode mode, AudioAttributes attributes)
    {
        if (mode == MobileAudioMode.MixNormally)
        {
            _state.ReadyWithoutFocus();
            return;
        }

        var gain = mode == MobileAudioMode.MixAndDuck ? AudioFocus.GainTransientMayDuck : AudioFocus.GainTransient;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            using var builder = new AudioFocusRequestClass.Builder(gain);
            _request = builder.SetAudioAttributes(attributes)
                .SetAcceptsDelayedFocusGain(true)
                .SetWillPauseWhenDucked(true)
                .SetOnAudioFocusChangeListener(this)
                .Build() ?? throw new InvalidOperationException("Android audio focus request could not be created.");
            var result = manager.RequestAudioFocus(_request);
            _state.Requested(result is AudioFocusRequest.Granted or AudioFocusRequest.Delayed,
                delayed: result == AudioFocusRequest.Delayed);
        }
        else
        {
#pragma warning disable CS0618, CA1422
            var result = manager.RequestAudioFocus(this, global::Android.Media.Stream.Music, gain);
#pragma warning restore CS0618, CA1422
            _state.Requested(result == AudioFocusRequest.Granted);
        }
        ReportInterruption();
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        var status = focusChange switch
        {
            AudioFocus.Gain => AndroidClipFocusStatus.Granted,
            AudioFocus.Loss => AndroidClipFocusStatus.PermanentLoss,
            AudioFocus.LossTransient => AndroidClipFocusStatus.TransientLoss,
            AudioFocus.LossTransientCanDuck => AndroidClipFocusStatus.DuckableLoss,
            _ => _state.Status
        };
        if (!_state.OnChange(status))
            return;
        ReportInterruption();
        if (_state.Status != AndroidClipFocusStatus.Granted)
            LostAudibility?.Invoke();
    }

    public void Release()
    {
        if (_state.Release())
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(26) && _request is { } request)
                    manager.AbandonAudioFocusRequest(request);
                else
                {
#pragma warning disable CS0618, CA1422
                    manager.AbandonAudioFocus(this);
#pragma warning restore CS0618, CA1422
                }
            }
            finally { ReportInterruption(released: true); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Release(); }
            finally { _request?.Dispose(); }
        }
        base.Dispose(disposing);
    }
}
