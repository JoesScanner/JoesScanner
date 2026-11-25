using System.Collections.Concurrent;
using System.Threading.Channels;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Maui.Dispatching;

internal static partial class HistoryPlaybackDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync()
    {
        await ContendedPlaybackAndOlderResultsAsync();
        await StopWithPendingNotificationsAsync();
        await NavigateDuringPlaybackStartupAsync();
        await SelectedPlaybackSkipsFailedRecordingsAsync();
        await PrefetchRespectsThresholdAsync();
        await PrefetchKeepsPlaybackSequenceAsync();
        await PrefetchUsesAnExistingPageAsync();
        await PreviousSharesTheOlderPageAsync();
        await LatestPreparationWinsAsync();
        await OlderPageFailuresPreservePlaybackAsync();
        await ScopeChangesRetirePendingWorkAsync();
    }

    private static async Task ContendedPlaybackAndOlderResultsAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var ownership = new PlaybackOwnership();
        var player = new HandoffAudio();
        var history = new HistoryViewModel(ownership, player);
        var finishLive = LiveHistoryHandoffTests.Signal();
        var finishFirstClip = LiveHistoryHandoffTests.Signal();
        var firstClipStarted = LiveHistoryHandoffTests.Signal();
        var live = ownership.RunNativeAsync(ownership.Live!, default, _ => finishLive.Task);
        player.Playing = (id, _) =>
        {
            if (id != "history")
                return Task.CompletedTask;
            firstClipStarted.SetResult();
            return finishFirstClip.Task;
        };
        var violations = ObserveBoundState(history, dispatcher);
        var newer = new CallItem { BackendId = "newer" };
        var older = new CallItem { BackendId = "older" };
        Task playback = Task.CompletedTask;
        await dispatcher.DispatchAsync(() =>
        {
            history.UseHistoryDispatcher(dispatcher);
            history.OpenForTest();
            history.CallsForTest.Insert(0, newer);
            playback = history.PlayForTestAsync();
            LiveHistoryHandoffTests.Check(history.CallsForTest[1].IsPlaying && player.Started.IsEmpty,
                "History must publish selection on its dispatcher while waiting for the native gate.");
        });
        finishLive.SetResult();
        await firstClipStarted.Task.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            history.CallsForTest.Add(older);
            LiveHistoryHandoffTests.Check(history.CallsForTest[1].IsPlaying,
                "Loading older results must retain the active card.");
        });
        finishFirstClip.SetResult();
        await live.WaitAsync(Timeout);
        await playback.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(player.Completed.SequenceEqual(["history", "newer"]) &&
                history.CallsForTest.Count == 3 && !history.IsRunningForTest && !newer.IsPlaying && violations.IsEmpty,
                "Loading older recordings must preserve chronological playback and dispatcher ownership.");
        });
        Console.WriteLine("PASS History plays loaded recordings chronologically while older results arrive and keeps UI changes on the dispatcher");
    }

    private static async Task StopWithPendingNotificationsAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var player = new HandoffAudio();
        var history = new HistoryViewModel(new PlaybackOwnership(), player);
        var firstEnd = LiveHistoryHandoffTests.Signal();
        var secondEnd = LiveHistoryHandoffTests.Signal();
        var replacementStarted = LiveHistoryHandoffTests.Signal();
        var firstStarted = LiveHistoryHandoffTests.Signal();
        var attempts = 0;
        player.Playing = (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstStarted.SetResult();
                return firstEnd.Task;
            }
            replacementStarted.SetResult();
            return secondEnd.Task;
        };
        var violations = ObserveBoundState(history, dispatcher);
        await OpenAsync(dispatcher, history);

        // A Play notification queued before Stop cannot start afterward.
        var queuedStart = dispatcher.HoldNextPost();
        var canceledStartup = Task.Run(history.PlayForTestAsync);
        await queuedStart.Enqueued.Task.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(history.StopForTestAsync).Unwrap();
        queuedStart.Release();
        await canceledStartup.WaitAsync(Timeout);
        LiveHistoryHandoffTests.Check(player.Started.IsEmpty, "Stop must invalidate Play waiting in the dispatcher queue.");

        var playback = Task.Run(history.PlayForTestAsync);
        await firstStarted.Task.WaitAsync(Timeout);
        var stalePublish = dispatcher.HoldNextPost();
        firstEnd.SetResult();
        await stalePublish.Enqueued.Task.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(history.StopForTestAsync).Unwrap();
        var replacement = Task.Run(history.PlayForTestAsync);
        await replacementStarted.Task.WaitAsync(Timeout);
        stalePublish.Release();
        await playback.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(history.IsRunningForTest && history.CallsForTest[0].IsPlaying,
                "The obsolete completion must not clear the replacement recording's playing indicators.");
        });
        secondEnd.SetResult();
        await replacement.WaitAsync(Timeout);
        LiveHistoryHandoffTests.Check(violations.IsEmpty, "History Stop and completion notifications must use the owning dispatcher.");
        Console.WriteLine("PASS History Stop retires pending dispatcher startup and stale completion leaves replacement playback intact");
    }

    private static async Task NavigateDuringPlaybackStartupAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var ownership = new PlaybackOwnership();
        var player = new HandoffAudio();
        var history = new HistoryViewModel(ownership, player);
        var finishLive = LiveHistoryHandoffTests.Signal();
        var live = ownership.RunNativeAsync(ownership.Live!, default, _ => finishLive.Task);
        var finishCurrent = LiveHistoryHandoffTests.Signal();
        var currentStarted = LiveHistoryHandoffTests.Signal();
        player.Playing = (_, _) =>
        {
            currentStarted.SetResult();
            return finishCurrent.Task;
        };
        var violations = ObserveBoundState(history, dispatcher);
        await OpenAsync(dispatcher, history);
        Task retired = Task.CompletedTask;
        Task current = Task.CompletedTask;
        await dispatcher.DispatchAsync(() =>
        {
            retired = history.PlayForTestAsync();
            _ = history.OnPageClosedAsync();
            LiveHistoryHandoffTests.Check(ownership.Live != null, "Closing History must return ownership to Live.");
            history.OpenForTest();
            current = history.PlayForTestAsync();
        });
        finishLive.SetResult();
        await Task.WhenAll(live, retired).WaitAsync(Timeout);
        await currentStarted.Task.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(player.Started.SequenceEqual(["history"]) &&
                history.IsRunningForTest && history.CallsForTest[0].IsPlaying && violations.IsEmpty,
                "Only the replacement History visit should begin native playback or publish its UI.");
        });
        finishCurrent.SetResult();
        await current.WaitAsync(Timeout);
        Console.WriteLine("PASS Navigation cancels History startup waiting for Live teardown and rapid reentry preserves new playback state");
    }

    private static async Task SelectedPlaybackSkipsFailedRecordingsAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var ownership = new PlaybackOwnership();
        var player = new HandoffAudio();
        var history = new HistoryViewModel(ownership, player) { RateForTest = 2 };
        await OpenAsync(dispatcher, history);
        var newest = history.MakeCallForTest("newest", -1);
        var corrupt = history.MakeCallForTest("corrupt", -2);
        var unavailable = history.MakeCallForTest("unavailable", -3);
        var first = history.MakeCallForTest("selected", -4);
        var unused = history.MakeCallForTest("older", -5);
        var preparations = new ConcurrentQueue<string>();
        history.PrepareForTest = (call, _) =>
        {
            preparations.Enqueue(call.BackendId);
            return call == unavailable ? Task.FromException<PlaybackAudioLease>(new IOException("No audio.")) :
                Task.FromResult(new PlaybackAudioLease(call.BackendId));
        };
        player.Playing = (id, _) => id == "corrupt" ?
            Task.FromException(new InvalidDataException("Cannot decode.")) : Task.CompletedTask;
        await dispatcher.DispatchAsync(() => history.SearchForTestAsync([newest, corrupt, unavailable, first, unused], false)).Unwrap();
        await history.SelectForTestAsync(first).WaitAsync(Timeout);

        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(player.Started.SequenceEqual(["selected", "corrupt", "newest"]) &&
                player.Completed.SequenceEqual(["selected", "newest"]) && player.Rates.All(rate => rate == 2) &&
                preparations.SequenceEqual(["selected", "unavailable", "corrupt", "newest"]) &&
                history.PagesForTest.PageRequests == 0 && ReferenceEquals(history.ActiveCallForTest, newest) &&
                !history.IsRunningForTest && history.CallsForTest.All(call => !call.IsPlaying) && ownership.Live == null,
                "Selected History playback must skip failed clips once, reach the newest item and stop inside History.");
        });
        Console.WriteLine("PASS Selected History playback advances at the chosen rate, skips failures once, and stops at the newest result");
    }

    private static async Task PreviousSharesTheOlderPageAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var player = new HandoffAudio();
        var starts = HoldNativePlayback(player);
        var history = new HistoryViewModel(new PlaybackOwnership(), player);
        await OpenAsync(dispatcher, history);
        var newest = history.MakeCallForTest("newest", -1);
        var oldest = history.MakeCallForTest("row_10", -2, archiveOffset: 249);
        var older = history.MakeCallForTest("row_9", -2, archiveOffset: 250);
        var last = history.MakeCallForTest("row_8", -3);
        var prefetchStarted = LiveHistoryHandoffTests.Signal();
        var response = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        HistorySearchResult? observed = null;
        history.PagesForTest.Older = (prior, _) =>
        {
            observed = prior;
            prefetchStarted.SetResult();
            return response.Task;
        };

        var cache = Directory.CreateTempSubdirectory("JoesScanner.HistoryPlayback-");
        try
        {
            var acquired = new ConcurrentQueue<string>();
            var shared = new PlaybackAudioCache(Path.Combine(cache.FullName, "media"), Path.Combine(cache.FullName, "fallback"),
                25, _ => null);
            history.PrepareForTest = async (call, token) =>
            {
                var key = RecordingCacheKey.Create(call.ServerId, call.RecordingServer!, call.BackendId, null, null);
                var audio = await shared.AcquireAsync(key, (_, _) => Task.CompletedTask, async (transfer, cancellation) =>
                {
                    acquired.Enqueue(call.BackendId);
                    using var response = AudioCacheTests.WaveResponse(256);
                    return await transfer.StoreAsync(response, ".wav", cancellation);
                }, token) ?? throw new InvalidOperationException("Missing test audio.");
                return new PlaybackAudioLease(call.BackendId, audio.CacheHit, audio.Dispose, audio.Invalidate);
            };
            await dispatcher.DispatchAsync(() => history.SearchForTestAsync([newest, oldest], true)).Unwrap();
            var original = history.SelectForTestAsync(oldest);
            await ExpectStartAsync(starts, "row_10");
            await prefetchStarted.Task.WaitAsync(Timeout);
            var manual = await dispatcher.DispatchAsync(history.LoadOlderForTestAsync);
            var crossing = history.PreviousForTestAsync();
            await dispatcher.DispatchAsync(() =>
            {
                LiveHistoryHandoffTests.Check(history.PagesForTest.PageRequests == 1 && oldest.IsPlaying &&
                    ReferenceEquals(history.ActiveCallForTest, oldest) && history.CallsForTest.Count == 2,
                    "Load More and Previous must share the pending prefetch while the current card plays.");
            });
            response.SetResult(new(observed!.Snapshot, observed.Cursor, [older, last], false));
            await manual.WaitAsync(Timeout);
            await ExpectStartAsync(starts, "row_9");
            await original.WaitAsync(Timeout);
            await dispatcher.DispatchAsync(() =>
            {
                LiveHistoryHandoffTests.Check(history.CallsForTest.SequenceEqual([newest, oldest, older, last]) &&
                    ReferenceEquals(history.ActiveCallForTest, older) && older.IsPlaying && !oldest.IsPlaying,
                    "The adjacent older recording must use stable ordering and existing card identities for tied timestamps.");
            });

            var newer = history.NextForTestAsync();
            await ExpectStartAsync(starts, "row_10");
            await crossing.WaitAsync(Timeout);
            var again = history.PreviousForTestAsync();
            await ExpectStartAsync(starts, "row_9");
            await newer.WaitAsync(Timeout);
            await history.StopForTestAsync();
            await again.WaitAsync(Timeout);
            await history.OnPageClosedAsync();
            LiveHistoryHandoffTests.Check(acquired.SequenceEqual(["row_10", "row_9"]) &&
                history.PagesForTest.PageRequests == 1 && shared.UsageBytes > 0,
                "Adjacent navigation must reuse audio and leave it available to the shared cache after History closes.");
        }
        finally
        {
            cache.Delete(recursive: true);
        }
        Console.WriteLine("PASS Prefetch, Load More and Previous share one request, and navigation preserves tie order and reuses audio");
    }

    private static async Task LatestPreparationWinsAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var player = new HandoffAudio();
        var starts = HoldNativePlayback(player);
        var history = new HistoryViewModel(new PlaybackOwnership(), player);
        await OpenAsync(dispatcher, history);
        var active = history.MakeCallForTest("active", -1);
        var slow = history.MakeCallForTest("slow", -2);
        var bad = history.MakeCallForTest("bad", -3);
        var latest = history.MakeCallForTest("latest", -4);
        var slowStarted = LiveHistoryHandoffTests.Signal();
        var late = new TaskCompletionSource<PlaybackAudioLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken slowToken = default;
        history.PrepareForTest = (call, token) =>
        {
            if (call == slow)
            {
                slowToken = token;
                slowStarted.SetResult();
                return late.Task;
            }
            if (call == bad)
                return Task.FromException<PlaybackAudioLease>(new IOException("Failed to download."));
            return Task.FromResult(new PlaybackAudioLease(call.BackendId));
        };
        await dispatcher.DispatchAsync(() => history.SearchForTestAsync([active, slow, bad, latest], false)).Unwrap();
        var original = history.SelectForTestAsync(active);
        await ExpectStartAsync(starts, "active");
        var old = history.SelectForTestAsync(slow);
        await slowStarted.Task.WaitAsync(Timeout);
        await history.SelectForTestAsync(bad).WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(slowToken.IsCancellationRequested && active.IsPlaying &&
                ReferenceEquals(history.ActiveCallForTest, active) && player.Started.SequenceEqual(["active"]) &&
                history.StatusForTest.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                "A failed replacement must leave the current recording selected and audible.");
        });

        var selected = history.SelectForTestAsync(latest);
        await ExpectStartAsync(starts, "latest");
        late.SetResult(new("slow"));
        await Task.WhenAll(original, old).WaitAsync(Timeout);
        LiveHistoryHandoffTests.Check(player.Started.SequenceEqual(["active", "latest"]),
            "A late obsolete preparation must never enter the native player.");
        await history.StopForTestAsync();
        await selected.WaitAsync(Timeout);
        Console.WriteLine("PASS History preparations follow newest-request-wins and leave current playback intact when preparation fails");
    }

    private static async Task OlderPageFailuresPreservePlaybackAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var player = new HandoffAudio();
        var starts = HoldNativePlayback(player);
        var history = new HistoryViewModel(new PlaybackOwnership(), player);
        await OpenAsync(dispatcher, history);
        var active = history.MakeCallForTest("oldest", -1);
        var older = history.MakeCallForTest("older", -2);
        var prefetchStarted = LiveHistoryHandoffTests.Signal();
        var response = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        HistorySearchResult? failedCursor = null;
        history.PagesForTest.Older = (previous, _) =>
        {
            failedCursor = previous;
            prefetchStarted.SetResult();
            return response.Task;
        };
        await dispatcher.DispatchAsync(() => history.SearchForTestAsync([active], true)).Unwrap();
        var playing = history.SelectForTestAsync(active);
        await ExpectStartAsync(starts, "oldest");
        await prefetchStarted.Task.WaitAsync(Timeout);
        var failure = await dispatcher.DispatchAsync(history.LoadOlderForTestAsync);
        response.SetException(new HttpRequestException("Temporary recorder error.", null, System.Net.HttpStatusCode.ServiceUnavailable));
        await failure.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(history.CallsForTest.SequenceEqual([active]) && active.IsPlaying &&
                history.CanLoadMoreOlder && history.StatusForTest.Contains("Try again", StringComparison.Ordinal) &&
                history.PagesForTest.PageRequests == 1,
                "A prefetch failure must preserve the active card and leave the same page available for explicit retry.");
        });
        history.PagesForTest.Older = (previous, _) =>
        {
            LiveHistoryHandoffTests.Check(ReferenceEquals(previous, failedCursor), "Retry must resume the same snapshot and cursor.");
            return Task.FromResult(new HistorySearchResult(previous.Snapshot, previous.Cursor, [older], true));
        };
        await dispatcher.DispatchAsync(history.LoadOlderForTestAsync).Unwrap().WaitAsync(Timeout);
        LiveHistoryHandoffTests.Check(active.IsPlaying && history.CallsForTest.SequenceEqual([active, older]),
            "A successful manual retry must append without changing playback.");
        var driftStarted = LiveHistoryHandoffTests.Signal();
        var drift = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        history.PagesForTest.Older = (_, _) =>
        {
            driftStarted.SetResult();
            return drift.Task;
        };
        var olderPlayback = history.PreviousForTestAsync();
        await ExpectStartAsync(starts, "older");
        await playing.WaitAsync(Timeout);
        await driftStarted.Task.WaitAsync(Timeout);
        var invalidated = await dispatcher.DispatchAsync(history.LoadOlderForTestAsync);
        drift.SetException(new HistoryResultsChangedException());
        await invalidated.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(history.LoadOlderForTestAsync).Unwrap().WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(older.IsPlaying && ReferenceEquals(history.ActiveCallForTest, older) &&
                history.CallsForTest.SequenceEqual([active, older]) && !history.CanLoadMoreOlder &&
                history.PagesForTest.PageRequests == 3 &&
                history.StatusForTest.Contains("Start a new search", StringComparison.Ordinal),
                "An invalidated archive cursor must require a new search and leave existing audio alone.");
        });
        await history.StopForTestAsync();
        await olderPlayback.WaitAsync(Timeout);
        Console.WriteLine("PASS Failed History prefetch leaves a manual retry, and archive drift requires a new search without interrupting audio");
    }

    private static async Task ScopeChangesRetirePendingWorkAsync()
    {
        foreach (var change in new[] { "recorder", "filters", "account", "stop", "tab", "search" })
        {
            using var dispatcher = new HistoryTestDispatcher();
            var ownership = new PlaybackOwnership();
            var player = new HandoffAudio();
            var starts = HoldNativePlayback(player);
            var history = new HistoryViewModel(ownership, player);
            await OpenAsync(dispatcher, history);
            var pending = history.MakeCallForTest("preparing", -1);
            var active = history.MakeCallForTest("old", -2);
            var delayedRow = history.MakeCallForTest("late page", -3);
            var page = new TaskCompletionSource<HistorySearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var audio = new TaskCompletionSource<PlaybackAudioLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pagingStarted = LiveHistoryHandoffTests.Signal();
            var preparationStarted = LiveHistoryHandoffTests.Signal();
            CancellationToken pagingToken = default;
            CancellationToken preparationToken = default;
            HistorySearchResult? pageStart = null;
            history.PagesForTest.Older = (initial, token) =>
            {
                pagingToken = token;
                pageStart = initial;
                pagingStarted.SetResult();
                return page.Task;
            };
            history.PrepareForTest = (call, token) =>
            {
                if (call != pending)
                    return Task.FromResult(new PlaybackAudioLease(call.BackendId));
                preparationToken = token;
                preparationStarted.SetResult();
                return audio.Task;
            };
            await dispatcher.DispatchAsync(() => history.SearchForTestAsync([pending, active], true, hosted: change == "account")).Unwrap();
            var playing = history.SelectForTestAsync(active);
            await ExpectStartAsync(starts, "old");
            await pagingStarted.Task.WaitAsync(Timeout);
            var paging = await dispatcher.DispatchAsync(history.LoadOlderForTestAsync);
            var preparing = history.SelectForTestAsync(pending);
            await preparationStarted.Task.WaitAsync(Timeout);

            switch (change)
            {
                case "recorder": await dispatcher.DispatchAsync(history.ChangeRecorderForTest); break;
                case "filters": await dispatcher.DispatchAsync(history.ChangeFiltersForTest); break;
                case "account": await dispatcher.DispatchAsync(history.ChangeAccountForTest); break;
                case "stop":
                    await history.StopForTestAsync();
                    await dispatcher.DispatchAsync(() =>
                        LiveHistoryHandoffTests.Check(history.CallsForTest.SequenceEqual([pending, active]) &&
                            ReferenceEquals(history.ActiveCallForTest, active) && !active.IsPlaying && history.CanLoadMoreOlder,
                            "Stop must cancel the prefetch and audio while keeping the existing results and cursor."));
                    break;
                case "tab":
                    await history.OnPageClosedAsync();
                    LiveHistoryHandoffTests.Check(ownership.Live != null, "Closing History must return ownership to Live.");
                    await dispatcher.DispatchAsync(history.OpenForTest);
                    break;
            }
            var replacementCall = history.MakeCallForTest("replacement", -1);
            await dispatcher.DispatchAsync(() => history.SearchForTestAsync([replacementCall], false)).Unwrap();
            await Task.WhenAll(paging, playing).WaitAsync(Timeout);
            var replacement = history.SelectForTestAsync(replacementCall);
            await ExpectStartAsync(starts, "replacement");
            if (change == "filters")
                page.SetException(new HttpRequestException("Retired transport failure."));
            else
                page.SetResult(new(pageStart!.Snapshot, pageStart.Cursor, [delayedRow], false));
            audio.SetResult(new("preparing"));
            await preparing.WaitAsync(Timeout);
            await dispatcher.DispatchAsync(() =>
            {
                LiveHistoryHandoffTests.Check(pagingToken.IsCancellationRequested && preparationToken.IsCancellationRequested &&
                    history.CallsForTest.SequenceEqual([replacementCall]) && replacementCall.IsPlaying &&
                    player.Started.SequenceEqual(["old", "replacement"]) && ownership.Live == null,
                    $"A {change} must retire both requests and fence late results from the current History search and player.");
            });
            await history.StopForTestAsync();
            await replacement.WaitAsync(Timeout);
        }
        Console.WriteLine("PASS Stop, new search, recorder, filter, account and tab changes fence late History prefetch and preparations");
    }

    private static ChannelReader<string> HoldNativePlayback(HandoffAudio player)
    {
        var starts = Channel.CreateUnbounded<string>();
        player.Playing = (id, token) =>
        {
            starts.Writer.TryWrite(id);
            return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        };
        return starts.Reader;
    }

    private static async Task ExpectStartAsync(ChannelReader<string> starts, string expected)
    {
        var actual = await starts.ReadAsync().AsTask().WaitAsync(Timeout);
        LiveHistoryHandoffTests.Check(actual == expected, $"Expected {expected}, got {actual}.");
    }

    private static Task OpenAsync(HistoryTestDispatcher dispatcher, HistoryViewModel history) =>
        dispatcher.DispatchAsync(() =>
        {
            history.UseHistoryDispatcher(dispatcher);
            history.OpenForTest();
        });

    private static ConcurrentQueue<string> ObserveBoundState(HistoryViewModel history, HistoryTestDispatcher dispatcher)
    {
        var violations = new ConcurrentQueue<string>();
        void Verify(string? property)
        {
            if (dispatcher.IsDispatchRequired)
                violations.Enqueue(property ?? "Unknown property");
        }
        void ObserveCall(CallItem call) => call.PropertyChanged += (_, change) => Verify(change.PropertyName);
        history.PropertyChanged += (_, change) => Verify(change.PropertyName);
        foreach (var call in history.CallsForTest)
            ObserveCall(call);
        history.CallsForTest.CollectionChanged += (_, change) =>
        {
            Verify("History calls collection");
            if (change.NewItems != null)
                foreach (CallItem call in change.NewItems)
                    ObserveCall(call);
        };
        return violations;
    }

    private sealed class HistoryTestDispatcher : IDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private HeldPost? _held;

        public HistoryTestDispatcher()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                    action();
            }) { IsBackground = true };
            _thread.Start();
        }

        public bool IsDispatchRequired => Environment.CurrentManagedThreadId != _thread.ManagedThreadId;

        public bool Dispatch(Action action)
        {
            var hold = Interlocked.Exchange(ref _held, null);
            if (hold != null)
            {
                hold.Action = action;
                hold.Enqueued.SetResult();
            }
            else
            {
                _queue.Add(action);
            }
            return true;
        }

        internal HeldPost HoldNextPost()
        {
            var hold = new HeldPost(_queue);
            LiveHistoryHandoffTests.Check(Interlocked.CompareExchange(ref _held, hold, null) == null,
                "A dispatcher callback is already held.");
            return hold;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        internal sealed class HeldPost(BlockingCollection<Action> queue)
        {
            internal TaskCompletionSource Enqueued { get; } = LiveHistoryHandoffTests.Signal();
            internal Action? Action { get; set; }
            internal void Release() => queue.Add(Action!);
        }
    }
}

namespace Microsoft.Maui.Dispatching
{
    internal interface IDispatcher
    {
        bool IsDispatchRequired { get; }
        bool Dispatch(Action action);
    }

    internal static class DispatcherExtensions
    {
        internal static Task DispatchAsync(this IDispatcher dispatcher, Action action) =>
            dispatcher.DispatchAsync(() =>
            {
                action();
                return true;
            });

        internal static Task<T> DispatchAsync<T>(this IDispatcher dispatcher, Func<T> action)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.Dispatch(() =>
            {
                try { result.SetResult(action()); }
                catch (Exception error) { result.SetException(error); }
            });
            return result.Task;
        }
    }
}
