using Android.Content;
using Android.Media;
using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.Platforms.Android.Services;

internal static class AndroidAudioEnvironment
{
    private static readonly AndroidAudioInterruption State = new();
    private static TaskCompletionSource _changed = NewSignal();
    private static NoisyReceiver? _receiver;
    private static DeviceWatcher? _devices;
    private static int _watchers;

    public static bool RequiresPlay => State.RequiresPlay;
    public static bool IsBlocked => State.IsBlocked;
    public static Task Changed => _changed.Task;
    public static event Action? ResumeStateChanged;

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
                    _devices = new DeviceWatcher(manager, settings);
            }
            catch (Java.Lang.RuntimeException ex)
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
        manager.AddOnModeChangedListener(executor, watch);
        return watch;
    }

    private static void SetCallMode(Mode mode)
    {
        var wasBlocked = State.IsBlocked;
        var requiredPlay = State.RequiresPlay;
        State.SetCallActive(mode is Mode.InCall or Mode.InCommunication or Mode.Ringtone);
        PublishChange(wasBlocked, requiredPlay);
    }

    public static void BlockForFocus()
    {
        var wasBlocked = State.IsBlocked;
        var requiredPlay = State.RequiresPlay;
        State.BlockForFocus();
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
        if (wasBlocked != State.IsBlocked || requiredPlay != State.RequiresPlay)
        {
            var prior = _changed;
            _changed = NewSignal();
            prior.TrySetResult();
        }
        if (requiredPlay != State.RequiresPlay)
            ResumeStateChanged?.Invoke();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                manager.RemoveOnModeChangedListener(this);
            }
            base.Dispose(disposing);
        }
    }

    private sealed class NoisyReceiver(ISettingsService settings) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy)
                OnRouteDisconnected(settings);
        }
    }

    private sealed class DeviceWatcher : AudioDeviceCallback
    {
        private readonly AudioManager _manager;
        private readonly ISettingsService _settings;
        private bool _disposed;

        public DeviceWatcher(AudioManager manager, ISettingsService settings)
        {
            _manager = manager;
            _settings = settings;
            using var handler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _manager.RegisterAudioDeviceCallback(this, handler);
        }

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices)
        {
            // The noisy broadcast may be absent in a silent gap. Use the device list
            // for the last private output, so unplugging an unused secondary headset
            // does not pause an active private route.
            if (!_disposed && removedDevices?.Any(IsPrivateOutput) == true &&
                _manager.GetDevices(GetDevicesTargets.Outputs)?.Any(IsPrivateOutput) != true)
                OnRouteDisconnected(_settings);
        }

        private static bool IsPrivateOutput(AudioDeviceInfo device) =>
            device.IsSink &&
            (device.Type is AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset or AudioDeviceType.BluetoothA2dp ||
             OperatingSystem.IsAndroidVersionAtLeast(26) && device.Type == AudioDeviceType.UsbHeadset ||
             OperatingSystem.IsAndroidVersionAtLeast(31) && device.Type == AudioDeviceType.BleHeadset);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _manager.UnregisterAudioDeviceCallback(this);
            }
            base.Dispose(disposing);
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
            RequestPlay();
        }
    }
}
