using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;

internal static class VerifiedPlaybackLagTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 19, 0, 0, TimeSpan.Zero);
    private static readonly Guid A = Guid.Parse("26395dd6-6d3e-4214-bc13-0176439271ad");
    private static readonly Guid B = Guid.Parse("6dab5464-c782-4da5-b4ce-ed0a91a1b9d1");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync()
    {
        await TestAsync("Verified lag must strictly exceed the threshold and requires two matching waiting recordings", ThresholdsAsync);
        await TestAsync("Uncertain clocks, incomplete metadata, and a single bad timestamp cannot verify a Jump", IndeterminateAsync);
        await TestAsync("Verified lag preferences persist independently of mobile and count safeguards", PreferencesAsync);
        await TestAsync("Scoped lag recovery waits for the audio boundary and preserves the other recorder and call cards", BoundaryAsync);
        await TestAsync("Manual selection, source switching, and History fence obsolete lag evidence", OwnershipAsync);
        await TestAsync("Stop, restart and replacement reject an obsolete recorder's lag commit", RetirementAsync);
    }

    private static async Task ThresholdsAsync(string path)
    {
        var clock = new Clock(Now);
        var queue = new DurableLiveQueue(new TestPaths(path));
        foreach (var (index, expectedSeconds, choice, jumps) in new[]
        {
            (1, 300, new PlaybackLagRecoveryPreference(), false),
            (2, 299, new PlaybackLagRecoveryPreference(), false),
            (3, 301, new PlaybackLagRecoveryPreference(), true),
            (4, 3, new PlaybackLagRecoveryPreference(PlaybackLagRecoveryPreference.Immediately), true),
            (5, 400, new PlaybackLagRecoveryPreference(PlaybackLagRecoveryPreference.Never), false),
            (6, 719, new PlaybackLagRecoveryPreference(PlaybackLagRecoveryPreference.Custom, 12), false),
            (7, 721, new PlaybackLagRecoveryPreference(PlaybackLagRecoveryPreference.Custom, 12), true)
        })
        {
            var run = new DurableLiveQueue.Run($"https://recorder-{index}.example", index, null);
            await queue.StartListeningAsync(run, Now.AddMinutes(-20), default);
            var (evidence, old) = Proof(clock, expectedSeconds);
            foreach (var row in old)
                await queue.ObserveAsync(run, row.Id, row.Start, Audio(row.Id), default, RecordingOrigin.Live);
            var result = await queue.TryAdvanceVerifiedLagAsync(run, 0, null, evidence, choice, Commit, default);
            Check(result.Lag?.TotalSeconds == expectedSeconds && result.Floor.HasValue == jumps,
                $"Unexpected decision for lower bound {expectedSeconds}s and threshold {choice}.");
            Check(await queue.CountReadyAsync([run], _ => 0, null, default) == (jumps ? 0 : 2),
                "Only a verified threshold crossing may advance the floor.");
        }
    }

    private static async Task IndeterminateAsync(string path)
    {
        var clock = new Clock(Now);
        var edge = Edge(clock);
        var (evidence, old) = Proof(clock, 301);
        var run = new DurableLiveQueue.Run("https://uncertain.example", 1, null);
        var queue = new DurableLiveQueue(new TestPaths(path));
        await queue.StartListeningAsync(run, Now.AddMinutes(-10), default);
        foreach (var row in old)
            await queue.ObserveAsync(run, row.Id, row.Start, Audio(row.Id), default, RecordingOrigin.Live);
        foreach (var rows in new PlaybackLagEvidence.Stamp?[][]
        {
            [edge[0], edge[1], old[0], null],
            [edge[0], edge[1], old[0], old[0]],
            [edge[0], edge[1], old[0], old[1] with { Start = Now.AddDays(-12) }],
            [edge[0], edge[1], old[0], old[1] with { Start = Now.AddDays(3) }],
            [..edge.Select(item => item with { Start = item.Start.AddMinutes(-8), End = item.End.AddMinutes(-8) }), ..old]
        })
        {
            Check(PlaybackLagEvidence.VerifyRecorderSnapshot(rows, 4, Now.AddMinutes(-10), Now,
                clock, clock.GetTimestamp()) == null, "Incomplete, repeated, implausible or skewed recorder timestamps must fail closed.");
        }
        Check(PlaybackLagEvidence.VerifyRecorderSnapshot([..edge, ..old], 5, Now.AddMinutes(-10), Now,
            clock, clock.GetTimestamp()) == null, "Partial snapshots cannot verify playback lag.");
        Check(PlaybackLagEvidence.Stamp.Read("no-timezone", "2026-11-24T19:00:00", 5) == null &&
            PlaybackLagEvidence.Stamp.Read("no-length", Now.ToUnixTimeSeconds().ToString(), null) == null,
            "Lag requires explicit UTC metadata and a real duration.");

        await queue.CommitCompletionAsync(run, old[0].Id);
        var outcome = await queue.TryAdvanceVerifiedLagAsync(run, 0, null, evidence,
            new(PlaybackLagRecoveryPreference.Immediately), Commit, default);
        Check(outcome.Lag == null && outcome.Floor == null, "One remaining ready timestamp cannot verify actual lag.");
        clock.Advance(TimeSpan.FromMinutes(3));
        Check(evidence.Measure(old.Select(row => (row.Id, (long?)row.Start.UtcTicks))) == null,
            "Stalled evaluation must not act on an old snapshot.");
        Check((await queue.ReadReadyWindowAsync([run], _ => 0, 2, default)).Single().Row.Id == old[1].Id,
            "Unverified recovery must leave traversal intact.");
    }

    private static Task PreferencesAsync(string path)
    {
        Check(LivePlaybackPreferences.Read().VerifiedLagRecovery == new PlaybackLagRecoveryPreference(),
            "The clean settings store must default to five minutes.");
        var persisted = LivePlaybackPreferences.Default with
        {
            VerifiedLagRecovery = new(PlaybackLagRecoveryPreference.Custom, 1440)
        };
        var mobile = MobileAudioPreferencesStore.Read(isIos: false);
        persisted.Save();
        Check(LivePlaybackPreferences.Read() == persisted &&
            MobileAudioPreferencesStore.Read(isIos: false) == mobile, "Custom lag must survive a reload independently.");
        Check(LivePlaybackPreferences.Parse("""{"GroupCalls":false}""").VerifiedLagRecovery == new PlaybackLagRecoveryPreference() &&
            LivePlaybackPreferences.Parse("""{"VerifiedLagRecovery":{"Minutes":-2,"CustomMinutes":1441}}""").VerifiedLagRecovery ==
            new PlaybackLagRecoveryPreference(), "Legacy or invalid options must recover to a valid default.");
        var guard = new LiveBacklogGuard();
        var never = persisted with { VerifiedLagRecovery = new(PlaybackLagRecoveryPreference.Never) };
        Check(guard.Observe(80, never, false) && !guard.JumpPending, "The 80-call warning remains independent.");
        guard.Observe(100, never, false);
        Check(guard.JumpPending, "Never must leave the 100-ready-call protection enabled.");
        return Task.CompletedTask;
    }
    private static async Task BoundaryAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await OpenAsync(path, player);
        var active = Signal();
        var finish = Signal();
        player.Playing = async (id, token) =>
        {
            if (id == "b-playing")
            {
                active.TrySetResult();
                await finish.Task.WaitAsync(token);
            }
        };
        await live.AddForTestAsync("b-playing", serverId: B);
        var playing = live.StartQueueForTestAsync();
        await active.Task.WaitAsync(Timeout);
        var complete = await AddLagForTestAsync(live);
        await live.AddForTestAsync("b-waiting", serverId: B);
        await complete();
        Check(player.Started.SequenceEqual(["b-playing"]) && await live.IsPendingForTestAsync("a-old-1"),
            "A verified result may not interrupt the active B clip or jump inside it.");
        finish.SetResult();
        await playing.WaitAsync(Timeout);
        Check(player.Completed.SequenceEqual(["b-playing", "b-waiting"]) && !await live.IsPendingForTestAsync("a-old-1") &&
            live.DisplayedCallsForTest == 4, "Only A's verified automatic backlog must be skipped at the boundary.");
        await live.AddForTestAsync("a-fresh", serverId: A);
        await live.StartQueueForTestAsync().WaitAsync(Timeout);
        live.VisibleServerForTest = A;
        await live.SelectForTestAsync("a-old-1").WaitAsync(Timeout);
        await live.ResumedQueue.WaitAsync(Timeout);
        Check(player.Completed.Take(4).SequenceEqual(["b-playing", "b-waiting", "a-fresh", "a-old-1"]) &&
            !player.Started.Contains("a-old-2"), "Explicit replay still works without reviving skipped automatic audio.");
        await live.StopForTestAsync();
    }

    private static async Task OwnershipAsync(string path)
    {
        var ownership = new PlaybackOwnership();
        var player = new HandoffAudio();
        var live = await OpenAsync(path, player, ownership);
        var complete = await AddLagForTestAsync(live);
        var preparing = Signal();
        var finish = Signal();
        live.PrepareForTest = async (id, token) =>
        {
            if (id == "explicit")
            {
                preparing.TrySetResult();
                await finish.Task.WaitAsync(token);
            }
        };
        await live.AddForTestAsync("explicit", serverId: B);
        var manual = live.SelectForTestAsync("explicit");
        await preparing.Task.WaitAsync(Timeout);
        await complete();
        Check(await live.IsPendingForTestAsync("a-old-1"), "An explicit preparation must retire older lag evidence.");
        finish.SetResult();
        await manual.WaitAsync(Timeout);
        await live.ResumedQueue.WaitAsync(Timeout);

        live.PlaybackOptionsForTest = live.PlaybackOptionsForTest with { SourceMode = LiveAudioSourceMode.SelectedOnly };
        Check(await live.WaitForSourceForTestAsync().WaitAsync(Timeout), "The initial selected source must be active.");
        // A source change wakes the automatic queue. Let that wake finish before
        // seeding the next proof, so normal playback cannot consume its test rows.
        await live.ResumedQueue.WaitAsync(Timeout);
        var previousSource = await AddLagForTestAsync(live, "-source");
        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(B));
        Check(await live.WaitForSourceForTestAsync().WaitAsync(Timeout), "The alternate source must be active.");
        await previousSource();
        Check(await live.IsPendingForTestAsync("a-old-1-source"),
            "A reply captured for the previous source may not change its floor after the switch.");

        var previousHistoryOwner = await AddLagForTestAsync(live, "-history");
        var history = new HistoryViewModel(ownership, player);
        history.OpenForTest();
        await history.PlayForTestAsync().WaitAsync(Timeout);
        await history.OnPageClosedAsync();
        await previousHistoryOwner();
        await live.ResumedQueue.WaitAsync(Timeout);
        Check(await live.IsPendingForTestAsync("a-old-1-history") && player.Completed.Contains("history"),
            "A late recorder proof may not mutate the released History lease or the new Live lease.");
        await live.StopForTestAsync();
    }

    private static async Task RetirementAsync(string path)
    {
        var clock = new Clock(Now);
        var queue = new DurableLiveQueue(new TestPaths(path));
        var oldRun = new DurableLiveQueue.Run("https://before.example", 1, null, ServerId: A);
        var (proof, old) = Proof(clock, 450);
        await queue.StartListeningAsync(oldRun, Now.AddMinutes(-10), default);
        foreach (var row in old)
            await queue.ObserveAsync(oldRun, row.Id, row.Start, Audio(row.Id), default, RecordingOrigin.Live);
        var audio = new PlaybackLagGeneration();
        var lease = audio.Capture();
        var rollback = await queue.TryAdvanceVerifiedLagAsync(oldRun, 0, null, proof, new(), action =>
        {
            audio.Retire();
            return lease.TryCommit(action);
        }, default);
        Check(rollback.Superseded && await queue.CountReadyAsync([oldRun], _ => 0, null, default) == 2,
            "Retirement at the SQLite boundary must roll back both the floor and pending dispositions.");

        await queue.StopAsync(oldRun);
        var replacement = oldRun with { Owner = 2, Server = "https://after.example" };
        await queue.StartListeningAsync(replacement, Now, default);
        await queue.ObserveAsync(replacement, "replacement", Now, Audio("replacement"), default, RecordingOrigin.Live);
        var obsolete = await queue.TryAdvanceVerifiedLagAsync(oldRun, 0, null, proof, new(), _ => false, default);
        Check(obsolete.Floor == null && (await queue.NextAsync(replacement, 0, default))?.Id == "replacement",
            "A stopped owner may not apply old-origin evidence against the same permanent recorder ID.");
    }

    private static async Task<Func<Task>> AddLagForTestAsync(MainViewModel live, string suffix = "")
    {
        var clock = new Clock(Now);
        var (evidence, old) = Proof(clock, 400, suffix);
        foreach (var row in old)
            await live.AddForTestAsync(row.Id, serverId: A, recordedAt: row.Start);
        return await live.HoldPlaybackLagForTestAsync(A, Now.AddMinutes(-10), evidence);
    }

    private static async Task<MainViewModel> OpenAsync(string path, HandoffAudio player, PlaybackOwnership? ownership = null)
    {
        var live = new MainViewModel(ownership ?? new PlaybackOwnership(), player, path);
        live.ConfigureAudioSourceRecordersForTest(A, B);
        live.PlaybackOptionsForTest = new(GroupCalls: false);
        await live.StartForTestAsync();
        return live;
    }

    private static (PlaybackLagEvidence Evidence, PlaybackLagEvidence.Stamp[] Old) Proof(Clock clock, int lag, string suffix = "")
    {
        var edge = Edge(clock);
        var old = new[]
        {
            Stamp("a-old-1" + suffix, clock.GetUtcNow().AddSeconds(-lag - 13)),
            Stamp("a-old-2" + suffix, clock.GetUtcNow().AddSeconds(-lag - 11))
        };
        var evidence = PlaybackLagEvidence.VerifyRecorderSnapshot([..edge, ..old], 4, Now.AddMinutes(-20),
            Now, clock, clock.GetTimestamp());
        return (evidence ?? throw new InvalidOperationException("Synthetic snapshot must verify."), old);
    }

    private static PlaybackLagEvidence.Stamp[] Edge(Clock clock) =>
        [Stamp("fresh-1", clock.GetUtcNow().AddSeconds(-5)), Stamp("fresh-2", clock.GetUtcNow().AddSeconds(-10))];

    private static PlaybackLagEvidence.Stamp Stamp(string id, DateTimeOffset time) => new(id, time, time.AddSeconds(1));
    private static LiveCallSnapshot Audio(string id) =>
        new(new CallCardMetadata(), null, CallCardIdFallback.None, "https://recorder.example/" + id + ".wav");
    private static bool Commit(Action action) { action(); return true; }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) => LiveHistoryHandoffTests.Check(condition, message);

    private static async Task TestAsync(string name, Func<string, Task> test)
    {
        var originalDatabase = AppPaths.DatabaseDirectory;
        var directory = Directory.CreateTempSubdirectory("JoesScanner-VerifiedLag-");
        try
        {
            AppPaths.DatabaseDirectory = directory.FullName;
            await test(Path.Combine(directory.FullName, "queue.db"));
            Console.WriteLine("PASS " + name);
        }
        finally
        {
            AppPaths.DatabaseDirectory = originalDatabase;
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed record TestPaths(string DbPath) : IDatabasePathProvider;
}
