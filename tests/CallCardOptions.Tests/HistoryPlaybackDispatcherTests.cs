using System.Collections.Concurrent;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Maui.Dispatching;

internal static class HistoryPlaybackDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync()
    {
        await ContendedPlaybackAndPagingAsync();
        await StopWithPendingNotificationsAsync();
        await NavigateDuringPlaybackStartupAsync();
        await NavigateDuringPagingAsync();
    }

    private static async Task ContendedPlaybackAndPagingAsync()
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
        var newer = new CallItem { BackendId = "newer", Timestamp = new DateTime(2026, 11, 19) };
        await history.HoldPagingGateForTestAsync();
        Task playback = Task.CompletedTask;
        await dispatcher.DispatchAsync(() =>
        {
            history.UseHistoryDispatcher(dispatcher);
            history.OpenForTest();
            history.EnablePagingForTest(_ => Task.Run(() => new HistoryCallsPage { Calls = [newer], TotalMatches = 2 }));
            playback = history.PlayForTestAsync();
            LiveHistoryHandoffTests.Check(history.CallsForTest[0].IsPlaying && player.Started.IsEmpty,
                "History must publish selection on its dispatcher while waiting for the native gate.");
        });
        finishLive.SetResult();
        await firstClipStarted.Task.WaitAsync(Timeout);
        finishFirstClip.SetResult();
        await live.WaitAsync(Timeout);
        history.ReleasePagingGateForTest();
        await playback.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(player.Completed.SequenceEqual(["history", "newer"]) &&
                history.CallsForTest.Count == 2 && !history.IsRunningForTest && !newer.IsPlaying && violations.IsEmpty,
                "History must append and play the newer call and publish every bound change on the owning dispatcher.");
        });
        Console.WriteLine("PASS History playback and automatic paging publish bound changes on the owning dispatcher after contended native and paging locks");
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

    private static async Task NavigateDuringPagingAsync()
    {
        using var dispatcher = new HistoryTestDispatcher();
        var player = new HandoffAudio();
        var history = new HistoryViewModel(new PlaybackOwnership(), player);
        var requestStarted = LiveHistoryHandoffTests.Signal();
        var result = new TaskCompletionSource<HistoryCallsPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var violations = ObserveBoundState(history, dispatcher);
        await OpenAsync(dispatcher, history);
        await dispatcher.DispatchAsync(() => history.EnablePagingForTest(_ =>
        {
            requestStarted.SetResult();
            return result.Task;
        }));
        var retired = Task.Run(history.PlayForTestAsync);
        await requestStarted.Task.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(history.OnPageClosedAsync).Unwrap();
        await dispatcher.DispatchAsync(history.OpenForTest);
        result.SetResult(new HistoryCallsPage
        {
            Calls = [new CallItem { BackendId = "stale", Timestamp = new DateTime(2026, 11, 19) }]
        });
        await retired.WaitAsync(Timeout);
        await dispatcher.DispatchAsync(() =>
        {
            LiveHistoryHandoffTests.Check(history.CallsForTest.Count == 1 && !history.IsRunningForTest &&
                player.Completed.SequenceEqual(["history"]) && violations.IsEmpty,
                "A delayed History page from a retired playback request must not append into the new visit.");
        });
        Console.WriteLine("PASS Late History results from a closed visit cannot mutate the visible collection after reentry");
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
