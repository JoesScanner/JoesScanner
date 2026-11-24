using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;
using Handoff = LiveHistoryHandoffTests;

internal static class FreshLiveSessionContractTests
{
    private const string Server = "https://scanner.example";
    private static readonly DateTimeOffset Recorded = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    internal static async Task<int> RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.FreshLiveSession-");
        (string Name, Func<string, Task> Run)[] cases =
        [
            ("SES-01 AutoPlay Off launches without a scanner transport", AutoPlayOffAsync),
            ("SES-02 / SES-06 AutoPlay On launches at the fresh Live edge", AutoPlayOnAsync),
            ("SES-03 On-screen and system Play drive the queue with AutoPlay Off", UserPlayAsync),
            ("SES-04 / MAN-04 Stop preserves cards for one-shot playback", StopKeepsCardsForOneShotAsync),
            ("SES-05 A delayed queue read cannot play after Stop and Play", RetiredQueueReadAsync),
            ("Retired session callbacks cannot change the replacement", RetiredCallbacksAsync),
            ("Passive refresh cannot reach a stopped or replaced session", PassiveMetadataRefreshAsync),
            ("History return preserves Stopped", HistoryReturnWhileStoppedAsync),
            ("MOB-04 History return preserves Running and skips Live backlog", HistoryReturnWhileRunningAsync),
            ("History's Live edge keeps late recovery and automatic Jump from replaying backlog", HistoryRecoveryBoundaryAsync)
        ];

        var failed = 0;
        try
        {
            for (var i = 0; i < cases.Length; i++)
            {
                var (name, run) = cases[i];
                try
                {
                    await run(Path.Combine(directory.FullName, $"case-{i}.sqlite"));
                    Console.WriteLine($"PASS {name}");
                }
                catch (Exception error)
                {
                    failed++;
                    Console.Error.WriteLine($"FAIL {name} ({error.GetType().Name})");
                    Console.Error.WriteLine(error.Message);
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }

        Console.WriteLine($"Fresh Live session contract: {cases.Length - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static async Task AutoPlayOffAsync(string path)
    {
        var audio = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), audio, path);
        await live.StartMonitoringWithAutoplayAsync(() =>
            throw new InvalidOperationException("AutoPlay Off must not schedule a scanner startup."));
        await live.RefreshStaleCallMetadataAsync();
        Handoff.Check(live.MonitoringGenerationForContractTest == null && live.OpenedTransportsForTest == 0 &&
            live.MetadataRequestsForTest == 0 && live.VisibleCallIdsForContractTest.Length == 0 && audio.Started.IsEmpty,
            "AutoPlay Off must leave a new process Stopped with an empty Live list and no scanner requests.");

        live.AutoPlayForTest = true;
        await live.StartMonitoringWithAutoplayAsync();
        Handoff.Check(live.OpenedTransportsForTest == 0,
            "A later window callback must not re-run the process's launch decision.");
    }

    private static async Task AutoPlayOnAsync(string path)
    {
        var audio = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), audio, path) { AutoPlayForTest = true };
        // Simulate an earlier process exiting with pending playback.
        var previous = new DurableLiveQueue(new TestPaths(path));
        var old = new DurableLiveQueue.Run(Server, 1, null);
        await previous.StartListeningAsync(old, Recorded, default);
        await previous.ObserveAsync(old, "prior-completed", Recorded, Snapshot("Already heard"), default);
        await previous.CommitCompletionAsync(old, "prior-completed");
        await previous.ObserveAsync(old, "prior-process", Recorded, Snapshot("Old audio"), default);

        await live.StartMonitoringWithAutoplayAsync();
        Handoff.Check(live.OpenedTransportsForTest == 1 && live.MonitoringGenerationForContractTest.HasValue &&
            live.VisibleCallIdsForContractTest.Length == 0 && await live.PendingHeadForTestAsync() == null,
            "AutoPlay On must start fresh without a persisted restore permit or old pending playback.");
        await live.JumpForTestAsync();
        Handoff.Check(audio.Started.IsEmpty && live.VisibleCallIdsForContractTest.Length == 0,
            "The current Live edge must exclude prior-process audio even before a new call arrives.");
        await live.DeliverForTestAsync("first-live");
        Handoff.Check(audio.Completed.SequenceEqual(["first-live"]) && live.AutoPlayForTest,
            "Only a new Live recording may play automatically after launch.");
        await live.StopMonitoringAsync();

        var unconfigured = new MainViewModel(new PlaybackOwnership(), audio, path)
        {
            AutoPlayForTest = true,
            ServerUrlForTest = null
        };
        await unconfigured.StartMonitoringWithAutoplayAsync();
        Handoff.Check(unconfigured.OpenedTransportsForTest == 0,
            "AutoPlay On must not open a scanner without a configured address.");

        var delayedLaunch = new MainViewModel(new PlaybackOwnership(), audio, path) { AutoPlayForTest = true };
        var windowReady = Handoff.Signal();
        var launch = delayedLaunch.StartMonitoringWithAutoplayAsync(() => windowReady.Task);
        await delayedLaunch.PressPlayStopForTestAsync();
        await delayedLaunch.PressPlayStopForTestAsync();
        windowReady.TrySetResult();
        await launch.WaitAsync(Watchdog);
        Handoff.Check(delayedLaunch.OpenedTransportsForTest == 1 &&
            delayedLaunch.MonitoringGenerationForContractTest == null,
            "A delayed startup decision must honor an explicit Stop in the same process.");
    }

    private static async Task UserPlayAsync(string path)
    {
        var audio = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), audio, path);
        await live.StartMonitoringWithAutoplayAsync();
        await live.PressPlayStopForTestAsync();
        var firstRun = live.MonitoringGenerationForContractTest;
        await live.DeliverForTestAsync("first");
        await live.DeliverForTestAsync("second");
        Handoff.Check(firstRun.HasValue && live.OpenedTransportsForTest == 1 && !live.AutoPlayForTest &&
            audio.Completed.SequenceEqual(["first", "second"]),
            "The visible Play handler must start continuous automatic playback with AutoPlay Off.");

        var oldCard = live.CardForTest("first");
        await live.PressPlayStopForTestAsync();
        Handoff.Check(live.MonitoringGenerationForContractTest == null &&
            live.VisibleCallIdsForContractTest.SequenceEqual(["second", "first"]),
            "The visible Stop handler must retain review cards.");

        await live.PressSystemPlayForTestAsync();
        Handoff.Check(live.MonitoringGenerationForContractTest is { } newRun && newRun != firstRun &&
            live.OpenedTransportsForTest == 2 && live.VisibleCallIdsForContractTest.Length == 0,
            "System Play must start the same fresh session and clear the previous cards.");
        await live.PressVisibleCardForTestAsync(oldCard);
        await live.DeliverForTestAsync("third");
        await live.DeliverForTestAsync("fourth");
        Handoff.Check(!live.AutoPlayForTest && audio.Completed.SequenceEqual(["first", "second", "third", "fourth"]),
            "A delayed tap on a cleared card must have no effect on the replacement run.");
        await live.StopMonitoringAsync();
    }

    private static async Task StopKeepsCardsForOneShotAsync(string path)
    {
        var audio = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), audio, path);
        await live.PressPlayStopForTestAsync();
        await live.AddForTestAsync("active");
        await live.AddForTestAsync("review");
        await live.AddForTestAsync("waiting");

        var started = Handoff.Signal();
        audio.Playing = (_, token) =>
        {
            started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var playing = live.StartQueueForTestAsync();
        try
        {
            await started.Task.WaitAsync(Watchdog);
            await live.PressPlayStopForTestAsync().WaitAsync(Watchdog);
            await playing.WaitAsync(Watchdog);
            Handoff.Check(live.MonitoringGenerationForContractTest == null && await live.PendingHeadForTestAsync() == null,
                "Stop must retire the listening run and leave no eligible pending playback.");
            Handoff.Check(live.VisibleCallIdsForContractTest.SequenceEqual(["waiting", "review", "active"]),
                "The existing cards must remain available for review after Stop.");

            audio.Playing = null;
            await live.PressVisibleCardForTestAsync(live.CardForTest("review")).WaitAsync(Watchdog);
            Handoff.Check(audio.Started.SequenceEqual(["active", "review"]) && audio.Completed.SequenceEqual(["review"]),
                "Stopped playback must play only the chosen card. The canceled automatic clip must not complete.");
            Handoff.Check(live.MonitoringGenerationForContractTest == null && live.ResumeRequests == 0,
                "The manual selection must leave monitoring and the automatic queue stopped.");
        }
        finally
        {
            await live.StopForTestAsync().WaitAsync(Watchdog);
        }
    }

    private static async Task RetiredQueueReadAsync(string path)
    {
        var audio = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), audio, path);
        await live.PressPlayStopForTestAsync();
        var firstGeneration = live.MonitoringGenerationForContractTest;
        await live.AddForTestAsync("old-queued");
        var oldRead = live.HoldNextSelection();
        var retiring = live.StartQueueForTestAsync();
        try
        {
            await oldRead.Reached.Task.WaitAsync(Watchdog);
            await live.PressPlayStopForTestAsync().WaitAsync(Watchdog);
            await live.PressPlayStopForTestAsync().WaitAsync(Watchdog);
            var replacement = live.MonitoringGenerationForContractTest;
            Handoff.Check(replacement.HasValue && replacement != firstGeneration,
                "Play after Stop must get a different listening generation.");
            Handoff.Check(live.VisibleCallIdsForContractTest.Length == 0, "Fresh Play must clear the old card list.");

            await live.AddForTestAsync("new-first");
            await live.StartQueueForTestAsync().WaitAsync(Watchdog);
            oldRead.Return.TrySetResult();
            await retiring.WaitAsync(Watchdog);

            Handoff.Check(audio.Started.SequenceEqual(["new-first"]) && audio.Completed.SequenceEqual(["new-first"]),
                "An old queue read delivered after replacement playback must have no effect on audio.");
            Handoff.Check(live.MonitoringGenerationForContractTest == replacement && live.ResumeRequests == 0,
                "The delayed selection must leave the replacement session running without starting another worker.");
        }
        finally
        {
            oldRead.Return.TrySetResult();
            await live.StopForTestAsync().WaitAsync(Watchdog);
            await retiring.WaitAsync(Watchdog);
        }
    }

    private static async Task RetiredCallbacksAsync(string path)
    {
        using var timeout = new CancellationTokenSource(Watchdog);
        var token = timeout.Token;
        var queue = new DurableLiveQueue(new TestPaths(path));
        var lifecycle = new MonitoringLifecycle();
        var oldRun = lifecycle.TryBeginStart() ?? throw new InvalidOperationException("Expected the original run.");
        var oldScope = new DurableLiveQueue.Run(Server, oldRun.Id, null);
        var recovery = await LiveStreamRecovery.StartListeningAsync(queue, oldScope, Recorded, token);
        Handoff.Check(lifecycle.Activate(oldRun), "Expected the original run to activate.");
        await recovery.RequestAsync(Recorded);
        var oldWindow = recovery.OutstandingWindow ?? throw new InvalidOperationException("Expected a recovery window.");
        var oldCompletion = new PlaybackCompletion();

        await lifecycle.StopAsync(_ => { }, _ => { }, _ => queue.StopAsync(oldScope));
        var currentRun = lifecycle.TryBeginStart() ?? throw new InvalidOperationException("Expected the replacement run.");
        var currentScope = oldScope with { Owner = currentRun.Id };
        try
        {
            var currentRecovery = await LiveStreamRecovery.StartListeningAsync(queue, currentScope, Recorded.AddMinutes(1), token);
            Handoff.Check(lifecycle.Activate(currentRun) && currentRecovery.IsInherited(oldWindow),
                "The replacement must activate while retaining the inherited recovery checkpoint.");
            var current = await queue.ObserveWithPositionAsync(currentScope, "current", Recorded,
                Snapshot("Current transcript"), token, RecordingOrigin.Live, existingOnly: false);
            Handoff.Check(current.Changed && current.ArrivalSequence.HasValue, "Expected a fresh recording in the replacement run.");

            Handoff.Check(!lifecycle.CanRecover(oldRun) && !oldCompletion.Confirm(oldRun.Token),
                "The old run must reject an event and a native completion delivered after replacement.");

            // Simulate transport and persistence callbacks that reach storage even after cancellation.
            var lateUpdate = await queue.ObserveWithPositionAsync(oldScope, "current", Recorded,
                Snapshot("Retired transcript"), token, RecordingOrigin.Live, existingOnly: true);
            var lateNewCall = await queue.ObserveWithPositionAsync(oldScope, "retired", Recorded,
                Snapshot("Retired call"), token, RecordingOrigin.Live, existingOnly: false);
            var lateReceipt = await queue.CompleteRecoveryAsync(oldScope, oldWindow,
                error => throw new InvalidOperationException("Unexpected recovery write failure.", error), token);
            await queue.CommitCompletionAsync(oldScope, "current");

            var next = await queue.NextAsync(currentScope, 0, token);
            Handoff.Check(!lateUpdate.Changed && !lateNewCall.Changed && !lateReceipt &&
                  await queue.GetArrivalSequenceAsync(currentScope, "retired", token) == null &&
                  await queue.ReadRecoveryWindowAsync(currentScope, token) == oldWindow,
                "An old transport callback must neither admit or edit a call nor close the replacement's checkpoint.");
            Handoff.Check(lifecycle.CanRecover(currentRun) && next is { Id: "current", Snapshot.Transcription: "Current transcript" } &&
                  next.ArrivalSequence == current.ArrivalSequence,
                "Retired playback and transport callbacks must preserve the replacement's pending call and arrival position.");
        }
        finally
        {
            await lifecycle.StopAsync(_ => { }, _ => { }, _ => queue.StopAsync(currentScope));
        }
    }

    private static async Task PassiveMetadataRefreshAsync(string path)
    {
        var live = new MainViewModel(new PlaybackOwnership(), new HandoffAudio(), path);
        await live.PressPlayStopForTestAsync();
        await live.AddForTestAsync("old-card");
        var issued = Handoff.Signal();
        var deliver = Handoff.Signal();
        CancellationToken requestToken = default;
        live.RefreshMetadataForTest = token =>
        {
            requestToken = token;
            issued.TrySetResult();
            return deliver.Task; // Simulate an HTTP response delivered despite cancellation.
        };
        var refresh = live.RefreshStaleCallMetadataAsync();
        try
        {
            await issued.Task.WaitAsync(Watchdog);
            await live.PressPlayStopForTestAsync();
            Handoff.Check(requestToken.IsCancellationRequested, "Stop must cancel the active metadata request.");
            await live.PressPlayStopForTestAsync();
            await live.AddForTestAsync("new-card");
            deliver.TrySetResult();
            await refresh.WaitAsync(Watchdog);
            Handoff.Check(live.MetadataRequestsForTest == 1 && live.MetadataUpdatesForTest == 0 &&
                live.VisibleCallIdsForContractTest.SequenceEqual(["new-card"]),
                "An old HTTP response must not publish into the replacement session.");

            await live.RefreshStaleCallMetadataAsync();
            Handoff.Check(live.MetadataRequestsForTest == 2 && live.MetadataUpdatesForTest == 1,
                "Passive refresh must still publish current-session results while Running.");

            await live.PressPlayStopForTestAsync();
            await live.RefreshStaleCallMetadataAsync();
            Handoff.Check(live.MetadataRequestsForTest == 2 && live.VisibleCallIdsForContractTest.SequenceEqual(["new-card"]),
                "Passive refresh while Stopped must leave the retained review cards alone.");
        }
        finally
        {
            deliver.TrySetResult();
            await live.StopMonitoringAsync();
            await refresh.WaitAsync(Watchdog);
        }
    }

    private static async Task HistoryReturnWhileStoppedAsync(string path)
    {
        var audio = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, audio, path);
        var history = new HistoryViewModel(ownership, audio);
        await live.StartForTestAsync(fresh: true);
        await live.AddForTestAsync("retained");
        await live.StopForTestAsync();
        history.OpenForTest();
        await history.PlayForTestAsync().WaitAsync(Watchdog);
        await history.OnPageClosedAsync().WaitAsync(Watchdog);

        Handoff.Check(ownership.Live != null && live.MonitoringGenerationForContractTest == null &&
              live.ResumeRequests == 0 && audio.Started.SequenceEqual(["history"]),
            "Leaving History while Stopped must release ownership without starting a Live run or playing a Live card.");
        Handoff.Check(live.VisibleCallIdsForContractTest.SequenceEqual(["retained"]),
            "History return must preserve the stopped session's review cards.");
    }

    private static async Task HistoryReturnWhileRunningAsync(string path)
    {
        var audio = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, audio, path);
        var history = new HistoryViewModel(ownership, audio);
        await live.StartForTestAsync(fresh: true);
        var generation = live.MonitoringGenerationForContractTest;
        await live.AddForTestAsync("before-history");
        var oldRead = live.HoldNextSelection();
        var retiring = live.StartQueueForTestAsync();
        try
        {
            await oldRead.Reached.Task.WaitAsync(Watchdog);
            history.OpenForTest();
            await history.PlayForTestAsync().WaitAsync(Watchdog);
            await live.AddForTestAsync("during-history");
            oldRead.Return.TrySetResult();
            await retiring.WaitAsync(Watchdog);
            Handoff.Check(ownership.Live == null && live.MonitoringGenerationForContractTest == generation &&
                  audio.Started.SequenceEqual(["history"]),
                "History must own audio exclusively while the same Live run continues observing calls.");

            await history.OnPageClosedAsync().WaitAsync(Watchdog);
            await live.DeliverForTestAsync("after-history").WaitAsync(Watchdog);

            Handoff.Check(ownership.Live != null && live.MonitoringGenerationForContractTest == generation &&
                  live.VisibleCallIdsForContractTest.SequenceEqual(["after-history", "during-history", "before-history"]),
                "Returning to Live must preserve the listening run and available review cards.");
            Handoff.Check(audio.Started.SequenceEqual(["history", "after-history"]) &&
                  audio.Completed.SequenceEqual(["history", "after-history"]),
                $"Expected audio starts [history, after-history]. Observed [{string.Join(", ", audio.Started)}].");
        }
        finally
        {
            oldRead.Return.TrySetResult();
            await live.StopForTestAsync().WaitAsync(Watchdog);
            await retiring.WaitAsync(Watchdog);
        }
    }

    private static async Task HistoryRecoveryBoundaryAsync(string path)
    {
        using var timeout = new CancellationTokenSource(Watchdog);
        var token = timeout.Token;
        var queue = new DurableLiveQueue(new TestPaths(path));
        var run = new DurableLiveQueue.Run(Server, 1, null);
        var recovery = await LiveStreamRecovery.StartListeningAsync(queue, run, Recorded, token);
        await queue.ObserveAsync(run, "before-return", Recorded, Snapshot("Live backlog"), token);
        await recovery.RequestAsync(Recorded);
        var window = recovery.OutstandingWindow ?? throw new InvalidOperationException("Expected an open recovery window.");

        var returning = queue.SetPlaybackFloorAtLiveEdgeAsync(run, token, skipOutstandingRecovery: true);
        var late = await queue.ObserveWithPositionAsync(run, "late-http", Recorded, Snapshot("Recovered backlog"),
            token, RecordingOrigin.History, existingOnly: false, recoveryWindowId: window.Id);
        await returning;
        Handoff.Check(late.IsRecoveryBacklog && await queue.ReadRecoveryWindowAsync(run, token) == window &&
            await queue.JumpToLiveAsync(run, 0, skipSelected: false, token) == null &&
            await queue.NextAsync(run, 0, token) == null,
            "A pre-return recovery window must remain open without supplying audible backlog, even on automatic Jump.");

        await queue.ObserveAsync(run, "after-return", Recorded, Snapshot("New Live"), token);
        Handoff.Check((await queue.NextAsync(run, 0, token))?.Id == "after-return",
            "A Live admission after the boundary must remain eligible.");
    }

    private static LiveCallSnapshot Snapshot(string transcript) =>
        new(new(), transcript, CallCardIdFallback.None, Server + "/recording.mp3");

    private sealed record TestPaths(string DbPath) : IDatabasePathProvider;
}

namespace JoesScanner.ViewModels
{
    public partial class MainViewModel
    {
        internal long? MonitoringGenerationForContractTest => _monitoring.Current?.Id;
        internal string[] VisibleCallIdsForContractTest => Calls.Select(call => call.BackendId).ToArray();
    }
}
