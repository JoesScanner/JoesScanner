using JoesScanner.Models;
using JoesScanner.Services;

internal static class IosAudioTests
{
    internal static async Task RunAsync()
    {
        RecordingModesAndLeaseOwnership();
        LongRecordingStopsDucking();
        FailedDeactivation();
        BoundedReleaseRetries();
        await StalledPlaybackRecoveryAsync();
        MissingAndOutOfOrderInterruptions();
        await ItemFailureWhileInterruptedAsync();
        await ResumeRecordingAndAdvanceAtBoundaryAsync();
        PrivateRouteRecovery();
        await StopAndReplaceAsync();
        MediaResetAndPlayerRebuild();
        await ForegroundSocketRecoveryAsync();
    }

    private static void LongRecordingStopsDucking()
    {
        var calls = new List<string>();
        var session = new IosClipSession(options => { calls.Add(options.ToString()); return true; },
            () => { calls.Add("release"); return true; });
        var recording = new IosRecordingState();
        var selected = MobileAudioMode.MixAndDuck;
        var first = session.TryStart(recording.ModeForActivation(selected));
        recording.Activated(selected);
        recording.Observe(1.25);

        session.Release(first);
        recording.EndDuckedSegment(TimeSpan.FromSeconds(1), windowExpired: false);
        Check(recording.DuckingRemaining(TimeSpan.Zero) == TimeSpan.FromSeconds(2),
            "An interruption must not reset the recording's ducking window.");

        var resumed = session.TryStart(recording.ModeForActivation(selected));
        session.Release(resumed);
        recording.EndDuckedSegment(TimeSpan.FromSeconds(2), windowExpired: true);
        var continuation = session.TryStart(recording.ModeForActivation(MobileAudioMode.Exclusive));
        session.Release(resumed);
        Check(session.Owns(continuation) && session.ActiveMode == MobileAudioMode.MixNormally &&
              recording.SelectedMode(MobileAudioMode.Exclusive) == selected && recording.SeekTarget(0) == 1.25 &&
              calls.SequenceEqual(["Mix, Duck", "release", "Mix, Duck", "release", "Mix"]),
            "A long recording must release ducking and continue in plain mixing with its selected mode and position preserved.");

        session.Release(continuation);
        var next = new IosRecordingState();
        Check(next.DuckingRemaining(TimeSpan.Zero) == TimeSpan.FromSeconds(3) &&
              next.ModeForActivation(selected) == selected,
            "Each new recording receives its own ducking window.");
        Console.WriteLine("PASS iOS bounded ducking and plain-mix continuation of the same recording");
    }

    private static async Task ItemFailureWhileInterruptedAsync()
    {
        var state = new IosAudioSuspension();
        state.BeginInterruption();
        var changed = new TaskCompletionSource();
        var finished = new TaskCompletionSource();
        var failure = new TaskCompletionSource();
        var wait = state.WaitUntilPlayableAsync(() => changed.Task, default, Task.WhenAny(finished.Task, failure.Task));
        Check(!wait.IsCompleted, "An interrupted recording must wait before attempting playback.");
        failure.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Check(state.IsBlocked && !finished.Task.IsCompleted,
            "A failed item must wake the wait and remain failed while the audio session is still blocked.");
        Console.WriteLine("PASS iOS item failure wakes interruption wait without permitting playback");
    }

    private static void RecordingModesAndLeaseOwnership()
    {
        var events = new List<string>();
        var activationAllowed = true;
        var session = new IosClipSession(options =>
        {
            events.Add(options.ToString());
            return activationAllowed;
        }, () => { events.Add("release"); return true; });

        var mixed = session.TryStart(MobileAudioMode.MixNormally);
        Check(session.ActiveMode == MobileAudioMode.MixNormally && events[^1] == "Mix",
            "Mix normally must activate with only the mix option.");
        session.Release(mixed);

        var item = new IosRecordingState();
        var selectedMode = item.ModeForActivation(MobileAudioMode.MixAndDuck);
        activationAllowed = false;
        Check(session.TryStart(selectedMode) == 0 && session.ActiveMode == null &&
              item.ModeForActivation(MobileAudioMode.MixNormally) == MobileAudioMode.MixNormally,
            "A denied activation must neither own a lease nor lock that recording's mode.");
        activationAllowed = true;
        var ducked = session.TryStart(selectedMode);
        item.Activated(selectedMode);
        Check(session.ActiveMode == MobileAudioMode.MixAndDuck &&
              item.ModeForActivation(MobileAudioMode.Exclusive) == MobileAudioMode.MixAndDuck,
            "A setting change cannot alter the active recording or its resumed segments.");
        session.Release(ducked);
        var resumed = session.TryStart(item.ModeForActivation(MobileAudioMode.Exclusive));
        Check(events.TakeLast(3).SequenceEqual(["Mix, Duck", "release", "Mix, Duck"]),
            "Ducking must be released before the next activation, including when resuming.");
        session.Release(ducked);
        Check(session.Owns(resumed) && events[^1] == "Mix, Duck", "Old release callbacks cannot unduck a new segment.");
        session.Release(resumed);

        var nextDucked = session.TryStart(new IosRecordingState().ModeForActivation(MobileAudioMode.MixAndDuck));
        session.Release(nextDucked);
        Check(events.TakeLast(4).SequenceEqual(["Mix, Duck", "release", "Mix, Duck", "release"]),
            "Two consecutive ducked recordings each release priority.");
        var nextExclusive = session.TryStart(new IosRecordingState().ModeForActivation(MobileAudioMode.Exclusive));
        Check(events[^1] == "None" && session.ActiveMode == MobileAudioMode.Exclusive, "The next recording uses the new mode.");
        session.Release(nextExclusive);
        Check(events[^1] == "release" && session.ActiveMode == null, "Exclusive priority ends between recordings.");
        Console.WriteLine("PASS iOS clip-scoped mixing, back-to-back ducking, stale release, and settings boundaries");
    }

    private static void FailedDeactivation()
    {
        var deactivationAllowed = false;
        var deactivations = 0;
        var activations = 0;
        var session = new IosClipSession(_ => { activations++; return true; }, () =>
        {
            deactivations++;
            return deactivationAllowed;
        });
        var outgoing = session.TryStart(MobileAudioMode.MixAndDuck);
        Check(!session.Release(outgoing), "A failed release must leave native cleanup pending.");
        Check(session.TryStart(MobileAudioMode.Exclusive) == 0 && activations == 1,
            "A replacement cannot change category until the preceding deactivation succeeds.");
        deactivationAllowed = true;
        Check(session.FinishDeactivation(outgoing), "The original release can be retried.");
        var replacement = session.TryStart(MobileAudioMode.Exclusive);
        Check(session.FinishDeactivation(outgoing) && session.Owns(replacement) && deactivations == 3,
            "A queued cleanup callback cannot deactivate a newer recording.");
        session.Release(replacement);
        Console.WriteLine("PASS iOS failed session release is retried before a new category can activate");
    }

    private static void BoundedReleaseRetries()
    {
        var calls = new List<string>();
        var session = new IosClipSession(options => { calls.Add(options.ToString()); return true; },
            () => { calls.Add("deactivate"); return false; },
            () => { calls.Add("plain mix"); return true; });
        var old = session.TryStart(MobileAudioMode.MixAndDuck);
        Check(!session.Release(old), "A deactivation failure must retain lease-owned cleanup.");
        var delays = new List<TimeSpan>();
        while (session.NextReleaseDelay(old) is { } delay)
        {
            delays.Add(delay);
            session.RetryRelease(old);
        }
        Check(delays.Count == 4 && delays.SequenceEqual(delays.Order()) &&
              calls.Count(call => call == "plain mix") == 1 && session.PendingRelease == 0,
            "The bounded backoff must fall back to a non-ducking category after native release retries fail.");

        var newer = session.TryStart(MobileAudioMode.MixAndDuck);
        var callsBeforeStaleRetry = calls.Count;
        session.RetryRelease(old);
        Check(session.Owns(newer) && session.ActiveMode == MobileAudioMode.MixAndDuck &&
              calls.Count == callsBeforeStaleRetry,
            "A delayed release must not deactivate or neutralize the next ducking lease.");

        var stuckAttempts = 0;
        var stuck = new IosClipSession(_ => true, () => { ++stuckAttempts; return false; }, () => false);
        var stuckLease = stuck.TryStart(MobileAudioMode.Exclusive);
        stuck.Release(stuckLease);
        for (var retry = 0; retry < 10; retry++)
            stuck.RetryRelease(stuckLease);
        Check(stuckAttempts == 5 && stuck.PendingRelease == stuckLease && stuck.NextReleaseDelay(stuckLease) == null,
            "If the OS rejects both deactivation and plain mixing, idle retries must stop at the budget.");
        Console.WriteLine("PASS iOS bounded session-release retry, plain-mixing fallback, and stale lease fencing");
    }

    private static async Task StalledPlaybackRecoveryAsync()
    {
        static TimeSpan Elapsed(double seconds) => TimeSpan.FromSeconds(seconds);
        var progress = new IosPlaybackProgress(12.5);
        Check(!progress.IsStalled(12.5, waiting: true, Elapsed(0.25)) &&
              !progress.IsStalled(12.5, waiting: true, Elapsed(1.9)) &&
              progress.IsStalled(12.501, waiting: true, Elapsed(2.3)),
            "WaitingToPlayAtSpecifiedRate with no meaningful progress must quickly release priority.");
        var briefWait = new IosPlaybackProgress(1);
        Check(!briefWait.IsStalled(1, true, Elapsed(0)) && !briefWait.IsStalled(1.25, false, Elapsed(1.9)),
            "A brief buffer wait that recovers must keep the active lease.");
        Check(new IosPlaybackProgress(12.5).IsStalled(12.49, waiting: false, Elapsed(5.1)),
            "A player reporting Playing with a stationary or invalid timestamp must also time out.");

        var recording = new IosRecordingState();
        var released = 0;
        var session = new IosClipSession(_ => true, () => { released++; return true; });
        var mode = MobileAudioMode.MixAndDuck;
        var lease = session.TryStart(mode);
        recording.Activated(mode);
        recording.Observe(12.5);
        var retiredItem = new PlaybackCompletion();
        Check(retiredItem.Reject() && session.Release(lease) && released == 1 &&
              recording.TryRecoverFromStall() && recording.SeekTarget(0) == 12.5,
            "Retire the stalled item before releasing priority and retain its position for one rebuild.");
        var latest = session.TryStart(recording.ModeForActivation(MobileAudioMode.Exclusive));
        Check(session.Owns(latest) && session.ActiveMode == mode && !retiredItem.Confirm(default) &&
              !recording.TryRecoverFromStall(),
            "The resumed clip keeps its original mode and bounded recovery, and the old item cannot signal completion.");
        session.Release(latest);

        var requests = new LivePlaybackRequests();
        using var original = requests.Begin(manual: false);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = PlaybackAttempt.RunAsync(() => paused.Task.WaitAsync(original.Token), original.Token,
            _ => throw new InvalidOperationException("Expected cancellation after stopping a stalled clip."));
        requests.RetireCurrent();
        Check(await retry == PlaybackOutcome.Canceled, "Explicit Stop must cancel the pending rebuild without advancing the queue.");
        using var manual = requests.Begin(manual: true);
        session.RetryRelease(lease);
        Check(manual.CanContinue && !original.CanContinue, "An old cleanup cannot supersede a rapid manual selection.");
        var failed = await PlaybackAttempt.RunAsync(() => Task.FromException(new TimeoutException()), manual.Token, _ => {});
        Check(failed == PlaybackOutcome.Failed && !failed.AdvancesQueue(),
            "A stall after the recovery budget must leave the recording pending.");
        Console.WriteLine("PASS iOS waiting and no-progress stalls release the lease, preserve position, and honor Stop");
    }

    private static void MissingAndOutOfOrderInterruptions()
    {
        var state = new IosAudioSuspension();
        state.RecommendResume(true);
        Check(state.BeginInterruption() && !state.BeginInterruption() && state.IsBlocked,
            "An unpaired early end cannot authorize playback after a later begin, and duplicate begins coalesce.");
        state.Reconcile(canRetryActivation: false);
        Check(state.IsBlocked, "Foregrounding alone cannot authorize audio when higher-priority audio is still possible.");
        state.Reconcile(canRetryActivation: true);
        Check(!state.IsBlocked && state.NeedsReconciliation, "An eligible foreground grants a probe for a missing legacy end.");
        state.ActivationDenied();
        Check(state.IsBlocked && state.RequiresPlay, "A rejected probe leaves the recording pending without spinning.");

        state.SetCallActive(true);
        state.RecommendResume(true);
        Check(state.IsBlocked, "Even a positive resumption recommendation cannot bypass an observed call.");
        state.SetCallActive(false);
        Check(!state.IsBlocked, "The same recording may try to activate after the call ends.");
        state.ActivationSucceeded();
        state.RecommendResume(false);
        Check(!state.IsBlocked && !state.NeedsReconciliation, "A stale negative recommendation cannot pause a resumed item.");

        state.SetCallActive(true);
        state.SetCallActive(false);
        Check(state.IsBlocked, "CallKit alone must keep the recording pending until safe reconciliation.");
        state.Reconcile(canRetryActivation: true);
        Check(!state.IsBlocked, "An observed call ending is a fallback when neither native interruption notification arrives.");
        state.ActivationSucceeded();
        state.UnreportedPause();
        state.RecommendResume(false);
        state.Reconcile(canRetryActivation: true);
        state.RecommendResume(true);
        Check(state.RequiresPlay && state.IsBlocked, "A negative recommendation requires explicit action despite later foregrounding.");
        state.RequestPlay();
        Check(!state.IsBlocked, "Explicit Play can ask iOS to arbitrate again.");
        Console.WriteLine("PASS iOS missing, duplicate, and reordered interruption signals require safe activation");
    }

    private static async Task ResumeRecordingAndAdvanceAtBoundaryAsync()
    {
        var session = new IosClipSession(_ => true, () => true);
        var state = new IosAudioSuspension();
        var position = new IosRecordingState();
        var queue = new Queue<int>([10, 11, 12]);
        var finished = new TaskCompletionSource();
        var recording = PlaybackAttempt.RunAsync(() => finished.Task, CancellationToken.None, _ => { });
        var outgoing = session.TryStart(MobileAudioMode.MixAndDuck);
        position.Observe(5.4);
        state.BeginInterruption();
        session.Invalidate();
        session.Release(outgoing);
        Check(state.IsBlocked && !recording.IsCompleted && queue.Peek() == 10, "An interrupted segment cannot complete its recording.");
        state.RecommendResume(true);
        var resumed = session.TryStart(MobileAudioMode.MixAndDuck);
        state.ActivationSucceeded();
        Check(session.Owns(resumed) && position.SeekTarget(5.4) == null && position.SeekTarget(0) == 5.4,
            "Resume the same prepared player or seek a rebuilt player to the saved position.");

        state.BeginInterruption();
        session.Invalidate();
        finished.SetResult();
        if ((await recording).AdvancesQueue())
            queue.Dequeue();
        Check(queue.Peek() == 11 && state.IsBlocked, "A real end at an interruption boundary advances once while the next item waits.");
        state.RecommendResume(true);
        Check(!state.IsBlocked && queue.SequenceEqual([11, 12]), "Recovery must not replay the completed item or skip a pending one.");
        Console.WriteLine("PASS iOS resumption retains position and only true completion advances at an interruption boundary");
    }

    private static void PrivateRouteRecovery()
    {
        var routes = new IosPrivateAudioRoutes();
        var state = new IosAudioSuspension();
        var pause = AudioDisconnectionBehavior.PausePlayback;
        var keepPlaying = AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker;
        routes.AcceptCurrentRoute(["wired"]);
        Check(!routes.Reconcile(["wired"], pause, out var disconnected) && !disconnected,
            "An unchanged private route is safe after suspension.");
        Check(routes.Reconcile([], pause, out disconnected) && disconnected &&
              routes.Reconcile([], pause, out disconnected) && !disconnected,
            "A missing or unknown private route requires Play and must be reported once.");
        Check(!routes.DeviceUnavailable(["wired"], ["speaker"]), "A late callback for the reconciled route is a duplicate.");
        state.RouteDisconnected(pause);
        state.BeginInterruption();
        state.RecommendResume(true);
        state.Reconcile(canRetryActivation: true);
        Check(state.RequiresPlay && state.IsBlocked, "A system resumption recommendation cannot override route privacy.");
        routes.AcceptCurrentRoute([]);
        state.RequestPlay();
        Check(!routes.Reconcile([], pause, out _) && !state.IsBlocked &&
              !routes.DeviceUnavailable(["wired"], ["speaker"]),
            "An explicit Play accepts the speaker; a duplicate old removal must not pause it again.");
        state.ActivationSucceeded();

        routes.AcceptCurrentRoute(["bluetooth"]);
        Check(!routes.Reconcile([], keepPlaying, out disconnected) && disconnected,
            "Continue can reconcile a missed Bluetooth disconnect without requiring Play.");
        state.UnreportedPause();
        state.RouteDeactivated(keepPlaying, hadClip: false);
        state.RouteDeactivated(keepPlaying, hadClip: false);
        Check(!state.IsBlocked && state.RouteGeneration == 2, "A verified Continue route change can resume the same item once.");
        Console.WriteLine("PASS iOS suspended wired and Bluetooth privacy, speaker opt-in, and route deduplication");
    }

    private static async Task StopAndReplaceAsync()
    {
        var session = new IosClipSession(_ => true, () => true);
        var state = new IosAudioSuspension();
        var lifecycle = new MonitoringLifecycle();
        var run = lifecycle.TryBeginStart()!;
        lifecycle.Activate(run);
        var playback = new LivePlaybackRequests();
        using var original = playback.Begin(manual: false);
        var originalLease = session.TryStart(MobileAudioMode.MixAndDuck);
        var oldSignal = session.SignalGeneration;
        state.BeginInterruption();
        session.Invalidate();
        var completed = new TaskCompletionSource();
        var result = PlaybackAttempt.RunAsync(() => completed.Task.WaitAsync(original.Token), original.Token, _ => { });
        await lifecycle.StopAsync(_ => playback.RetireCurrent(), _ => session.InvalidatePendingSignals(), _ =>
        {
            state.Stop();
            return Task.CompletedTask;
        });

        using var newRequest = playback.Begin(manual: true);
        state.RequestPlay();
        var replacementLease = session.TryStart(MobileAudioMode.Exclusive);
        if (session.IsCurrentSignal(oldSignal))
            state.RecommendResume(false);
        session.Release(originalLease);
        Check(!session.IsCurrentSignal(oldSignal) && session.Owns(replacementLease) && newRequest.CanContinue,
            "A queued old native signal or skipped player's release cannot alter a rapid manual replacement.");
        Check(!(await result).AdvancesQueue() && !lifecycle.CanRecover(run) && !state.IsBlocked,
            "Explicit Stop wins over a delayed recovery, and the next explicit action can activate normally.");
        session.Release(replacementLease);
        Console.WriteLine("PASS iOS explicit Stop and rapid manual replacement fence stale recovery and release callbacks");
    }

    private static void MediaResetAndPlayerRebuild()
    {
        var session = new IosClipSession(_ => true, () => true);
        var state = new IosAudioSuspension();
        var recording = new IosRecordingState();
        recording.Observe(7.5);
        recording.Observe(0);
        recording.Observe(double.NaN);
        var initial = state.MediaGeneration;
        state.MediaServicesLost();
        state.RequestPlay();
        Check(state.IsBlocked, "Explicit Play cannot start while media services are unavailable.");
        var queuedReset = session.SignalGeneration;
        session.InvalidatePendingSignals();
        Check(!session.IsCurrentSignal(queuedReset) && session.IsCurrentSignal(queuedReset, sessionWide: true),
            "Replacing a clip fences its callbacks but cannot discard a queued media-server reset.");
        if (session.IsCurrentSignal(queuedReset, sessionWide: true))
            state.MediaServicesReset();
        state.MediaServicesReset();
        state.Reconcile(canRetryActivation: true);
        Check(state.RequiresPlay && state.MediaGeneration == initial + 1,
            "Duplicate native or player reset reports must invalidate one generation and require Play.");
        state.RequestPlay();
        Check(!state.IsBlocked && recording.SeekTarget(0) == 7.5, "After Play a rebuilt item can seek the last reliable position.");
        recording.Restart();
        Check(recording.SeekTarget(0) == null, "If seeking fails, the replacement starts at the beginning.");
        Console.WriteLine("PASS iOS media reset, saved clip position, and replay fallback");
    }

    private static async Task ForegroundSocketRecoveryAsync()
    {
        var recovery = new LiveStreamRecovery();
        var since = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_800);
        var signal = recovery.Pending;
        recovery.Request(since.AddMinutes(1));
        recovery.Request(since);
        var (first, isNew) = recovery.BeginAttempt();
        Check(signal.IsCompletedSuccessfully && isNew && first?.Since == DateTimeOffset.FromUnixTimeSeconds(since.ToUnixTimeSeconds()),
            "Foreground recovery should wake the socket and keep the earliest eligible boundary.");
        Check(!recovery.Pending.IsCompleted && recovery.BeginAttempt() == (first, false),
            "Starting recovery must retain the window until all pages are consumed.");
        recovery.Request(since.AddMinutes(2));
        await recovery.CompleteAsync(first!, default);
        var (overlap, overlapIsNew) = recovery.BeginAttempt();
        Check(overlapIsNew && overlap?.Since == first!.Since && !ReferenceEquals(first, overlap),
            "Completing an older attempt cannot acknowledge a newer overlapping request.");
        await recovery.CompleteAsync(overlap!, default);
        Check(!recovery.Pending.IsCompleted && recovery.BeginAttempt().Window == null && new LiveStreamRecovery().BeginAttempt().Window == null,
            "A reconciled recovery must not repeat or carry into a new monitoring run.");
        Console.WriteLine("PASS iOS foreground socket recovery coalescing and per-run isolation");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
