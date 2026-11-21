using Android.Content;
using Android.Media;
using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.Platforms.Android.Services;

internal static class AndroidAudioEnvironment
{
    private static readonly AndroidAudioInterruption State = new();
    private static readonly AndroidAudioRoutes Routes = new();
    private static TaskCompletionSource _changed = NewSignal();
    private static NoisyReceiver? _receiver;
    private static DeviceWatcher? _devices;
    private static int _watchers;

    public static bool RequiresPlay => State.RequiresPlay;
    public static bool IsBlocked => State.IsBlocked;
    public static bool CallActive => State.CallActive;
    public static bool RequiresNewFocusRequest => State.FocusBlocked;
    public static bool RoutePaused => State.RoutePaused;
    public static long CallRevision => State.CallRevision;
    public static AndroidFocusAttemptAction GetFocusAction(AndroidClipFocusStatus status, long callRevision) =>
        State.GetFocusAction(status, callRevision);
    public static Task Changed => _changed.Task;
    public static event Action? ResumeStateChanged;
    public static event Action? PlaybackBlocked;
    internal static event Action? InterruptionChanged;
    internal static event Action<bool>? ClipFocusInterrupted;

    internal static void ReportClipFocusInterruption(bool interrupted) => ClipFocusInterrupted?.Invoke(interrupted);

    public static IDisposable Watch(Context context, ISettingsService settings)
    {
        if (_watchers == 0)
        {
            var receiver = new NoisyReceiver(settings);
            using var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
            else
                context.RegisterReceiver(receiver, filter);
            _receiver = receiver;
            try
            {
                if (context.GetSystemService(Context.AudioService) is AudioManager manager)
                {
                    _devices = new DeviceWatcher(manager, settings, context.GetSystemService(Context.MediaRouterService) as MediaRouter);
                    _devices.RefreshExpectedRoute();
                }
            }
            catch (Exception ex) when (ex is Java.Lang.RuntimeException or InvalidOperationException)
            {
                AppLog.Add(() => $"Android audio-device watcher could not start ({ex.GetType().Name}).");
            }
        }
        ++_watchers;
        return new RouteWatch(context);
    }

    public static void ReadCallMode(AudioManager manager)
        => SetCallMode(manager.Mode);

    public static IDisposable? WatchCallMode(Context context, AudioManager manager)
    {
        ReadCallMode(manager);
        if (!OperatingSystem.IsAndroidVersionAtLeast(31) || context.MainExecutor is not { } executor)
            return null;

        var watch = new CallModeWatch(manager);
        try
        {
            manager.AddOnModeChangedListener(executor, watch);
            return watch;
        }
        catch (Java.Lang.RuntimeException ex)
        {
            AppLog.DebugWriteLine(() => $"Android audio-mode listener unavailable ({ex.GetType().Name}).");
            watch.Dispose();
            return null;
        }
    }

    private static void SetCallMode(Mode mode)
    {
        var wasBlocked = State.IsBlocked;
        var requiredPlay = State.RequiresPlay;
        State.SetCallActive(mode is Mode.InCall or Mode.InCommunication or Mode.Ringtone);
        PublishChange(wasBlocked, requiredPlay);
    }

    public static void BlockForFocus(long requestCallRevision)
    {
        var wasBlocked = State.IsBlocked;
        var requiredPlay = State.RequiresPlay;
        State.BlockForFocus(requestCallRevision);
        PublishChange(wasBlocked, requiredPlay);
    }

    public static void RequestPlay()
    {
        var wasBlocked = State.IsBlocked;
        var requiredPlay = State.RequiresPlay;
        State.RequestPlay();
        PublishChange(wasBlocked, requiredPlay);
    }

    private static void PublishChange(bool wasBlocked, bool requiredPlay)
    {
        InterruptionChanged?.Invoke();
        if (wasBlocked != State.IsBlocked || requiredPlay != State.RequiresPlay)
        {
            var prior = _changed;
            _changed = NewSignal();
            prior.TrySetResult();
        }
        if (!wasBlocked && State.IsBlocked)
            PlaybackBlocked?.Invoke();
        if (requiredPlay != State.RequiresPlay)
            ResumeStateChanged?.Invoke();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Use the actual player route while it makes sound. API 33+ also lets us
    // check the expected media route during silent gaps and before restarting.
    public static void RefreshExpectedRoute() => _devices?.RefreshExpectedRoute();

    public static PlayerRouteWatch WatchPlayer(MediaPlayer player) => new(player);

    private static void OnRouteDisconnected(ISettingsService settings)
    {
        var wasBlocked = State.IsBlocked;
        var requiredPlay = State.RequiresPlay;
        State.RouteDisconnected(settings.MobileAudio.OnDisconnect);
        PublishChange(wasBlocked, requiredPlay);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private sealed class CallModeWatch(AudioManager manager) : Java.Lang.Object, AudioManager.IOnModeChangedListener
    {
        private bool _disposed;

        public void OnModeChanged(int mode)
        {
            if (!_disposed)
                ReadCallMode(manager);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try { manager.RemoveOnModeChangedListener(this); }
                catch (Java.Lang.RuntimeException ex)
                {
                    AppLog.DebugWriteLine(() => $"Android audio-mode listener cleanup failed ({ex.GetType().Name}).");
                }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class NoisyReceiver(ISettingsService settings) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (!ReferenceEquals(_receiver, this) || intent?.Action != AudioManager.ActionAudioBecomingNoisy)
                return;
            if (Routes.BecomingNoisy())
                OnRouteDisconnected(settings);
        }
    }

    private sealed class DeviceWatcher : AudioDeviceCallback
    {
        private readonly AudioManager _manager;
        private readonly ISettingsService _settings;
        private readonly AudioAttributes _attributes;
        private readonly MediaRouter? _router;
        private readonly RouteSelectionWatcher? _selection;
        private PlayerRouteWatch? _audiblePlayer;
        private bool _disposed;

        public DeviceWatcher(AudioManager manager, ISettingsService settings, MediaRouter? router)
        {
            _manager = manager;
            _settings = settings;
            using var builder = new AudioAttributes.Builder();
            builder.SetUsage(AudioUsageKind.Media);
            builder.SetContentType(AudioContentType.Speech);
            _attributes = builder.Build() ?? throw new InvalidOperationException("Android media routing attributes are unavailable.");
            using var handler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            try { _manager.RegisterAudioDeviceCallback(this, handler); }
            catch
            {
                _attributes.Dispose();
                throw;
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && router != null)
            {
                var selection = new RouteSelectionWatcher(RefreshRoute);
                try
                {
                    // Observe local media selection without asking Android to
                    // discover or select devices on the user's behalf.
                    router.AddCallback(MediaRouteType.LiveAudio, selection);
                    _router = router;
                    _selection = selection;
                }
                catch (Java.Lang.RuntimeException ex)
                {
                    selection.Dispose();
                    AppLog.DebugWriteLine(() => $"Android media route selection callback unavailable ({ex.GetType().Name}).");
                }
            }
        }

        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices)
        {
            if (_disposed || addedDevices == null)
                return;
            Routes.DevicesAdded(GetPrivateIds(addedDevices));
            RefreshRoute();
        }

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices)
        {
            if (_disposed || removedDevices == null)
                return;
            var removed = GetPrivateIds(removedDevices);
            var connected = ConnectedPrivateIds();
            var lost = _audiblePlayer == null
                ? Routes.IdleDevicesRemoved(removed, connected, ReadExpectedRoute())
                : Routes.DevicesRemoved(removed, connected);
            if (lost)
                OnRouteDisconnected(_settings);
            RefreshRoute();
        }

        internal void RefreshRoute()
        {
            if (_audiblePlayer is { } player)
                player.ReadRoute();
            else
                RefreshExpectedRoute();
        }

        internal void BeginPlayback(PlayerRouteWatch owner) => _audiblePlayer = owner;

        internal void EndPlayback(PlayerRouteWatch owner)
        {
            if (!ReferenceEquals(_audiblePlayer, owner))
                return;
            _audiblePlayer = null;
            if (Routes.EnterIdle(ReadExpectedRoute(), ConnectedPrivateIds()))
                OnRouteDisconnected(_settings);
        }

        private int[]? ReadExpectedRoute()
        {
            if (_disposed || !OperatingSystem.IsAndroidVersionAtLeast(33))
                return null;
            try
            {
                var devices = _manager.GetAudioDevicesForAttributes(_attributes);
                return devices is { Count: > 0 } ? GetPrivateIds(devices) : null;
            }
            catch (Java.Lang.RuntimeException ex)
            {
                AppLog.DebugWriteLine(() => $"Expected Android media route unavailable ({ex.GetType().Name}).");
                return null;
            }
        }

        internal void RefreshExpectedRoute()
        {
            if (!_disposed && _audiblePlayer == null &&
                Routes.CheckIdleRoute(ReadExpectedRoute(), ConnectedPrivateIds()))
                OnRouteDisconnected(_settings);
        }

        internal void Observe(PlayerRouteWatch owner, IList<AudioDeviceInfo>? devices)
        {
            if (ReferenceEquals(_audiblePlayer, owner) && devices is { Count: > 0 } &&
                Routes.ObserveRoute(GetPrivateIds(devices), ConnectedPrivateIds()))
                OnRouteDisconnected(_settings);
        }

        private int[] ConnectedPrivateIds() => GetPrivateIds(_manager.GetDevices(GetDevicesTargets.Outputs) ?? []);

        private static int[] GetPrivateIds(IEnumerable<AudioDeviceInfo> devices) =>
            devices.Where(IsPrivateOutput).Select(device => device.Id).ToArray();

        private static bool IsPrivateOutput(AudioDeviceInfo device) =>
            device.IsSink &&
            (device.Type is AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset or AudioDeviceType.BluetoothA2dp ||
             OperatingSystem.IsAndroidVersionAtLeast(26) && device.Type == AudioDeviceType.UsbHeadset ||
             OperatingSystem.IsAndroidVersionAtLeast(28) && device.Type == AudioDeviceType.HearingAid ||
             OperatingSystem.IsAndroidVersionAtLeast(31) && device.Type == AudioDeviceType.BleHeadset);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (_selection != null)
                {
                    _router!.RemoveCallback(_selection);
                    _selection.Dispose();
                }
                _manager.UnregisterAudioDeviceCallback(this);
                _attributes.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class RouteSelectionWatcher(Action changed) : MediaRouter.SimpleCallback
    {
        public override void OnRouteSelected(MediaRouter? router, MediaRouteType type, MediaRouter.RouteInfo? info) => changed();
        public override void OnRouteChanged(MediaRouter? router, MediaRouter.RouteInfo? info) => changed();
    }

    internal sealed class PlayerRouteWatch : IDisposable
    {
        private readonly MediaPlayer _player;
        private bool _audible;
        private bool _hasRoute;
        private bool _disposed;

        public PlayerRouteWatch(MediaPlayer player)
        {
            _player = player;
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                _player.RoutingChanged += OnRoutingChanged;
        }

        public void SetAudible(bool audible)
        {
            _audible = audible;
            _hasRoute = false;
            if (audible)
            {
                _devices?.BeginPlayback(this);
                ReadInitialRoute();
            }
            else
                _devices?.EndPlayback(this);
        }

        public void ReadInitialRoute()
        {
            if (!_hasRoute && OperatingSystem.IsAndroidVersionAtLeast(28))
                ReadRoute();
        }

        private void OnRoutingChanged(object? sender, EventArgs args) => MainThread.BeginInvokeOnMainThread(ReadRoute);

        internal void ReadRoute()
        {
            if (_disposed || !_audible || _devices == null)
                return;
            if (!_player.IsPlaying)
            {
                _hasRoute = false;
                return;
            }
            IList<AudioDeviceInfo>? devices = OperatingSystem.IsAndroidVersionAtLeast(36) ? _player.RoutedDevices :
                OperatingSystem.IsAndroidVersionAtLeast(28) && _player.RoutedDevice is { } device ? [device] : null;
            _hasRoute = devices is { Count: > 0 };
            _devices.Observe(this, devices);
        }

        public void Dispose()
        {
            _disposed = true;
            SetAudible(false);
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                _player.RoutingChanged -= OnRoutingChanged;
        }
    }

    private sealed class RouteWatch(Context context) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (--_watchers != 0 || _receiver is not { } receiver)
                return;
            _receiver = null;
            context.UnregisterReceiver(receiver);
            receiver.Dispose();
            _devices?.Dispose();
            _devices = null;
            Routes.Reset();
            SetCallMode(Mode.Normal);
            RequestPlay();
        }
    }
}
