using System.Collections.Concurrent;

// UIKit dispatches captured await continuations to the same UI thread as user actions.
internal sealed class PlaybackTestUiContext : SynchronizationContext
{
    private readonly BlockingCollection<Action> _turns = new();
    private event Action? TurnCompleted;

    public override void Post(SendOrPostCallback callback, object? state) =>
        _turns.Add(() => callback(state));

    private void Dispatch(Action action)
    {
        if (Current == this)
            action();
        else
            _turns.Add(action);
    }

    internal static Task RunAsync(Func<PlaybackTestUiContext, Task> scenario) => Task.Run(() =>
    {
        var ui = new PlaybackTestUiContext();
        var previous = Current;
        var previousPost = MainThread.PostForTest;
        using var turns = ui._turns;
        try
        {
            SetSynchronizationContext(ui);
            MainThread.PostForTest = ui.Dispatch;
            var test = scenario(ui);
            _ = test.ContinueWith(_ => turns.CompleteAdding(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            foreach (var turn in turns.GetConsumingEnumerable())
            {
                turn();
                ui.TurnCompleted?.Invoke();
            }
            test.GetAwaiter().GetResult();
        }
        finally
        {
            MainThread.PostForTest = previousPost;
            SetSynchronizationContext(previous);
        }
    });

    // Observe state only between complete UI callbacks, including the seek validation
    // and publication that run together in one production continuation.
    internal async Task WaitForStateAsync(Func<bool> reached, TimeSpan timeout)
    {
        if (reached())
            return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            if (reached())
                ready.TrySetResult();
        }

        TurnCompleted += Observe;
        try { await ready.Task.WaitAsync(timeout); }
        finally { TurnCompleted -= Observe; }
    }
}
