using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;

internal static class LiveAudioSourceTests
{
    private static readonly Guid A = Guid.Parse("fcf0bd07-3537-4165-b826-515c642ca8dc");
    private static readonly Guid B = Guid.Parse("83bcf244-3a4c-4988-af19-debdd564cee1");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly LivePlaybackPreferences Selected = new(GroupCalls: false,
        SourceMode: LiveAudioSourceMode.SelectedOnly);

    internal static async Task RunAsync()
    {
        var prior = AppPaths.DatabaseDirectory;
        var root = Directory.CreateTempSubdirectory("JoesScanner.AudioSource-");
        try
        {
            AppPaths.DatabaseDirectory = root.FullName;
            await DefaultAndSelectedAsync(Path.Combine(root.FullName, "default.db"));
            await SwitchAtNaturalBoundaryAsync(Path.Combine(root.FullName, "switch.db"));
            await FavoritesAndMembershipAsync(Path.Combine(root.FullName, "favorites.db"));
            await DisabledOrRemovedAsync(Path.Combine(root.FullName, "disable.db"));
            await SourceBacklogJumpAsync(Path.Combine(root.FullName, "backlog.db"));
            await HistoryDuringSourceChangeAsync(Path.Combine(root.FullName, "history.db"));
            await RetiredSelectionAndReconnectAsync(Path.Combine(root.FullName, "reconnect.db"));
        }
        finally
        {
            AppPaths.DatabaseDirectory = prior;
            SqliteConnection.ClearAllPools();
            try { root.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    private static MainViewModel TwoRecorders(string path, HandoffAudio player, PlaybackOwnership? ownership = null)
    {
        var live = new MainViewModel(ownership ?? new PlaybackOwnership(), player, path);
        live.ConfigureAudioSourceRecordersForTest(A, B);
        return live;
    }

    private static async Task DefaultAndSelectedAsync(string path)
    {
        var player = new HandoffAudio();
        var live = TwoRecorders(path, player);
        Check(LivePlaybackPreferences.Default.SourceMode == LiveAudioSourceMode.AllEnabledServers &&
            LivePlaybackPreferences.Parse("{\"GroupCalls\":false}").SourceMode == LiveAudioSourceMode.AllEnabledServers &&
            LivePlaybackPreferences.Parse("{\"SourceMode\":1234}").SourceMode == LiveAudioSourceMode.AllEnabledServers,
            "The default and saved settings from earlier releases must use all enabled servers.");
        live.PlaybackOptionsForTest = LivePlaybackPreferences.Default with { GroupCalls = false };
        await live.StartForTestAsync();
        await live.AddForTestAsync("a1", serverId: A);
        await live.AddForTestAsync("b1", serverId: B);
        await live.AddForTestAsync("a2", serverId: A);
        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(A));
        await DrainAudioAsync(live);
        Check(player.Completed.SequenceEqual(["a1", "b1", "a2"]), "All mode must preserve shared FIFO.");

        live.PlaybackOptionsForTest = Selected;
        Check(await live.WaitForSourceForTestAsync().WaitAsync(Timeout), "Selected mode could not reserve its edge.");
        await live.AddForTestAsync("displayed-b", serverId: B);
        await live.AddForTestAsync("selected-a", serverId: A);
        Check(live.CallsWaiting == 1 && live.DisplayedCallsForTest == 5, "The count must match selected audio while both recorders display calls.");
        await DrainAudioAsync(live);
        Check(player.Completed.LastOrDefault() == "selected-a" && !player.Started.Contains("displayed-b"),
            "Selected mode must only play automatic audio from the chosen recorder.");

        await live.SetAudioForTestAsync(false);
        await live.AddForTestAsync("silent-a", serverId: A);
        await live.AddForTestAsync("silent-b", serverId: B);
        await DrainAudioAsync(live);
        await live.SelectForTestAsync("silent-b").WaitAsync(Timeout);
        Check(player.Completed.LastOrDefault() == "silent-b" && !player.Started.Contains("silent-a") && live.OpenedTransportsForTest == 2,
            "Audio Off must keep both monitors open and allow one explicit play from another recorder.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS All-enabled FIFO defaults, recorder-only playback and counts, and Audio Off with cross-recorder manual play");
    }

    private static async Task SwitchAtNaturalBoundaryAsync(string path)
    {
        var player = new HandoffAudio();
        var live = TwoRecorders(path, player);
        live.PlaybackOptionsForTest = Selected;
        await live.StartForTestAsync();
        var playing = LiveHistoryHandoffTests.Signal();
        var finish = LiveHistoryHandoffTests.Signal();
        var canceled = false;
        player.Playing = async (id, token) =>
        {
            if (id != "active-a")
                return;
            using var notice = token.Register(() => canceled = true);
            playing.TrySetResult();
            await finish.Task;
        };
        await live.AddForTestAsync("active-a", serverId: A);
        await live.AddForTestAsync("waiting-a", serverId: A);
        await live.AddForTestAsync("old-b", serverId: B);
        var queue = live.StartQueueForTestAsync();
        await playing.Task.WaitAsync(Timeout);
        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(B));
        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(A));
        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(B));
        await live.AddForTestAsync("while-finishing-b", serverId: B);
        Check(live.LiveAudioSourceText.Contains("Next", StringComparison.Ordinal) && live.CallsWaiting == 0 &&
            player.Started.SequenceEqual(["active-a"]) && !canceled, "Rapid source changes must leave the in-flight native clip alone.");

        finish.SetResult();
        await live.WaitForSourceForTestAsync().WaitAsync(Timeout);
        await queue.WaitAsync(Timeout);
        await live.AddForTestAsync("late-recovered-b", serverId: B, origin: RecordingOrigin.History);
        await live.AddForTestAsync("new-b", serverId: B);
        await live.AddForTestAsync("while-away-a", serverId: A);
        await DrainAudioAsync(live);
        Check(player.Completed.SequenceEqual(["active-a", "new-b"]), "The source edge must exclude arrivals before the completed clip and late older recovery.");

        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(A));
        await live.WaitForSourceForTestAsync().WaitAsync(Timeout);
        await live.AddForTestAsync("new-a", serverId: A);
        await DrainAudioAsync(live);
        Check(player.Completed.SequenceEqual(["active-a", "new-b", "new-a"]) && live.OpenedTransportsForTest == 2 &&
            live.DisplayedCallsForTest == 8, "Switching back must drop old audio without re-opening either recorder or losing cards.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS Rapid source changes coalesce after the active clip, skip old and recovered audio, and retain both monitors");
    }

    private static async Task FavoritesAndMembershipAsync(string path)
    {
        var player = new HandoffAudio();
        var live = TwoRecorders(path, player);
        await live.SetAudioFavoritesForTestAsync((A, "10"), (B, "20"));
        live.ChooseLiveAudioSource(LiveAudioSource.Favorites);
        live.PlaybackOptionsForTest = Selected;
        await live.StartForTestAsync();
        for (var i = 0; i < 23; i++)
            await live.AddForTestAsync($"outside-{i}", serverId: B, talkgroup: "10");
        await live.AddForTestAsync("favorite-b", serverId: B, talkgroup: "20");
        await live.AddForTestAsync("favorite-a", serverId: A, talkgroup: "10");
        await live.AddForTestAsync("old-before-star", serverId: B, talkgroup: "30");
        Check(live.CallsWaiting == 2, "Only same-recorder matching Favorites should count before the lookahead.");
        await DrainAudioAsync(live);
        Check(player.Completed.SequenceEqual(["favorite-b", "favorite-a"]), "Favorites must be filtered before the scheduler limit.");

        await live.AddForTestAsync("removed-favorite", serverId: A, talkgroup: "10");
        await live.AddForTestAsync("retained-favorite", serverId: B, talkgroup: "20");
        await live.SetAudioFavoritesForTestAsync((B, "20"), (B, "30"));
        await live.ResumedQueue.WaitAsync(Timeout);
        await live.AddForTestAsync("newly-starred", serverId: B, talkgroup: "30");
        await DrainAudioAsync(live);
        Check(player.Completed.SequenceEqual(["favorite-b", "favorite-a", "retained-favorite", "newly-starred"]) &&
            live.LiveAudioSourceText.EndsWith("Favorites", StringComparison.Ordinal),
            "Membership changes must preserve existing eligible favorites and start the new one from its own edge.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS Favorites-only queries use scoped membership before lookahead and edits affect the right waiting calls");
    }

    private static async Task DisabledOrRemovedAsync(string path)
    {
        var player = new HandoffAudio();
        var live = TwoRecorders(path, player);
        live.PlaybackOptionsForTest = Selected;
        await live.StartForTestAsync();
        live.SetLiveAudioRecorders([(A, "Recorder A", false), (B, "Recorder B", true)]);
        await live.WaitForSourceForTestAsync().WaitAsync(Timeout);
        await live.AddForTestAsync("waiting-b", serverId: B);
        await DrainAudioAsync(live);
        Check(live.LiveAudioSourceText.Contains("disabled", StringComparison.OrdinalIgnoreCase) &&
            player.Started.IsEmpty && live.CallsWaiting == 0, "Disabling the chosen recorder must pause automatic audio.");

        live.SetLiveAudioRecorders([(B, "Recorder B", true)]);
        Check(live.LiveAudioSourceText.Contains("removed", StringComparison.OrdinalIgnoreCase) &&
            player.Started.IsEmpty, "Removing the chosen recorder must preserve a visible paused state.");
        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(B));
        await live.WaitForSourceForTestAsync().WaitAsync(Timeout);
        var reopened = new MainViewModel(new PlaybackOwnership(), new HandoffAudio(), path)
        {
            PlaybackOptionsForTest = Selected
        };
        reopened.SetLiveAudioRecorders([(A, "Recorder A", true), (B, "Recorder B", true)]);
        Check(reopened.LiveAudioSourceText.EndsWith("Recorder B", StringComparison.Ordinal),
            "A fresh view model must restore the saved concrete source when it reads recorder profiles.");
        await live.AddForTestAsync("new-b", serverId: B);
        await DrainAudioAsync(live);
        Check(player.Completed.SequenceEqual(["new-b"]), "Explicit reselection must skip the alternate recorder's old backlog.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS Disabled and removed selections pause automatic audio until a concrete source is selected");
    }

    private static async Task SourceBacklogJumpAsync(string path)
    {
        var player = new HandoffAudio();
        var live = TwoRecorders(path, player);
        live.PlaybackOptionsForTest = Selected with { WarningThreshold = 2, JumpThreshold = 3 };
        await live.StartForTestAsync();
        var start = LiveHistoryHandoffTests.Signal();
        var finish = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, _) =>
        {
            if (id != "active")
                return Task.CompletedTask;
            start.TrySetResult();
            return finish.Task;
        };
        await live.AddForTestAsync("active", serverId: A);
        var queue = live.StartQueueForTestAsync();
        await start.Task.WaitAsync(Timeout);
        for (var i = 0; i < 5; i++)
            await live.AddForTestAsync($"other-{i}", serverId: B);
        await live.ReadBacklogForTestAsync();
        Check(live.CallsWaiting == 0 && !live.BacklogWarningForTest && !live.BacklogJumpForTest,
            "Display-only backlog must not trigger either threshold.");
        for (var i = 0; i < 3; i++)
            await live.AddForTestAsync($"waiting-{i}", serverId: A);
        await live.ReadBacklogForTestAsync();
        Check(live.CallsWaiting == 3 && live.BacklogWarningForTest && live.BacklogJumpForTest, "Selected audio must retain both thresholds.");
        finish.SetResult();
        await queue.WaitAsync(Timeout);
        await live.AddForTestAsync("new-a", serverId: A);
        await DrainAudioAsync(live);
        Check(player.Completed.SequenceEqual(["active", "new-a"]) && live.DisplayedCallsForTest == 10,
            "Automatic Jump must skip the selected waiting calls and preserve visible cards.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS Backlog warning and automatic Jump count only eligible audio for the selected recorder");
    }

    private static async Task HistoryDuringSourceChangeAsync(string path)
    {
        var player = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = TwoRecorders(path, player, ownership);
        var history = new HistoryViewModel(ownership, player);
        live.PlaybackOptionsForTest = Selected;
        await live.StartForTestAsync();
        var start = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, token) =>
        {
            if (id != "active-a")
                return Task.CompletedTask;
            start.TrySetResult();
            return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        };
        await live.AddForTestAsync("active-a", serverId: A);
        var old = live.StartQueueForTestAsync();
        await start.Task.WaitAsync(Timeout);
        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(B));
        history.OpenForTest();
        await live.AddForTestAsync("while-in-history", serverId: B);
        await history.PlayForTestAsync().WaitAsync(Timeout);
        await old.WaitAsync(Timeout);
        Check(player.Completed.SequenceEqual(["history"]), "History must own audio while a source transition is pending.");
        await history.OnPageClosedAsync();
        await live.ResumedQueue.WaitAsync(Timeout);
        await live.AddForTestAsync("new-a", serverId: A);
        await live.AddForTestAsync("new-b", serverId: B);
        await DrainAudioAsync(live);
        Check(player.Completed.SequenceEqual(["history", "new-b"]) && live.OpenedTransportsForTest == 2,
            "Returning from History must retain the requested source and skip the intervening backlog.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS History retires pending Live audio source changes and resumes the chosen source at the Live edge");
    }

    private static async Task RetiredSelectionAndReconnectAsync(string path)
    {
        var player = new HandoffAudio();
        var live = TwoRecorders(path, player);
        live.PlaybackOptionsForTest = Selected;
        await live.StartForTestAsync();
        await live.AddForTestAsync("stale-a", serverId: A);
        var delayed = live.HoldNextSelection();
        var oldQueue = live.StartQueueForTestAsync();
        await delayed.Reached.Task.WaitAsync(Timeout);
        live.ChooseLiveAudioSource(LiveAudioSource.Recorder(B));
        await live.WaitForSourceForTestAsync().WaitAsync(Timeout);
        await live.ReconnectSecondaryRecorderForTestAsync();
        await live.AddForTestAsync("new-a", serverId: A);
        await live.AddForTestAsync("reconnected-b", serverId: B);
        delayed.Return.SetResult();
        await oldQueue.WaitAsync(Timeout);
        Check(player.Completed.SequenceEqual(["reconnected-b"]) && live.OpenedTransportsForTest == 3 &&
            live.DisplayedCallsForTest == 3, "A late scheduler read cannot select the retired source, and either recorder can reconnect independently.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS Retired source admissions cannot play after a delayed read or independent recorder reconnection");
    }

    private static async Task DrainAudioAsync(MainViewModel live)
    {
        await live.StartQueueForTestAsync().WaitAsync(Timeout);
        await live.ResumedQueue.WaitAsync(Timeout);
    }

    private static void Check(bool condition, string message) => LiveHistoryHandoffTests.Check(condition, message);
}
