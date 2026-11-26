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
            // releases priority, while native deactivation remains pending.
            FinishDeactivation(lease);
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
    internal bool IsRecoveryBlocked(bool waitForActivation = false) => IsBlocked || waitForActivation && NeedsReconciliation;
    internal int MediaGeneration { get; private set; }
    internal int RouteGeneration { get; private set; }
    // Native output changes can arrive before the main-thread disconnect handler.
    private long _outputGeneration;
    internal long OutputGeneration => Interlocked.Read(ref _outputGeneration);
    internal void NoteOutputChange() => Interlocked.Increment(ref _outputGeneration);
    internal int RecordingRestartRevision { get; private set; }

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
        ++RecordingRestartRevision;
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
        {
            _recovery = Recovery.PlayerPaused;
            ++RecordingRestartRevision;
        }
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
        if (!_routePaused && preference == AudioDisconnectionBehavior.PausePlayback)
        {
            _routePaused = true;
            ++RecordingRestartRevision;
        }
    }

    internal void ForegroundAfterSuspension() => ++RecordingRestartRevision;

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

// Capture both identities while the ducking lease still owns the session. A live
// UID comparison detects a change before notification delivery. The generation
// also detects a notified change away and back between inspections.
internal sealed class IosHandoffOutputs(IosAudioSuspension suspension, Func<IEnumerable<string>> readOutputs)
{
    private readonly long _generation = suspension.OutputGeneration;
    private readonly HashSet<string> _ids = readOutputs().ToHashSet(StringComparer.Ordinal);

    internal bool MatchesCurrent() => _ids.Count != 0 && _ids.SetEquals(readOutputs()) &&
        suspension.OutputGeneration == _generation;
}

// Belongs to one player, item and playback request. Arm while the outgoing
// lease still owns the session. Consume before calling into AVAudioSession,
// even when that call fails before allocating a new lease.
internal sealed class IosDuckingContinuation(IosClipSession session, IosAudioSuspension suspension,
    Func<bool> ownsPlayback, Func<IEnumerable<string>> readOutputs, Func<MobileAudioMode, MobileAudioMode, long> activate)
{
    private long _outgoingLease;
    private IosHandoffOutputs? _outputs;
    private int _route;
    private int _media;
    private int _restart;

    internal void DuckingExpired(long lease)
    {
        if (session.ActiveMode != MobileAudioMode.MixAndDuck || !session.Owns(lease) ||
            !session.IsCurrentSignal(lease) || suspension.IsBlocked || !ownsPlayback())
            return;

        _outputs = new(suspension, readOutputs);
        _outgoingLease = lease;
        _route = suspension.RouteGeneration;
        _media = suspension.MediaGeneration;
        _restart = suspension.RecordingRestartRevision;
    }

    internal long StartClip(MobileAudioMode selected, MobileAudioMode effective, out IosHandoffOutputs? handoffOutputs)
    {
        var outgoing = _outgoingLease;
        var outputs = _outputs;
        _outgoingLease = 0;
        _outputs = null;
        if (outputs != null && (selected != MobileAudioMode.MixAndDuck || effective != MobileAudioMode.MixNormally ||
            !session.IsCurrentSignal(outgoing) || !StillCurrent() || !outputs.MatchesCurrent()))
            outputs = null;

        var lease = activate(selected, effective);
        handoffOutputs = outputs != null && lease == outgoing + 1 && session.Owns(lease) &&
            session.IsCurrentSignal(lease) && StillCurrent() && outputs.MatchesCurrent() ? outputs : null;
        return lease;
    }

    private bool StillCurrent() => !suspension.IsBlocked && suspension.RouteGeneration == _route &&
        suspension.MediaGeneration == _media && suspension.RecordingRestartRevision == _restart && ownsPlayback();
}

// AVPlayer can briefly report Paused after PlayImmediatelyAtRate on the same
// item when the ducking lease is exchanged for plain mixing. Main-thread KVO
// inspections and the segment's cancellable watchdog sample this one handoff.
internal sealed class IosDuckingHandoff(IosClipSession session, IosAudioSuspension suspension,
    long lease, IosHandoffOutputs outputs, double startPosition, Func<bool> ownsPlayerAndRequest, Action reconcileRoute)
{
    private static readonly TimeSpan ConfirmationDelay = TimeSpan.FromMilliseconds(100);
    private readonly int _route = suspension.RouteGeneration;
    private readonly int _media = suspension.MediaGeneration;
    private readonly int _restart = suspension.RecordingRestartRevision;
    private TimeSpan? _pausedSince;
    private TimeSpan _lastPaused;
    private bool _settled;

    internal bool NeedsSample => !_settled;

    private void Settle()
    {
        _pausedSince = null;
        _settled = true;
    }

    // Waiting alone can still stall. After observing Paused, give the pause
    // path precedence until playback spends a full buffering timeout in Waiting.
    internal bool AllowsStallRecovery(bool waiting, TimeSpan elapsed) =>
        !_pausedSince.HasValue || (waiting && elapsed - _lastPaused >= IosPlaybackProgress.WaitingTimeout);

    private bool IsCurrent() => session.Owns(lease) && session.IsCurrentSignal(lease) && !suspension.IsBlocked &&
        suspension.RouteGeneration == _route && suspension.MediaGeneration == _media &&
        suspension.RecordingRestartRevision == _restart && ownsPlayerAndRequest();

    internal bool ShouldReportPause(bool paused, bool playing, double seconds, TimeSpan elapsed)
    {
        // A changed private route is actionable immediately, even if the
        // native route notification is delayed. Reconciliation can end this lease.
        if (paused && IsCurrent())
            reconcileRoute();
        if (!IsCurrent())
        {
            Settle();
            return false;
        }

        // The player can observe a different output before its route notification.
        // Once a sample sees a mismatch, later restoration cannot re-arm protection.
        if (!_settled && !outputs.MatchesCurrent())
            Settle();
        if (_settled)
            return paused;

        if (paused)
            _lastPaused = elapsed;

        var advanced = double.IsFinite(seconds) && double.IsFinite(startPosition) && seconds - startPosition >= 0.025;
        if (double.IsFinite(seconds) && (!double.IsFinite(startPosition) || advanced))
            startPosition = seconds;
        if (playing || advanced)
        {
            // The last advancing Paused sample is also the first evidence of
            // a possible stationary pause. A later sample can confirm directly.
            if (paused)
                _pausedSince = elapsed;
            else
                Settle();
            return false;
        }

        // Stationary Waiting cannot confirm a pause or restart its clock.
        // With a continuously Paused player, confirmation is due at 100 ms
        // and sampled by KVO or the 250 ms watchdog (normally 100–350 ms).
        // A busy or suspended main thread can delay either observation.
        if (!paused)
            return false;
        _pausedSince ??= elapsed;
        if (elapsed - _pausedSince < ConfirmationDelay)
            return false;
        // Confirmation consumes the handoff even if the ordinary near-end
        // guard waits for AVPlayer's end notification. A duplicate must not
        // re-arm a provisional clock or block the independent stall watchdog.
        Settle();
        return true;
    }
}

// Unlike an AVPlayer media-time observer, this clock keeps advancing during a
// buffer stall. It is reset for every audible segment, including recovery.
internal sealed class IosPlaybackProgress(double position)
{
    internal static readonly TimeSpan WaitingTimeout = TimeSpan.FromSeconds(2);
    private TimeSpan _lastProgress;
    private TimeSpan? _waitingSince;

    internal static bool ShouldReportNativePause(IosDuckingHandoff? handoff, bool paused, bool playing,
        double seconds, double duration, TimeSpan elapsed)
    {
        if (handoff != null && !handoff.ShouldReportPause(paused, playing, seconds, elapsed))
            return false;
        var atEnd = double.IsFinite(duration) && duration > 0 &&
            double.IsFinite(seconds) && duration - seconds <= 0.1;
        return paused && !atEnd;
    }

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
            _waitingSince is { } since && elapsed - (since > _lastProgress ? since : _lastProgress) >= WaitingTimeout;
    }
}
