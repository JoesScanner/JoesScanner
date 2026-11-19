using JoesScanner.Models;
using JoesScanner.Services;

internal static class IosAudioTests
{
    internal static void Run()
    {
        var activations = new List<IosAudioOptions>();
        var releases = 0;
        var activationAllowed = true;
        var session = new IosClipSession(options =>
        {
            activations.Add(options);
            return activationAllowed;
        }, () => releases++);

        var mixed = session.TryStart(MobileAudioMode.MixNormally);
        Check(session.ActiveMode == MobileAudioMode.MixNormally && activations[^1] == IosAudioOptions.Mix,
            "Mix normally should activate a mixing-only session.");
        session.Release(mixed);
        var ducked = session.TryStart(MobileAudioMode.MixAndDuck);
        Check(activations[^1] == (IosAudioOptions.Mix | IosAudioOptions.Duck),
            "Mix and duck should use a mixing session with temporary ducking.");
        session.Release(ducked);
        Check(releases == 2 && session.ActiveMode == null,
            "A completed ducking clip must immediately release its session.");
        var exclusive = session.TryStart(MobileAudioMode.Exclusive);
        Check(activations[^1] == IosAudioOptions.None && session.ActiveMode == MobileAudioMode.Exclusive,
            "Exclusive should activate without mixing options.");
        session.Release(ducked);
        Check(releases == 2 && session.ActiveMode == MobileAudioMode.Exclusive,
            "A stale callback must not release the next clip's session.");
        session.Release(exclusive);
        session.Release(exclusive);
        Check(releases == 3, "Consecutive recordings must each release exactly once.");
        activationAllowed = false;
        Check(session.TryStart(MobileAudioMode.Exclusive) == 0 && session.ActiveMode == null,
            "A rejected activation must never own or start a clip.");
        Console.WriteLine("PASS iOS modes, clip-only ducking, and stale session release");

        activationAllowed = true;
        var suspension = new IosAudioSuspension();
        var queue = new Queue<int>([1, 2, 3]);
        var recording = queue.Peek();
        var interrupted = session.TryStart(MobileAudioMode.MixAndDuck);
        Check(suspension.BeginInterruption() && !suspension.BeginInterruption(),
            "A duplicate begin must stay in the same interruption.");
        session.Invalidate();
        session.Release(interrupted);
        Check(suspension.IsBlocked && queue.Peek() == recording && releases == 3,
            "A phone interruption must preserve the current recording and the OS-owned deactivation.");
        suspension.EndInterruption(shouldResume: true);
        Check(!suspension.IsBlocked && queue.Peek() == recording,
            "Resume should continue the interrupted recording.");
        suspension.EndInterruption(shouldResume: false);
        Check(!suspension.IsBlocked, "A stale end callback must not block resumed playback.");
        suspension.BeginInterruption();
        suspension.Foreground();
        Check(!suspension.IsBlocked, "Foreground recovery should allow iOS to arbitrate after a missing end.");
        suspension.BeginInterruption();
        suspension.EndInterruption(shouldResume: false);
        suspension.Foreground();
        Check(suspension.RequiresPlay, "A denied resume must require user action, including after foregrounding.");
        suspension.RequestPlay();
        Check(queue.Dequeue() == recording && queue.SequenceEqual([2, 3]),
            "Only completion should move past the interrupted recording.");
        Console.WriteLine("PASS iOS interrupted clip, duplicate or missing notifications, and queue boundary");

        var routes = new IosPrivateAudioRoutes();
        Check(!routes.DeviceUnavailable(["bluetooth"], ["bluetooth", "speaker"]),
            "A private output still present is not a disconnection.");
        Check(routes.DeviceUnavailable(["bluetooth"], ["speaker"]) &&
              !routes.DeviceUnavailable(["bluetooth"], ["speaker"]),
            "A Bluetooth disconnect and its duplicate should produce one event.");
        suspension.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
        suspension.BeginInterruption();
        suspension.EndInterruption(shouldResume: true);
        suspension.Foreground();
        Check(suspension.IsBlocked && suspension.RequiresPlay,
            "A route pause must survive call recovery and require an explicit action.");
        suspension.RequestPlay();
        routes.DeviceAvailable();
        Check(routes.DeviceUnavailable(["wired"], ["speaker"]), "A wired headset removal should be handled between clips.");
        suspension.UnreportedPause();
        suspension.RouteDisconnected(AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker);
        Check(!suspension.IsBlocked && suspension.RouteGeneration == 2,
            "A verified disconnect in Continue mode should resolve AVPlayer's automatic pause.");
        Console.WriteLine("PASS iOS wired and Bluetooth disconnection, duplicate events, and route preferences");

        var suspendedRoutes = new IosPrivateAudioRoutes();
        suspendedRoutes.AcceptCurrentRoute(["wired"]);
        Check(!suspendedRoutes.Reconcile(["wired"], AudioDisconnectionBehavior.PausePlayback),
            "A confirmed private route should still be usable.");
        Check(suspendedRoutes.Reconcile([], AudioDisconnectionBehavior.PausePlayback) &&
              suspendedRoutes.Reconcile([], AudioDisconnectionBehavior.PausePlayback),
            "A missing route event or initially unknown output must not erase the protected route.");
        suspension.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
        suspension.Foreground();
        Check(suspension.RequiresPlay, "Foreground activation cannot release the privacy pause.");
        suspendedRoutes.AcceptCurrentRoute([]);
        suspension.RequestPlay();
        Check(!suspendedRoutes.Reconcile([], AudioDisconnectionBehavior.PausePlayback) && !suspension.RequiresPlay,
            "An explicit Play can accept the new speaker route.");
        suspendedRoutes.AcceptCurrentRoute(["bluetooth"]);
        Check(!suspendedRoutes.Reconcile([], AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker),
            "The continue preference should allow a missing Bluetooth output.");
        Console.WriteLine("PASS iOS privacy survives suspension with missing route notifications");

        var playback = new LivePlaybackRequests();
        using var request = playback.Begin(manual: false);
        suspension.BeginInterruption();
        playback.CancelCurrent();
        suspension.Stop();
        suspension.EndInterruption(shouldResume: true);
        Check(!request.CanContinue, "A late interruption end must not restart explicitly stopped playback.");

        var generation = suspension.MediaGeneration;
        suspension.MediaServicesLost();
        suspension.RequestPlay();
        Check(suspension.IsBlocked, "Explicit Play cannot start until iOS media services are available.");
        suspension.MediaServicesReset();
        suspension.MediaServicesReset();
        suspension.Foreground();
        Check(suspension.RequiresPlay && suspension.MediaGeneration == generation + 1,
            "A media reset must invalidate the old player once and wait for explicit Play.");
        suspension.RequestPlay();
        Check(!suspension.IsBlocked, "Explicit Play should create a new player after a media reset.");
        Console.WriteLine("PASS iOS media reset and explicit Stop during interruption");

        var recovery = new LiveStreamRecovery();
        var since = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_800);
        var signal = recovery.Pending;
        recovery.Request(since.AddMinutes(1));
        recovery.Request(since);
        Check(signal.IsCompletedSuccessfully && recovery.Take() == DateTimeOffset.FromUnixTimeSeconds(since.ToUnixTimeSeconds()),
            "Foreground recovery should wake the socket and keep the earliest eligible boundary.");
        Check(!recovery.Pending.IsCompleted && recovery.Take() == null && new LiveStreamRecovery().Take() == null,
            "A handled recovery must not repeat or carry into a new monitoring run.");
        Console.WriteLine("PASS iOS foreground recovery coalescing and per-run isolation");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
