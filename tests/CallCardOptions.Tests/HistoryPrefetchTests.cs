using System.Threading.Channels;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Maui.Dispatching;

internal static partial class HistoryPlaybackDispatcherTests
{
    private static async Task PrefetchRespectsThresholdAsync()
    {
        foreach (var (fromOldest, hasMore, expectedRequests) in new[] { (25, true, 1), (26, true, 0), (1, false, 0) })
        {
            using var dispatcher = new HistoryTestDispatcher();
            var player = new HandoffAudio();
            var history = new HistoryViewModel(new PlaybackOwnership(), player);
            await OpenAsync(dispatcher, history);
            var cards = Enumerable.Range(1, 35).Select(index => history.MakeCallForTest($"row_{index}", -index)).ToArray();
            var older = history.MakeCallForTest("older", -36);
            var requested = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            history.PagesForTest.Older = (previous, _) =>
            {
                requested.TrySetResult(previous);
                return response.Task;
            };

            await dispatcher.DispatchAsync(() => history.SearchForTestAsync(cards, hasMore)).Unwrap();
            var selected = cards[^fromOldest];
            await history.SelectForTestAsync(selected).WaitAsync(Timeout);
            if (expectedRequests != 0)
            {
                var previous = await requested.Task.WaitAsync(Timeout);
                var page = await dispatcher.DispatchAsync(history.LoadOlderForTestAsync);
                response.SetResult(new(previous.Snapshot, previous.Cursor, [older], true));
                await page.WaitAsync(Timeout);
            }

            await dispatcher.DispatchAsync(() =>
                LiveHistoryHandoffTests.Check(history.PagesForTest.PageRequests == expectedRequests &&
                    player.Started.TryPeek(out var first) && first == selected.BackendId,
                    $"Selection {fromOldest} from the oldest with HasMore={hasMore} must request {expectedRequests} older pages."));
        }
        Console.WriteLine("PASS History prefetch includes the 25th oldest result, excludes the 26th, and skips exhausted searches");
    }

    private static async Task PrefetchKeepsPlaybackSequenceAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var ownership = new PlaybackOwnership();
        var player = new HandoffAudio();
        var history = new HistoryViewModel(ownership, player) { RateForTest = 1.75 };
        await OpenAsync(dispatcher, history);
        var transitions = 0;
        ownership.Changed += () => Interlocked.Increment(ref transitions);
        var violations = ObserveBoundState(history, dispatcher);
        var selected = history.MakeCallForTest("selected", -6, 6);
        var middle = history.MakeCallForTest("middle", -4, 5);
        var tied = history.MakeCallForTest("tied", -4, 2);
        var newest = history.MakeCallForTest("newest", -2, 1);
        var importedTie = history.MakeCallForTest("imported-tie", -2, 253);
        var importedMiddle = history.MakeCallForTest("imported-middle", -3, 252);
        var importedNewest = history.MakeCallForTest("imported-newest", -1, 251);
        var older = history.MakeCallForTest("older", -7, 254);
        var loaded = new[] { newest, tied, middle, selected };
        var requested = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageStartedAfterAudio = false;
        history.PagesForTest.Older = (previous, _) =>
        {
            pageStartedAfterAudio = player.Started.TryPeek(out var first) && first == selected.BackendId;
            requested.TrySetResult(previous);
            return response.Task;
        };

        var preparing = LiveHistoryHandoffTests.Signal();
        var prepared = new TaskCompletionSource<PlaybackAudioLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        history.PrepareForTest = (call, token) =>
        {
            if (call != selected)
                return Task.FromResult(new PlaybackAudioLease(call.BackendId));
            preparing.SetResult();
            return prepared.Task.WaitAsync(token);
        };
        var starts = Channel.CreateUnbounded<string>();
        var finishSelected = LiveHistoryHandoffTests.Signal();
        var finishMiddle = LiveHistoryHandoffTests.Signal();
        player.Playing = (id, token) =>
        {
            starts.Writer.TryWrite(id);
            return id == selected.BackendId ? finishSelected.Task.WaitAsync(token) :
                id == middle.BackendId ? finishMiddle.Task.WaitAsync(token) : Task.CompletedTask;
        };
        var anchorBefore = -1;
        var anchorAfter = -1;
        await dispatcher.DispatchAsync(() =>
        {
            history.OlderHistoryResultsAdding += () => anchorBefore = history.CallsForTest.IndexOf(tied);
            history.OlderHistoryResultsAdded += () => anchorAfter = history.CallsForTest.IndexOf(tied);
        });
        await dispatcher.DispatchAsync(() => history.SearchForTestAsync(loaded, true)).Unwrap();
        var playing = history.SelectForTestAsync(selected);
        await preparing.Task.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() => LiveHistoryHandoffTests.Check(history.PagesForTest.PageRequests == 0 &&
            player.Started.IsEmpty, "History must prepare and start its selected audio before fetching an older page."));
        prepared.SetResult(new(selected.BackendId));
        await ExpectStartAsync(starts.Reader, selected.BackendId);
        var initial = await requested.Task.WaitAsync(Timeout);
        var fetching = await dispatcher.DispatchAsync(history.LoadOlderForTestAsync);
        finishSelected.SetResult();
        await ExpectStartAsync(starts.Reader, middle.BackendId);
        LiveHistoryHandoffTests.Check(pageStartedAfterAudio && !fetching.IsCompleted,
            "The selected clip must start before paging, and playback must advance while that page is pending.");

        response.SetResult(new(initial.Snapshot, initial.Cursor, [importedNewest, importedTie, importedMiddle, older], true));
        await fetching.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(history.CallsForTest.SequenceEqual(
                    [importedNewest, newest, importedTie, importedMiddle, tied, middle, selected, older]) &&
                anchorBefore == 1 && anchorAfter == 4 && middle.IsPlaying && ReferenceEquals(history.ActiveCallForTest, middle) &&
                player.Started.SequenceEqual([selected.BackendId, middle.BackendId]),
                "The merge must preserve the scroll anchor and active card while inserting timestamp ties and imports.");
        });
        finishMiddle.SetResult();
        await playing.WaitAsync(Timeout);

        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(player.Completed.SequenceEqual([selected.BackendId, middle.BackendId, tied.BackendId, newest.BackendId]) &&
                player.Started.SequenceEqual(player.Completed) && player.Rates.All(rate => rate == 1.75) &&
                history.PagesForTest.PageRequests == 1 && history.CanLoadMoreOlder && !history.IsRunningForTest &&
                ReferenceEquals(history.ActiveCallForTest, newest) && violations.IsEmpty && ownership.Live == null && transitions == 0,
                "Playback must finish its original sequence exactly once at its chosen rate without another page or ownership change.");
        });
        Console.WriteLine("PASS History prefetch starts after native audio, leaves later audio unblocked, and keeps a fixed sequence across reordered results");
    }

    private static async Task PrefetchUsesAnExistingPageAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var player = new HandoffAudio();
        var history = new HistoryViewModel(new PlaybackOwnership(), player);
        await OpenAsync(dispatcher, history);
        var selected = history.MakeCallForTest("selected", -1);
        var older = history.MakeCallForTest("older", -2);
        var response = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        HistorySearchResult? requested = null;
        history.PagesForTest.Older = (previous, _) =>
        {
            requested = previous;
            return response.Task;
        };
        await dispatcher.DispatchAsync(() => history.SearchForTestAsync([selected], true)).Unwrap();
        var manual = await dispatcher.DispatchAsync(history.LoadOlderForTestAsync);
        await history.SelectForTestAsync(selected).WaitAsync(Timeout);
        response.SetResult(new(requested!.Snapshot, requested.Cursor, [older], true));
        await manual.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(history.PagesForTest.PageRequests == 1 && history.CanLoadMoreOlder &&
                history.CallsForTest.SequenceEqual([selected, older]) && player.Completed.SequenceEqual([selected.BackendId]),
                "An existing manual page must satisfy prefetch even when that page has a further cursor.");
        });
        Console.WriteLine("PASS History playback reuses a manual older-page request that was already in flight");
    }
}
