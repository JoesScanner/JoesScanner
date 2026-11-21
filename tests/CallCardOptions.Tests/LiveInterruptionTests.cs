using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class LiveInterruptionTests
{
    internal static async Task RunAsync()
    {
        CheckThresholdsAndQuietGaps();
        CheckPlatformSignalsAndRestart();
        await CheckBoundaryAsync();
    }

    private static void CheckThresholdsAndQuietGaps()
    {
        var clock = new Clock();
        var episode = new LiveInterruptionRecovery(clock, _ => { });
        clock.Advance(TimeSpan.FromHours(8));
        Check(episode.ShouldJump(new(InterruptionRecoveryOption.Immediately)) == null, "Quiet time must not create an interruption.");

        episode.SetBlocked(LiveInterruptionSource.AndroidFocus, true);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        episode.SetBlocked(LiveInterruptionSource.AndroidFocus, false);
        clock.Advance(TimeSpan.FromHours(3));
        Check(episode.ShouldJump(InterruptionRecoveryPreference.Default) == false,
            "A short interruption must remain short even while History owns audio or playback is muted.");
        Check(episode.ShouldJump(new(InterruptionRecoveryOption.Immediately)) == true, "Immediately must return to Live after recovery.");
        episode.Complete();

        episode.SetBlocked(LiveInterruptionSource.Connection, true);
        clock.Advance(TimeSpan.FromMinutes(5));
        episode.SetBlocked(LiveInterruptionSource.Connection, false);
        Check(episode.ShouldJump(InterruptionRecoveryPreference.Default) == true, "Exactly five minutes must return to Live.");
        Check(episode.ShouldJump(new(InterruptionRecoveryOption.Never)) == false, "Never must preserve the backlog.");
        Check(episode.ShouldJump(new(InterruptionRecoveryOption.Custom, 7)) == false, "Custom must apply the saved duration.");
        Console.WriteLine("PASS Interruption thresholds are inclusive and a quiet scanner creates no recovery");
    }

    private static void CheckPlatformSignalsAndRestart()
    {
        var clock = new Clock();
        LiveInterruptionRecovery.Checkpoint? saved = null;
        var episode = new LiveInterruptionRecovery(clock, value => saved = value);
        var ios = new IosAudioSuspension();
        ios.BeginInterruption();
        episode.SetBlocked(LiveInterruptionSource.NativeAudio, ios.HasRecoveryInterruption);
        episode.SetBlocked(LiveInterruptionSource.Background, true);
        clock.Advance(TimeSpan.FromMinutes(6));
        ios.RecommendResume(true);
        episode.SetBlocked(LiveInterruptionSource.NativeAudio, ios.HasRecoveryInterruption);
        Check(episode.ShouldJump(InterruptionRecoveryPreference.Default) == null, "iOS foreground recovery must still be pending.");
        episode.SetBlocked(LiveInterruptionSource.Background, false);
        Check(episode.ShouldJump(InterruptionRecoveryPreference.Default) == true, "Long iOS suspension must jump on foreground recovery.");
        episode.Complete();

        var android = new AndroidAudioInterruption();
        android.SetCallActive(true);
        episode.SetBlocked(LiveInterruptionSource.NativeAudio, android.CallActive);
        clock.Advance(TimeSpan.FromMinutes(1));
        android.RouteDisconnected(AudioDisconnectionBehavior.PausePlayback);
        android.SetCallActive(false);
        episode.SetBlocked(LiveInterruptionSource.NativeAudio, android.RoutePaused, android.RoutePaused);
        var restored = new LiveInterruptionRecovery(clock, value => saved = value);
        restored.Restore(saved ?? throw new InvalidOperationException("Missing persistent Android route pause."));
        clock.Advance(TimeSpan.FromMinutes(4));
        restored.SetBlocked(LiveInterruptionSource.Connection, false);
        Check(restored.ShouldJump(InterruptionRecoveryPreference.Default) == null && restored.RequiresPlay,
            "A route pause must require explicit Play across a restart.");
        restored.AllowExplicitPlay();
        Check(restored.ShouldJump(InterruptionRecoveryPreference.Default) == true,
            "The original interruption duration must survive restart until explicit Play.");
        restored.Complete();
        Check(saved == null, "A reconciled episode must clear the checkpoint.");
        Console.WriteLine("PASS iOS and Android interruption signals compose, including a route pause across restart");
    }

    private static async Task CheckBoundaryAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.Interruption-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        try
        {
            var clock = new Clock();
            var paths = new Paths(Path.Combine(directory.FullName, "queue.sqlite"));
            var queue = new DurableLiveQueue(paths);
            var run = new DurableLiveQueue.Run("https://scanner.example", 1, null);
            await queue.OpenAsync(run, clock.GetUtcNow(), token);
            await Add("complete", -2);
            await Add("awaiting-audio", 45);
            await Add("live-edge", -200);

            var allowBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jump = queue.JumpToLiveAsync(run, 0, skipSelected: false, token, prepareBoundary: async () =>
            {
                await allowBoundary.Task.WaitAsync(token);
                await queue.CommitCompletionAsync(run, "complete");
                return 0;
            }, commitPlayback: transaction => AppStateStore.Write(transaction, "test_interruption", "consumed"));
            var duringJump = Add("new-arrival", -400);
            Check(!duringJump.IsCompleted, "A new observation must wait behind the reserved jump boundary.");
            allowBoundary.TrySetResult();
            Check((await jump)?.Target.Id == "live-edge", "The jump must target the last observed row before its reservation.");
            await duringJump;
            var ordered = await queue.NextAsync(run, 0, token);
            Check(ordered?.Id == "live-edge", "Automatic playback must start at the reserved live edge.");
            await queue.MarkAsync(run, "live-edge", PlaybackOutcome.Completed, token);
            Check((await queue.NextAsync(run, 0, token))?.Id == "new-arrival", "The new recording must remain pending.");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DbPath }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT group_concat(disposition, ',') FROM (
                    SELECT disposition FROM live_queue_recordings ORDER BY arrival_sequence)
                """;
            Check((string?)command.ExecuteScalar() == "1,2,1,0", "The boundary must keep completion and skip dispositions distinct.");
            command.CommandText = "SELECT setting_value FROM app_settings WHERE setting_key = 'test_interruption'";
            Check((string?)command.ExecuteScalar() == "consumed", "The jump and recovery checkpoint must commit together.");

            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(token);
            var waitingBeforeCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceledJump = queue.JumpToLiveAsync(run, 0, skipSelected: false, stopped.Token, prepareBoundary: async () =>
            {
                waitingBeforeCommit.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token);
                return 0;
            }, commitPlayback: transaction => AppStateStore.Write(transaction, "test_interruption", "incorrect"));
            await waitingBeforeCommit.Task.WaitAsync(token);
            stopped.Cancel();
            await queue.StopAsync(run);
            try
            {
                await canceledJump;
                throw new InvalidOperationException("Stop did not cancel the automatic boundary.");
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            Check((string?)command.ExecuteScalar() == "consumed", "A canceled jump must not commit its checkpoint.");

            Task<bool> Add(string id, int minutes) => queue.ObserveAsync(run, id, clock.GetUtcNow().AddMinutes(minutes),
                new LiveCallSnapshot(new(), null, CallCardIdFallback.None,
                    id == "awaiting-audio" ? null : $"https://scanner.example/{id}.mp3"), token);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
        Console.WriteLine("PASS Automatic Jump preserves native completion and new arrivals, and Explicit Stop cancels its SQLite boundary");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _epoch = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        private TimeSpan _elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => _epoch + _elapsed;
        public void Advance(TimeSpan elapsed) => _elapsed += elapsed;
    }

    private sealed class Paths(string path) : IDatabasePathProvider
    {
        public string DbPath { get; } = path;
        public string GetDbPath() => DbPath;
    }
}
