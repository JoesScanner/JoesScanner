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
internal sealed class IosClipSession(Func<IosAudioOptions, bool> activate, Func<bool> deactivate,
    Func<bool>? releasePriority = null)
{
    private static readonly int[] ReleaseDelaysMs = [100, 250, 500, 1000];
    private long _signalGeneration;
    private long _activeLease;
    private long _pendingRelease;
    private int _releaseRetries;

    internal static IosAudioOptions OptionsFor(MobileAudioMode mode) => mode switch
    {
        MobileAudioMode.MixNormally => IosAudioOptions.Mix,
        MobileAudioMode.MixAndDuck => IosAudioOptions.Mix | IosAudioOptions.Duck,
        MobileAudioMode.Exclusive => IosAudioOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal MobileAudioMode? ActiveMode { get; private set; }
    internal long SignalGeneration => Interlocked.Read(ref _signalGeneration);
    internal bool IsCurrentSignal(long generation, bool sessionWide = false) => sessionWide || SignalGeneration == generation;
    internal void InvalidatePendingSignals() => Interlocked.Increment(ref _signalGeneration);
    internal bool Owns(long lease) => lease != 0 && _activeLease == lease;
    internal long PendingRelease => _pendingRelease;

    internal long TryStart(MobileAudioMode mode)
    {
        if (_activeLease != 0)
            throw new InvalidOperationException("An iOS clip already owns the audio session.");

        var lease = Interlocked.Increment(ref _signalGeneration);
        if (!FinishDeactivation() || !activate(OptionsFor(mode)))
            return 0;

        ActiveMode = mode;
        return _activeLease = lease;
    }

    internal bool Release(long lease)
    {
        if (!Owns(lease))
            return true;

        ClearLease();
        _pendingRelease = lease;
        _releaseRetries = 0;
        return FinishDeactivation();
    }

    internal bool FinishDeactivation(long lease = 0)
    {
        if (_pendingRelease != 0 && _activeLease == 0 && (lease == 0 || _pendingRelease == lease) && deactivate())
            _pendingRelease = 0;
        return _pendingRelease == 0;
    }

    internal TimeSpan? NextReleaseDelay(long lease) =>
        lease != 0 && _pendingRelease == lease && _releaseRetries < ReleaseDelaysMs.Length
            ? TimeSpan.FromMilliseconds(ReleaseDelaysMs[_releaseRetries]) : null;

    internal void RetryRelease(long lease)
    {
        if (NextReleaseDelay(lease) == null || FinishDeactivation(lease))
            return;
        if (++_releaseRetries == ReleaseDelaysMs.Length && releasePriority?.Invoke() == true)
        {
            // Some failures leave the session active. A plain mixing category
            // releases ducking or exclusive priority even in that case.
            FinishDeactivation(lease);
            _pendingRelease = 0;
        }
    }

    // iOS has already deactivated the session on interruption or server loss.
    internal void Invalidate()
    {
        ClearLease();
        _pendingRelease = 0;
    }

    private void ClearLease()
    {
        _activeLease = 0;
        ActiveMode = null;
    }
}

internal sealed class IosAudioSuspension
{
    private enum Recovery { None, Interrupted, ActivationDenied, PlayerPaused }

    private Recovery _recovery;
    private bool _mayRetryActivation;
    private bool _callActive;
    private bool _resumeRequired;
    private bool _routePaused;
    private bool _mediaUnavailable;
    private bool _mediaResetPending;

    internal bool RequiresPlay => _routePaused || _resumeRequired ||
        (!_callActive && !_mayRetryActivation && NeedsReconciliation);
    internal bool IsBlocked => _routePaused || _resumeRequired || _callActive || _mediaUnavailable ||
        (_recovery != Recovery.None && !_mayRetryActivation);
    internal bool NeedsReconciliation => _recovery != Recovery.None;
    internal bool IsRoutePaused => _routePaused;
    internal bool HasRecoveryInterruption => _routePaused || _callActive || _resumeRequired || _mediaUnavailable ||
        (_recovery is Recovery.Interrupted or Recovery.PlayerPaused && !_mayRetryActivation);
    internal int MediaGeneration { get; private set; }
    internal int RouteGeneration { get; private set; }

    internal async Task WaitUntilPlayableAsync(Func<Task> changed, CancellationToken token, Task? terminal)
    {
        token.ThrowIfCancellationRequested();
        while (IsBlocked && terminal?.IsCompleted != true)
        {
            var signal = changed();
            await (terminal == null ? signal : Task.WhenAny(signal, terminal)).WaitAsync(token);
        }
    }

    internal bool BeginInterruption()
    {
        if (_recovery == Recovery.Interrupted && !_mayRetryActivation)
            return false;

        _recovery = Recovery.Interrupted;
        _mayRetryActivation = false;
        return true;
    }

    internal void RecommendResume(bool shouldResume)
    {
        if (_recovery == Recovery.None)
            return;

        if (shouldResume)
            _mayRetryActivation = true;
        else
        {
            _resumeRequired = true;
            ClearRecovery();
        }
    }

    internal void ActivationDenied()
    {
        _recovery = Recovery.ActivationDenied;
        _mayRetryActivation = false;
    }

    internal void UnreportedPause()
    {
        if (_recovery == Recovery.None)
            _recovery = Recovery.PlayerPaused;
        _mayRetryActivation = false;
    }

    internal void SetCallActive(bool active)
    {
        _callActive = active;
        if (active && _recovery == Recovery.None)
            BeginInterruption();
    }

    // Reconciliation grants another activation attempt. The interruption is
    // resolved only after AVAudioSession actually accepts that activation.
    internal void Reconcile(bool canRetryActivation)
    {
        if (NeedsReconciliation && canRetryActivation && !_callActive)
            _mayRetryActivation = true;
    }

    internal void ActivationSucceeded() => ClearRecovery();

    private void ClearRecovery()
    {
        _recovery = Recovery.None;
        _mayRetryActivation = false;
    }

    internal void RouteDisconnected(AudioDisconnectionBehavior preference)
    {
        ++RouteGeneration;
        if (_recovery == Recovery.PlayerPaused)
            ClearRecovery();
        _routePaused |= preference == AudioDisconnectionBehavior.PausePlayback;
    }

    internal void RouteDeactivated(AudioDisconnectionBehavior preference, bool hadClip)
    {
        // A route deactivation can follow AVPlayer's pause or the route-change
        // callback. Continue still has to clear an unexplained player pause.
        if (!_routePaused && (hadClip || preference == AudioDisconnectionBehavior.PausePlayback ||
            _recovery == Recovery.PlayerPaused))
            RouteDisconnected(preference);
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
        ClearRecovery();
    }

    internal void RequestPlay()
    {
        _routePaused = false;
        if (NeedsReconciliation)
            _mayRetryActivation = true;
        if (!_mediaUnavailable)
        {
            _resumeRequired = false;
            _mediaResetPending = false;
        }
    }

    internal void Stop()
    {
        ClearRecovery();
        _callActive = false;
        _resumeRequired = false;
        _routePaused = false;
        _mediaUnavailable = false;
        _mediaResetPending = false;
    }
}

internal sealed class IosPrivateAudioRoutes
{
    private readonly HashSet<string> _disconnected = new(StringComparer.Ordinal);
    private HashSet<string> _lastPrivateOutputs = new(StringComparer.Ordinal);

    internal void AcceptCurrentRoute(IEnumerable<string> privateOutputs)
    {
        var current = privateOutputs.ToHashSet(StringComparer.Ordinal);
        _disconnected.UnionWith(_lastPrivateOutputs.Where(output => !current.Contains(output)));
        _disconnected.ExceptWith(current);
        _lastPrivateOutputs = current;
    }

    internal bool Reconcile(IEnumerable<string> privateOutputs, AudioDisconnectionBehavior preference, out bool disconnected)
    {
        var current = privateOutputs.ToHashSet(StringComparer.Ordinal);
        // An empty snapshot may mean AVAudioSession has not reported the route
        // yet. Keep the last private route protected until confirmed or accepted.
        var missing = _lastPrivateOutputs.Where(output => !current.Contains(output)).ToArray();
        disconnected = DeviceUnavailable(missing, current);
        if (preference == AudioDisconnectionBehavior.PausePlayback && missing.Length != 0)
            return true;

        AcceptCurrentRoute(current);
        return false;
    }

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

// Monotonic samples from one recording survive a player/session rebuild. The
// native player can momentarily report zero after a media reset.
internal sealed class IosRecordingState
{
    private static readonly TimeSpan DuckingWindow = TimeSpan.FromSeconds(3);
    private double _seconds;
    private MobileAudioMode? _mode;
    private bool _recoveredFromStall;
    private TimeSpan _duckingUsed;

    internal MobileAudioMode SelectedMode(MobileAudioMode configured) => _mode ?? configured;
    internal MobileAudioMode ModeForActivation(MobileAudioMode configured)
    {
        var mode = SelectedMode(configured);
        return mode == MobileAudioMode.MixAndDuck && _duckingUsed >= DuckingWindow ? MobileAudioMode.MixNormally : mode;
    }

    internal void Activated(MobileAudioMode mode) => _mode ??= mode;

    internal TimeSpan DuckingRemaining(TimeSpan segmentElapsed) => DuckingWindow - _duckingUsed - segmentElapsed;

    internal void EndDuckedSegment(TimeSpan elapsed, bool windowExpired)
    {
        _duckingUsed = windowExpired ? DuckingWindow : _duckingUsed + elapsed;
    }

    internal void Observe(double seconds)
    {
        if (double.IsFinite(seconds) && seconds > _seconds)
            _seconds = seconds;
    }

    internal double? SeekTarget(double currentSeconds) =>
        _seconds > 0 && (!double.IsFinite(currentSeconds) || Math.Abs(_seconds - currentSeconds) > 0.25)
            ? _seconds
            : null;

    internal void Restart() => _seconds = 0;

    internal bool TryRecoverFromStall()
    {
        if (_recoveredFromStall)
            return false;
        _recoveredFromStall = true;
        return true;
    }
}

// Unlike an AVPlayer media-time observer, this clock keeps advancing during a
// buffer stall. It is reset for every audible segment, including recovery.
internal sealed class IosPlaybackProgress(double position)
{
    private TimeSpan _lastProgress;
    private TimeSpan? _waitingSince;

    internal bool IsStalled(double seconds, bool waiting, TimeSpan elapsed)
    {
        if (double.IsFinite(seconds) && (!double.IsFinite(position) || seconds - position >= 0.025))
        {
            position = seconds;
            _lastProgress = elapsed;
        }

        if (!waiting)
            _waitingSince = null;
        else
            _waitingSince ??= elapsed;

        return elapsed - _lastProgress >= TimeSpan.FromSeconds(5) ||
            _waitingSince is { } since && elapsed - (since > _lastProgress ? since : _lastProgress) >= TimeSpan.FromSeconds(2);
    }
}
