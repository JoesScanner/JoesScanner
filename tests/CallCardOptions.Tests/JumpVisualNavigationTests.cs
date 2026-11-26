using System.Collections.ObjectModel;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;

// Exercises production JumpToLiveAsync through the linked MainViewModel test fixture.
// LiveReadingState integration below covers the visual-follow contract; actual
// CollectionView scrolling remains a native installed-app release check.
internal static class JumpVisualNavigationTests
{
    private static readonly Guid A = Guid.Parse("fcf0bd07-3537-4165-b826-515c642ca8dc");
    private static readonly Guid B = Guid.Parse("83bcf244-3a4c-4988-af19-debdd564cee1");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly LivePlaybackPreferences Selected = new(GroupCalls: false,
        SourceMode: LiveAudioSourceMode.SelectedOnly);

    internal static async Task RunAsync()
    {
        var previous = AppPaths.DatabaseDirectory;
        var root = Directory.CreateTempSubdirectory("JoesScanner.JumpVisual-");
        try
        {
            AppPaths.DatabaseDirectory = root.FullName;
            await SelectedRecorderWithoutAudioAsync(Path.Combine(root.FullName, "selected.db"));
            await FavoritesWithoutAudioAsync(Path.Combine(root.FullName, "favorites.db"));
            await SeparatePanelWithoutAudioAsync(Path.Combine(root.FullName, "separate.db"));
            await AudioOffAsync(Path.Combine(root.FullName, "audio-off.db"));
            await UnavailableAudioAsync(Path.Combine(root.FullName, "unavailable.db"));
            await MutedAndDisabledAsync(Path.Combine(root.FullName, "filtered.db"));
            await EmptyThenArrivalAsync(Path.Combine(root.FullName, "empty.db"));
            await NewCallsResetWithoutQueueMutationAsync(Path.Combine(root.FullName, "new-calls.db"));
            await HistoryAndRestartAsync(Path.Combine(root.FullName, "history.db"));
            await RetiredInFlightJumpAsync(Path.Combine(root.FullName, "retired.db"));
            await RepeatedAndRetiredJumpsAsync(Path.Combine(root.FullName, "rapid.db"));
            await DuplicateBackendIdsAsync(Path.Combine(root.FullName, "duplicate.db"));
        }
        finally
        {
            AppPaths.DatabaseDirectory = previous;
            SqliteConnection.ClearAllPools();
            try { root.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task SelectedRecorderWithoutAudioAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player, LiveAudioSource.Recorder(A));
        await live.AddForTestAsync("visible-b", serverId: B);
        live.TrackJumpScrollForTest();
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && live.DisplayedCallsForTest == 1 &&
            player.Started.IsEmpty && await live.IsPendingForTestAsync("visible-b", B),
            "MAN-11: a populated Combined view must navigate when selected A has no audio; B remains pending.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 selected-recorder audio unavailable, other recorder remains visible and pending");
    }

    private static async Task FavoritesWithoutAudioAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player, LiveAudioSource.Favorites);
        await live.AddForTestAsync("ordinary", serverId: B, talkgroup: "99");
        live.UseFavoritesViewForTest(_ => false);
        live.TrackJumpScrollForTest();
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && live.DisplayedCallsForTest == 1 &&
            player.Started.IsEmpty && await live.IsPendingForTestAsync("ordinary", B),
            "MAN-11: an empty Favorites audio source must navigate without playing ordinary audio.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 Favorites audio source has no eligible recording");
    }

    private static async Task SeparatePanelWithoutAudioAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player, LiveAudioSource.Recorder(A));
        await live.AddForTestAsync("panel-b", serverId: B);
        live.VisibleServerForTest = B;
        var reader = Reading(live.CardForTest("panel-b", B));
        reader.RecordAdmission(live.CardForTest("panel-b", B));
        live.TrackJumpScrollForTest(reader.FollowLatest);
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && reader.IsFollowing && reader.UnseenCount == 0 &&
            player.Started.IsEmpty && await live.IsPendingForTestAsync("panel-b", B),
            "MAN-11: the selected separate recorder panel must return to its visual Live edge.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 Separate panel follows Live independent of selected automatic audio source");
    }

    private static async Task AudioOffAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player);
        await live.AddForTestAsync("already-visible", serverId: A);
        await live.SetAudioForTestAsync(false);
        live.TrackJumpScrollForTest();
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && live.DisplayedCallsForTest == 1 &&
            !live.IsAudioEnabledForTest && player.Started.IsEmpty,
            "MAN-11: visual Jump with Audio Off must not enable audio or discard existing cards.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 Audio Off with previously received visible recordings");
    }

    private static async Task UnavailableAudioAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player, LiveAudioSource.Recorder(A));
        await live.AddForTestAsync("no-url", serverId: A, audio: false);
        live.TrackJumpScrollForTest();
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && live.DisplayedCallsForTest == 1,
            "MAN-11: a missing audio URL must not suppress the visual Jump, regardless of existing audio retry behavior.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 missing recording audio does not block navigation");
    }

    private static async Task MutedAndDisabledAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player, LiveAudioSource.Recorder(A));
        live.MutedForTest = "muted";
        live.HiddenForTest = "disabled";
        await live.AddForTestAsync("muted", serverId: A);
        await live.AddForTestAsync("disabled", serverId: A);
        live.TrackJumpScrollForTest();
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && player.Started.IsEmpty,
            "MAN-11: filtered audio must not be played merely to obtain a visual Jump target.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 muted and disabled recordings never become forced audio targets");
    }

    private static async Task EmptyThenArrivalAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player);
        var cards = new ObservableCollection<CallItem>();
        var reader = new LiveReadingState(cards);
        reader.SetVisible(true);
        live.TrackJumpScrollForTest(reader.FollowLatest);
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && reader.IsFollowing && reader.UnseenCount == 0,
            "MAN-11: jumping an empty view must restore Live-follow.");
        await live.AddForTestAsync("first-later", serverId: B);
        var first = live.CardForTest("first-later", B);
        cards.Insert(0, first);
        reader.RecordAdmission(first);
        Check(reader.IsFollowing && reader.UnseenCount == 0 && player.Started.IsEmpty,
            "MAN-11: the first subsequent visible call should appear while the reader is following Live.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 empty list returns to follow and admits next visible call normally");
    }

    private static async Task NewCallsResetWithoutQueueMutationAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player, LiveAudioSource.Recorder(A));
        await live.AddForTestAsync("regular-b", serverId: B);
        var b = live.CardForTest("regular-b", B);
        var reader = Reading(b);
        reader.RecordAdmission(b);
        Check(reader.UnseenCount == 1 && !reader.IsFollowing, "Fixture must start with a new-call count while reading.");
        live.TrackJumpScrollForTest(reader.FollowLatest);
        var pendingBefore = await live.IsPendingForTestAsync("regular-b", B);
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && reader.UnseenCount == 0 && reader.IsFollowing &&
            pendingBefore && await live.IsPendingForTestAsync("regular-b", B) && player.Started.IsEmpty,
            "MAN-11: Jump should clear the visible New Calls count without consuming other-source queue items.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 New Calls reset and unrelated durable queue stays pending");
    }

    private static async Task HistoryAndRestartAsync(string path)
    {
        var player = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, player, path);
        live.ConfigureAudioSourceRecordersForTest(A, B);
        await live.StartForTestAsync();
        live.TrackJumpScrollForTest();
        var history = ownership.EnterHistory();
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 0, "MAN-11: History must reject Live Jump while it owns playback.");
        ownership.LeaveHistory(history);
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1, "MAN-11: after leaving History, an accepted Jump must visually navigate.");
        await live.StopForTestAsync().WaitAsync(Timeout);
        await live.PressPlayStopForTestAsync().WaitAsync(Timeout);
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 2,
            "MAN-11: a new listening session should accept its own Jump without reusing a stale callback.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 History ownership, Stop and subsequent listening session");
    }

    private static async Task RepeatedAndRetiredJumpsAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player);
        live.TrackJumpScrollForTest();
        var first = live.JumpForTestAsync();
        var second = live.JumpForTestAsync();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 2,
            "MAN-11: two accepted user Jumps may each navigate once, not emit delayed duplicate notifications.");
        await live.StopForTestAsync().WaitAsync(Timeout);
        var before = live.JumpScrollRequestsForTest;
        await Task.Yield();
        Check(live.JumpScrollRequestsForTest == before,
            "MAN-11: a retired request must not publish an additional scroll after Stop.");
        Console.WriteLine("PASS MAN-11 repeated/superseded requests have no delayed visual navigation");
    }

    private static async Task RetiredInFlightJumpAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player);
        await live.AddForTestAsync("holding", serverId: A);
        var started = LiveHistoryHandoffTests.Signal();
        var release = LiveHistoryHandoffTests.Signal();
        player.Playing = async (id, token) =>
        {
            if (id != "holding")
                return;
            started.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                await release.Task.WaitAsync(Timeout);
                throw;
            }
        };
        var automatic = live.StartQueueForTestAsync();
        await started.Task.WaitAsync(Timeout);
        live.TrackJumpScrollForTest();
        var jumping = live.JumpForTestAsync();
        Check(live.JumpScrollRequestsForTest == 1,
            "MAN-11: visual navigation must occur synchronously before delayed native teardown.");
        var stopping = live.StopForTestAsync();
        release.SetResult();
        await Task.WhenAll(automatic, jumping, stopping).WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && player.Completed.IsEmpty,
            "MAN-11: Stop must retire an in-flight Jump without any late scroll or confirmed native end.");
        Console.WriteLine("PASS MAN-11 canceled in-flight Jump cannot post another scroll into a later session");
    }

    private static async Task DuplicateBackendIdsAsync(string path)
    {
        var player = new HandoffAudio();
        var live = await NewLiveAsync(path, player, LiveAudioSource.Recorder(A));
        await live.AddForTestAsync("same-id", serverId: A, audio: false);
        await live.AddForTestAsync("same-id", serverId: B);
        live.TrackJumpScrollForTest();
        await live.JumpForTestAsync().WaitAsync(Timeout);
        Check(live.JumpScrollRequestsForTest == 1 && live.DisplayedCallsForTest == 2 &&
            await live.IsPendingForTestAsync("same-id", B),
            "MAN-11: same backend IDs on two recorders must not make B audio eligible for selected A.");
        await live.StopForTestAsync();
        Console.WriteLine("PASS MAN-11 duplicate backend IDs retain independent recorder scope");
    }

    private static LiveReadingState Reading(params CallItem[] visible)
    {
        var cards = new ObservableCollection<CallItem>(visible);
        var reader = new LiveReadingState(cards);
        reader.SetVisible(true);
        reader.ObserveViewport(0, -31, userScroll: true);
        return reader;
    }

    private static async Task<MainViewModel> NewLiveAsync(string path, HandoffAudio player,
        LiveAudioSource? source = null)
    {
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        live.ConfigureAudioSourceRecordersForTest(A, B);
        if (source is { } selected)
        {
            live.PlaybackOptionsForTest = Selected;
            live.ChooseLiveAudioSource(selected);
        }
        await live.StartForTestAsync();
        if (source != null)
            Check(await live.WaitForSourceForTestAsync().WaitAsync(Timeout), "The selected automatic source did not settle.");
        return live;
    }

    private static void Check(bool condition, string message) => LiveHistoryHandoffTests.Check(condition, message);
}
