#if IOS
using AVFoundation;
using Foundation;
using JoesScanner.Models;
using JoesScanner.Services;
using UIKit;

namespace JoesScanner.Platforms.iOS.Services;

// The main dispatcher owns all transitions and AVAudioSession access. Observers
// can remain installed between recordings without keeping the session active.
internal static class IosAudioEnvironment
{
    private static readonly IosClipSession Session = new(Activate, Deactivate);
    private static readonly IosAudioSuspension Suspension = new();
    private static TaskCompletionSource _changed = NewSignal();
    private static ObserverScope? _observers;
    private static int _watchers;
    private static DateTimeOffset? _backgroundSince;

    internal static bool RequiresPlay => Suspension.RequiresPlay;
    internal static bool IsBlocked => Suspension.IsBlocked;
    internal static int MediaGeneration => Suspension.MediaGeneration;
    internal static int RouteGeneration => Suspension.RouteGeneration;
    internal static MobileAudioMode? ActiveMode => Session.ActiveMode;
    internal static Task Changed => _changed.Task;
    internal static event Action? ResumeStateChanged;
    internal static event Action<DateTimeOffset>? ForegroundRecovered;
    internal static event Action? PlaybackStateChanged;

    internal static IDisposable Watch(ISettingsService settings)
    {
        _observers ??= new ObserverScope(settings);
        ++_watchers;
        return new WatchLease();
    }

    internal static long StartClip(MobileAudioMode mode)
    {
        _observers?.ReconcileCurrentRoute();
        if (IsBlocked)
            return 0;

        var lease = Session.TryStart(mode);
        if (lease != 0 && _observers?.ReconcileCurrentRoute() == true)
        {
            ReleaseClip(lease);
            return 0;
        }
        if (lease == 0 && !IsBlocked)
            Change(Suspension.ActivationDenied);
        return lease;
    }

    internal static void ReleaseClip(long lease) => Session.Release(lease);

    internal static void RequestPlay()
    {
        _observers?.AcceptCurrentRoute();
        Change(Suspension.RequestPlay);
    }
    internal static void HandleUnreportedPause() => Change(Suspension.UnreportedPause);

    internal static void HandlePlayerMediaServicesLoss() => Change(() =>
    {
        Session.Invalidate();
        Suspension.MediaServicesLost();
    });

    private static void Change(Action transition)
    {
        var requiredBefore = RequiresPlay;
        transition();
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

        if (session.SetCategory(AVAudioSessionCategory.Playback, nativeOptions) is { } categoryError)
            return HandleActivationFailure(categoryError);
        if (OperatingSystem.IsIOSVersionAtLeast(17))
        {
            session.SetPrefersInterruptionOnRouteDisconnect(
                _observers?.DisconnectPreference == AudioDisconnectionBehavior.PausePlayback, out var routeError);
            if (routeError != null)
                AppLog.DebugWriteLine(() => $"iOS audio route preference failed ({routeError.Code}).");
        }

        if (session.SetActive(true, out var error))
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

    private static void Deactivate()
    {
        AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation,
            out var error);
        if (error != null)
            AppLog.DebugWriteLine(() => $"iOS audio session deactivation reported ({error.Code}).");
    }

    private static void Interruption(AVAudioSessionInterruptionType type, bool shouldResume, bool wasSuspended,
        bool routeDisconnected)
    {
        if (type == AVAudioSessionInterruptionType.Began)
        {
            if (routeDisconnected)
            {
                if (_observers?.DisconnectPreference == AudioDisconnectionBehavior.PausePlayback)
                    Change(() =>
                    {
                        Session.Invalidate();
                        if (!Suspension.IsRoutePaused)
                            Suspension.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
                    });
                return;
            }
            if (wasSuspended)
                _observers?.ReconcileCurrentRoute();
            else
            {
                Change(() =>
                {
                    if (Suspension.BeginInterruption())
                        Session.Invalidate();
                });
            }
        }
        else if (type == AVAudioSessionInterruptionType.Ended && !routeDisconnected)
        {
            Change(() => Suspension.EndInterruption(shouldResume));
        }
    }

    private static void EnterBackground()
    {
        _observers?.ReconcileCurrentRoute();
        _backgroundSince ??= DateTimeOffset.UtcNow;
    }

    private static void Foreground()
    {
        _observers?.ReconcileCurrentRoute();
        Change(Suspension.Foreground);
        if (_backgroundSince is { } since)
        {
            _backgroundSince = null;
            ForegroundRecovered?.Invoke(since);
        }
    }

    private sealed class ObserverScope : IDisposable
    {
        private readonly List<NSObject> _tokens = [];
        private readonly ISettingsService _settings;
        private readonly IosPrivateAudioRoutes _routes = new();
        private bool _disposed;

        internal ObserverScope(ISettingsService settings)
        {
            _settings = settings;
            AcceptCurrentRoute();
            _tokens.Add(AVAudioSession.Notifications.ObserveInterruption((_, e) =>
            {
                var type = e.InterruptionType;
                var shouldResume = e.Option.HasFlag(AVAudioSessionInterruptionOptions.ShouldResume);
                var wasSuspended = e.WasSuspended == true;
                var routeDisconnected = OperatingSystem.IsIOSVersionAtLeast(17) &&
                    e.Reason == AVAudioSessionInterruptionReason.RouteDisconnected;
                Dispatch(() => Interruption(type, shouldResume, wasSuspended, routeDisconnected));
            }));
            _tokens.Add(AVAudioSession.Notifications.ObserveRouteChange((_, e) =>
            {
                if (e.Reason == AVAudioSessionRouteChangeReason.NewDeviceAvailable)
                {
                    Dispatch(() =>
                    {
                        _routes.DeviceAvailable();
                        var current = CurrentPrivateOutputs().ToArray();
                        if (!Suspension.IsRoutePaused && current.Length > 0)
                            _routes.AcceptCurrentRoute(current);
                    });
                }
                else if (e.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable &&
                    e.PreviousRoute?.Outputs.Where(IsPrivateOutput).Select(port => port.UID).ToArray() is { Length: > 0 } oldOutputs)
                {
                    Dispatch(() =>
                    {
                        var current = AVAudioSession.SharedInstance().CurrentRoute.Outputs.Select(port => port.UID);
                        if (_routes.DeviceUnavailable(oldOutputs, current))
                            Change(() => Suspension.RouteDisconnected(_settings.MobileAudio.OnDisconnect));
                    });
                }
            }));
            _tokens.Add(AVAudioSession.Notifications.ObserveMediaServicesWereLost((_, _) => Dispatch(HandlePlayerMediaServicesLoss)));
            _tokens.Add(AVAudioSession.Notifications.ObserveMediaServicesWereReset((_, _) => Dispatch(() => Change(() =>
            {
                Session.Invalidate();
                Suspension.MediaServicesReset();
            }))));
            var center = NSNotificationCenter.DefaultCenter;
            _tokens.Add(center.AddObserver(UIApplication.DidEnterBackgroundNotification, _ => Dispatch(EnterBackground)));
            _tokens.Add(center.AddObserver(UIApplication.DidBecomeActiveNotification, _ => Dispatch(Foreground)));
            _tokens.Add(center.AddObserver(UIScene.DidEnterBackgroundNotification, _ => Dispatch(EnterBackground)));
            _tokens.Add(center.AddObserver(UIScene.DidActivateNotification, _ => Dispatch(Foreground)));
        }

        internal AudioDisconnectionBehavior DisconnectPreference => _settings.MobileAudio.OnDisconnect;

        internal void AcceptCurrentRoute() => _routes.AcceptCurrentRoute(CurrentPrivateOutputs());

        internal bool ReconcileCurrentRoute()
        {
            if (!_routes.Reconcile(CurrentPrivateOutputs(), DisconnectPreference))
                return false;

            if (!Suspension.IsRoutePaused)
                Change(() => Suspension.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback));
            return true;
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

        public void Dispose()
        {
            _disposed = true;
            foreach (var observer in _tokens)
            {
                NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
                observer.Dispose();
            }
        }
    }

    private sealed class WatchLease : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (--_watchers != 0)
                return;

            _observers?.Dispose();
            _observers = null;
            _backgroundSince = null;
            Change(Suspension.Stop);
        }
    }
}
#endif
