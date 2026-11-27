using JoesScanner.Services;
using JoesScanner.ViewModels;

internal static partial class IntelligentLivePlaybackTests
{
    private static readonly TimeSpan FavoritesTimeout = TimeSpan.FromSeconds(10);

    private static async Task RunFavoritesTraversalAsync(string directory)
    {
        var previous = AppPaths.DatabaseDirectory;
        AppPaths.DatabaseDirectory = directory;
        try
        {
            await FavoritesArrivalOrderAsync(Path.Combine(directory, "favorites-arrival.db"));
            await FavoritesCompletedAndViewAsync(Path.Combine(directory, "favorites-completed.db"));
            await FavoritesFiltersAndLateAudioAsync(Path.Combine(directory, "favorites-filters.db"));
            await FavoritesMembershipChangesAsync(Path.Combine(directory, "favorites-membership.db"));
            await FavoritesSelectionRequestsAsync(Path.Combine(directory, "favorites-requests.db"));
            await FavoritesBoundaryCanceledAsync(Path.Combine(directory, "favorites-stop.db"), stop: true);
            await FavoritesBoundaryCanceledAsync(Path.Combine(directory, "favorites-jump.db"), stop: false);
            await FavoritesOneShotAsync(Path.Combine(directory, "favorites-one-shot.db"));
            await FavoritesSourceChangeAsync(Path.Combine(directory, "favorites-source-change.db"));
        }
        finally
        {
            AppPaths.DatabaseDirectory = previous;
        }
    }

    private static async Task<MainViewModel> OpenFavoritesAsync(string path, HandoffAudio player,
        params (Guid Server, string Talkgroup)[] members)
    {
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        live.ConfigureAudioSourceRecordersForTest(ServerA, ServerB);
        await live.SetAudioFavoritesForTestAsync(members);
        live.UseFavoritesViewForTest(live.IsFavoriteForTest);
        await live.StartForTestAsync();
        return live;
    }

    private static async Task FavoritesArrivalOrderAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await OpenFavoritesAsync(path, player, (ServerA, "101"), (ServerB, "202"));
        live.AudioIdentityForTest = card => (card.ServerId == ServerA ? "A/" : "B/") + card.BackendId;
        live.PlaybackSpeedStep = 1;
        live.PlaybackOptionsForTest = Defaults with { WarningThreshold = 2, JumpThreshold = 3 };
        await live.AddForTestAsync("same-id", serverId: ServerA, talkgroup: "101", recordedAt: Now.AddMinutes(5));
        await live.AddForTestAsync("ordinary-old", serverId: ServerB, talkgroup: "101", recordedAt: Now);
        await live.AddForTestAsync("same-id", serverId: ServerB, talkgroup: "202", recordedAt: Now.AddMinutes(-5));
        await live.AddForTestAsync("ordinary-newer", serverId: ServerA, talkgroup: "303");

        var entered = LiveHistoryHandoffTests.Signal();
        var finish = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, token) =>
        {
            if (id != "A/same-id")
                return Task.CompletedTask;
            entered.TrySetResult();
            return finish.Task.WaitAsync(token);
        };
        var tap = live.PressVisibleCardForTestAsync(live.CardForTest("same-id", ServerA));
        await entered.Task.WaitAsync(FavoritesTimeout);
        await live.ReadBacklogForTestAsync();
        Check(live.BacklogWarningForTest && !live.BacklogJumpForTest && player.Started.SequenceEqual(["A/same-id"]),
            "Favorites replay must show the backlog warning without starting an automatic Jump.");
        finish.SetResult();
        await tap.WaitAsync(FavoritesTimeout);
        await live.ResumedQueue.WaitAsync(FavoritesTimeout);

        Check(player.Completed.SequenceEqual(["A/same-id", "B/same-id", "A/ordinary-newer"]),
            "Recorder-scoped Favorites must play in arrival order, then the newer ordinary call may play automatically.");
        Check(player.Rates.SequenceEqual([1.25, 1, 1]),
            "Favorites catch-up speed must only consider later Favorites. The ordinary tail is separate.");
        Check(await live.IsPendingForTestAsync("ordinary-old", ServerB) &&
            !await live.IsPendingForTestAsync("same-id", ServerA) && !await live.IsPendingForTestAsync("same-id", ServerB),
            "A bypassed ordinary recording stays pending below the floor. Each recorder's Favorite completes independently.");
        Check(!live.AutoPlayForTest && live.OpenedTransportsForTest == 2,
            "Manual replay must preserve AutoPlay and both recorder transports.");
        Console.WriteLine("PASS Favorites traverse SQLite arrival order across duplicate IDs, preserve seek state, and resume automatic audio");
    }

    private static async Task FavoritesCompletedAndViewAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await OpenFavoritesAsync(path, player, (ServerA, "1"), (ServerB, "2"));
        await live.AddForTestAsync("selected", talkgroup: "1");
        await live.AddForTestAsync("completed-shown", serverId: ServerB, talkgroup: "2");
        await live.AddForTestAsync("completed-offscreen", serverId: ServerB, talkgroup: "2", visible: false);
        await live.AddForTestAsync("completed-ordinary", talkgroup: "3");
        await live.AddForTestAsync("pending-tail", talkgroup: "1");
        await live.MarkCompletedForTestAsync("completed-shown", ServerB);
        await live.MarkCompletedForTestAsync("completed-offscreen", ServerB);
        await live.MarkCompletedForTestAsync("completed-ordinary");

        var entered = LiveHistoryHandoffTests.Signal();
        var finish = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, token) =>
        {
            if (id != "selected")
                return Task.CompletedTask;
            entered.TrySetResult();
            return finish.Task.WaitAsync(token);
        };
        var tap = live.PressVisibleCardForTestAsync(live.CardForTest("selected"));
        await entered.Task.WaitAsync(FavoritesTimeout);
        live.EvictCardForTest(live.CardForTest("completed-shown", ServerB));
        live.UseFavoritesViewForTest(null);
        live.VisibleServerForTest = ServerA;
        Check(player.Started.SequenceEqual(["selected"]) && !tap.IsCompleted,
            "Changing the displayed view must leave the native selection alone.");
        finish.SetResult();
        await tap.WaitAsync(FavoritesTimeout);
        await live.ResumedQueue.WaitAsync(FavoritesTimeout);
        Check(player.Completed.SequenceEqual(["selected", "completed-shown", "pending-tail"]),
            "Replay only completed Favorites captured in the originating view, even after changing views or eviction.");
        Check(!await live.IsPendingForTestAsync("completed-shown", ServerB),
            "Replaying a completed row must preserve its terminal disposition.");
        Console.WriteLine("PASS Favorites replay captured completed cards with compacted payloads after changing the displayed recorder");
    }

    private static async Task FavoritesFiltersAndLateAudioAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await OpenFavoritesAsync(path, player, (ServerA, "1"));
        await live.AddForTestAsync("selected-muted", talkgroup: "1");
        await live.AddForTestAsync("muted", talkgroup: "1");
        await live.AddForTestAsync("disabled", talkgroup: "1");
        await live.AddForTestAsync("unavailable", talkgroup: "1", audio: false);
        await live.AddForTestAsync("late", talkgroup: "1", audio: false);
        await live.AddForTestAsync("failed-prepare", talkgroup: "1");
        await live.AddForTestAsync("final", talkgroup: "1");
        live.MutedForTest = "selected-muted";
        live.HiddenForTest = "disabled";
        live.PrepareForTest = (id, _) => id == "failed-prepare"
            ? Task.FromException(new IOException("Synthetic unavailable recording."))
            : Task.CompletedTask;

        var entered = LiveHistoryHandoffTests.Signal();
        var finish = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, token) =>
        {
            if (id != "selected-muted")
                return Task.CompletedTask;
            entered.TrySetResult();
            return finish.Task.WaitAsync(token);
        };
        var tap = live.PressVisibleCardForTestAsync(live.CardForTest("selected-muted"));
        await entered.Task.WaitAsync(FavoritesTimeout);
        live.MutedForTest = "muted";
        await live.AddForTestAsync("late", talkgroup: "1", visible: false);
        finish.SetResult();
        await tap.WaitAsync(FavoritesTimeout);
        await live.ResumedQueue.WaitAsync(FavoritesTimeout);
        await live.AddForTestAsync("unavailable", talkgroup: "1", visible: false);
        await live.StartQueueForTestAsync().WaitAsync(FavoritesTimeout);

        Check(player.Completed.SequenceEqual(["selected-muted", "late", "final"]),
            "Only the explicit selection can bypass filtering. In-order late audio plays once and failed or missing audio cannot stall.");
        Check(!await live.IsPendingForTestAsync("muted") && !await live.IsPendingForTestAsync("disabled") &&
            await live.IsPendingForTestAsync("unavailable"), "Filtered candidates are skipped. Audio repaired behind the floor stays reviewable.");
        Console.WriteLine("PASS Favorites respect Mute and Disable, bypass unavailable audio, and admit late audio ahead of the cursor");
    }

    private static async Task FavoritesMembershipChangesAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await OpenFavoritesAsync(path, player, (ServerA, "1"), (ServerA, "2"), (ServerB, "4"));
        await live.AddForTestAsync("now-playing", talkgroup: "1");
        await live.AddForTestAsync("removed", talkgroup: "2");
        await live.AddForTestAsync("added-completed", talkgroup: "3");
        await live.MarkCompletedForTestAsync("added-completed");
        await live.AddForTestAsync("added-pending", talkgroup: "3");
        await live.AddForTestAsync("retained", serverId: ServerB, talkgroup: "4");

        var entered = LiveHistoryHandoffTests.Signal();
        var finish = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, token) =>
        {
            if (id != "now-playing")
                return Task.CompletedTask;
            entered.TrySetResult();
            return finish.Task.WaitAsync(token);
        };
        var tap = live.PressVisibleCardForTestAsync(live.CardForTest("now-playing"));
        await entered.Task.WaitAsync(FavoritesTimeout);
        await live.SetAudioFavoritesForTestAsync((ServerA, "3"), (ServerB, "4"));
        Check(!tap.IsCompleted && player.Started.SequenceEqual(["now-playing"]),
            "Removing the current Favorite must leave its audio alone.");
        finish.SetResult();
        await tap.WaitAsync(FavoritesTimeout);
        await live.ResumedQueue.WaitAsync(FavoritesTimeout);
        Check(player.Completed.SequenceEqual(["now-playing", "added-pending", "retained"]) &&
            await live.IsPendingForTestAsync("removed"), "The next boundary must use current membership without adding completed cards from outside the capture.");
        Console.WriteLine("PASS Membership changes affect the next Favorites choice without interrupting the clip or expanding completed replay");
    }

    private static async Task FavoritesSelectionRequestsAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await OpenFavoritesAsync(path, player, (ServerA, "1"));
        foreach (var id in new[] { "playing", "fail", "obsolete", "newest", "tail" })
            await live.AddForTestAsync(id, talkgroup: "1");
        var entered = LiveHistoryHandoffTests.Signal();
        var holdCurrent = LiveHistoryHandoffTests.Signal();
        var preparingOld = LiveHistoryHandoffTests.Signal();
        var obsoleteReply = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, token) =>
        {
            if (id != "playing")
                return Task.CompletedTask;
            entered.TrySetResult();
            return holdCurrent.Task.WaitAsync(token);
        };
        live.PrepareForTest = async (id, _) =>
        {
            if (id == "fail")
                throw new IOException("Synthetic missing Favorite.");
            if (id == "obsolete")
            {
                preparingOld.TrySetResult();
                await obsoleteReply.Task;
            }
        };
        var original = live.PressVisibleCardForTestAsync(live.CardForTest("playing"));
        await entered.Task.WaitAsync(FavoritesTimeout);
        await live.PressVisibleCardForTestAsync(live.CardForTest("fail")).WaitAsync(FavoritesTimeout);
        Check(player.Started.SequenceEqual(["playing"]) && !original.IsCompleted,
            "A failed Favorite preparation must preserve the active clip and its traversal.");

        var obsolete = live.PressVisibleCardForTestAsync(live.CardForTest("obsolete"));
        await preparingOld.Task.WaitAsync(FavoritesTimeout);
        await live.PressVisibleCardForTestAsync(live.CardForTest("newest")).WaitAsync(FavoritesTimeout);
        obsoleteReply.SetResult();
        await Task.WhenAll(original, obsolete, live.ResumedQueue).WaitAsync(FavoritesTimeout);
        Check(player.Started.SequenceEqual(["playing", "newest", "tail"]) && player.Completed.SequenceEqual(["newest", "tail"]),
            "Only the latest successful selection can replace Favorites playback or move its floor.");
        Console.WriteLine("PASS Failed and superseded Favorites preparations preserve the right native owner and traversal");
    }

    private static async Task FavoritesBoundaryCanceledAsync(string path, bool stop)
    {
        var player = new HandoffAudio();
        var live = await OpenFavoritesAsync(path, player, (ServerA, "1"));
        await live.AddForTestAsync("selected", talkgroup: "1");
        await live.AddForTestAsync("retired-favorite", talkgroup: "1");
        await live.AddForTestAsync("newest-ordinary", talkgroup: "2");
        var delayed = live.HoldNextSelection();
        await live.PressVisibleCardForTestAsync(live.CardForTest("selected")).WaitAsync(FavoritesTimeout);
        await delayed.Reached.Task.WaitAsync(FavoritesTimeout);
        var oldTraversal = live.ResumedQueue;

        if (stop)
            await live.StopForTestAsync().WaitAsync(FavoritesTimeout);
        else
            await live.JumpForTestAsync().WaitAsync(FavoritesTimeout);
        delayed.Return.SetResult();
        await oldTraversal.WaitAsync(FavoritesTimeout);
        await live.ResumedQueue.WaitAsync(FavoritesTimeout);
        Check(player.Completed.SequenceEqual(stop ? new[] { "selected" } : ["selected", "newest-ordinary"]),
            "The retired durable Favorites read must stay fenced after the Stop or Jump action.");
        if (stop)
            await live.PressPlayStopForTestAsync().WaitAsync(FavoritesTimeout);
        await live.AddForTestAsync("fresh", talkgroup: "2");
        await live.StartQueueForTestAsync().WaitAsync(FavoritesTimeout);
        await live.ResumedQueue.WaitAsync(FavoritesTimeout);
        Check(player.Completed.LastOrDefault() == "fresh" && !player.Started.Contains("retired-favorite"),
            "Later arrivals must resume in the regular queue without reviving the retired Favorites pass.");
        Console.WriteLine(stop
            ? "PASS Stop fences the old Favorites read and fresh Play starts at Live"
            : "PASS Jump to Live fences a delayed Favorites read and switches to normal audio");
    }

    private static async Task FavoritesOneShotAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await OpenFavoritesAsync(path, player, (ServerA, "1"));
        await live.SetAudioForTestAsync(false);
        await live.AddForTestAsync("first", talkgroup: "1");
        await live.AddForTestAsync("second", talkgroup: "1");
        await live.PressVisibleCardForTestAsync(live.CardForTest("first")).WaitAsync(FavoritesTimeout);
        Check(player.Completed.SequenceEqual(["first"]) && !live.IsAudioEnabledForTest,
            "Running Favorites with Audio Off must play just the explicit selection.");
        await live.StopForTestAsync();
        await live.PressVisibleCardForTestAsync(live.CardForTest("second")).WaitAsync(FavoritesTimeout);
        Check(player.Completed.SequenceEqual(["first", "second"]) && live.OpenedTransportsForTest == 2 &&
            !live.IsAudioEnabledForTest && !live.AutoPlayForTest, "Stopped Favorites must stay one-shot with both settings and transports unchanged.");
        Console.WriteLine("PASS Favorites taps stay one-shot with Audio Off or monitoring stopped");
    }

    private static Task FavoritesSourceChangeAsync(string path) => PlaybackTestUiContext.RunAsync(async ui =>
    {
        await SwitchFavoritesSourceAsync(ui, path, afterSeek: false);
        await SwitchFavoritesSourceAsync(ui, Path.ChangeExtension(path, "after.db"), afterSeek: true);
    });

    private static async Task SwitchFavoritesSourceAsync(PlaybackTestUiContext ui, string path, bool afterSeek)
    {
        var player = new HandoffAudio();
        var live = await OpenFavoritesAsync(path, player, (ServerA, "1"), (ServerB, "2"));
        // Select Favorites before enabling selected-only playback so setup starts a single queue read.
        live.ChooseLiveAudioSource(LiveAudioSource.Favorites);
        var initialRead = live.HoldNextSelection();
        live.PlaybackOptionsForTest = Defaults with { SourceMode = LiveAudioSourceMode.SelectedOnly };
        await initialRead.Reached.Task.WaitAsync(FavoritesTimeout);
        initialRead.Return.SetResult();
        await live.ResumedQueue.WaitAsync(FavoritesTimeout);
        await live.AddForTestAsync("selected", talkgroup: "1");
        await live.AddForTestAsync("old-favorite", serverId: ServerB, talkgroup: "2");

        var entered = LiveHistoryHandoffTests.Signal();
        var finish = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, token) =>
        {
            Check(SynchronizationContext.Current == ui, "Playback must start on the test UI context.");
            if (id != "selected")
                return Task.CompletedTask;
            entered.TrySetResult();
            return finish.Task.WaitAsync(token);
        };
        var card = live.CardForTest("selected");
        Check(card.LiveArrivalSequence > live.PlaybackFloorForTest, "The selected Favorite must be ahead of the playback floor.");
        var releaseWriter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = live.HoldJournalForTestAsync(releaseWriter.Task);
        Task selection;
        try
        {
            selection = live.PressVisibleCardForTestAsync(card);
            await entered.Task.WaitAsync(FavoritesTimeout);
            if (afterSeek)
            {
                releaseWriter.SetResult(true);
                await ui.WaitForStateAsync(() => live.PlaybackFloorForTest == card.LiveArrivalSequence, FavoritesTimeout);
            }

            Check(SynchronizationContext.Current == ui && live.ManualNativeRequestActiveForTest && player.Completed.IsEmpty,
                "The source switch must run on the UI context while the selected manual request owns native playback.");
            Check((live.PlaybackFloorForTest == card.LiveArrivalSequence) == afterSeek,
                "The Favorites seek must be on the intended side of validation when the source changes.");
            live.ChooseLiveAudioSource(LiveAudioSource.Recorder(ServerB));
            Check(!selection.IsCompleted && player.Started.SequenceEqual(["selected"]),
                "An audio-source change must allow the current native recording to finish.");
        }
        finally
        {
            releaseWriter.TrySetResult(true);
            finish.TrySetResult();
        }
        await writer.WaitAsync(FavoritesTimeout);
        await selection.WaitAsync(FavoritesTimeout);
        Check(await live.WaitForSourceForTestAsync().WaitAsync(FavoritesTimeout),
            "Recorder B must become the effective automatic source.");
        await live.ResumedQueue.WaitAsync(FavoritesTimeout);
        Check((live.PlaybackFloorForTest == card.LiveArrivalSequence) == afterSeek,
            "A seek retired before validation must never publish its playback floor.");
        await live.AddForTestAsync("new-source", serverId: ServerB, talkgroup: "3");
        await live.StartQueueForTestAsync().WaitAsync(FavoritesTimeout);
        Check(player.Completed.SequenceEqual(["selected", "new-source"]) && live.OpenedTransportsForTest == 2,
            "The previous Favorites traversal must stay retired after the new automatic-source boundary.");
        Check(player.Started.SequenceEqual(["selected", "new-source"]),
            "The retired Favorite must never start, even if its playback would later be canceled.");
        Console.WriteLine(afterSeek
            ? "PASS Switching after Favorites seek validation preserves manual playback and retires the old source"
            : "PASS Switching before Favorites seek validation preserves manual playback and retires the old source");
    }
}
