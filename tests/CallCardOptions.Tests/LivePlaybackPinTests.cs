using System.Threading.Channels;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;
using Handoff = LiveHistoryHandoffTests;

internal static class LivePlaybackPinTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.LivePins-");
        try
        {
            await NativeStartAndEndAsync(Path.Combine(directory.FullName, "native.sqlite"));
            await ReplacedAttemptsCannotPublishAsync(Path.Combine(directory.FullName, "replacement.sqlite"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task NativeStartAndEndAsync(string path)
    {
        var player = new HandoffAudio { NotifyStartForTest = false };
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync();
        await live.AddForTestAsync("unavailable");
        var preparing = Handoff.Signal();
        var failPreparation = Handoff.Signal();
        live.PrepareForTest = async (_, token) =>
        {
            preparing.SetResult();
            await failPreparation.Task.WaitAsync(token);
            throw new IOException("Fixture preparation failure.");
        };
        var failedPreparation = live.StartQueueForTestAsync();
        await preparing.Task.WaitAsync(Deadline);
        Handoff.Check(live.ActiveLiveCard == null && !live.CardForTest("unavailable").IsPlaying, "Preparation cannot publish a playing card.");
        failPreparation.SetResult();
        await failedPreparation.WaitAsync(Deadline);
        Handoff.Check(live.ActiveLiveCard == null && player.Started.IsEmpty, "Failed preparation must never publish a pin.");

        live.PrepareForTest = null;
        await live.AddForTestAsync("native");
        var card = live.CardForTest("native");
        var entered = new TaskCompletionSource<Attempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNative = Handoff.Signal();
        player.NativeAttemptForTest = (id, token, completion) =>
        {
            entered.SetResult(new(id, token, completion));
            return releaseNative.Task.WaitAsync(token);
        };
        var playback = live.StartQueueForTestAsync();
        var attempt = await entered.Task.WaitAsync(Deadline);
        Handoff.Check(attempt.Id == "native" && live.ActiveLiveCard == null && !card.IsPlaying,
            "Holding the native slot before audio starts must leave the pin clear.");
        attempt.Completion.NotifyStarted(attempt.Token);
        Handoff.Check(ReferenceEquals(live.ActiveLiveCard, card) && card.IsPlaying, "Native start must publish the selected canonical card.");
        Handoff.Check(attempt.Completion.Confirm(attempt.Token) && live.ActiveLiveCard == null && !card.IsPlaying &&
              await live.IsPendingForTestAsync("native"),
            "A confirmed native end must clear the pin even while teardown and completion persistence are pending.");
        releaseNative.SetResult();
        await playback.WaitAsync(Deadline);
        Handoff.Check(!await live.IsPendingForTestAsync("native"), "Natural playback must retain its durable completion.");

        await live.AddForTestAsync("native-failure");
        var failedCard = live.CardForTest("native-failure");
        var observedFailure = false;
        player.NativeAttemptForTest = (_, token, completion) =>
        {
            completion.NotifyStarted(token);
            observedFailure = ReferenceEquals(live.ActiveLiveCard, failedCard);
            throw new IOException("Fixture failure after native start.");
        };
        await live.StartQueueForTestAsync().WaitAsync(Deadline);
        Handoff.Check(observedFailure && live.ActiveLiveCard == null && !failedCard.IsPlaying,
            "Native failure without a terminal signal must still clear the active presentation.");

        await live.StopForTestAsync().WaitAsync(Deadline);
        Console.WriteLine("PASS Live pin waits for native start and clears on completion, preparation failure, and native failure");
    }

    private static async Task ReplacedAttemptsCannotPublishAsync(string path)
    {
        var player = new HandoffAudio { NotifyStartForTest = false };
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, player, path);
        await live.StartForTestAsync();
        await live.AddForTestAsync("old");
        await live.AddForTestAsync("current");
        await live.AddForTestAsync("bad");
        var old = live.CardForTest("old");
        var current = live.CardForTest("current");
        var bad = live.CardForTest("bad");
        var posts = Channel.CreateUnbounded<Action>();
        var starts = Channel.CreateUnbounded<Attempt>();
        player.NativeAttemptForTest = (id, token, completion) =>
        {
            starts.Writer.TryWrite(new(id, token, completion));
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        MainThread.PostForTest = action => posts.Writer.TryWrite(action);
        try
        {
            var retired = live.PressVisibleCardForTestAsync(old);
            var first = await starts.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            first.Completion.NotifyStarted(first.Token);
            var staleStart = await NextPost();

            var replacement = live.PressVisibleCardForTestAsync(current);
            var second = await starts.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            var staleEnd = await NextPost();
            second.Completion.NotifyStarted(second.Token);
            (await NextPost())();
            staleStart();
            staleEnd();
            await retired.WaitAsync(Deadline);
            Handoff.Check(ReferenceEquals(live.ActiveLiveCard, current) && current.IsPlaying && !old.IsPlaying,
                "Late start and end callbacks cannot replace or clear a newer manual pin.");

            live.PrepareForTest = (_, _) => Task.FromException(new IOException("Unplayable fixture."));
            await live.PressVisibleCardForTestAsync(bad).WaitAsync(Deadline);
            Handoff.Check(ReferenceEquals(live.ActiveLiveCard, current) && !bad.IsPlaying,
                "Failed replacement preparation must keep the playing recording pinned.");

            var history = new HistoryViewModel(ownership, player);
            history.OpenForTest();
            Handoff.Check(live.ActiveLiveCard == null && !current.IsPlaying, "Losing Live ownership to History must clear the pin immediately.");
            (await NextPost())();
            await replacement.WaitAsync(Deadline);
            await history.OnPageClosedAsync();
            await live.ResumedQueue.WaitAsync(Deadline);

            live.PrepareForTest = null;
            await live.AddForTestAsync("before-stop", later: true);
            var beforeStop = live.CardForTest("before-stop");
            var stopping = live.PressVisibleCardForTestAsync(beforeStop);
            var third = await starts.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            third.Completion.NotifyStarted(third.Token);
            (await NextPost())();
            Handoff.Check(ReferenceEquals(live.ActiveLiveCard, beforeStop), "A valid post-History native start must pin.");
            await Task.WhenAll(live.StopForTestAsync(), stopping).WaitAsync(Deadline);
            Handoff.Check(live.ActiveLiveCard == null && !beforeStop.IsPlaying, "Stop must clear the pin before queued callbacks execute.");
            var afterStop = await NextPost();
            await live.StartForTestAsync(fresh: true);
            afterStop();
            Handoff.Check(live.ActiveLiveCard == null, "A retired clip cannot affect a fresh listening session.");
            await live.StopForTestAsync().WaitAsync(Deadline);
            Console.WriteLine("PASS Rapid manual replacement, History, Stop, and fresh Live reject stale pin callbacks");
        }
        finally
        {
            MainThread.PostForTest = null;
        }

        Task<Action> NextPost() => posts.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
    }

    private readonly record struct Attempt(string Id, CancellationToken Token, PlaybackCompletion Completion);
}
