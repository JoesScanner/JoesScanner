using JoesScanner.Models;

namespace JoesScanner.Services;

[Flags]
internal enum IosAudioOptions
{
    None = 0,
    Mix = 1,
    Duck = 2
}

// The main dispatcher owns this lease. Media callbacks can only release their own segment.
internal sealed class IosClipSession(Func<IosAudioOptions, bool> activate, Action deactivate)
{
    private long _nextLease;
    private long _activeLease;

    internal static IosAudioOptions OptionsFor(MobileAudioMode mode) => mode switch
    {
        MobileAudioMode.MixNormally => IosAudioOptions.Mix,
        MobileAudioMode.MixAndDuck => IosAudioOptions.Mix | IosAudioOptions.Duck,
        MobileAudioMode.Exclusive => IosAudioOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal MobileAudioMode? ActiveMode { get; private set; }

    internal long TryStart(MobileAudioMode mode)
    {
        if (_activeLease != 0)
            throw new InvalidOperationException("An iOS clip already owns the audio session.");

        if (!activate(OptionsFor(mode)))
            return 0;

        ActiveMode = mode;
        return _activeLease = ++_nextLease;
    }

    internal void Release(long lease)
    {
        if (lease == 0 || _activeLease != lease)
            return;

        Invalidate();
        deactivate();
    }

    // iOS has already deactivated the session on interruption or server loss.
    internal void Invalidate()
    {
        _activeLease = 0;
        ActiveMode = null;
    }
}

internal sealed class IosAudioSuspension
{
    private bool _interrupted;
    private bool _resumeRequired;
    private bool _routePaused;
    private bool _mediaUnavailable;
    private bool _mediaResetPending;
    private bool _activationDenied;
    private bool _nativePaused;

    internal bool RequiresPlay => _routePaused || _resumeRequired ||
        ((_activationDenied || _nativePaused) && !_interrupted);
    internal bool IsBlocked => RequiresPlay || _interrupted || _mediaUnavailable;
    internal bool IsRoutePaused => _routePaused;
    internal int MediaGeneration { get; private set; }
    internal int RouteGeneration { get; private set; }

    internal bool BeginInterruption()
    {
        if (_interrupted)
            return false;

        _interrupted = true;
        return true;
    }

    internal void EndInterruption(bool shouldResume)
    {
        if (!_interrupted && !_activationDenied && !_nativePaused)
            return;

        _interrupted = false;
        _activationDenied = false;
        _nativePaused = false;
        _resumeRequired |= !shouldResume;
    }

    internal void ActivationDenied() => _activationDenied = true;
    internal void UnreportedPause() => _nativePaused = true;

    // An end notification is not guaranteed. On return to the app iOS will
    // arbitrate a new activation, while an explicit-resume latch stays set.
    internal void Foreground()
    {
        _interrupted = false;
        _activationDenied = false;
        _nativePaused = false;
    }

    internal void RouteDisconnected(AudioDisconnectionBehavior preference)
    {
        ++RouteGeneration;
        _nativePaused = false;
        _routePaused |= preference == AudioDisconnectionBehavior.PausePlayback;
    }

    internal void MediaServicesLost()
    {
        if (_mediaUnavailable)
            return;

        _mediaUnavailable = true;
        _resumeRequired = true;
        ++MediaGeneration;
    }

    internal void MediaServicesReset()
    {
        if (_mediaResetPending && !_mediaUnavailable)
            return;

        MediaServicesLost();
        _mediaUnavailable = false;
        _mediaResetPending = true;
        _resumeRequired = true;
        _interrupted = false;
    }

    internal void RequestPlay()
    {
        _routePaused = false;
        _activationDenied = false;
        _nativePaused = false;
        if (!_mediaUnavailable)
        {
            _resumeRequired = false;
            _mediaResetPending = false;
        }
    }

    internal void Stop()
    {
        _interrupted = false;
        _resumeRequired = false;
        _routePaused = false;
        _activationDenied = false;
        _nativePaused = false;
        _mediaUnavailable = false;
        _mediaResetPending = false;
    }
}

internal sealed class IosPrivateAudioRoutes
{
    private readonly HashSet<string> _disconnected = new(StringComparer.Ordinal);
    private HashSet<string> _lastPrivateOutputs = new(StringComparer.Ordinal);

    internal void AcceptCurrentRoute(IEnumerable<string> privateOutputs) =>
        _lastPrivateOutputs = privateOutputs.ToHashSet(StringComparer.Ordinal);

    internal bool Reconcile(IEnumerable<string> privateOutputs, AudioDisconnectionBehavior preference)
    {
        var current = privateOutputs.ToHashSet(StringComparer.Ordinal);
        // An empty snapshot may mean AVAudioSession has not reported the route
        // yet. Keep the last private route protected until confirmed or accepted.
        if (preference == AudioDisconnectionBehavior.PausePlayback &&
            _lastPrivateOutputs.Any(output => !current.Contains(output)))
            return true;

        _lastPrivateOutputs = current;
        return false;
    }

    internal void DeviceAvailable() => _disconnected.Clear();

    internal bool DeviceUnavailable(IEnumerable<string> previousPrivateOutputs, IEnumerable<string> currentOutputs)
    {
        var current = currentOutputs.ToHashSet(StringComparer.Ordinal);
        var disconnected = false;
        foreach (var output in previousPrivateOutputs)
        {
            if (!current.Contains(output))
                disconnected |= _disconnected.Add(output);
        }
        return disconnected;
    }
}
