using System.Collections.ObjectModel;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using JoesScanner.Views;
using Microsoft.Data.Sqlite;

internal static partial class MobileRecoveryTests
{
    internal static Task LiveReaderFollowsOnlyTheActualTop()
    {
        var server = Guid.NewGuid();
        var display = new LiveCallDisplay(ReadingCards(server));
        var reader = new ReadingView(display.CombinedReader);
        display.Observe();
        reader.Open();

        Admit(display, new() { ServerId = server, BackendId = "top-arrival" });
        reader.List.Move(100); // A native layout can shift the previous first card.
        reader.List.Dispatcher.Drain();
        Check(reader.State.IsFollowing && reader.List.Offset == 0 && !reader.Button.IsVisible,
            "The actual top should continue following newly inserted cards.");

        reader.List.Move(0.5, user: true);
        Check(!reader.State.IsFollowing && reader.State.ResolveAnchor() is { Index: 0, Top: -0.5 },
            "A partially visible first card is away from the Live edge.");
        reader.List.Move(101, user: true);
        var anchor = reader.State.ResolveAnchor()!.Value.Card;
        Check(!reader.State.IsFollowing && reader.State.ResolveAnchor() is { Index: 1, Top: -1 },
            "A first visible index of one must remain in reading mode.");

        for (var i = 0; i < 5; i++)
            Admit(display, new() { ServerId = server, BackendId = $"new-{i}" });
        display.Combined.Insert(0, new() { ServerId = server, BackendId = "restored-from-durable" });
        Check(reader.List.Dispatcher.PendingCount == 1 && reader.Button.Text == "New Calls (5)" && reader.Button.IsVisible,
            "Rapid admissions should coalesce layout work and give their exact visual count.");
        reader.List.Dispatcher.Drain();
        Check(reader.State.ResolveAnchor() is { Top: -1 } retained && ReferenceEquals(retained.Card, anchor) &&
            reader.List.TopOf(retained.Index) == -1 && reader.List.Offset > 0,
            "Arrivals should preserve the anchor and the partially visible pixel offset.");

        reader.Button.Click();
        reader.List.Dispatcher.Drain();
        Check(reader.State.IsFollowing && reader.List.Offset == 0 && reader.State.UnseenCount == 0 && !reader.Button.IsVisible,
            "New Calls should return to the visual edge and clear its counter.");
        reader.List.Move(15); // A late programmatic scroll notification has no user input.
        Check(reader.State.IsFollowing, "Acknowledging a programmatic scroll must not change the reader's intent.");
        reader.List.Move(35, user: true);
        Admit(display, new() { ServerId = server, BackendId = "after-click" });
        reader.List.Move(0, user: true);
        reader.List.Dispatcher.Drain();
        Check(reader.State.IsFollowing && reader.State.UnseenCount == 0 && reader.List.Offset == 0,
            "A deliberate return to the real top must clear unread arrivals and cancel the obsolete correction.");
        reader.Close();
        display.Detach();
        return Task.CompletedTask;
    }

    internal static Task LiveReaderKeepsTranscriptAnchor()
    {
        var server = Guid.NewGuid();
        var state = new LiveCallState(RecorderA, "earlier-recording");
        var source = ReadingCards(server);
        var updating = new CallItem { ServerId = server, BackendId = "earlier-recording", LiveState = state };
        source[0] = updating;
        var display = new LiveCallDisplay(source);
        var reader = new ReadingView(display.CombinedReader);
        display.Observe();
        reader.Open();
        reader.List.Move(140, user: true);
        var anchor = source[1];
        var originalOrder = source.ToArray();

        var corrected = string.Join(" ", Enumerable.Repeat("A long dispatch transcription with a corrected street name.", 20));
        state.Observe(new(new(), corrected, 0), 1);
        Check(updating.ApplyLiveUpdate() && updating.Transcription == corrected, "A late transcription must update the canonical card.");
        reader.List.SetHeight(updating, 210);
        reader.Viewport.CardSizeChanged(updating);
        reader.Viewport.CardSizeChanged(updating);
        reader.List.Dispatcher.Drain();
        Check(reader.List.TopOf(1) == -40 && reader.List.OffsetCorrections == 1 &&
            ReferenceEquals(reader.State.ResolveAnchor()?.Card, anchor) && reader.List.TopMoves == 0,
            "Multiline growth above a partially visible reader anchor should use one pixel correction.");

        state.Observe(new(new(), "Corrected address.", 0), 2);
        Check(updating.ApplyLiveUpdate(), "The corrected text must replace the prior text on the same card.");
        reader.List.SetHeight(updating, 60);
        reader.Viewport.CardSizeChanged(updating);
        reader.List.Dispatcher.Drain();
        Check(reader.List.TopOf(1) == -40 && reader.List.OffsetCorrections == 2 &&
            source.SequenceEqual(originalOrder) && reader.State.UnseenCount == 0,
            "Transcript shrinkage should preserve the pixel anchor without reordering or producing a New Calls badge.");

        reader.List.SetHeight(anchor, 20);
        reader.Viewport.CardSizeChanged(anchor);
        reader.List.Dispatcher.Drain();
        var corrections = reader.List.OffsetCorrections;
        reader.Viewport.CardSizeChanged(anchor);
        reader.List.Dispatcher.Drain();
        Check(reader.List.TopOf(1) == 0 && reader.List.OffsetCorrections == corrections && !reader.State.IsFollowing,
            "If the former offset would hide a shorter card, keep that card readable without repeated moves.");
        reader.Close();
        display.Detach();
        return Task.CompletedTask;
    }

    internal static Task LiveReaderTracksEachProjection()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var source = ReadingCards(a);
        foreach (var card in ReadingCards(b))
            source.Add(card);
        var favorites = source.ToHashSet();
        var display = new LiveCallDisplay(source, favorite: favorites.Contains);
        display.SetServers([a, b]);
        display.Observe();
        var combined = new ReadingView(display.CombinedReader);
        var saved = new ReadingView(display.FavoritesReader);
        var first = new ReadingView(display.ReaderForServer(a));
        var second = new ReadingView(display.ReaderForServer(b));
        foreach (var projection in new[] { combined, saved, first, second })
        {
            projection.Open();
            projection.List.Move(120, user: true);
            projection.Close();
        }

        var callA = new CallItem { ServerId = a, BackendId = "shared" };
        var callB = new CallItem { ServerId = b, BackendId = "shared" };
        favorites.Add(callA);
        Admit(display, callA);
        Admit(display, callB);
        Check(combined.State.UnseenCount == 2 && saved.State.UnseenCount == 1 &&
            first.State.UnseenCount == 1 && second.State.UnseenCount == 1,
            "Combined counts both recorder identities and each server and Favorites count only their own projection.");

        favorites.Add(callB);
        display.RefreshFavorites();
        Check(saved.State.UnseenCount == 1 && display.Favorites.Contains(callB),
            "Revealing an existing card through a Favorite edit must not create an admission.");
        favorites.Remove(callA);
        display.RefreshFavorites();
        Check(saved.State.UnseenCount == 0 && first.State.UnseenCount == 1,
            "A call removed from Favorites no longer counts there.");

        display.SetSeparate(true);
        display.SetServers([b, a]);
        display.Select(a);
        first.Open();
        first.Button.Click();
        first.List.Dispatcher.Drain();
        first.Close();
        second.Open();
        Check(first.State.IsFollowing && first.State.UnseenCount == 0 && !second.State.IsFollowing &&
            second.Button.Text == "New Calls (1)" && combined.State.UnseenCount == 2 && !saved.State.IsFollowing,
            "Returning one recorder to its edge must not clear any other reader after tab or panel reordering.");
        second.Close();
        display.Detach();
        return Task.CompletedTask;
    }

    internal static Task LiveReaderRetiresDelayedScrolls()
    {
        var server = Guid.NewGuid();
        var display = new LiveCallDisplay(ReadingCards(server));
        display.Observe();
        var reader = new ReadingView(display.CombinedReader);
        reader.Open();
        reader.List.Move(140, user: true);
        Admit(display, new() { ServerId = server, BackendId = "arrival-before-stop" });
        reader.Viewport.CancelPendingScroll();
        reader.List.Dispatcher.Drain();
        Check(reader.List.TopMoves == 0 && reader.List.OffsetCorrections == 0 && reader.State.UnseenCount == 1,
            "Stop must cancel deferred scrolling and keep existing reader state.");

        reader.Viewport.CardSizeChanged(display.Combined[0]);
        reader.Close();
        Check(reader.List.ScrollSubscribers == 0, "Leaving Live must detach the viewport's collection and native event subscriptions.");
        reader.List.Dispatcher.Drain();
        Check(reader.List.OffsetCorrections == 0 && !reader.Button.IsVisible, "A callback queued before leaving cannot restore a hidden view.");

        reader.Open();
        reader.Viewport.CardSizeChanged(display.Combined[0]);
        reader.List.Unmount();
        reader.List.Dispatcher.Drain();
        Check(reader.List.OffsetCorrections == 1, "A retired native handler must not receive deferred corrections.");
        reader.List.Mount();
        display.Combined.Clear();
        Admit(display, new() { ServerId = server, BackendId = "fresh-session" });
        reader.List.Move(0);
        reader.List.Dispatcher.Drain();
        Check(reader.State.IsFollowing && reader.State.ResolveAnchor() == null && reader.State.UnseenCount == 0 &&
            reader.List.OffsetCorrections == 1, "A fresh listening session must clear reader anchors and fence obsolete layout work.");
        reader.Close();
        display.Detach();
        return Task.CompletedTask;
    }

    internal static async Task LiveNewCallsLeavesAudioAndRetentionUntouched()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var database = new IngestionDatabase();
        var server = Guid.NewGuid();
        var run = new DurableLiveQueue.Run(RecorderA, 1, null, ServerId: server);
        var queue = database.OpenQueue();
        await queue.StartListeningAsync(run, Now, token);
        foreach (var id in new[] { "completed", "pending" })
            await queue.ObserveAsync(run, id, Now, new(new(), "Test transcript.", 0, RecorderA + "/" + id + ".wav"), token);
        await queue.CommitCompletionAsync(run, "completed");
        var before = await ReadQueueAsync();

        var ownership = new PlaybackOwnership();
        var lease = ownership.Live!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playing = ownership.RunNativeAsync(lease, token, cancellation =>
        {
            started.SetResult();
            return finish.Task.WaitAsync(cancellation);
        });
        await started.Task.WaitAsync(token);

        var source = ReadingCards(server, LiveCallDisplay.CardLimit);
        var playingCard = source[^1];
        var display = new LiveCallDisplay(source);
        display.Observe();
        var reader = new ReadingView(display.CombinedReader);
        reader.Open();
        reader.List.Move(49000, user: true);
        for (var i = 0; i < LiveCallDisplay.CardLimit + 10; i++)
        {
            var card = new CallItem { ServerId = server, BackendId = $"admitted-{i}" };
            Check(LiveCallDisplay.InsertCard(source, card, 0, playingCard), "A new Live card should enter the capped display.");
            display.RecordVisibleAdmission(card);
        }
        reader.List.Dispatcher.Drain();
        Check(source.Count == LiveCallDisplay.CardLimit && source.Contains(playingCard) &&
            reader.State.UnseenCount == LiveCallDisplay.CardLimit - 1 && reader.List.Offset > 0,
            "The 500-card window should retain its playing reference and count only unseen cards still in the display.");
        reader.Button.Click();
        reader.List.Dispatcher.Drain();
        Check(reader.List.Offset == 0 && reader.State.UnseenCount == 0 && ownership.CanPublishLivePlayback &&
            ReferenceEquals(ownership.Live, lease) && !playing.IsCompleted && before == await ReadQueueAsync(),
            "New Calls must preserve the native clip, durable owner, playback floor, completed rows and pending work.");
        finish.SetResult();
        await playing.WaitAsync(token);
        reader.Close();
        display.Detach();

        async Task<string?> ReadQueueAsync()
        {
            using var connection = new SqliteConnection($"Data Source={database.DbPath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(token);
            using var read = connection.CreateCommand();
            read.CommandText = """
                SELECT owner || ':' || playback_floor || ':' ||
                    (SELECT group_concat(recording_id || '=' || disposition, ',' ORDER BY arrival_sequence)
                     FROM live_queue_recordings)
                FROM live_queue_servers;
                """;
            return (string?)await read.ExecuteScalarAsync(token);
        }
    }

    internal static async Task LiveTranscriptOnlyObservationsDoNotWakePlayback()
    {
        using var database = new IngestionDatabase();
        var queue = database.OpenQueue();
        var run = new DurableLiveQueue.Run(RecorderA, 1, null);
        await queue.StartListeningAsync(run, Now, default);
        var clip = new LiveCallSnapshot(new(), null, 0);
        var original = await SaveAsync(clip, initial: true);
        var transcript = clip with { Transcription = "Late transcript." };
        var textOnly = await SaveAsync(transcript);
        var playable = transcript with { AudioUrl = RecorderA + "/clip.wav" };
        var lateAudio = await SaveAsync(playable);
        var correction = await SaveAsync(playable with { Transcription = "Corrected transcript." });
        await queue.CommitCompletionAsync(run, "clip");
        var afterCompletion = await SaveAsync(playable with { Transcription = "Final corrected transcript." });

        Check(original is { FirstAdmission: true, PlaybackChanged: false } &&
            textOnly is { Changed: true, FirstAdmission: false, PlaybackChanged: false } &&
            lateAudio is { Changed: true, FirstAdmission: false, PlaybackChanged: true } &&
            correction is { Changed: true, FirstAdmission: false, PlaybackChanged: false } &&
            afterCompletion is { FirstAdmission: false, PlaybackChanged: false } &&
            original.ArrivalSequence == afterCompletion.ArrivalSequence && await queue.NextAsync(run, 0, default) == null,
            "Transcript updates must preserve identity and completion without waking playback, while an audio arrival still wakes it.");

        Task<DurableLiveQueue.Observation> SaveAsync(LiveCallSnapshot snapshot, bool initial = false) =>
            queue.ObserveWithPositionAsync(run, "clip", Now, snapshot, default, RecordingOrigin.Live, existingOnly: !initial);
    }

    private static ObservableCollection<CallItem> ReadingCards(Guid server, int count = 6) =>
        new(Enumerable.Range(0, count).Select(i => new CallItem { ServerId = server, BackendId = $"old-{i}" }));

    private static void Admit(LiveCallDisplay display, CallItem card)
    {
        display.Combined.Insert(0, card);
        display.RecordVisibleAdmission(card);
    }

    private sealed class ReadingView
    {
        internal ReadingView(LiveReadingState state)
        {
            State = state;
            List = new(state.Cards);
            Viewport = new(List, Button, state);
        }

        internal LiveReadingState State { get; }
        internal CollectionView List { get; }
        internal Button Button { get; } = new();
        internal LiveReadingViewport Viewport { get; }

        internal void Open()
        {
            Viewport.Show(true);
            List.Dispatcher.Drain();
        }

        internal void Close() => Viewport.Show(false);
    }
}
