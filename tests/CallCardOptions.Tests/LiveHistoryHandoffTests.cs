using System.Collections.Concurrent;
using System.Threading.Channels;
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
            await ManualSelectionDuringPlaybackAsync(Path.Combine(directory.FullName, "manual.sqlite"));
            await ManualSelectionDuringCommitAsync(Path.Combine(directory.FullName, "manual-commit.sqlite"));
            await ForwardManualSeekAsync(Path.Combine(directory.FullName, "forward.sqlite"));
            await BackwardManualReplayAsync(Path.Combine(directory.FullName, "backward.sqlite"));
            await RapidTapsEndAtOlderAnchorAsync(Path.Combine(directory.FullName, "rapid-anchor.sqlite"));
            await ManualSeekAcrossHistoryAsync(Path.Combine(directory.FullName, "manual-history.sqlite"));
            await AudioToggleThenRestartAsync(Path.Combine(directory.FullName, "audio-toggle.sqlite"));
            await NavigationContinuesAsync(Path.Combine(directory.FullName, "navigation.sqlite"));
            await StopDuringManualPreparationAsync(Path.Combine(directory.FullName, "manual-stop.sqlite"));
            await StopDuringNativePlaybackAsync(Path.Combine(directory.FullName, "native-stop.sqlite"), manual: false);
            await StopDuringNativePlaybackAsync(Path.Combine(directory.FullName, "native-manual-stop.sqlite"), manual: true);
            await StopAndListenFreshAsync(Path.Combine(directory.FullName, "fresh-native.sqlite"));
            await TransportDuringPlaybackAsync(Path.Combine(directory.FullName, "transport.sqlite"));
            await AddressAlertContinuationAsync(Path.Combine(directory.FullName, "address.sqlite"));
            await FiltersDuringManualPlaybackAsync(Path.Combine(directory.FullName, "filters.sqlite"));
            await BacklogPlaybackRateAsync(Path.Combine(directory.FullName, "speed.sqlite"));
            CheckRequestRetirement();
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
        Check(playback.Completed.SequenceEqual(["history"]) && await live.PendingHeadForTestAsync() == null &&
            await live.IsPendingForTestAsync("first") && await live.IsPendingForTestAsync("arrival"),
            "The returning Live worker must skip existing backlog without completing its recordings.");
        await live.DeliverForTestAsync("after-return");
        Check(playback.Completed.SequenceEqual(["history", "after-return"]),
            "Only an admission after the return boundary may play.");
        Console.WriteLine(closeBeforeRead
            ? "PASS Rapid History close and reopen cannot revive a stale Live SQLite selection"
            : "PASS History return skips the blocked Live selection and calls received while away");
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
        Check(playback.Started.SequenceEqual(["first", "history"]) &&
            playback.Completed.SequenceEqual(["history"]) && await live.PendingHeadForTestAsync() == null &&
            await live.IsPendingForTestAsync("first"),
            "The interrupted clip must stay available for review without resuming automatically.");
        Console.WriteLine("PASS Active Live cancellation drains native playback before History and skips interrupted audio on return");
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

    private static async Task ManualSelectionDuringPlaybackAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path) { AutoPlayForTest = true };
        await live.StartForTestAsync();
        foreach (var id in new[] { "playing", "two", "three", "latest", "backlog" })
            await live.AddForTestAsync(id);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var playing = Signal();
        var stopping = Signal();
        var releasePlayer = Signal();
        var attempts = 0;
        player.Playing = async (id, cancellation) =>
        {
            if (id != "playing" || Interlocked.Increment(ref attempts) != 1)
                return;
            playing.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            catch (OperationCanceledException)
            {
                stopping.TrySetResult();
                // Model native teardown, including relinquishing audio focus.
                await releasePlayer.Task.WaitAsync(token);
                throw;
            }
        };

        var preparingTwo = Signal();
        var preparingThree = Signal();
        live.PrepareForTest = (id, cancellation) =>
        {
            var signal = id == "two" ? preparingTwo : id == "three" ? preparingThree : null;
            return signal != null && signal.TrySetResult()
                ? Task.Delay(Timeout.Infinite, cancellation) : Task.CompletedTask;
        };

        var original = live.StartQueueForTestAsync();
        await playing.Task.WaitAsync(token);
        var two = live.SelectForTestAsync("two");
        await stopping.Task.WaitAsync(token);
        await preparingTwo.Task.WaitAsync(token);
        var three = live.SelectForTestAsync("three");
        await preparingThree.Task.WaitAsync(token);
        var latest = live.SelectForTestAsync("latest");
        await Task.WhenAll(two, three).WaitAsync(token);
        Check(player.Started.SequenceEqual(["playing"]) && await live.IsPendingForTestAsync("playing"),
            "A card tap must cancel native playback and start preparation without waiting for teardown or marking the clip complete.");
        releasePlayer.TrySetResult();
        await Task.WhenAll(original, latest).WaitAsync(token);
        await live.ResumedQueue.WaitAsync(token);
        Check(player.Started.SequenceEqual(["playing", "latest", "backlog"]) &&
              player.Completed.SequenceEqual(["latest", "backlog"]) &&
              await live.IsPendingForTestAsync("playing") && await live.IsPendingForTestAsync("two"),
            "The latest tap must own playback and bypass older pending rows without marking them terminal.");
        Console.WriteLine("PASS Live card taps cancel active native playback and preparation, play only the latest tap, and resume durable FIFO");
    }

    private static async Task ManualSelectionDuringCommitAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path) { AutoPlayForTest = true };
        await live.StartForTestAsync();
        foreach (var id in new[] { "first", "second", "third", "backlog" })
            await live.AddForTestAsync(id);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var committing = Signal();
        var allowCommit = Signal();
        live.BeforeCommitForTest = () =>
        {
            committing.TrySetResult();
            return allowCommit.Task.WaitAsync(token);
        };
        var secondStarted = Signal();
        var thirdStarted = Signal();
        player.Playing = (id, _) =>
        {
            if (id == "second") secondStarted.TrySetResult();
            if (id == "third") thirdStarted.TrySetResult();
            return Task.CompletedTask;
        };

        var first = live.StartQueueForTestAsync();
        await committing.Task.WaitAsync(token);
        var second = live.SelectForTestAsync("second");
        await secondStarted.Task.WaitAsync(token);
        var third = live.SelectForTestAsync("third");
        await thirdStarted.Task.WaitAsync(token);
        Check(!first.IsCompleted && await live.IsPendingForTestAsync("first"),
            "Manual calls must acquire the released native player while the prior confirmed end is still being committed.");
        allowCommit.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(token);
        await live.ResumedQueue.WaitAsync(token);
        Check(player.Completed.SequenceEqual(["first", "second", "third", "backlog"]),
            "Multiple confirmed ends must persist without letting automatic FIFO replay a heard recording.");
        Console.WriteLine("PASS Manual playback bypasses outstanding completion writes and FIFO waits for confirmed dispositions");
    }

    private static async Task StopDuringManualPreparationAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path) { AutoPlayForTest = true };
        await live.StartForTestAsync();
        await live.AddForTestAsync("unavailable");
        await live.AddForTestAsync("selected");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var preparing = Signal();
        live.PrepareForTest = (id, cancellation) =>
        {
            if (id == "unavailable")
                return Task.FromException(new IOException("Test recording unavailable."));
            preparing.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellation);
        };
        await live.StartQueueForTestAsync().WaitAsync(token);
        Check(live.RetryForTest == "unavailable", "Unavailable audio should leave the original recording pending for retry.");
        var manual = live.SelectForTestAsync("selected");
        await preparing.Task.WaitAsync(token);
        Check(live.RetryForTest == null, "The manual call must take priority over the blocked FIFO head.");
        await live.StopForTestAsync().WaitAsync(token);
        await manual.WaitAsync(token);
        Check(player.Started.IsEmpty && live.ResumeRequests == 0, "Explicit Stop must cancel manual preparation without starting native audio.");
        await live.AssertPendingAfterRestartAsync("unavailable");
        await live.AssertPendingAfterRestartAsync("selected");
        Console.WriteLine("PASS Manual selection can replace an unavailable recording and Explicit Stop leaves both recordings pending");
    }

    private static async Task TransportDuringPlaybackAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path) { MutedForTest = "muted" };
        await live.StartForTestAsync();
        await live.AddForTestAsync("old", later: true);
        await live.AddForTestAsync("missing", audio: false);
        await live.AddForTestAsync("muted");
        await live.AddForTestAsync("middle");
        await live.AddForTestAsync("edge");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var started = Channel.CreateUnbounded<string>();
        var jumpRetired = Signal();
        var releaseJump = Signal();
        var middleAttempts = 0;
        player.Playing = async (id, cancellation) =>
        {
            var jumping = id == "middle" && Interlocked.Increment(ref middleAttempts) == 2;
            started.Writer.TryWrite(id);
            if (id == "edge")
                return;
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            catch (OperationCanceledException) when (jumping)
            {
                jumpRetired.TrySetResult();
                await releaseJump.Task.WaitAsync(token);
                throw;
            }
        };

        var queue = live.StartQueueForTestAsync();
        Check(await started.Reader.ReadAsync(token) == "old", "FIFO starts at the first local arrival.");
        var manual = live.SelectForTestAsync("middle");
        Check(await started.Reader.ReadAsync(token) == "middle", "A tap must interrupt autoplay.");
        var previous = live.NavigateForTestAsync(1);
        Check(await started.Reader.ReadAsync(token) == "old", "Previous must bypass unavailable and muted rows.");
        var next = live.NavigateForTestAsync(-1);
        Check(await started.Reader.ReadAsync(token) == "middle", "Next must use the playing call's arrival anchor.");
        Check(await live.IsPendingForTestAsync("old") && await live.IsPendingForTestAsync("middle"),
            "Transport cancellation must leave interrupted rows pending.");
        var jump = live.JumpForTestAsync();
        await jumpRetired.Task.WaitAsync(token);
        var arrival = live.AddForTestAsync("new");
        Check(!arrival.IsCompleted, "Jump must reserve its boundary before waiting for native cleanup.");
        releaseJump.TrySetResult();
        await arrival.WaitAsync(token);
        Check(await started.Reader.ReadAsync(token) == "edge", "Jump must interrupt the current clip and select the FIFO edge.");
        await Task.WhenAll(queue, manual, previous, next, jump).WaitAsync(token);
        Check(player.Completed.SequenceEqual(["edge"]) && !await live.IsPendingForTestAsync("missing"),
            "Only deliberate Jump may skip the older backlog.");
        Check(await live.IsPendingForTestAsync("new"), "An arrival after Jump must remain eligible.");
        Console.WriteLine("PASS Previous, Next, and Jump preempt native playback using the durable arrival anchor");
    }

    private static async Task StopDuringNativePlaybackAsync(string path, bool manual)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path) { AutoPlayForTest = true };
        await live.StartForTestAsync();
        await live.AddForTestAsync("clip");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var started = Signal();
        var canceled = Signal();
        var finishTeardown = Signal();
        player.Playing = async (_, cancellation) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                await finishTeardown.Task.WaitAsync(token);
                throw;
            }
        };
        var playback = manual ? live.SelectForTestAsync("clip") : live.StartQueueForTestAsync();
        await started.Task.WaitAsync(token);
        var stop = live.StopForTestAsync();
        await canceled.Task.WaitAsync(token);
        Check(!stop.IsCompleted, "Stop waits for the canceled native owner to finish teardown.");
        finishTeardown.TrySetResult();
        await Task.WhenAll(playback, stop).WaitAsync(token);
        await live.AssertPendingAfterRestartAsync("clip");
        Check(player.Completed.IsEmpty && live.ResumeRequests == 0, "A canceled native clip must stay pending without restarting Live.");
        Console.WriteLine($"PASS Explicit Stop during {(manual ? "manual" : "automatic")} playback cancels the native owner and preserves SQLite state");
    }

    private static async Task StopAndListenFreshAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path) { AutoPlayForTest = true };
        await live.StartForTestAsync(fresh: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        for (var i = 0; i < 3; i++)
        {
            var clip = $"active-{i}";
            var backlog = $"backlog-{i}";
            var started = Signal();
            player.Playing = (id, cancellation) =>
            {
                Check(id == clip, "Start must leave the previous session's backlog silent.");
                started.TrySetResult();
                return Task.Delay(Timeout.Infinite, cancellation);
            };
            await live.AddForTestAsync(clip);
            await live.AddForTestAsync(backlog);
            var playback = live.StartQueueForTestAsync();
            await started.Task.WaitAsync(token);
            var stopping = live.StopForTestAsync();
            var restarting = live.StartForTestAsync(fresh: true);
            await Task.WhenAll(playback, stopping, restarting).WaitAsync(token);
            await live.StartQueueForTestAsync().WaitAsync(token);
            Check(player.Started.Count == i + 1 && player.Completed.IsEmpty && live.ResumeRequests == 0,
                "Rapid Stop then Play must wait at Live after retiring interrupted audio and accumulated backlog.");
        }
        player.Playing = null;
        await live.AddForTestAsync("first-after-start");
        await live.AddForTestAsync("second-after-start");
        await live.StartQueueForTestAsync().WaitAsync(token);
        Check(player.Completed.SequenceEqual(["first-after-start", "second-after-start"]),
            "Only current-session arrivals should play after Start, in FIFO order.");
        Console.WriteLine("PASS Rapid Stop then Play cancels the old owner and waits for new FIFO arrivals on each new session");
    }

    private static async Task AddressAlertContinuationAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync();
        foreach (var id in new[] { "earlier-pending", "alert", "newer-one", "newer-two" })
            await live.AddForTestAsync(id);
        await live.SelectAddressForTestAsync("alert");
        await live.ResumedQueue.WaitAsync(TimeSpan.FromSeconds(10));
        Check(player.Completed.SequenceEqual(["alert", "newer-one", "newer-two"]) &&
              await live.IsPendingForTestAsync("earlier-pending"),
            "The alert anchors playback at its arrival and keeps bypassed recordings available for explicit retrieval.");
        Console.WriteLine("PASS Address alerts continue toward Live from the selected arrival");
    }

    private static async Task FiltersDuringManualPlaybackAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path)
        {
            HiddenForTest = "hidden", MutedForTest = "muted"
        };
        await live.StartForTestAsync();
        foreach (var id in new[] { "hidden", "muted", "playing" })
            await live.AddForTestAsync(id);
        await live.SelectForTestAsync("hidden");
        await live.SelectForTestAsync("muted");
        await live.ResumedQueue.WaitAsync(TimeSpan.FromSeconds(10));
        Check(player.Completed.SequenceEqual(["muted", "playing"]),
            "An explicit tap can hear muted audio, while a hidden recording cannot take native playback.");
        Console.WriteLine("PASS Manual taps can play muted calls, hidden calls stay silent, and FIFO does not replay them");
    }

    private static async Task ForwardManualSeekAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync();
        foreach (var id in new[] { "A", "B", "C", "D", "E" })
            await live.AddForTestAsync(id, later: id is "A" or "C");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var started = new SemaphoreSlim(0);
        var releaseD = Signal();
        player.Playing = (id, cancellation) =>
        {
            if (id is "B" or "D") started.Release();
            return id == "B" ? Task.Delay(Timeout.Infinite, cancellation) :
                id == "D" ? releaseD.Task.WaitAsync(cancellation) : Task.CompletedTask;
        };

        var queue = live.StartQueueForTestAsync();
        await started.WaitAsync(token);
        var seek = live.SelectForTestAsync("D");
        await started.WaitAsync(token);
        await live.AddForTestAsync("HTTP-after-D", origin: RecordingOrigin.History);
        Check(live.CallsWaiting == 2, "The Live counter must count E and the new call, without including bypassed B or C.");
        releaseD.TrySetResult();
        await Task.WhenAll(queue, seek).WaitAsync(token);
        await live.ResumedQueue.WaitAsync(token);
        await live.AddForTestAsync("Live-after-catch-up");
        await live.StartQueueForTestAsync().WaitAsync(token);

        Check(player.Started.SequenceEqual(["A", "B", "D", "E", "HTTP-after-D", "Live-after-catch-up"]) &&
              player.Completed.SequenceEqual(["A", "D", "E", "HTTP-after-D", "Live-after-catch-up"]) &&
              await live.IsPendingForTestAsync("B") && await live.IsPendingForTestAsync("C") && !live.AutoPlayForTest,
            "D must anchor continuation at E and later HTTP/Live arrivals, retain bypassed rows, and leave AutoPlay off.");
        Console.WriteLine("PASS Forward seek plays D, E, and later arrivals without returning to canceled B or pending C");
    }

    private static async Task BackwardManualReplayAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync();
        foreach (var id in new[] { "A", "B", "C", "offscreen", "D", "E" })
            await live.AddForTestAsync(id, visible: id != "offscreen");
        foreach (var id in new[] { "A", "C", "E", "offscreen" })
            await live.MarkCompletedForTestAsync(id);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var startedD = Signal();
        var attemptsD = 0;
        player.Playing = (id, cancellation) =>
            id == "D" && Interlocked.Increment(ref attemptsD) == 1 && startedD.TrySetResult()
                ? Task.Delay(Timeout.Infinite, cancellation) : Task.CompletedTask;

        var ahead = live.SelectForTestAsync("D");
        await startedD.Task.WaitAsync(token);
        await live.SelectForTestAsync("B").WaitAsync(token);
        await Task.WhenAll(ahead, live.ResumedQueue).WaitAsync(token);
        await live.AddForTestAsync("F");
        await live.StartQueueForTestAsync().WaitAsync(token);

        Check(player.Started.SequenceEqual(["D", "B", "C", "D", "E", "F"]) &&
              player.Completed.SequenceEqual(["B", "C", "D", "E", "F"]) &&
              !await live.IsPendingForTestAsync("C") && !await live.IsPendingForTestAsync("E") &&
              !await live.IsPendingForTestAsync("offscreen"),
            "Backward seek must merge visible completed replays with pending FIFO, leaving terminal rows unchanged.");
        Console.WriteLine("PASS Backward seek plays B, C, D, E, and F while completed replays retain their dispositions");
    }

    private static async Task RapidTapsEndAtOlderAnchorAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync();
        foreach (var id in new[] { "A", "B", "C", "D", "E" })
            await live.AddForTestAsync(id);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var prepareB = Signal();
        var prepareD = Signal();
        live.PrepareForTest = (id, cancellation) =>
        {
            var hold = id == "B" ? prepareB : id == "D" ? prepareD : null;
            return hold != null && hold.TrySetResult()
                ? Task.Delay(Timeout.Infinite, cancellation) : Task.CompletedTask;
        };

        var b = live.SelectForTestAsync("B");
        await prepareB.Task.WaitAsync(token);
        var d = live.SelectForTestAsync("D");
        await prepareD.Task.WaitAsync(token);
        var a = live.SelectForTestAsync("A");
        await Task.WhenAll(b, d, a).WaitAsync(token);
        await live.ResumedQueue.WaitAsync(token);

        Check(player.Started.SequenceEqual(["A", "B", "C", "D", "E"]) &&
              player.Completed.SequenceEqual(["A", "B", "C", "D", "E"]),
            "Only the latest tap should own manual playback, then A must drive FIFO through B, C, D, and E.");
        Console.WriteLine("PASS Rapid B, D, A taps start only A and continue through B, C, D, and E");
    }

    private static async Task ManualSeekAcrossHistoryAsync(string path)
    {
        var player = new HandoffAudio();
        var ownership = new PlaybackOwnership();
        var live = new MainViewModel(ownership, player, path);
        var history = new HistoryViewModel(ownership, player);
        await live.StartForTestAsync();
        foreach (var id in new[] { "old", "selected", "after-one", "after-two" })
            await live.AddForTestAsync(id);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var started = Signal();
        player.Playing = (id, cancellation) => id == "selected" && started.TrySetResult()
            ? Task.Delay(Timeout.Infinite, cancellation) : Task.CompletedTask;

        var manual = live.SelectForTestAsync("selected");
        await started.Task.WaitAsync(token);
        while (await live.PendingHeadForTestAsync() != "selected")
            await Task.Delay(10, token);
        history.OpenForTest();
        await history.PlayForTestAsync().WaitAsync(token);
        await manual.WaitAsync(token);
        await history.OnPageClosedAsync().WaitAsync(token);
        await live.ResumedQueue.WaitAsync(token);
        Check(player.Started.SequenceEqual(["selected", "history"]) && await live.PendingHeadForTestAsync() == null &&
              await live.IsPendingForTestAsync("old") && await live.IsPendingForTestAsync("selected"),
            "History return must abandon the interrupted manual traversal and retain the recordings.");
        await live.DeliverForTestAsync("after-return");
        Check(player.Completed.SequenceEqual(["history", "after-return"]),
            "Only a call arriving after History closed may play automatically.");
        Console.WriteLine("PASS History interrupts manual traversal and returns at the current Live edge");
    }

    private static async Task AudioToggleThenRestartAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync(fresh: true);
        await live.AddForTestAsync("old-bypassed");
        await live.AddForTestAsync("manual");
        await live.AddForTestAsync("first-catch-up");
        await live.SelectForTestAsync("manual");
        await live.ResumedQueue.WaitAsync(TimeSpan.FromSeconds(10));

        await live.SetAudioForTestAsync(false);
        await live.AddForTestAsync("muted-period");
        var boundary = live.SetAudioForTestAsync(true);
        await live.AddForTestAsync("after-audio-on");
        await live.AddForTestAsync("after-audio-on-2");
        await boundary;
        await live.StartQueueForTestAsync();
        Check(player.Completed.SequenceEqual(["manual", "first-catch-up", "after-audio-on", "after-audio-on-2"]) &&
              await live.IsPendingForTestAsync("muted-period") && !live.AutoPlayForTest,
            "Enabling audio must reserve its FIFO boundary ahead of subsequent arrivals without changing AutoPlay.");

        await live.AddForTestAsync("pending-at-restart");
        var reopened = new MainViewModel(new PlaybackOwnership(), player, path);
        await reopened.StartForTestAsync(fresh: true);
        await reopened.AddForTestAsync("fresh-A");
        await reopened.AddForTestAsync("fresh-B");
        await reopened.StartQueueForTestAsync();
        Check(player.Completed.SequenceEqual(["manual", "first-catch-up", "after-audio-on", "after-audio-on-2", "fresh-A", "fresh-B"]),
            "A fresh process must discard the prior listening cursor and old playback backlog.");
        Console.WriteLine("PASS Audio Off/On and fresh launch establish independent Live boundaries");
    }

    private static async Task NavigationContinuesAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path);
        await live.StartForTestAsync();
        foreach (var id in new[] { "A", "B", "C", "D", "E" })
            await live.AddForTestAsync(id, visible: id != "B");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var c = Signal();
        var b = Signal();
        var d = Signal();
        var cAttempts = 0;
        var dAttempts = 0;
        player.Playing = (id, cancellation) =>
        {
            var hold = id switch
            {
                "C" when Interlocked.Increment(ref cAttempts) == 1 => c,
                "B" => b,
                "D" when Interlocked.Increment(ref dAttempts) == 1 => d,
                _ => null
            };
            hold?.TrySetResult();
            return hold != null ? Task.Delay(Timeout.Infinite, cancellation) : Task.CompletedTask;
        };

        var actions = new List<Task> { live.SelectForTestAsync("C") };
        try
        {
            await c.Task.WaitAsync(token);
            actions.Add(live.NavigateForTestAsync(1));
            await b.Task.WaitAsync(token);
            actions.Add(live.NavigateForTestAsync(-1));
            await d.Task.WaitAsync(token);
            actions.Add(live.JumpForTestAsync());
            await Task.WhenAll(actions).WaitAsync(token);
            await live.ResumedQueue.WaitAsync(token);
            await live.DeliverForTestAsync("F").WaitAsync(token);
            await live.DeliverForTestAsync("G").WaitAsync(token);
            Check(player.Started.SequenceEqual(["C", "B", "C", "D", "E", "F", "G"]) &&
                  player.Completed.SequenceEqual(["C", "E", "F", "G"]) &&
                  !await live.IsPendingForTestAsync("A") && !await live.IsPendingForTestAsync("D"),
                "Previous must reach off-screen B, Next must continue C to D, and Jump must leave only the edge and later arrivals.");
        }
        finally
        {
            // Drain held attempts even when a test assertion fails, before the suite deletes SQLite files.
            await live.StopForTestAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(actions.Append(live.ResumedQueue)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Console.WriteLine("PASS Previous and Next move the playback anchor, including off-screen calls, and Jump keeps later arrivals");
    }

    private static async Task BacklogPlaybackRateAsync(string path)
    {
        var player = new HandoffAudio();
        var live = new MainViewModel(new PlaybackOwnership(), player, path) { PlaybackSpeedStep = 2 };
        await live.StartForTestAsync();
        await live.AddForTestAsync("first", later: true);
        await live.AddForTestAsync("edge");
        await live.StartQueueForTestAsync();
        Check(player.Rates.SequenceEqual([1.5, 1.0]),
            "Catch-up speed must follow newer pending arrival positions and return to normal at the edge.");
        Console.WriteLine("PASS Backlog playback speeds up by arrival order and returns to normal at the Live edge");
    }

    private static void CheckRequestRetirement()
    {
        var requests = new LivePlaybackRequests();
        using var old = requests.Begin(manual: false);
        using var cancellation = old.Token.Register(old.Dispose);
        using var latest = requests.Begin(manual: true);
        Check(old.Token.IsCancellationRequested && latest.CanContinue,
            "Inline disposal from cancellation must preserve the old token and leave the newer request in control.");
        requests.RetireCurrent();
        Check(latest.Token.IsCancellationRequested && !latest.CanContinue, "Explicit retirement must leave no current request.");
        Console.WriteLine("PASS Cancellation callbacks can retire old requests without disposing the newer request or invalidating the old token");
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
    internal ConcurrentQueue<double> Rates { get; } = new();
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
        Rates.Enqueue(playbackRate);
        if (Playing is { } play)
            await play(audioUrl, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LiveHistoryHandoffTests.Check(completion.Confirm(cancellationToken), "The fake native end must be confirmed.");
        Completed.Enqueue(audioUrl);
    }

    public Task StopAsync() => throw new InvalidOperationException("A page must never stop the shared player globally.");
}
