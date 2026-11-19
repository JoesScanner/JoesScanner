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

    public Task<bool> Granted => _state.Ready;
    public Task Lost => _state.Lost;

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
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        if (focusChange == AudioFocus.Gain)
            _state.OnGain();
        else if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient or AudioFocus.LossTransientCanDuck)
            _state.OnLoss();
    }

    public void Release()
    {
        if (_state.Release())
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26) && _request is { } request)
            {
                manager.AbandonAudioFocusRequest(request);
            }
            else
            {
#pragma warning disable CS0618, CA1422
                manager.AbandonAudioFocus(this);
#pragma warning restore CS0618, CA1422
            }
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
