using System.Collections.Concurrent;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;

internal static class MobileInterruptionIntegrationTests
{
    private static readonly Guid First = Guid.Parse("d03d915d-7f17-4603-a798-2ab23d2bfc41");
    private static readonly Guid Second = Guid.Parse("742a8458-6b47-41ba-8f85-541901b4888f");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("JoesScanner.MobileInterruption-");
        var prior = AppPaths.DatabaseDirectory;
        try
        {
            await ShortPhoneAndFocusAsync(root.CreateSubdirectory("short").FullName);
            await LongInterruptionAndTwoRecordersAsync(root.CreateSubdirectory("long").FullName);
            await BluetoothRoutePreferencesAsync(root.CreateSubdirectory("route").FullName);
            await StopDuringReservedRecoveryAsync(root.CreateSubdirectory("stop").FullName);
            await ManualSelectionDuringRecoveryAsync(root.CreateSubdirectory("manual").FullName);
            await HistoryRetiresRecoveryAsync(root.CreateSubdirectory("history").FullName);
        }
        finally
        {
            AppPaths.DatabaseDirectory = prior;
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static async Task ShortPhoneAndFocusAsync(string directory)
    {
        var player = new HandoffAudio();
        var (live, clock) = await OpenAsync(directory, player);
        var nativeStarted = Signal();
        var preloadStarted = Signal();
        var preloadRetired = Signal();
        var resume = Signal();
        var restartPositions = new ConcurrentQueue<int>();
        var iOSPosition = new IosRecordingState();
        var androidFocus = new AndroidClipFocusState();
        androidFocus.Requested(registered: true);
        PlaybackCompletion? activeCompletion = null;
        MobileRecordingAttempt? interrupted = null;
        var commits = 0;
        live.BeforeCommitForTest = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };
        live.ForecastForTest = _ => Task.FromResult<IReadOnlyList<LiveAudioPreloader.Candidate>>([new(2, async token =>
        {
            try
            {
                preloadStarted.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
            finally { preloadRetired.TrySetResult(); }
        })]);
        player.NativeAttemptForTest = async (id, token, completion) =>
        {
            using var first = new MobileRecordingAttempt(completion);
            first.Start();
            if (id != "thirty-second-call")
            {
                Check(first.Confirm(token), "The next recording must have its own natural end.");
                return;
            }
            activeCompletion = completion;
            interrupted = first;
            restartPositions.Enqueue(0);
            nativeStarted.SetResult();
            await resume.Task.WaitAsync(token);
            iOSPosition.Restart();
            restartPositions.Enqueue(iOSPosition.SeekTarget(0) is { } oldPosition ? (int)oldPosition : 0);
            using var replacement = new MobileRecordingAttempt(completion);
            replacement.Start();
            Check(!first.Confirm(token) && replacement.Confirm(token) && !replacement.Confirm(token),
                "The obsolete player and duplicate end callbacks cannot confirm a restarted recording.");
        };
        await live.AddForTestAsync("thirty-second-call", serverId: First);
        await live.AddForTestAsync("next-recording", serverId: Second);
        var worker = live.StartQueueForTestAsync();
        await nativeStarted.Task.WaitAsync(Timeout);
        await preloadStarted.Task.WaitAsync(Timeout);
        clock.Advance(TimeSpan.FromSeconds(12));
        iOSPosition.Observe(12);
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, true);
        Check(interrupted!.Interrupt(), "The first audible attempt must be retired at 12 seconds.");
        await preloadRetired.Task.WaitAsync(Timeout);

        var focusChanged = androidFocus.Changed;
        Check(androidFocus.OnChange(AndroidClipFocusStatus.TransientLoss) && focusChanged.IsCompleted,
            "A transient loss must immediately wake the focus waiter.");
        await live.SignalFocusForTestAsync(activeCompletion!, true);
        clock.Advance(TimeSpan.FromMinutes(2));
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, false);
        Check(live.InterruptionIsBlockingForTest && !activeCompletion!.Confirmed && commits == 0,
            "The phone ending does not release overlapping focus loss.");
        Check(androidFocus.OnChange(AndroidClipFocusStatus.Granted), "The current registered request can regain focus.");
        await live.SignalFocusForTestAsync(activeCompletion!, false);
        Check(!live.InterruptionIsBlockingForTest && !live.HasInterruptCheckpointForTest && commits == 0,
            "The final gain ends one short interruption without advancing the queue.");
        resume.SetResult();
        await worker.WaitAsync(Timeout);
        Check(restartPositions.SequenceEqual([0, 0]) && player.Completed.SequenceEqual(["thirty-second-call", "next-recording"]) &&
            commits == 2 && live.OpenedTransportsForTest == 2,
            "The clip restarts at zero, advances once, and leaves both recorders connected.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS A 30-second clip interrupted at 12 seconds by a two-minute call and overlapping focus restarts once at zero and cancels preloading");
    }

    private static async Task LongInterruptionAndTwoRecordersAsync(string directory)
    {
        var player = new HandoffAudio();
        var (live, clock) = await OpenAsync(directory, player);
        var started = Signal();
        player.Playing = async (id, token) =>
        {
            if (id == "interrupted")
            {
                started.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
        };
        await live.AddForTestAsync("interrupted", serverId: First);
        var worker = live.StartQueueForTestAsync();
        await started.Task.WaitAsync(Timeout);
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, true);
        await live.AddForTestAsync("waiting-first", serverId: First);
        await live.AddForTestAsync("waiting-second", serverId: Second);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, false).WaitAsync(Timeout);
        await worker.WaitAsync(Timeout);
        Check(player.Started.SequenceEqual(["interrupted"]) && player.Completed.IsEmpty && live.CallsWaiting == 0 &&
            live.DisplayedCallsForTest == 3 && live.OpenedTransportsForTest == 2,
            "A long interruption must skip both recorders' old audio while preserving cards and transports.");
        await live.AddForTestAsync("new-second", serverId: Second);
        await live.AddForTestAsync("new-first", serverId: First);
        await live.StartQueueForTestAsync().WaitAsync(Timeout);
        Check(player.Completed.SequenceEqual(["new-second", "new-first"]) && live.OpenedTransportsForTest == 2,
            "Only fresh admissions may be played after recovery at the Live edge.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS A long interruption reserves the fresh Live edge across two independently connected recorders");
    }

    private static async Task StopDuringReservedRecoveryAsync(string directory)
    {
        var player = new HandoffAudio();
        var (live, clock) = await OpenAsync(directory, player);
        var playing = Signal();
        var stopping = Signal();
        var release = Signal();
        player.Playing = async (id, token) =>
        {
            if (id != "retired")
                return;
            playing.SetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token); }
            finally
            {
                stopping.SetResult();
                await release.Task;
            }
        };
        await live.AddForTestAsync("retired", serverId: First);
        var worker = live.StartQueueForTestAsync();
        await playing.Task.WaitAsync(Timeout);
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.Background, true);
        clock.Advance(TimeSpan.FromMinutes(6));
        var jump = live.SignalInterruptionForTestAsync(LiveInterruptionSource.Background, false);
        await stopping.Task.WaitAsync(Timeout);
        var stop = live.StopForTestAsync();
        release.SetResult();
        await Task.WhenAll(worker, jump, stop).WaitAsync(Timeout);
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.Background, false);
        Check(!live.HasInterruptCheckpointForTest && player.Started.SequenceEqual(["retired"]) && player.Completed.IsEmpty,
            "Stop must cancel an in-flight foreground recovery boundary.");
        await live.StartForTestAsync(fresh: true);
        await live.AddForTestAsync("replacement", serverId: First);
        await live.StartQueueForTestAsync().WaitAsync(Timeout);
        Check(player.Completed.SequenceEqual(["replacement"]), "The retired boundary may not change the replacement session.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS Explicit Stop retires a pending foreground recovery and leaves the replacement fresh session intact");
    }

    private static async Task BluetoothRoutePreferencesAsync(string directory)
    {
        var player = new HandoffAudio();
        var (live, clock) = await OpenAsync(directory, player);
        var device = new AndroidAudioInterruption();
        var started = Signal();
        var finish = Signal();
        player.Playing = async (id, cancellation) =>
        {
            if (id == "route")
            {
                started.SetResult();
                await finish.Task.WaitAsync(cancellation);
            }
        };
        await live.AddForTestAsync("route", serverId: First);
        var queue = live.StartQueueForTestAsync();
        await started.Task.WaitAsync(Timeout);
        device.RouteDisconnected(AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker);
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, device.IsBlocked, device.RoutePaused);
        Check(!live.InterruptionIsBlockingForTest && !live.HasInterruptCheckpointForTest && player.Started.Count == 1,
            "Continue Through Speaker does not create a recovery episode for the same playing clip.");

        device.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, device.IsBlocked, device.RoutePaused);
        clock.Advance(TimeSpan.FromSeconds(30));
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.AndroidFocus, false);
        Check(live.InterruptionIsBlockingForTest && player.Completed.IsEmpty,
            "A focus gain or an idle interval may not release a Bluetooth Pause.");
        live.RequestNativeResumeForTest = async () =>
        {
            device.RequestPlay();
            await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, device.IsBlocked, device.RoutePaused);
            finish.SetResult();
        };
        await live.PressPlayStopForTestAsync().WaitAsync(Timeout);
        await queue.WaitAsync(Timeout);
        Check(!live.InterruptionIsBlockingForTest && player.Completed.SequenceEqual(["route"]) && live.OpenedTransportsForTest == 2,
            "The existing Play control releases the route pause without restarting the listening session.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS Bluetooth Continue stays audible and Pause waits for the visible Play control without reconnecting");
    }

    private static async Task ManualSelectionDuringRecoveryAsync(string directory)
    {
        var player = new HandoffAudio();
        var (live, clock) = await OpenAsync(directory, player);
        var original = Signal();
        var selected = Signal();
        var permit = Signal();
        PlaybackCompletion? retiredCompletion = null;
        player.NativeAttemptForTest = async (id, token, completion) =>
        {
            using var attempt = new MobileRecordingAttempt(completion);
            attempt.Start();
            if (id == "original")
            {
                retiredCompletion = completion;
                original.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
            else if (id == "manual")
            {
                selected.SetResult();
                await permit.Task.WaitAsync(token);
            }
            Check(attempt.Confirm(token), "The active manual traversal must complete normally.");
        };
        await live.AddForTestAsync("original", serverId: First);
        await live.AddForTestAsync("manual", serverId: First);
        await live.AddForTestAsync("manual-successor", serverId: First);
        var worker = live.StartQueueForTestAsync();
        await original.Task.WaitAsync(Timeout);
        await live.SignalFocusForTestAsync(retiredCompletion!, true);
        clock.Advance(TimeSpan.FromMinutes(6));
        var manual = live.SelectForTestAsync("manual");
        await selected.Task.WaitAsync(Timeout);
        await live.SignalFocusForTestAsync(retiredCompletion!, true);
        await live.SignalFocusForTestAsync(retiredCompletion!, false);
        Check(!live.HasInterruptCheckpointForTest && !live.InterruptionIsBlockingForTest,
            "Retired focus callbacks cannot revive the old episode after a manual selection.");
        permit.SetResult();
        await Task.WhenAll(worker, manual).WaitAsync(Timeout);
        await live.ResumedQueue.WaitAsync(Timeout);
        Check(player.Completed.SequenceEqual(["manual", "manual-successor"]),
            "The explicit recording and its traversal must win over the old long recovery.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS Manual selection supersedes recovery and fences delayed focus callbacks from the previous native owner");
    }

    private static async Task HistoryRetiresRecoveryAsync(string directory)
    {
        var ownership = new PlaybackOwnership();
        var player = new HandoffAudio();
        var (live, clock) = await OpenAsync(directory, player, ownership);
        var initial = Signal();
        player.Playing = async (id, token) =>
        {
            if (id != "old-live")
                return;
            initial.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        };
        await live.AddForTestAsync("old-live", serverId: First);
        var worker = live.StartQueueForTestAsync();
        await initial.Task.WaitAsync(Timeout);
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, true);
        clock.Advance(TimeSpan.FromMinutes(6));
        var history = new HistoryViewModel(ownership, player);
        history.OpenForTest();
        await worker.WaitAsync(Timeout);
        await live.SignalInterruptionForTestAsync(LiveInterruptionSource.NativeAudio, false);
        await live.AddForTestAsync("received-in-history", serverId: Second);
        await history.PlayForTestAsync().WaitAsync(Timeout);
        await history.OnPageClosedAsync();
        await live.ResumedQueue.WaitAsync(Timeout);
        Check(!live.HasInterruptCheckpointForTest && player.Completed.SequenceEqual(["history"]) &&
            live.OpenedTransportsForTest == 2, "History must own the player and clear the obsolete Live recovery.");
        await live.AddForTestAsync("new-live", serverId: First);
        await live.StartQueueForTestAsync().WaitAsync(Timeout);
        Check(player.Completed.SequenceEqual(["history", "new-live"]),
            "History return must preserve recording independence and play only fresh Live audio.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS History audio ownership takes precedence and returning to Live skips the retired interruption backlog");
    }

    private static async Task<(MainViewModel Live, Clock Clock)> OpenAsync(string directory, HandoffAudio player,
        PlaybackOwnership? ownership = null)
    {
        AppPaths.DatabaseDirectory = directory;
        var live = new MainViewModel(ownership ?? new PlaybackOwnership(), player, Path.Combine(directory, "queue.db"));
        live.ConfigureAudioSourceRecordersForTest(First, Second);
        live.PlaybackOptionsForTest = new(GroupCalls: false);
        await live.StartForTestAsync();
        var clock = new Clock();
        live.TrackInterruptionsForTest(clock);
        return (live, clock);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) => LiveHistoryHandoffTests.Check(condition, message);

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(1_800_000_000).AddTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
