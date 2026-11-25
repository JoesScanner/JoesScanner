#if IOS
using AVFoundation;
using CallKit;
using CoreFoundation;
using Foundation;
using JoesScanner.Models;
using JoesScanner.Services;
using UIKit;

namespace JoesScanner.Platforms.iOS.Services;

// The main dispatcher owns all transitions and AVAudioSession access. Observers
// can remain installed between recordings without keeping the session active.
internal static class IosAudioEnvironment
{
    private static readonly IosClipSession Session = new(Activate, Deactivate, ReleasePriority);
    private static readonly IosAudioSuspension Suspension = new();
    private static TaskCompletionSource _changed = NewSignal();
    private static ObserverScope? _observers;
    private static int _watchers;
    private static Task _releaseRetries = Task.CompletedTask;

    internal static bool RequiresPlay => Suspension.RequiresPlay;
    internal static bool IsBlocked => Suspension.IsBlocked;
    internal static bool IsRecoveryBlocked(bool waitForActivation) => Suspension.IsRecoveryBlocked(waitForActivation);
    internal static bool RoutePaused => Suspension.IsRoutePaused;
    internal static int MediaGeneration => Suspension.MediaGeneration;
    internal static int RouteGeneration => Suspension.RouteGeneration;
    internal static int RecordingRestartRevision => Suspension.RecordingRestartRevision;
    internal static MobileAudioMode? ActiveMode => Session.ActiveMode;
    internal static Task Changed => _changed.Task;
    internal static event Action? ResumeStateChanged;
    internal static event Action<DateTimeOffset, DateTimeOffset?>? ForegroundRecovered;
    internal static event Action<DateTimeOffset>? BackgroundMonitoringCheckpoint;
    internal static event Action? RecoveryInterruptionChanged;
    internal static event Action? PlaybackStateChanged;
    internal static event Action? ReconcilePlayerState;

    internal static IDisposable Watch(ISettingsService settings, bool monitoring = false)
    {
        _observers ??= new ObserverScope(settings);
        ++_watchers;
        if (monitoring)
            _observers.BeginMonitoringRecovery();
        return new WatchLease(monitoring);
    }

    internal static Task WaitForReleaseAsync(CancellationToken token) => _releaseRetries.WaitAsync(token);

    internal static Task WaitUntilPlayableAsync(CancellationToken token, Task? terminal = null) =>
        Suspension.WaitUntilPlayableAsync(static () => Changed, token, terminal);

    internal static long StartClip(MobileAudioMode selected, MobileAudioMode mode)
    {
        _observers?.RefreshCallState();
        _observers?.ReconcileCurrentRoute();
        if (IsBlocked)
            return 0;

        AppLog.DebugWriteLine(() => $"iOS audio session requested (selected={selected}, effective={mode}).");
        var lease = Session.TryStart(mode);
        if (lease != 0)
        {
            _observers?.RefreshCallState();
            _observers?.ReconcileCurrentRoute();
            if (IsBlocked)
            {
                ReleaseClip(lease);
                return 0;
            }
            if (Suspension.NeedsReconciliation)
                Change(Suspension.ActivationSucceeded);
        }
        else if (!IsBlocked)
            Change(Suspension.ActivationDenied);
        return lease;
    }

    internal static void ReleaseClip(long lease)
    {
        if (Session.Release(lease))
            return;

        _releaseRetries = RetryReleaseAsync(lease);
    }

    private static async Task RetryReleaseAsync(long lease)
    {
        while (await MainThread.InvokeOnMainThreadAsync(() => Session.NextReleaseDelay(lease)) is { } delay)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() => Session.RetryRelease(lease));
        }
    }
    internal static void RequestPlay()
    {
        Session.InvalidatePendingSignals();
        _observers?.AcceptCurrentRoute();
        _observers?.RefreshCallState();
        Change(Suspension.RequestPlay);
    }

    internal static void HandleUnreportedPause(long lease)
    {
        var route = RouteGeneration;
        _observers?.ReconcileCurrentRoute();
        if (Session.Owns(lease) && route == RouteGeneration)
            Change(Suspension.UnreportedPause);
    }

    private static void OnMediaServicesLost() => Change(() =>
    {
        AppLog.DebugWriteLine("iOS audio media services lost.");
        Session.Invalidate();
        Suspension.MediaServicesLost();
    });

    internal static void HandleMediaServicesReset() => Change(() =>
    {
        AppLog.DebugWriteLine("iOS audio media services reset. Waiting for Play.");
        Session.Invalidate();
        Suspension.MediaServicesReset();
    });

    private static void Change(Action transition)
    {
        var requiredBefore = RequiresPlay;
        transition();
        RecoveryInterruptionChanged?.Invoke();
        var prior = _changed;
        _changed = NewSignal();
        prior.TrySetResult();
        PlaybackStateChanged?.Invoke();
        if (RequiresPlay != requiredBefore)
            ResumeStateChanged?.Invoke();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool Activate(IosAudioOptions options)
    {
        var session = AVAudioSession.SharedInstance();
        var nativeOptions = (AVAudioSessionCategoryOptions)0;
        if ((options & IosAudioOptions.Mix) != 0)
            nativeOptions |= AVAudioSessionCategoryOptions.MixWithOthers;
        if ((options & IosAudioOptions.Duck) != 0)
            nativeOptions |= AVAudioSessionCategoryOptions.DuckOthers;

        var categoryError = session.SetCategory(AVAudioSessionCategory.Playback, nativeOptions);
        AppLog.DebugWriteLine(() => $"iOS audio category Playback (options={nativeOptions}, error={categoryError?.Code ?? 0}).");
        if (categoryError != null)
            return HandleActivationFailure(categoryError);
        if (OperatingSystem.IsIOSVersionAtLeast(17))
        {
            session.SetPrefersInterruptionOnRouteDisconnect(
                _observers?.DisconnectPreference == AudioDisconnectionBehavior.PausePlayback, out var routeError);
            if (routeError != null)
                AppLog.DebugWriteLine(() => $"iOS audio route preference failed ({routeError.Code}).");
        }

        var activated = session.SetActive(true, out var error);
        AppLog.DebugWriteLine(() => $"iOS audio activation (succeeded={activated}, error={error?.Code ?? 0}).");
        if (activated)
            return true;

        return HandleActivationFailure(error);
    }

    private static bool HandleActivationFailure(NSError? error)
    {
        var code = error == null ? AVAudioSessionErrorCode.Unspecified : (AVAudioSessionErrorCode)(long)error.Code;
        if (code is AVAudioSessionErrorCode.InsufficientPriority or AVAudioSessionErrorCode.CannotInterruptOthers
            or AVAudioSessionErrorCode.SiriIsRecording or AVAudioSessionErrorCode.IsBusy or AVAudioSessionErrorCode.CannotStartPlaying)
        {
            AppLog.DebugWriteLine(() => $"iOS audio activation deferred ({code}).");
            return false;
        }
        if (code == AVAudioSessionErrorCode.MediaServicesFailed)
        {
            Change(Suspension.MediaServicesLost);
            return false;
        }

        throw new InvalidOperationException($"iOS audio session activation failed ({code}).");
    }

    private static bool Deactivate()
    {
        var result = AVAudioSession.SharedInstance().SetActive(false,
            AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var error);
        // AVAudioSession documents IsBusy as having deactivated the session.
        var released = result && error == null || error?.Code == (long)AVAudioSessionErrorCode.IsBusy;
        AppLog.DebugWriteLine(() => $"iOS audio deactivation (notify others, result={result}, released={released}, error={error?.Code ?? 0}).");
        return released;
    }

    private static bool ReleasePriority()
    {
        var error = AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback,
            AVAudioSessionCategoryOptions.MixWithOthers);
        AppLog.DebugWriteLine(() => $"iOS audio release fallback Playback (options=MixWithOthers, error={error?.Code ?? 0}).");
        return error == null;
    }

    private static void SystemDeactivated(bool wasSuspended, bool routeDisconnected)
    {
        AppLog.DebugWriteLine(() => $"iOS audio deactivated by system (wasSuspended={wasSuspended}, routeDisconnected={routeDisconnected}).");
        if (wasSuspended)
        {
            _observers?.NoteProcessWasSuspended();
            // This notification describes a past process suspension. The route
            // and the current player state tell us whether recovery is still needed.
            _observers?.ReconcileCurrentRoute();
            ReconcilePlayerState?.Invoke();
            return;
        }

        if (routeDisconnected)
        {
            var preference = _observers?.DisconnectPreference ?? AudioDisconnectionBehavior.PausePlayback;
            Change(() =>
            {
                var hadClip = Session.ActiveMode != null;
                Session.Invalidate();
                Suspension.RouteDeactivated(preference, hadClip);
            });
            return;
        }

        Change(() =>
        {
            if (Suspension.BeginInterruption())
                Session.Invalidate();
        });
    }

    private static void ResumeRecommendation(bool shouldResume)
    {
        AppLog.DebugWriteLine(() => $"iOS audio resumption recommendation (shouldResume={shouldResume}).");
        _observers?.ReconcileCurrentRoute();
        _observers?.RefreshCallState();
        Change(() => Suspension.RecommendResume(shouldResume));
    }

    private sealed class ObserverScope : IDisposable
    {
        private readonly List<NSObject> _tokens = [];
        private readonly ISettingsService _settings;
        private readonly IosPrivateAudioRoutes _routes = new();
        private readonly CXCallObserver _calls = new();
        private readonly CallObserverDelegate _callDelegate;
        private DateTimeOffset? _backgroundSince;
        private DateTimeOffset _lastBackgroundPulseUtc;
        private DateTimeOffset? _suspendedSince;
        private long _lastBackgroundPulse;
        private CancellationTokenSource? _backgroundProbe;
        private bool _monitoringRecovery;
        private bool _checkForeground = true;
        private bool _callActive;
        private bool _disposed;

        internal ObserverScope(ISettingsService settings)
        {
            _settings = settings;
            AcceptCurrentRoute();
            _callDelegate = new CallObserverDelegate(RefreshCallState);
            _calls.SetDelegate(_callDelegate, DispatchQueue.MainQueue);
            RefreshCallState();

            if (OperatingSystem.IsIOSVersionAtLeast(27))
                ObserveSessionLifecycle();
            else
                ObserveLegacyInterruptions();
            _tokens.Add(AVAudioSession.Notifications.ObserveRouteChange((_, e) =>
            {
                if (e.Reason == AVAudioSessionRouteChangeReason.NewDeviceAvailable)
                    Dispatch(() => ReconcileCurrentRoute());
                else if (e.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable &&
                    e.PreviousRoute?.Outputs.Where(IsPrivateOutput).Select(port => port.UID).ToArray() is { Length: > 0 } oldOutputs)
                {
                    DispatchAudio(() =>
                    {
                        var current = AVAudioSession.SharedInstance().CurrentRoute.Outputs.Select(port => port.UID);
                        if (_routes.DeviceUnavailable(oldOutputs, current))
                            Change(() => Suspension.RouteDisconnected(_settings.MobileAudio.OnDisconnect));
                    });
                }
            }));
            _tokens.Add(AVAudioSession.Notifications.ObserveMediaServicesWereLost((_, _) => DispatchAudio(OnMediaServicesLost, sessionWide: true)));
            _tokens.Add(AVAudioSession.Notifications.ObserveMediaServicesWereReset((_, _) => DispatchAudio(HandleMediaServicesReset, sessionWide: true)));
            var center = NSNotificationCenter.DefaultCenter;
            _tokens.Add(center.AddObserver(UIApplication.WillResignActiveNotification, _ => Dispatch(() => _checkForeground = true)));
            _tokens.Add(center.AddObserver(UIScene.WillDeactivateNotification, _ => Dispatch(() => _checkForeground = true)));
            _tokens.Add(center.AddObserver(UIApplication.DidEnterBackgroundNotification, _ => Dispatch(EnterBackground)));
            _tokens.Add(center.AddObserver(UIApplication.DidBecomeActiveNotification, _ => Dispatch(Foreground)));
            _tokens.Add(center.AddObserver(UIScene.DidEnterBackgroundNotification, _ => Dispatch(EnterBackground)));
            _tokens.Add(center.AddObserver(UIScene.DidActivateNotification, _ => Dispatch(Foreground)));
        }

        [System.Runtime.Versioning.SupportedOSPlatform("ios27.0")]
        private void ObserveSessionLifecycle()
        {
            var session = AVAudioSession.SharedInstance();
            _tokens.Add(AVAudioSession.Notifications.ObserveDidBecomeInactive(session, (_, e) =>
            {
                if (e.Notification.UserInfo?[AVAudioSession.DeactivationContextKey] is not AVAudioSessionDeactivationContext context ||
                    context.Source != AVAudioSessionDeactivationSource.System)
                    return;

                var reason = context.InterruptionContext?.Reason;
                DispatchAudio(() => SystemDeactivated(wasSuspended: false,
                    reason == AVAudioSessionInterruptionReason.RouteDisconnected));
            }));
            _tokens.Add(AVAudioSession.Notifications.ObserveResumptionRecommendation(session, (_, e) =>
            {
                if (e.Notification.UserInfo?[AVAudioSession.ResumptionContextKey] is not AVAudioSessionResumptionContext context)
                    return;

                var recommendation = context.Recommendation;
                switch (recommendation)
                {
                    case AVAudioSessionResumptionRecommendation.ShouldResume:
                        DispatchAudio(() => ResumeRecommendation(true));
                        break;
                    case AVAudioSessionResumptionRecommendation.ShouldNotResume:
                        DispatchAudio(() => ResumeRecommendation(false));
                        break;
                }
            }));
        }

        private void ObserveLegacyInterruptions()
        {
            _tokens.Add(AVAudioSession.Notifications.ObserveInterruption((_, e) =>
            {
                var wasSuspended = e.WasSuspended == true;
                var routeDisconnected = OperatingSystem.IsIOSVersionAtLeast(17) &&
                    e.Reason == AVAudioSessionInterruptionReason.RouteDisconnected;
                if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
                    DispatchAudio(() => SystemDeactivated(wasSuspended, routeDisconnected));
                else if (e.InterruptionType == AVAudioSessionInterruptionType.Ended && !routeDisconnected)
                {
                    var shouldResume = e.Option.HasFlag(AVAudioSessionInterruptionOptions.ShouldResume);
                    DispatchAudio(() => ResumeRecommendation(shouldResume));
                }
            }));
        }

        internal void RefreshCallState()
        {
            if (_disposed)
                return;
            // Read the current call list so a delayed delegate callback cannot
            // restore a stale ringing/in-call state. No call details are kept.
            var active = _calls.Calls.Any(call => !call.HasEnded);
            if (active == _callActive)
                return;
            _callActive = active;
            Change(() => Suspension.SetCallActive(active));
            if (!active)
                ReconcileRecovery();
        }

        private void EnterBackground()
        {
            _checkForeground = true;
            ReconcileCurrentRoute();
            if (!_monitoringRecovery || _backgroundSince.HasValue)
                return;
            _backgroundSince = _lastBackgroundPulseUtc = DateTimeOffset.UtcNow;
            _lastBackgroundPulse = System.Diagnostics.Stopwatch.GetTimestamp();
            BackgroundMonitoringCheckpoint?.Invoke(_lastBackgroundPulseUtc);
            _backgroundProbe = new CancellationTokenSource();
            _ = ProbeBackgroundExecutionAsync(_backgroundProbe);
        }

        private async Task ProbeBackgroundExecutionAsync(CancellationTokenSource probe)
        {
            // This timer observes whether the dispatcher is still executing. It
            // acquires no background execution time and plays no audio.
            using (probe)
            {
                try
                {
                    while (!probe.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), probe.Token).ConfigureAwait(false);
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            if (_backgroundProbe == probe)
                                ObserveBackgroundPulse();
                        });
                    }
                }
                catch (OperationCanceledException) when (probe.IsCancellationRequested) { }
            }
        }

        private void ObserveBackgroundPulse()
        {
            var next = System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastBackgroundPulse, next) > TimeSpan.FromSeconds(5))
                NoteProcessWasSuspended();
            _lastBackgroundPulse = next;
            _lastBackgroundPulseUtc = DateTimeOffset.UtcNow;
        }

        internal void NoteProcessWasSuspended()
        {
            if (_backgroundSince.HasValue)
                _suspendedSince ??= _lastBackgroundPulseUtc;
        }

        internal void BeginMonitoringRecovery()
        {
            _monitoringRecovery = true;
            if (UIApplication.SharedApplication.ApplicationState == UIApplicationState.Background)
                EnterBackground();
        }

        internal void EndMonitoringRecovery()
        {
            _monitoringRecovery = false;
            _backgroundSince = null;
            _suspendedSince = null;
            StopBackgroundProbe();
        }

        private void StopBackgroundProbe()
        {
            var probe = _backgroundProbe;
            _backgroundProbe = null;
            probe?.Cancel();
        }

        private void Foreground()
        {
            if (!_checkForeground || UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active)
                return;
            _checkForeground = false;
            RefreshCallState();
            if (_backgroundSince is { } since)
            {
                ObserveBackgroundPulse();
                _backgroundSince = null;
                StopBackgroundProbe();
                var suspendedSince = _suspendedSince;
                _suspendedSince = null;
                ForegroundRecovered?.Invoke(since, suspendedSince);
                if (suspendedSince.HasValue)
                    Change(Suspension.ForegroundAfterSuspension);
            }
            ReconcilePlayerState?.Invoke();
            Session.FinishDeactivation();
            ReconcileRecovery();
        }

        private void ReconcileRecovery()
        {
            ReconcileCurrentRoute();
            // There is no public AVAudioSession IsActive or interruption-owner
            // query. At a verified foreground/call transition, ask the OS to
            // arbitrate again only if we have no conflicting audio evidence.
            if (Suspension.NeedsReconciliation && !_callActive &&
                !AVAudioSession.SharedInstance().SecondaryAudioShouldBeSilencedHint)
                Change(() => Suspension.Reconcile(canRetryActivation: true));
        }

        internal AudioDisconnectionBehavior DisconnectPreference => _settings.MobileAudio.OnDisconnect;

        internal void AcceptCurrentRoute() => _routes.AcceptCurrentRoute(CurrentPrivateOutputs());

        internal void ReconcileCurrentRoute()
        {
            var preference = DisconnectPreference;
            var mustPause = _routes.Reconcile(CurrentPrivateOutputs(), preference, out var disconnected);
            if (disconnected || (mustPause && !Suspension.IsRoutePaused))
                Change(() => Suspension.RouteDisconnected(preference));
        }

        private static IEnumerable<string> CurrentPrivateOutputs() =>
            AVAudioSession.SharedInstance().CurrentRoute.Outputs.Where(IsPrivateOutput).Select(port => port.UID);

        private static bool IsPrivateOutput(AVAudioSessionPortDescription port)
        {
            var type = port.PortType.ToString();
            return type == AVAudioSession.PortHeadphones.ToString() ||
                   type == AVAudioSession.PortBluetoothA2DP.ToString() ||
                   type == AVAudioSession.PortBluetoothHfp.ToString() ||
                   type == AVAudioSession.PortBluetoothLE.ToString() ||
                   type == AVAudioSession.PortUsbAudio.ToString();
        }

        private void Dispatch(Action callback) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_disposed)
                callback();
        });

        private void DispatchAudio(Action callback, bool sessionWide = false)
        {
            var generation = Session.SignalGeneration;
            // Defer native callbacks past SetActive/Release. A media server
            // event applies to every player, even if the clip changed in the meantime.
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (!_disposed && Session.IsCurrentSignal(generation, sessionWide))
                    callback();
            });
        }

        public void Dispose()
        {
            _disposed = true;
            _calls.SetDelegate(null, null);
            _callDelegate.Dispose();
            _calls.Dispose();
            foreach (var observer in _tokens)
            {
                NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
                observer.Dispose();
            }
        }
    }

    private sealed class CallObserverDelegate(Action changed) : CXCallObserverDelegate
    {
        public override void CallChanged(CXCallObserver observer, CXCall call) => changed();
    }

    private sealed class WatchLease(bool monitoring) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (monitoring)
            {
                // A History clip can still own the session after Live stops.
                // Let the clip or the last watcher retire its audio signals.
                _observers?.EndMonitoringRecovery();
            }
            if (--_watchers != 0)
                return;

            _observers?.Dispose();
            _observers = null;
            Session.InvalidatePendingSignals();
            Change(Suspension.Stop);
        }
    }
}
#endif
