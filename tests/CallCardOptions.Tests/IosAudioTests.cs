using JoesScanner.Models;
using JoesScanner.Services;

internal static class IosAudioTests
{
    internal static async Task RunAsync()
    {
        RecordingModesAndLeaseOwnership();
        LongRecordingStopsDucking();
        TransientDuckingHandoff();
        PersistentDuckingHandoffPause();
        StationaryHandoffAndBuffering();
        NearEndHandoffPause();
        HandoffEligibilityContinuity();
        PhysicalOutputChangeAfterActivation();
        HandoffOutputNotificationOrder();
        HandoffReconcilesPrivateRoute();
        InvalidatedDuckingHandoff();
        OrdinaryMixAndExclusivePlayback();
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

    private static void TransientDuckingHandoff()
    {
        var session = new IosClipSession(_ => true, () => true);
        var state = new IosAudioSuspension();
        var recording = new IosRecordingState();
        using var request = new CancellationTokenSource();
        var completion = new PlaybackCompletion();
        int ends = 0;
        completion.OnEnded(() => ends++);
        using var item = new MobileRecordingAttempt(completion);
        item.Start();
        var duck = session.TryStart(MobileAudioMode.MixAndDuck);
        recording.Activated(MobileAudioMode.MixAndDuck);
        recording.Observe(2.931);
        var continuation = new IosDuckingContinuation(session, state, () => item.HasStarted && !request.IsCancellationRequested,
            () => ["speaker"], (_, mode) => session.TryStart(mode));
        continuation.DuckingExpired(duck);
        session.Release(duck);
        recording.EndDuckedSegment(TimeSpan.FromSeconds(3), windowExpired: true);
        var mix = continuation.StartClip(MobileAudioMode.MixAndDuck, recording.ModeForActivation(MobileAudioMode.MixAndDuck),
            out var eligible);
        Check(eligible != null, "The immediate plain-mix activation of the same player should receive handoff protection.");
        var handoff = new IosDuckingHandoff(session, state, mix, eligible!, 2.931,
            () => item.HasStarted && !request.IsCancellationRequested, () => { });

        // An inspection dispatched by Waiting can see Paused, then Playing.
        Check(!handoff.ShouldReportPause(false, false, 2.931, TimeSpan.Zero) &&
              !handoff.ShouldReportPause(true, false, 2.932, TimeSpan.FromMilliseconds(7)) &&
              !handoff.ShouldReportPause(false, true, 2.933, TimeSpan.FromMilliseconds(19)) &&
              !handoff.NeedsSample && !state.RequiresPlay && state.RecordingRestartRevision == 0,
            "A transient handoff pause must leave the original attempt running.");
        Check(handoff.ShouldReportPause(true, false, 4.5, TimeSpan.FromSeconds(2)),
            "After Playing, a later unexplained pause is handled immediately.");
        Check(recording.SeekTarget(2.933) == null && recording.SeekTarget(0) == 2.931 &&
              recording.DuckingRemaining(TimeSpan.Zero) == TimeSpan.Zero &&
              session.ActiveMode == MobileAudioMode.MixNormally,
            "The same item must retain position and the exhausted ducking budget.");
        recording.Observe(8);
        Check(item.Confirm(request.Token) && !item.Confirm(request.Token) && ends == 1 &&
              completion.Confirmed && recording.SeekTarget(0) == 8,
            "Only one native end owns this recording's completion.");

        var clockHandoff = new IosDuckingHandoff(session, state, mix, new IosHandoffOutputs(state, () => ["speaker"]), 2.931, () => true, () => { });
        Check(!clockHandoff.ShouldReportPause(true, false, 2.931, TimeSpan.Zero) &&
              !clockHandoff.ShouldReportPause(true, false, 3.2, TimeSpan.FromMilliseconds(250)) &&
              clockHandoff.NeedsSample &&
              !clockHandoff.ShouldReportPause(true, false, 3.45, TimeSpan.FromMilliseconds(500)) &&
              !clockHandoff.ShouldReportPause(false, true, 3.70, TimeSpan.FromMilliseconds(750)) &&
              !clockHandoff.NeedsSample,
            "Media progression clears a provisional pause even before Playing is observed.");
        Console.WriteLine("PASS iOS transient ducking handoff, media progress, retained position and exactly one completion");
    }

    private static void PersistentDuckingHandoffPause()
    {
        var session = new IosClipSession(_ => true, () => true);
        var state = new IosAudioSuspension();
        var lease = session.TryStart(MobileAudioMode.MixNormally);
        var handoff = new IosDuckingHandoff(session, state, lease, new IosHandoffOutputs(state, () => ["speaker"]), 2.931, () => true, () => { });
        Check(!handoff.ShouldReportPause(true, false, 2.931, TimeSpan.FromMilliseconds(40)) &&
              !handoff.ShouldReportPause(true, false, 2.932, TimeSpan.FromMilliseconds(139)) &&
              handoff.NeedsSample && !state.IsBlocked,
            "Repeated inspections and clock jitter cannot confirm a pause early.");
        if (handoff.ShouldReportPause(true, false, 2.932, TimeSpan.FromMilliseconds(140)))
            state.UnreportedPause();
        Check(state.IsBlocked && state.RequiresPlay && state.RecordingRestartRevision == 1 &&
              !handoff.ShouldReportPause(true, false, 2.932, TimeSpan.FromMilliseconds(250)),
            "A persistent stationary pause must trigger the existing interruption behavior once.");
        Console.WriteLine("PASS iOS persistent ducking handoff pause requires Play after bounded confirmation");
    }

    private static void StationaryHandoffAndBuffering()
    {
        var session = new IosClipSession(_ => true, () => true);
        var state = new IosAudioSuspension();
        var lease = session.TryStart(MobileAudioMode.MixNormally);
        IosDuckingHandoff New() => new(session, state, lease, new IosHandoffOutputs(state, () => ["speaker"]), 3, () => true, () => { });
        static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

        var oscillating = New();
        var stall = new IosPlaybackProgress(3);
        Check(!oscillating.ShouldReportPause(true, false, 3, Ms(4990)) &&
              !oscillating.ShouldReportPause(false, false, 3, Ms(5000)) &&
              stall.IsStalled(3, true, Ms(5000)) && !oscillating.AllowsStallRecovery(true, Ms(5000)),
            "An expired stall timer cannot retire the item during a newly provisional pause.");
        for (var ms = 5010; ms < 5090; ms += 10)
            Check(!oscillating.ShouldReportPause(ms % 20 == 0, false, 3, Ms(ms)),
                "Stationary status chatter cannot confirm before 100 ms.");
        Check(!oscillating.ShouldReportPause(false, false, 3, Ms(5090)) &&
              oscillating.ShouldReportPause(true, false, 3, Ms(5091)),
            "Waiting cannot confirm, but the first due Paused sample must use the original clock.");

        var buffering = New();
        Check(!buffering.ShouldReportPause(false, false, 3, Ms(0)) &&
              buffering.AllowsStallRecovery(true, Ms(2000)) &&
              !buffering.ShouldReportPause(false, true, 3.02, Ms(2100)) && !buffering.NeedsSample,
            "Waiting alone must retain normal buffering and allow Playing.");
        var returnedToWaiting = New();
        returnedToWaiting.ShouldReportPause(true, false, 3, Ms(50));
        Check(!returnedToWaiting.ShouldReportPause(false, false, 3, Ms(2050)) &&
              returnedToWaiting.AllowsStallRecovery(true, Ms(2050)),
            "A sustained return to Waiting may use the existing buffering timeout.");

        var finalAdvance = New();
        Check(!finalAdvance.ShouldReportPause(true, false, 3, Ms(0)) &&
              !finalAdvance.ShouldReportPause(true, false, 3.1, Ms(250)) &&
              !finalAdvance.ShouldReportPause(true, false, 3.101, Ms(349)) &&
              finalAdvance.ShouldReportPause(true, false, 3.101, Ms(350)),
            "The final advancing Paused sample anchors a new 100 ms confirmation interval.");
        var watchdogOnly = New();
        Check(!watchdogOnly.ShouldReportPause(true, false, 3.1, Ms(250)) &&
              watchdogOnly.ShouldReportPause(true, false, 3.1, Ms(500)),
            "One stationary watchdog sample after the final advance must be sufficient.");
        var betweenTicks = New();
        Check(!betweenTicks.ShouldReportPause(true, false, 3.1, Ms(151)) &&
              !betweenTicks.ShouldReportPause(true, false, 3.1, Ms(250)) &&
              betweenTicks.ShouldReportPause(true, false, 3.1, Ms(500)),
            "A 99 ms watchdog sample is too early; the next tick confirms at 349 ms.");
        var delayedDispatcher = New();
        Check(!delayedDispatcher.ShouldReportPause(true, false, 3.1, Ms(0)) &&
              delayedDispatcher.ShouldReportPause(true, false, 3.1, Ms(3000)),
            "After a blocked dispatcher, the first stationary sample confirms without another wait.");
        Console.WriteLine("PASS iOS oscillating handoff, buffering and stall precedence, final advancing pause timing");
    }

    private static void NearEndHandoffPause()
    {
        const double position = 2.931, duration = 3.020;
        foreach (var nativeEndMs in new int?[] { 50, 125, null })
        {
            var session = new IosClipSession(_ => true, () => true);
            var state = new IosAudioSuspension();
            var lease = session.TryStart(MobileAudioMode.MixNormally);
            var completion = new PlaybackCompletion();
            int ends = 0;
            completion.OnEnded(() => ends++);
            using var attempt = new MobileRecordingAttempt(completion);
            attempt.Start();
            var handoff = new IosDuckingHandoff(session, state, lease, new IosHandoffOutputs(state, () => ["speaker"]), position,
                () => attempt.HasStarted && !completion.IsFinished, () => { });
            var progress = new IosPlaybackProgress(position);
            bool Inspect(int ms) => IosPlaybackProgress.ShouldReportNativePause(handoff, true, false,
                position, duration, TimeSpan.FromMilliseconds(ms));

            Check(!Inspect(0) && handoff.NeedsSample && !handoff.AllowsStallRecovery(false, TimeSpan.Zero),
                "The initial near-end pause is provisional.");
            if (nativeEndMs != 50)
            {
                Check(!Inspect(100) && !handoff.NeedsSample && !Inspect(110) && !Inspect(120) &&
                      !handoff.NeedsSample && handoff.AllowsStallRecovery(false, TimeSpan.FromMilliseconds(120)),
                    "A confirmed near-end pause and its duplicates must leave the stall watchdog available.");
            }
            Check(ends == 0 && !completion.IsFinished, "Proximity to the expected duration cannot consume the item.");
            if (nativeEndMs is { } ms)
            {
                Check(attempt.Confirm(CancellationToken.None) && !Inspect(ms) &&
                      !attempt.Confirm(CancellationToken.None) && !attempt.Reject() && ends == 1 && completion.Confirmed,
                    "A native end during or after provisional handling must win once against stale callbacks.");
            }
            else
            {
                var deadline = TimeSpan.FromSeconds(5);
                Check(!progress.IsStalled(position, false, deadline - TimeSpan.FromMilliseconds(1)) &&
                      progress.IsStalled(position, false, deadline) && handoff.AllowsStallRecovery(false, deadline) &&
                      attempt.Reject() && !attempt.Confirm(CancellationToken.None) && !completion.IsFinished && ends == 0,
                    "Without a native end, five seconds of stationary media must permit retirement without consumption.");
            }
        }
        Console.WriteLine("PASS iOS 3.020 s item paused at 2.931 s, duplicate pauses, stall deadline and native end ownership");
    }

    private static void HandoffEligibilityContinuity()
    {
        var duck = MobileAudioMode.MixAndDuck;
        var mix = MobileAudioMode.MixNormally;
        var session = new IosClipSession(_ => true, () => true);
        var state = new IosAudioSuspension();
        bool deny = true;
        var continuity = new IosDuckingContinuation(session, state, () => true, () => ["speaker"], (_, mode) =>
        {
            if (!deny)
                return session.TryStart(mode);
            state.ActivationDenied();
            return 0; // A blocked activation need not advance the session signal.
        });
        var outgoing = session.TryStart(duck);
        continuity.DuckingExpired(outgoing);
        session.Release(outgoing);
        Check(continuity.StartClip(duck, mix, out var eligible) == 0 && eligible == null, "Denied activation cannot own a handoff.");
        state.RequestPlay();
        state.ActivationSucceeded();
        deny = false;
        var recovered = continuity.StartClip(duck, mix, out eligible);
        Check(recovered == outgoing + 1 && eligible == null,
            "Even an adjacent lease after manual recovery cannot inherit consumed eligibility.");

        void CheckBreak(Action<IosClipSession, IosAudioSuspension> change, bool duringActivation = false)
        {
            var clips = new IosClipSession(_ => true, () => true);
            var audio = new IosAudioSuspension();
            var next = new IosDuckingContinuation(clips, audio, () => true, () => ["speaker"], (_, mode) =>
            {
                var lease = clips.TryStart(mode);
                if (duringActivation)
                    change(clips, audio);
                return lease;
            });
            var initial = clips.TryStart(duck);
            next.DuckingExpired(initial);
            clips.Release(initial);
            if (!duringActivation)
                change(clips, audio);
            next.StartClip(duck, mix, out var inherited);
            Check(inherited == null, "An intervening change cannot inherit ducking continuation.");
        }
        CheckBreak((_, audio) => audio.RouteDisconnected(AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker));
        CheckBreak((_, audio) => audio.RouteDisconnected(AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker), duringActivation: true);
        CheckBreak((_, audio) => { audio.BeginInterruption(); audio.RecommendResume(true); audio.ActivationSucceeded(); });
        CheckBreak((_, audio) => { audio.MediaServicesReset(); audio.RequestPlay(); });
        CheckBreak((_, audio) => audio.NoteOutputChange()); // A physical output changed away and back.
        CheckBreak((clips, _) => clips.InvalidatePendingSignals());
        CheckBreak((clips, _) => clips.Release(clips.TryStart(mix)));

        var output = "speaker";
        var physical = new IosDuckingContinuation(session, state, () => true, () => [output],
            (_, mode) => session.TryStart(mode));
        session.Release(recovered);
        outgoing = session.TryStart(duck);
        physical.DuckingExpired(outgoing);
        session.Release(outgoing);
        output = "headphones"; // New output without a delivered private-route loss.
        recovered = physical.StartClip(duck, mix, out eligible);
        Check(eligible == null, "A different physical output cannot inherit the outgoing handoff.");

        foreach (var changesBeforeActivation in new[] { false, true })
        {
            var clips = new IosClipSession(_ => true, () => true);
            var audio = new IosAudioSuspension();
            var currentOutput = "speaker";
            var exchange = new IosDuckingContinuation(clips, audio, () => true, () => [currentOutput], (_, mode) =>
            {
                // A pre-activation mismatch stays disqualifying if activation restores the output.
                currentOutput = changesBeforeActivation ? "speaker" : "headset";
                return clips.TryStart(mode);
            });
            var duckLease = clips.TryStart(duck);
            exchange.DuckingExpired(duckLease);
            clips.Release(duckLease);
            if (changesBeforeActivation)
                currentOutput = "headset";
            exchange.StartClip(duck, mix, out var protectedOutputs);
            Check(protectedOutputs == null, "An observable mismatch on either side of activation precludes protection.");
        }

        var current = true;
        var owner = new IosDuckingContinuation(session, state, () => current, () => ["speaker"], (_, mode) => session.TryStart(mode));
        session.Release(recovered);
        outgoing = session.TryStart(duck);
        owner.DuckingExpired(outgoing);
        session.Release(outgoing);
        current = false;
        owner.StartClip(duck, mix, out eligible);
        Check(eligible == null, "A canceled request or replacement player cannot inherit the handoff.");
        Console.WriteLine("PASS iOS one-shot handoff activation denial, recovery, generations and request ownership");
    }

    private static void PhysicalOutputChangeAfterActivation()
    {
        foreach (var preference in new[] { AudioDisconnectionBehavior.PausePlayback, AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker })
        foreach (var wasProvisional in new[] { false, true })
        foreach (var privateRouteLost in new[] { false, true })
        {
            var session = new IosClipSession(_ => true, () => true);
            var state = new IosAudioSuspension();
            var routes = new IosPrivateAudioRoutes();
            string[] outputs = ["headset"];
            routes.AcceptCurrentRoute(outputs);
            var continuation = new IosDuckingContinuation(session, state, () => true, () => outputs,
                (_, mode) => session.TryStart(mode));
            var outgoing = session.TryStart(MobileAudioMode.MixAndDuck);
            continuation.DuckingExpired(outgoing);
            session.Release(outgoing);
            var incoming = continuation.StartClip(MobileAudioMode.MixAndDuck, MobileAudioMode.MixNormally, out var captured);
            Check(captured != null, "Activation must transfer the validated output snapshot.");

            void ChangeOutputs()
            {
                outputs = [];
                state.NoteOutputChange();
                if (!privateRouteLost)
                {
                    outputs = ["headset"];
                    state.NoteOutputChange();
                }
            }

            if (!wasProvisional)
                ChangeOutputs(); // Even a change before observer construction must be visible.
            var handoff = new IosDuckingHandoff(session, state, incoming, captured!, 2.931, () => true, () =>
            {
                var pause = routes.Reconcile(outputs, preference, out var disconnected);
                if (disconnected || pause)
                    state.RouteDisconnected(preference);
            });
            bool Inspect(int ms) => IosPlaybackProgress.ShouldReportNativePause(handoff, true, false,
                2.931, 8, TimeSpan.FromMilliseconds(ms));
            if (wasProvisional)
            {
                Check(!Inspect(0) && handoff.NeedsSample, "Expected a provisional pause before the output changed.");
                ChangeOutputs();
            }
            Check(Inspect(25) == !privateRouteLost && !handoff.NeedsSample &&
                  handoff.AllowsStallRecovery(false, TimeSpan.FromMilliseconds(25)) &&
                  state.RouteGeneration == (privateRouteLost ? 1 : 0) &&
                  state.RequiresPlay == (privateRouteLost && preference == AudioDisconnectionBehavior.PausePlayback),
                "Route loss must use its policy; a still-owned unexplained pause after restoration must report immediately.");
            Check(Inspect(30) == !privateRouteLost && !handoff.NeedsSample,
                "A repeated observation cannot revive expired output eligibility.");
        }
        Console.WriteLine("PASS iOS physical output changes before inspection and during confirmation under Pause and Continue");
    }

    private static void HandoffOutputNotificationOrder()
    {
        foreach (var preference in Enum.GetValues<AudioDisconnectionBehavior>())
        foreach (var alreadyProvisional in new[] { false, true })
        foreach (var notifyBeforeInspection in new[] { false, true })
        foreach (var pausedAtChange in new[] { false, true })
        {
            var session = new IosClipSession(_ => true, () => true);
            var state = new IosAudioSuspension();
            var routes = new IosPrivateAudioRoutes();
            string output = "speaker";
            // Speaker and HDMI are both non-private. Route reconciliation alone
            // cannot identify this swap. Restoration must not revive the timer.
            var continuation = new IosDuckingContinuation(session, state, () => true, () => [output],
                (_, mode) => session.TryStart(mode));
            var duck = session.TryStart(MobileAudioMode.MixAndDuck);
            continuation.DuckingExpired(duck);
            session.Release(duck);
            var mix = continuation.StartClip(MobileAudioMode.MixAndDuck, MobileAudioMode.MixNormally, out var captured);
            Check(captured != null, "Unchanged outputs must qualify for the intentional handoff.");
            if (!alreadyProvisional)
                output = "HDMI"; // Before the incoming observer is constructed.
            var handoff = new IosDuckingHandoff(session, state, mix, captured!, 3, () => true, () =>
            {
                if (routes.Reconcile([], preference, out var disconnected) || disconnected)
                    state.RouteDisconnected(preference);
            });
            bool Inspect(bool paused, int ms) => IosPlaybackProgress.ShouldReportNativePause(handoff,
                paused, false, 3, 8, TimeSpan.FromMilliseconds(ms));

            if (alreadyProvisional)
            {
                Check(!Inspect(true, 0) && handoff.NeedsSample, "An unchanged route can defer the first pause.");
                output = "HDMI";
            }
            if (notifyBeforeInspection)
                state.NoteOutputChange();
            Check(Inspect(pausedAtChange, 10) == pausedAtChange && !handoff.NeedsSample &&
                  handoff.AllowsStallRecovery(false, TimeSpan.FromMilliseconds(10)) && state.RouteGeneration == 0,
                "The first observation of different live outputs must settle protection, including while Waiting.");

            output = "speaker";
            Check(Inspect(true, 20), "Restoring the original outputs cannot revive provisional protection.");
            state.NoteOutputChange();
            state.NoteOutputChange();
            Check(Inspect(true, 30) && !handoff.NeedsSample, "Late or repeated route notifications cannot re-arm the handoff.");
        }
        Console.WriteLine("PASS iOS live output mismatch before or after notification, restoration and duplicates under both preferences");
    }

    private static void HandoffReconcilesPrivateRoute()
    {
        foreach (var preference in new[] { AudioDisconnectionBehavior.PausePlayback, AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker })
        {
            var session = new IosClipSession(_ => true, () => true);
            var state = new IosAudioSuspension();
            var lease = session.TryStart(MobileAudioMode.MixNormally);
            var routes = new IosPrivateAudioRoutes();
            routes.AcceptCurrentRoute(["headset"]);
            var handoff = new IosDuckingHandoff(session, state, lease, new IosHandoffOutputs(state, () => ["speaker"]), 3, () => true, () =>
            {
                var mustPause = routes.Reconcile([], preference, out var disconnected);
                if (disconnected || mustPause)
                    state.RouteDisconnected(preference);
            });
            Check(!state.IsBlocked && !handoff.ShouldReportPause(true, false, 3, TimeSpan.Zero) &&
                  !handoff.NeedsSample && state.RouteGeneration == 1 &&
                  state.RequiresPlay == (preference == AudioDisconnectionBehavior.PausePlayback),
                "The first Paused inspection must detect a missing private output before deferral, honoring the preference.");
        }
        Console.WriteLine("PASS iOS first provisional sample reconciles missing private routes for Pause and Continue");
    }

    private static void InvalidatedDuckingHandoff()
    {
        foreach (var cause in new[] { "system interruption", "private route loss", "outgoing lease", "new signal",
                     "Stop", "replacement request", "replacement item", "media reset", "restart revision" })
        {
            var session = new IosClipSession(_ => true, () => true);
            var state = new IosAudioSuspension();
            var requests = new LivePlaybackRequests();
            using var original = requests.Begin(manual: false);
            var originalItem = new object();
            var currentItem = originalItem;
            var lease = session.TryStart(MobileAudioMode.MixNormally);
            var handoff = new IosDuckingHandoff(session, state, lease, new IosHandoffOutputs(state, () => ["speaker"]), 2.931,
                () => original.CanContinue && ReferenceEquals(currentItem, originalItem), () => { });
            Check(!handoff.ShouldReportPause(true, false, 2.931, TimeSpan.Zero) && handoff.NeedsSample,
                "Expected a provisional pause before " + cause);

            switch (cause)
            {
                case "system interruption":
                    state.BeginInterruption();
                    Check(state.IsBlocked && state.RecordingRestartRevision == 1, "Real interruption must block immediately.");
                    state.RecommendResume(true);
                    state.ActivationSucceeded();
                    break;
                case "private route loss":
                    state.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
                    Check(state.IsRoutePaused && state.RequiresPlay, "Private route loss must require explicit Play immediately.");
                    break;
                case "outgoing lease":
                    session.Release(lease);
                    var replacementLease = session.TryStart(MobileAudioMode.Exclusive);
                    Check(session.Owns(replacementLease), "Replacement must own its lease.");
                    break;
                case "new signal": session.InvalidatePendingSignals(); break;
                case "Stop": requests.RetireCurrent(); break;
                case "replacement request":
                    using (var next = requests.Begin(manual: true))
                        Check(next.CanContinue && !original.CanContinue, "New request must supersede the original.");
                    break;
                case "replacement item": currentItem = new object(); break;
                case "media reset":
                    state.MediaServicesReset();
                    Check(state.RequiresPlay && state.MediaGeneration == 1, "Media reset must block immediately.");
                    state.RequestPlay();
                    break;
                case "restart revision": state.ForegroundAfterSuspension(); break;
            }
            Check(!handoff.ShouldReportPause(true, false, 2.931, TimeSpan.FromMilliseconds(250)) && !handoff.NeedsSample,
                "A stale confirmation must have no effect after " + cause);
        }
        Console.WriteLine("PASS iOS provisional pause invalidation on interruption, route loss, Stop, replacement and media reset");
    }

    private static void OrdinaryMixAndExclusivePlayback()
    {
        foreach (var mode in new[] { MobileAudioMode.MixNormally, MobileAudioMode.Exclusive })
        {
            var state = new IosAudioSuspension();
            var recording = new IosRecordingState();
            var activations = new List<IosAudioOptions>();
            var session = new IosClipSession(options => { activations.Add(options); return true; }, () => true);
            var lease = session.TryStart(recording.ModeForActivation(mode));
            recording.Activated(mode);
            foreach (var seconds in new[] { 0.25, 3, 3.25, 7.9 })
            {
                recording.Observe(seconds);
                Check(session.Owns(lease) && recording.ModeForActivation(MobileAudioMode.MixAndDuck) == mode &&
                      recording.SeekTarget(seconds) == null, "Ordinary playback must keep its original mode and position.");
            }
            var options = mode == MobileAudioMode.Exclusive ? IosAudioOptions.None : IosAudioOptions.Mix;
            Check(activations.SequenceEqual([options]) && recording.DuckingRemaining(TimeSpan.Zero) == TimeSpan.FromSeconds(3),
                "An ordinary recording must neither exchange session priority nor consume a ducking budget.");
            var continuity = new IosDuckingContinuation(session, state, () => true, () => ["speaker"], (_, next) => session.TryStart(next));
            continuity.DuckingExpired(lease);
            session.Release(lease);
            continuity.StartClip(mode, mode, out var eligible);
            Check(eligible == null,
                "The handoff policy must never defer an ordinary Mix Normally or Exclusive pause.");
            state.UnreportedPause();
            Check(state.RequiresPlay && state.RecordingRestartRevision == 1,
                "An ordinary unexplained pause must still take the immediate interruption path.");
        }
        Console.WriteLine("PASS iOS ordinary Mix Normally and Exclusive preserve priority and immediate interruption behavior");
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
        var canDeactivate = false;
        var session = new IosClipSession(options => { calls.Add(options.ToString()); return true; },
            () => { calls.Add("deactivate"); return canDeactivate; },
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
              calls.Count(call => call == "plain mix") == 1 && session.PendingRelease == old,
            "The mixing fallback must relinquish priority without declaring an unsuccessful deactivation complete.");
        Check(session.TryStart(MobileAudioMode.MixAndDuck) == 0,
            "A failed deactivation is still pending after the retry budget.");

        canDeactivate = true;
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
        Check(!state.IsBlocked && !state.IsRecoveryBlocked() && state.IsRecoveryBlocked(waitForActivation: true),
            "Foreground grants an activation probe while an interrupted native clip still waits for actual activation.");
        state.ActivationDenied();
        Check(state.IsBlocked && state.RequiresPlay && state.IsRecoveryBlocked(),
            "A rejected probe leaves the recording pending and keeps automatic recovery blocked.");

        state.SetCallActive(true);
        state.RecommendResume(true);
        Check(state.IsBlocked, "Even a positive resumption recommendation cannot bypass an observed call.");
        state.SetCallActive(false);
        Check(!state.IsBlocked, "The same recording may try to activate after the call ends.");
        state.ActivationSucceeded();
        state.RecommendResume(false);
        Check(!state.IsBlocked && !state.IsRecoveryBlocked(waitForActivation: true),
            "A successful activation completes recovery; a stale negative recommendation cannot pause a resumed item.");

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
        var completion = new PlaybackCompletion();
        var recording = PlaybackAttempt.RunAsync(() => finished.Task, CancellationToken.None, _ => { }, completion);
        using var first = new MobileRecordingAttempt(completion);
        var outgoing = session.TryStart(MobileAudioMode.MixAndDuck);
        first.Start();
        position.Observe(12);
        var revision = state.RecordingRestartRevision;
        state.BeginInterruption();
        session.Invalidate();
        session.Release(outgoing);
        Check(state.RecordingRestartRevision == revision + 1 && first.Interrupt() && !first.Confirm(default) &&
              state.IsBlocked && !recording.IsCompleted && queue.Peek() == 10,
            "The interrupted player cannot complete or advance its recording.");
        state.RecommendResume(true);
        var resumed = session.TryStart(MobileAudioMode.MixAndDuck);
        state.ActivationSucceeded();
        position.Restart();
        using var replacement = new MobileRecordingAttempt(completion);
        replacement.Start();
        Check(session.Owns(resumed) && position.SeekTarget(0) == null && !first.Confirm(default) &&
              replacement.Confirm(default) && !replacement.Confirm(default),
            "Only the replacement item starting at zero can confirm the recording's end once.");

        state.BeginInterruption();
        session.Invalidate();
        finished.SetResult();
        if ((await recording).AdvancesQueue())
            queue.Dequeue();
        Check(queue.Peek() == 11 && state.IsBlocked, "A real end at an interruption boundary advances once while the next item waits.");
        state.RecommendResume(true);
        Check(!state.IsBlocked && queue.SequenceEqual([11, 12]), "Recovery must not replay the completed item or skip a pending one.");
        Console.WriteLine("PASS iOS short interruption restarts at zero and only one native item advances the queue");
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
