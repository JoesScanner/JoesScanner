using JoesScanner.Models;
using JoesScanner.Services;

internal static class AndroidAudioTests
{
    public static async Task RunAsync()
    {
        FocusDenialAndMixedMode();
        FocusLossAndGains();
        PhoneInterruptionOrdering();
        RouteSelectionAndDuplicateEvents();
        IdleRouteTransitions();
        ForegroundFailureAndReentry();
        await BufferingAndPositionWatchdogsAsync();
        await ManualSkipAndStopDuringInterruptionAsync();
    }

    private static void FocusDenialAndMixedMode()
    {
        var denied = new AndroidClipFocusState();
        denied.Requested(registered: false);
        denied.OnChange(AndroidClipFocusStatus.Granted);
        Check(denied.Status == AndroidClipFocusStatus.Denied && !denied.Release(),
            "Denied focus must neither permit playback nor abandon an unregistered request.");
        var mixed = new AndroidClipFocusState();
        mixed.ReadyWithoutFocus();
        Check(mixed.Status == AndroidClipFocusStatus.Granted && !mixed.Release(),
            "Mix normally should play without requesting focus.");
        Console.WriteLine("PASS Android focus denial and playback without focus");
    }

    private static void FocusLossAndGains()
    {
        var audio = new AndroidAudioInterruption();
        var focus = new AndroidClipFocusState();
        focus.Requested(registered: true, delayed: true);
        var pending = focus.Changed;
        Check(audio.GetFocusAction(focus.Status, audio.CallRevision) == AndroidFocusAttemptAction.Wait,
            "Delayed focus must wait for gain.");

        focus.OnChange(AndroidClipFocusStatus.TransientLoss);
        Check(pending.IsCompleted && audio.GetFocusAction(focus.Status, audio.CallRevision) == AndroidFocusAttemptAction.Wait,
            "A transient loss may arrive before a delayed gain.");
        var transient = focus.Changed;
        focus.OnChange(AndroidClipFocusStatus.Granted);
        Check(transient.IsCompleted && audio.GetFocusAction(focus.Status, audio.CallRevision) == AndroidFocusAttemptAction.Play,
            "Gain must wake the same request after a transient loss.");
        focus.OnChange(AndroidClipFocusStatus.DuckableLoss);
        Check(audio.GetFocusAction(focus.Status, audio.CallRevision) == AndroidFocusAttemptAction.Wait,
            "Spoken scanner audio should pause on a duckable loss and wait for gain.");
        focus.OnChange(AndroidClipFocusStatus.Granted);
        Check(audio.GetFocusAction(focus.Status, audio.CallRevision) == AndroidFocusAttemptAction.Play,
            "Navigation speech must not leave playback stuck.");

        focus.OnChange(AndroidClipFocusStatus.PermanentLoss);
        focus.OnChange(AndroidClipFocusStatus.TransientLoss);
        focus.OnChange(AndroidClipFocusStatus.Granted);
        Check(audio.GetFocusAction(focus.Status, audio.CallRevision) == AndroidFocusAttemptAction.Block,
            "A continued obsolete callback must not override permanent loss.");
        Check(focus.Release() && !focus.Release(), "The original request must be abandoned exactly once.");
        Console.WriteLine("PASS Android distinguishes transient, duckable and permanent focus loss");
    }

    private static void PhoneInterruptionOrdering()
    {
        var audio = new AndroidAudioInterruption();
        var original = audio.CallRevision;
        audio.BlockForFocus(original);
        Check(audio.RequiresPlay, "Unexplained permanent loss or denial needs explicit Play.");
        audio.SetCallActive(true);
        Check(audio.IsBlocked && !audio.RequiresPlay, "A subsequent call-mode signal accounts for the earlier focus loss.");
        audio.SetCallActive(false);
        Check(!audio.IsBlocked, "Ending the call must allow the same recording to reacquire focus.");
        audio.BlockForFocus(original);
        Check(!audio.IsBlocked, "A delayed loss from a request that spanned the ended call permits one retry.");
        audio.BlockForFocus(audio.CallRevision);
        Check(audio.RequiresPlay, "A fresh denial must not become a focus request loop.");

        audio.RequestPlay();
        original = audio.CallRevision;
        audio.SetCallActive(true);
        Check(audio.GetFocusAction(AndroidClipFocusStatus.Granted, original) == AndroidFocusAttemptAction.Renew,
            "Even a focus gain cannot allow playback during an observed call.");
        audio.SetCallActive(false);
        Check(audio.GetFocusAction(AndroidClipFocusStatus.TransientLoss, original) == AndroidFocusAttemptAction.Renew,
            "If Android did not deliver Gain at call end, start one new request.");
        Check(audio.GetFocusAction(AndroidClipFocusStatus.Pending, original) == AndroidFocusAttemptAction.Renew,
            "A delayed request that still has no gain at call end gets the same single renewal.");
        Check(audio.GetFocusAction(AndroidClipFocusStatus.TransientLoss, audio.CallRevision) == AndroidFocusAttemptAction.Wait,
            "A following transient interruption waits for gain without retrying in a loop.");
        Console.WriteLine("PASS Android phone-mode and focus events recover in either order");
    }

    private static void RouteSelectionAndDuplicateEvents()
    {
        var routes = new AndroidAudioRoutes();
        var audio = new AndroidAudioInterruption();
        Check(!routes.ObserveRoute([101], [101, 202]), "Remember the actual Bluetooth route.");
        Check(!routes.DevicesRemoved([202], [101]), "Removing an unused wired headset must not pause Bluetooth playback.");
        Check(routes.BecomingNoisy(), "The first warning for the current private route must pause before speaker playback.");
        audio.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
        Check(!routes.DevicesRemoved([101], [202]), "Audio-device removal must not repeat the noisy broadcast for the same route.");
        audio.SetCallActive(true);
        audio.SetCallActive(false);
        Check(audio.RequiresPlay, "Phone-call recovery cannot override the route preference.");
        audio.RequestPlay();
        Check(!routes.BecomingNoisy() && !audio.IsBlocked, "A late duplicate cannot pause again after explicit Play.");
        Check(!routes.ObserveRoute([], [202]) && !routes.BecomingNoisy(),
            "The eventual fallback and its duplicate cannot pause a second time.");

        routes.DevicesAdded([101]);
        Check(!routes.ObserveRoute([202], [202, 101]), "Track a new wired route while Bluetooth stays connected.");
        Check(routes.ObserveRoute([101], [101]),
            "Losing the active wired headset must be detected even when Android routes to another private output.");
        audio.RouteDisconnected(AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker);
        Check(!audio.IsBlocked && !routes.DevicesRemoved([202], [101]), "Continue allows fallback without a second event.");

        var legacy = new AndroidAudioRoutes();
        Check(!legacy.DevicesRemoved([101], [202]) && legacy.DevicesRemoved([202], []),
            "With no reliable route, the device-list fallback only considers loss of the last private output.");
        Check(!legacy.BecomingNoisy(), "The legacy fallback must deduplicate a following noisy broadcast.");
        Console.WriteLine("PASS Android actual Bluetooth and wired routes, legacy fallback, and duplicate disconnect events");
    }

    private static void IdleRouteTransitions()
    {
        var routes = new AndroidAudioRoutes();
        Check(!routes.ObserveRoute([101], [101, 202]), "An audible clip is on Bluetooth.");
        Check(!routes.EnterIdle([202], [101, 202]) && !routes.IdleDevicesRemoved([101], [202], [202]),
            "The previous Bluetooth device can disconnect while a different output is selected for the next clip.");
        Check(!routes.CheckIdleRoute([], [202]) && !routes.IdleDevicesRemoved([202], [], []),
            "A confirmed speaker selection while the headset was still attached rules out an active-route loss.");

        routes.Reset();
        Check(!routes.EnterIdle([101], [101]) && routes.CheckIdleRoute([], []),
            "The pre-start refresh must catch a genuinely lost idle private route even if its callback is delayed.");
        Check(!routes.IdleDevicesRemoved([101], [], []) && !routes.BecomingNoisy(),
            "A delayed inventory callback and noisy broadcast must be deduplicated.");

        routes.Reset();
        Check(!routes.ObserveRoute([101], [101, 202]) && !routes.EnterIdle(null, [101, 202]) &&
              !routes.IdleDevicesRemoved([101], [202], null),
            "On older Android, the last audible route is discarded at idle and a remaining private output is not treated as lost.");
        Check(routes.CheckIdleRoute(null, []) && !routes.BecomingNoisy(),
            "When legacy route identity is unknown, losing the final private device still protects against speaker fallback.");
        Console.WriteLine("PASS Android idle route refresh distinguishes speaker selection, unused Bluetooth, and legacy privacy fallback");
    }

    private static void ForegroundFailureAndReentry()
    {
        var state = new AndroidForegroundOwnership();
        state.SetActivityVisible(true);
        state.Begin(1);
        Check(state.TryRequest() && !state.CanUpdateNotification(1),
            "The service must publish the initial media notification while promoting; the pending run may not post an update first.");
        state.SetActivityVisible(false);
        var promotion = state.Changed;
        state.RequestFailed(1);
        Check(promotion.IsCompleted && state.StartFailed && !state.IsReady && !state.CanPlay(requiresForegroundService: true),
            "Promotion failure must wake the player without permitting background playback.");
        Check(!state.TryRequest(), "Do not start a replacement service from the background.");

        var visibility = state.Changed;
        state.SetActivityVisible(true);
        Check(visibility.IsCompleted && state.CanPlay(requiresForegroundService: true) && state.TryRequest(),
            "Returning to the visible activity must permit audio again and retry promotion.");
        Check(!state.CanPlay(requiresForegroundService: true, requiresTopActivity: true),
            "A visible but non-top activity cannot request focus on recent Android without the service.");
        state.SetTopActivity(true);
        Check(state.CanPlay(requiresForegroundService: true, requiresTopActivity: true),
            "The top activity may play while promotion is pending.");
        state.SetActivityVisible(false);
        Check(!state.CanPlay(requiresForegroundService: true), "An accepted start is not yet a promoted service.");
        var service = new object();
        Check(state.Promote(1, service) && state.OwnsService(1, service) && state.CanUpdateNotification(1) &&
            state.CanPlay(requiresForegroundService: true) && !state.StartFailed,
            "Promotion may complete after locking if the request was made while visible.");
        state.Destroy(1, service);
        Check(!state.IsReady && !state.CanPlay(requiresForegroundService: true), "Losing the service must suspend the pending recording again.");
        state.Begin(-1);
        state.RequestFailed(1);
        Check(!state.StartFailed && !state.IsReady && !state.OwnsService(1, service) && !state.CanUpdateNotification(1),
            "Standalone playback cannot inherit a previous run's service, notification, or failure.");
        Console.WriteLine("PASS Android foreground-service failure, reentry, lock, destruction, and standalone ownership");
    }

    private static async Task BufferingAndPositionWatchdogsAsync()
    {
        static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);
        var progress = new AndroidPlaybackProgress(100);
        progress.BufferingChanged(true, Seconds(0.5));
        Check(!progress.IsStalled(100, Seconds(3.9)), "A short buffering gap keeps the same audible attempt.");
        progress.BufferingChanged(false, Seconds(4));
        Check(!progress.IsStalled(110, Seconds(4.1)), "Progress after buffering must preserve the recording.");
        progress.BufferingChanged(true, Seconds(5));
        progress.BufferingChanged(true, Seconds(7));
        Check(progress.IsStalled(110, Seconds(9)), "Duplicate buffering-start callbacks must not extend the silent interval.");
        var withoutCallbacks = new AndroidPlaybackProgress(300);
        Check(withoutCallbacks.IsStalled(300, Seconds(8)), "A silent player without buffering callbacks must also release focus.");
        Check(!new AndroidPlaybackProgress(300).IsStalled(310, Seconds(8)), "Real position progress must keep focus.");

        var focus = new AndroidClipFocusState();
        focus.Requested(registered: true);
        int releases = 0;
        var outcome = await PlaybackAttempt.RunAsync(() =>
        {
            try
            {
                if (progress.IsStalled(110, Seconds(9)))
                    throw new TimeoutException();
                return Task.CompletedTask;
            }
            finally
            {
                if (focus.Release())
                    releases++;
            }
        }, CancellationToken.None, _ => { });
        Check(releases == 1 && outcome == PlaybackOutcome.Failed && !outcome.AdvancesQueue(),
            "A stalled attempt releases its own focus and leaves the recording pending for retry.");
        Console.WriteLine("PASS Android buffering stalls release focus and failed playback retains the queued recording");
    }

    private static async Task ManualSkipAndStopDuringInterruptionAsync()
    {
        var lifecycle = new MonitoringLifecycle();
        var run = lifecycle.TryBeginStart()!;
        lifecycle.Activate(run);
        var playback = new LivePlaybackRequests();
        using var old = playback.Begin(manual: false);
        var oldFocus = new AndroidClipFocusState();
        oldFocus.Requested(registered: true, delayed: true);
        oldFocus.OnChange(AndroidClipFocusStatus.TransientLoss);
        var blocked = oldFocus.Changed;
        var oldOutcome = PlaybackAttempt.RunAsync(() => blocked.WaitAsync(old.Token), old.Token, _ => { });
        using var next = playback.Begin(manual: true);
        Check(oldFocus.Release(), "Selecting another call must release the outgoing focus request.");
        Check(!oldFocus.OnChange(AndroidClipFocusStatus.Granted) &&
              !oldFocus.OnChange(AndroidClipFocusStatus.DuckableLoss),
            "A retired focus listener must not publish any new transition during a manual handoff.");
        Check(!blocked.IsCompleted && !(await oldOutcome).AdvancesQueue() && next.CanContinue,
            "Late outgoing gain must not play or advance an obsolete recording.");

        var nextFocus = new AndroidClipFocusState();
        nextFocus.Requested(registered: true, delayed: true);
        var outcome = PlaybackAttempt.RunAsync(() => nextFocus.Changed.WaitAsync(next.Token), next.Token, _ => { });
        await lifecycle.StopAsync(_ => playback.RetireCurrent(), _ => { }, _ => Task.CompletedTask);
        nextFocus.OnChange(AndroidClipFocusStatus.Granted);
        Check(!(await outcome).AdvancesQueue() && !next.CanContinue && !lifecycle.CanRecover(run),
            "Explicit Stop must win over a late gain and prevent automatic queue recovery.");
        Check(nextFocus.Release() && !nextFocus.Release(), "Cancellation must abandon only the interrupted clip's registration.");
        Console.WriteLine("PASS Android rapid selection and Stop win over delayed gain without advancing the queue");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
