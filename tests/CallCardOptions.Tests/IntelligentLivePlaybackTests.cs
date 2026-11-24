using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;

internal static class IntelligentLivePlaybackTests
{
    private static readonly Guid ServerA = Guid.Parse("c7c67640-0b4a-427c-8b26-70e2a0b8e3b1");
    private static readonly Guid ServerB = Guid.Parse("a2e5046e-b264-46d3-a71a-fac384c75b4d");
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);
    private static readonly LivePlaybackPreferences Defaults = LivePlaybackPreferences.Default;
    private static readonly TalkgroupKey Fire = Group(ServerA, "system-a", "fire");
    private static readonly TalkgroupKey Police = Group(ServerA, "system-a", "police");

    internal static async Task RunAsync()
    {
        OptionsAndIdentities();
        GroupingAndLimits();
        FavoritesAndFifo();

        var directory = Directory.CreateTempSubdirectory("joesscanner-scheduler-");
        try
        {
            await LateAudioAsync(Path.Combine(directory.FullName, "late.db"));
            await ScopedManualReplayAsync(Path.Combine(directory.FullName, "scope.db"));
            await AutomaticJumpAtBoundaryAsync(Path.Combine(directory.FullName, "boundary.db"));
            await ManualReplayExemptsJumpAsync(Path.Combine(directory.FullName, "manual.db"));
            await ManualPreparationFailureAsync(Path.Combine(directory.FullName, "failed.db"));
            await SilentArrivalsAsync(Path.Combine(directory.FullName, "silent.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static void OptionsAndIdentities()
    {
        Check(Defaults == new LivePlaybackPreferences(true, true, 3, 80, 100) &&
            LivePlaybackScheduler.Lookahead == 20, "The approved defaults must apply to a fresh installation.");
        var settings = LivePlaybackPreferences.Parse("""
            {"GroupCalls":false,"PrioritizeFavorites":false,"ConsecutiveLimit":10,"WarningThreshold":192,"JumpThreshold":126}
            """);
        Check(settings == new LivePlaybackPreferences(false, false, 10, 192, 126),
            "The two ready thresholds can be configured independently.");
        Check(LivePlaybackPreferences.Parse("{\"ConsecutiveLimit\":11,\"WarningThreshold\":0}") == Defaults,
            "Damaged preference fields must fall back without changing defaults.");

        var first = new CallCardMetadata { SystemId = "1", TalkgroupId = "42", SiteId = "10", Talkgroup = "Dispatch" };
        var renamedAtAnotherSite = first with { SiteId = "11", Talkgroup = "Fire Dispatch" };
        var otherSystem = first with { SystemId = "2" };
        var siteOnly = first with { SystemId = null };
        Check(TalkgroupKey.From(ServerA, first) == TalkgroupKey.From(ServerA, renamedAtAnotherSite) &&
            TalkgroupKey.From(ServerA, first) != TalkgroupKey.From(ServerA, otherSystem) &&
            TalkgroupKey.From(ServerA, siteOnly) != TalkgroupKey.From(ServerA, siteOnly with { SiteId = "11" }) &&
            TalkgroupKey.From(ServerA, first) != TalkgroupKey.From(ServerB, first) &&
            TalkgroupKey.From(ServerA, new()) == null,
            "Track actual talkgroups by permanent recorder and system, with a site fallback when needed.");
        Console.WriteLine("PASS Scheduler defaults and scoped talkgroup identities keep unrelated calls distinct");
    }

    private static void GroupingAndLimits()
    {
        for (var limit = 1; limit <= 10; limit++)
        {
            var scheduler = CreateScheduler();
            var waiting = Enumerable.Range(1, limit + 3)
                .Select(i => new LivePlaybackScheduler.Candidate(i, i == 2 ? Police : Fire)).ToList();
            var actual = Drain(scheduler, waiting, Defaults with { ConsecutiveLimit = limit });
            var expected = Enumerable.Range(1, limit + 3).Where(i => i != 2).Select(i => (long)i).ToList();
            expected.Insert(limit, 2);
            Check(actual.SequenceEqual(expected), $"Consecutive limit {limit} must yield while another talkgroup waits.");
        }
        Check(Drain(CreateScheduler(), [new(1, Fire), new(2, Fire), new(3, Fire)], Defaults with
            { ConsecutiveLimit = 1 }).SequenceEqual([1L, 2L, 3L]), "A single waiting group must never introduce a delay.");
        Console.WriteLine("PASS Groups stay FIFO internally and yield at every consecutive limit from 1 to 10");
    }

    private static void FavoritesAndFifo()
    {
        var favorite = Group(ServerB, "system-b", "fire");
        List<LivePlaybackScheduler.Candidate> queue =
            [new(1, Fire), new(2, Police), new(3, favorite), new(4, favorite), new(5, favorite), new(6, favorite), new(7, favorite)];
        Check(Drain(CreateScheduler(favorite), queue.ToList(), Defaults with { ConsecutiveLimit = 10 })
                .SequenceEqual([3L, 4L, 5L, 1L, 6L, 7L, 2L]),
            "After three favorites the oldest regular must receive its turn even with more favorites waiting.");
        Check(Drain(CreateScheduler(favorite), queue.ToList(), Defaults with { ConsecutiveLimit = 1 })
                .Take(3).SequenceEqual([3L, 1L, 4L]), "The group cap must apply separately from the favorite streak.");
        Check(Drain(CreateScheduler(favorite), queue.ToList(), Defaults with { GroupCalls = false })
                .SequenceEqual(Enumerable.Range(1, 7).Select(i => (long)i)),
            "Grouping Off must restore strict FIFO for all servers and favorites.");
        var beyondWindow = Enumerable.Range(1, 20).Select(i => new LivePlaybackScheduler.Candidate(i, Fire))
            .Append(new(21, favorite));
        Check(CreateScheduler(favorite).Choose(beyondWindow, Defaults)?.Candidate.Arrival == 1,
            "A favorite outside the 20 ready candidates cannot jump the window.");
        Console.WriteLine("PASS Favorites honor the fixed lookahead, three-favorite fairness, group caps, and FIFO mode");
    }

    private static async Task LateAudioAsync(string path)
    {
        var queue = new DurableLiveQueue(new Paths(path));
        var a = new DurableLiveQueue.Run("https://recorder-a.test", 11, null, ServerId: ServerA);
        var b = new DurableLiveQueue.Run("https://recorder-b.test", 12, null, ServerId: ServerB);
        await queue.StartListeningAsync(a, Now, default);
        await queue.StartListeningAsync(b, Now, default);
        var scheduler = CreateScheduler();

        var first = await Observe(queue, a, "A", audio: "a.wav", talkgroup: "dispatch");
        var missingB = await Observe(queue, b, "B", audio: null, talkgroup: "patrol");
        var missingC = await Observe(queue, a, "C", audio: null, talkgroup: "patrol");
        await Observe(queue, a, "D", audio: "d.wav", talkgroup: "dispatch");
        await Observe(queue, a, "E", audio: "e.wav", talkgroup: "dispatch");
        var played = new List<string>();
        var selected = (await scheduler.SelectAsync(queue, [b, a], _ => 0, Defaults, default))!;
        Check(selected.Row.Row.ArrivalSequence == first.ArrivalSequence, "Arrival order must span all recorders.");
        await Complete(selected);
        var d = (await scheduler.SelectAsync(queue, [a, b], _ => 0, Defaults, default))!;
        Check(d.Row.Row.Id == "D", "Missing B and C must not delay D.");
        await queue.MarkPlayingAsync(d.Row.Scope, "D", default);
        Check(await queue.CountReadyAsync([a, b], _ => 0, d.Row.Row.ArrivalSequence, default) == 1,
            "The active clip and unavailable assets are absent from the READY count.");

        Check((await Observe(queue, b, "B", "b.wav", "patrol")).ArrivalSequence == missingB.ArrivalSequence &&
            (await Observe(queue, a, "C", "c.wav", "patrol")).ArrivalSequence == missingC.ArrivalSequence,
            "Late audio must reuse its initial sequence.");
        await Complete(d);
        while (await scheduler.SelectAsync(queue, [a, b], _ => 0, Defaults, default) is { } entry)
            await Complete(entry);
        Check(played.SequenceEqual(["A", "D", "B", "C", "E"]),
            "The recovery order must be A, D, B, C, E even when E and D share a talkgroup.");

        await Observe(queue, b, "failed", "stale.wav", "dispatch");
        var failed = (await scheduler.SelectAsync(queue, [a, b], _ => 0, Defaults, default))!.Row;
        await queue.DeferAudioAsync(b, failed.Row.Id, failed.Row.AudioRevision, default);
        await Observe(queue, b, "failed", "stale.wav", "dispatch", transcript: "Updated text");
        Check(await scheduler.SelectAsync(queue, [a, b], _ => 0, Defaults, default) == null,
            "Metadata alone must not retry a failed audio reference.");
        await Observe(queue, b, "failed", "new.wav", "dispatch");
        await queue.DeferAudioAsync(b, failed.Row.Id, failed.Row.AudioRevision, default);
        var recovered = await scheduler.SelectAsync(queue, [a, b], _ => 0, Defaults, default);
        Check(recovered?.Row.Row.ArrivalSequence == failed.Row.ArrivalSequence &&
            recovered?.Row.Row.AudioRevision > failed.Row.AudioRevision, "A stale failure cannot block fresh evidence.");
        Console.WriteLine("PASS Missing and failed audio rejoin once at a boundary in the original arrival order");

        async Task Complete(LivePlaybackScheduler.Scheduled item)
        {
            await queue.CommitCompletionAsync(item.Row.Scope, item.Row.Row.Id);
            scheduler.Complete(item.Choice);
            played.Add(item.Row.Row.Id);
        }
    }

    private static async Task ScopedManualReplayAsync(string path)
    {
        var queue = new DurableLiveQueue(new Paths(path));
        var a = new DurableLiveQueue.Run("https://recorder-a.test", 21, null, ServerId: ServerA);
        var b = new DurableLiveQueue.Run("https://recorder-b.test", 22, null, ServerId: ServerB);
        await queue.StartListeningAsync(a, Now, default);
        await queue.StartListeningAsync(b, Now, default);
        var at = (await Observe(queue, a, "tap", "tap.wav", "dispatch")).ArrivalSequence!.Value;
        await Observe(queue, b, "other-view", "other.wav", "dispatch");
        var replay = (await Observe(queue, a, "already-played", "previous.wav", "patrol")).ArrivalSequence!.Value;
        await queue.CommitCompletionAsync(a, "already-played");
        await Observe(queue, a, "same-group-as-tap", "next.wav", "dispatch");
        var favorites = CreateScheduler(Group(ServerA, "radio-system", "dispatch"), Group(ServerB, "radio-system", "dispatch"));
        var next = await favorites.SelectAsync(queue, [a], _ => 0, Defaults, default, at, [replay]);
        Check(next?.Row.Row.Id == "already-played" && next.Row.Row.IsCompletedReplay,
            "Manual replay merges eligible visible completed rows with waiting calls in the original view.");
        next = await favorites.SelectAsync(queue, [a], _ => 0, Defaults, default, replay, [replay]);
        Check(next?.Row.Row.Id == "same-group-as-tap" && await queue.CountReadyAsync([b], _ => 0, null, default) == 1,
            "Manual replay must not consume another view or prioritize its Favorite.");
        Console.WriteLine("PASS Durable manual replay stays in its original view and follows arrival-order FIFO");
    }

    private static async Task AutomaticJumpAtBoundaryAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path)
        {
            PlaybackOptionsForTest = Defaults with { WarningThreshold = 2, JumpThreshold = 3 }
        };
        await live.StartForTestAsync();
        await live.AddForTestAsync("active");
        var started = LiveHistoryHandoffTests.Signal();
        var release = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, cancellation) => id == "active" && started.TrySetResult()
            ? release.Task.WaitAsync(cancellation) : Task.CompletedTask;
        var traversal = live.StartQueueForTestAsync();
        await started.Task.WaitAsync(token);
        await live.AddForTestAsync("ready1");
        await live.AddForTestAsync("missing", audio: false);
        await live.AddForTestAsync("ready2");
        await live.ReadBacklogForTestAsync();
        Check(live.CallsWaiting == 2 && live.BacklogWarningForTest && !live.BacklogJumpForTest,
            "Warn at two READY waiting and exclude the active and missing clips.");
        await live.AddForTestAsync("ready3");
        await live.ReadBacklogForTestAsync();
        Check(live.BacklogJumpForTest && !traversal.IsCompleted && player.Started.SequenceEqual(["active"]),
            "The jump must latch behind the active clip.");
        await live.AddForTestAsync("arrived-after-threshold");
        release.SetResult();
        await traversal.WaitAsync(token);
        await live.DeliverForTestAsync("new").WaitAsync(token);
        Check(player.Completed.SequenceEqual(["active", "new"]) && !live.BacklogWarningForTest,
            "The boundary skips all prior arrivals and rearms the warning.");
        var warnings = new LiveBacklogGuard();
        var options = live.PlaybackOptionsForTest;
        Check(warnings.Observe(2, options, false) && !warnings.Observe(3, options, false) &&
            !warnings.Observe(1, options, false) && warnings.Observe(2, options, false),
            "Warnings publish once per episode and rearm below threshold.");
        Console.WriteLine("PASS Automatic Jump waits for active audio and skips arrivals through its Live boundary");
    }

    private static async Task ManualReplayExemptsJumpAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path)
        {
            PlaybackOptionsForTest = Defaults with { WarningThreshold = 2, JumpThreshold = 3 }
        };
        await live.StartForTestAsync();
        await live.AddForTestAsync("manual");
        var started = LiveHistoryHandoffTests.Signal();
        var release = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, cancellation) => id == "manual" && started.TrySetResult()
            ? release.Task.WaitAsync(cancellation) : Task.CompletedTask;
        var selection = live.SelectForTestAsync("manual");
        await started.Task.WaitAsync(token);
        foreach (var id in new[] { "ready1", "ready2", "ready3", "ready4" })
            await live.AddForTestAsync(id);
        await live.ReadBacklogForTestAsync();
        Check(live.BacklogWarningForTest && !live.BacklogJumpForTest,
            "An intentional replay shows the warning without arming the jump.");
        release.SetResult();
        await selection.WaitAsync(token);
        await live.ResumedQueue.WaitAsync(token);
        Check(player.Completed.SequenceEqual(["manual", "ready1", "ready2", "ready3", "ready4"]),
            "Manual replay above the automatic limit must continue FIFO.");
        Console.WriteLine("PASS Manual replay preserves every eligible call above the automatic Jump threshold");
    }

    private static async Task ManualPreparationFailureAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync();
        await live.AddForTestAsync("active");
        await live.AddForTestAsync("unavailable", audio: false);
        var started = LiveHistoryHandoffTests.Signal();
        var release = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, cancellation) => started.TrySetResult() ? release.Task.WaitAsync(cancellation) : Task.CompletedTask;
        live.PrepareForTest = (id, _) => id == "unavailable"
            ? Task.FromException(new IOException("Synthetic audio preparation failure.")) : Task.CompletedTask;
        var original = live.StartQueueForTestAsync();
        await started.Task.WaitAsync(token);
        await live.SelectForTestAsync("unavailable").WaitAsync(token);
        Check(!original.IsCompleted && player.Started.SequenceEqual(["active"]),
            "An unavailable manual selection must leave the existing audio and traversal intact.");
        release.SetResult();
        await original.WaitAsync(token);
        Check(player.Completed.SequenceEqual(["active"]), "The active clip must finish normally.");
        Console.WriteLine("PASS Failed manual preparation leaves the existing clip and traversal intact");
    }

    private static async Task SilentArrivalsAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync();
        await live.SetAudioForTestAsync(false);
        await live.AddForTestAsync("silent1");
        await live.AddForTestAsync("silent2");
        await live.StartQueueForTestAsync();
        Check(player.Started.IsEmpty && live.CallsWaiting == 0 && !await live.IsPendingForTestAsync("silent1"),
            "Audio Off processes arrivals without preparing automatic audio.");
        await live.SelectForTestAsync("silent1");
        await live.SetAudioForTestAsync(true);
        await live.DeliverForTestAsync("new");
        Check(player.Completed.SequenceEqual(["silent1", "new"]),
            "Audio Off permits explicit one-shot playback and Audio On plays only new automatic arrivals.");
        Console.WriteLine("PASS Audio Off processes visible arrivals silently and leaves explicit replay available");
    }

    private static TalkgroupKey Group(Guid server, string system, string group) =>
        TalkgroupKey.From(server, new CallCardMetadata { SystemId = system, TalkgroupId = group })!.Value;

    private static LivePlaybackScheduler CreateScheduler(params TalkgroupKey[] favorites) =>
        new(new FavoriteTalkgroupSnapshot(favorites));

    private static List<long> Drain(LivePlaybackScheduler scheduler, List<LivePlaybackScheduler.Candidate> queue,
        LivePlaybackPreferences options)
    {
        var played = new List<long>();
        while (scheduler.Choose(queue, options) is { } selected)
        {
            played.Add(selected.Candidate.Arrival);
            scheduler.Complete(selected);
            queue.Remove(selected.Candidate);
        }
        return played;
    }

    private static Task<DurableLiveQueue.Observation> Observe(DurableLiveQueue queue, DurableLiveQueue.Run run,
        string id, string? audio, string talkgroup, string? transcript = null) =>
        queue.ObserveWithPositionAsync(run, id, Now.AddHours(-4), new LiveCallSnapshot(
            new CallCardMetadata { SystemId = "radio-system", TalkgroupId = talkgroup }, transcript,
            CallCardIdFallback.None, audio == null ? null : $"{run.Server}/{audio}"), default,
            RecordingOrigin.Live, existingOnly: false);

    private static void Check(bool condition, string message) => LiveHistoryHandoffTests.Check(condition, message);

    private sealed record Paths(string DbPath) : IDatabasePathProvider;
}
