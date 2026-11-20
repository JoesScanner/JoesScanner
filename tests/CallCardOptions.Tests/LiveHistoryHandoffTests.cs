using System.Collections.Concurrent;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;

internal static class LiveHistoryHandoffTests
{
    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.PlaybackHandoff-");
        try
        {
            await PendingSelectionAsync(Path.Combine(directory.FullName, "selection.sqlite"), closeBeforeRead: false);
            await PendingSelectionAsync(Path.Combine(directory.FullName, "rapid.sqlite"), closeBeforeRead: true);
            await ActivePlaybackAsync(Path.Combine(directory.FullName, "active.sqlite"));
            await CompletedBeforeHandoffAsync(Path.Combine(directory.FullName, "completion.sqlite"));
            await StopDuringHistoryAsync(Path.Combine(directory.FullName, "stop.sqlite"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task PendingSelectionAsync(string path, bool closeBeforeRead)
    {
        var playback = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, playback, path);
        var history = new HistoryViewModel(ownership, playback);
        await live.StartForTestAsync();
        await live.AddForTestAsync("first");
        var selection = live.HoldNextSelection();
        var retired = live.StartQueueForTestAsync();
        await selection.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        history.OpenForTest();
        await history.PlayForTestAsync();
        await live.AddForTestAsync("arrival", later: true);
        Check(await live.IsPendingForTestAsync("first") && await live.IsPendingForTestAsync("arrival"),
            "History must leave the unheard SQLite selection and arrivals pending.");
        Check(playback.Completed.SequenceEqual(["history"]), "Live must not acquire audio while History owns it.");

        if (closeBeforeRead)
        {
            await history.OnPageClosedAsync();
            history.OpenForTest();
            await history.OnPageClosedAsync();
        }
        else
        {
            selection.Return.TrySetResult();
            await retired.WaitAsync(TimeSpan.FromSeconds(10));
            Check(playback.Completed.SequenceEqual(["history"]) && await live.IsPendingForTestAsync("first"),
                "Returning a stale selection while History is visible must not play or mark it skipped.");
            await history.OnPageClosedAsync();
        }

        await live.ResumedQueue.WaitAsync(TimeSpan.FromSeconds(10));
        selection.Return.TrySetResult();
        await retired.WaitAsync(TimeSpan.FromSeconds(10));
        Check(playback.Completed.SequenceEqual(["history", "first", "arrival"]) &&
            !await live.IsPendingForTestAsync("first") && !await live.IsPendingForTestAsync("arrival"),
            "The new Live worker must play each pending recording once and ignore the stale SQLite result.");
        Console.WriteLine(closeBeforeRead
            ? "PASS Rapid History close and reopen cannot revive a stale Live SQLite selection"
            : "PASS History ownership leaves a blocked Live selection pending and resumes it and new arrivals exactly once");
    }

    private static async Task ActivePlaybackAsync(string path)
    {
        var playback = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, playback, path);
        var history = new HistoryViewModel(ownership, playback);
        await live.StartForTestAsync();
        await live.AddForTestAsync("first");
        var started = Signal();
        var stopping = Signal();
        var release = Signal();
        var liveAttempts = 0;
        playback.Playing = async (id, token) =>
        {
            if (id != "first" || Interlocked.Increment(ref liveAttempts) != 1)
                return;
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                stopping.TrySetResult();
                await release.Task;
                throw;
            }
        };

        var interrupted = live.StartQueueForTestAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        history.OpenForTest();
        var historyPlayback = history.PlayForTestAsync();
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(playback.Started.SequenceEqual(["first"]) && await live.IsPendingForTestAsync("first"),
            "History must wait for interrupted Live native teardown while the recording remains pending.");
        release.TrySetResult();
        await Task.WhenAll(interrupted, historyPlayback).WaitAsync(TimeSpan.FromSeconds(10));
        await history.OnPageClosedAsync();
        await live.ResumedQueue.WaitAsync(TimeSpan.FromSeconds(10));
        Check(playback.Started.SequenceEqual(["first", "history", "first"]) &&
            playback.Completed.SequenceEqual(["history", "first"]),
            "The interrupted clip must remain pending until a genuine end on the replacement attempt.");
        Console.WriteLine("PASS Active Live cancellation drains native playback before History and preserves the interrupted recording");
    }

    private static async Task StopDuringHistoryAsync(string path)
    {
        var playback = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, playback, path);
        var history = new HistoryViewModel(ownership, playback);
        await live.StartForTestAsync();
        await live.AddForTestAsync("first");
        var selection = live.HoldNextSelection();
        var retired = live.StartQueueForTestAsync();
        await selection.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        history.OpenForTest();
        var started = Signal();
        var release = Signal();
        playback.Playing = async (id, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var historyPlayback = history.PlayForTestAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await live.StopForTestAsync();
        await live.AssertPendingAfterRestartAsync("first");
        release.TrySetResult();
        await historyPlayback.WaitAsync(TimeSpan.FromSeconds(10));
        await history.OnPageClosedAsync();
        selection.Return.TrySetResult();
        await retired.WaitAsync(TimeSpan.FromSeconds(10));
        Check(playback.Completed.SequenceEqual(["history"]) && live.ResumeRequests == 0,
            "Explicit monitoring Stop must leave History running and prevent Live from resuming after release.");
        Console.WriteLine("PASS Stopping monitoring during History preserves pending SQLite work and suppresses automatic Live resume");
    }

    private static async Task CompletedBeforeHandoffAsync(string path)
    {
        var playback = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, playback, path);
        var history = new HistoryViewModel(ownership, playback);
        await live.StartForTestAsync();
        await live.AddForTestAsync("first");
        var committing = Signal();
        var commit = Signal();
        live.BeforeCommitForTest = () =>
        {
            committing.TrySetResult();
            return commit.Task;
        };

        var finished = live.StartQueueForTestAsync();
        await committing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        history.OpenForTest();
        await history.PlayForTestAsync();
        await history.OnPageClosedAsync();
        Check(!live.ResumedQueue.IsCompleted && await live.IsPendingForTestAsync("first"),
            "Live must wait for the confirmed disposition before reading the queue again.");
        commit.TrySetResult();
        await Task.WhenAll(finished, live.ResumedQueue).WaitAsync(TimeSpan.FromSeconds(10));
        Check(playback.Completed.SequenceEqual(["first", "history"]) && !await live.IsPendingForTestAsync("first"),
            "A genuine end before ownership changed must commit without replaying.");
        Console.WriteLine("PASS Live waits for a confirmed disposition across the History handoff without replaying");
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal sealed class HandoffAudio : IAudioPlaybackService
{
    internal ConcurrentQueue<string> Started { get; } = new();
    internal ConcurrentQueue<string> Completed { get; } = new();
    internal Func<string, CancellationToken, Task>? Playing { get; set; }

    public Task PlayAsync(string audioUrl, CancellationToken cancellationToken = default) =>
        PlayAsync(audioUrl, 1, cancellationToken);

    public Task PlayAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken = default) =>
        PlayAsync(audioUrl, playbackRate, cancellationToken, new PlaybackCompletion());

    public async Task PlayAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken, PlaybackCompletion completion)
    {
        using var rejection = cancellationToken.Register(() => completion.Reject());
        cancellationToken.ThrowIfCancellationRequested();
        Started.Enqueue(audioUrl);
        if (Playing is { } play)
            await play(audioUrl, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LiveHistoryHandoffTests.Check(completion.Confirm(cancellationToken), "The fake native end must be confirmed.");
        Completed.Enqueue(audioUrl);
    }

    public Task StopAsync() => throw new InvalidOperationException("A page must never stop the shared player globally.");
}
