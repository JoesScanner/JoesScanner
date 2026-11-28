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
        reader.List.RepeatPosition(0); // A delayed notification samples the current viewer position.
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

    internal static Task FavoritesReaderPreservesAdmissionsAndAnchor()
    {
        var server = Guid.NewGuid();
        var source = ReadingCards(server);
        var members = source.ToHashSet();
        var display = new LiveCallDisplay(source, favorite: members.Contains);
        display.SetServers([server]);
        display.Observe();
        var saved = new ReadingView(display.FavoritesReader);
        saved.Open();
        saved.List.Move(12, user: true);
        var anchor = display.Favorites[0];
        var ordinary = new CallItem { ServerId = server, BackendId = "nonmember" };
        Admit(display, ordinary);
        Check(saved.State.UnseenCount == 0 && saved.List.Offset == 12,
            "A nonmember must leave the partially visible first Favorite in place.");

        for (var i = 0; i < 3; i++)
        {
            var member = new CallItem { ServerId = server, BackendId = $"member-{i}" };
            members.Add(member);
            Admit(display, member);
            display.RecordVisibleAdmission(member);
        }
        saved.List.Move(0); // Native layout notification before restoration.
        saved.List.Dispatcher.Drain();
        Check(saved.State.UnseenCount == 3 && ReferenceEquals(saved.State.ResolveAnchor()?.Card, anchor) &&
            saved.List.TopOf(saved.State.IndexOf(anchor)) == -12,
            "Three Favorite admissions should retain the clipped card and each count once.");

        members.Add(ordinary);
        display.RefreshFavorites();
        saved.List.SetHeight(ordinary, 145);
        saved.Viewport.CardSizeChanged(ordinary);
        saved.List.Dispatcher.Drain();
        Check(saved.State.UnseenCount == 3 && saved.List.TopOf(saved.State.IndexOf(anchor)) == -12,
            "A membership edit and transcript growth must preserve offset and count.");
        source.Remove(anchor);
        saved.List.Dispatcher.Drain();
        Check(saved.State.ResolveAnchor() is { } fallback && saved.State.IndexOf(fallback.Card) >= 0 &&
            !saved.State.IsFollowing && saved.State.UnseenCount == 3, "Eviction must resolve a surviving Favorite.");
        saved.Viewport.CardSizeChanged(ordinary);
        saved.Button.Click();
        saved.List.Dispatcher.Drain();
        Check(saved.State.IsFollowing && saved.List.Offset == 0 && saved.State.UnseenCount == 0,
            "Explicit return to Live must supersede the queued Favorite anchor correction.");
        saved.Close();
        display.Detach();
        return Task.CompletedTask;
    }

    internal static Task WindowsReaderRetainsWheelRestoration()
    {
        var server = Guid.NewGuid();
        var display = new LiveCallDisplay(ReadingCards(server, 20), favorite: _ => true);
        display.SetServers([server]);
        display.Observe();
        var reader = display.FavoritesReader;
        reader.SetVisible(true);
        var intent = new WindowsLiveScrollIntent();
        intent.BeginPhysical();
        reader.ObserveViewport(0, -12, intent.IsPhysical);
        var saved = reader.ResolveAnchor()!.Value.Card;
        for (var i = 0; i < 3; i++)
        {
            Admit(display, new() { ServerId = saved.ServerId });
            reader.ObserveViewport(reader.IndexOf(saved), -12 - 12 * (i + 1), intent.IsPhysical);
        }
        intent.EndPhysical();
        Check(reader.UnseenCount == 3 && reader.ResolveAnchor() is { Top: -48 } anchor &&
            ReferenceEquals(anchor.Card, saved) && intent.PendingOperations == 0,
            "Three Favorites must admit exactly once while the physical gesture continues with no application move.");

        var obsolete = intent.Generation;
        intent.BeginPhysical();
        reader.ObserveViewport(0, -7, intent.IsPhysical);
        Check(!intent.IsCurrent(obsolete) && reader.ResolveAnchor() is { Index: 0, Top: -7 },
            "Later input must supersede work carrying the previous gesture generation.");
        intent.EndPhysical();
        display.Detach();
        return Task.CompletedTask;
    }

    internal static Task WindowsReaderRecognizesLateRestorations()
    {
        var calls = ReadingCards(Guid.NewGuid());
        var reader = new LiveReadingState(calls);
        reader.SetVisible(true);
        reader.ReadFrom(calls[0]);
        reader.RememberPosition(calls[0], 0);
        reader.RecordAdmission(calls[1]);
        var owner = new WindowsLiveScrollIntent();
        owner.SubmittedApplication(owner.Cancel(), 17);
        foreach (var id in new[] { 18, 17, 17, 19 })
            reader.ObserveViewport(0, 0, owner.AcceptAccessibilityCompletion(id));
        Check(!reader.IsFollowing && reader.UnseenCount == 1 && reader.ResolveAnchor() is { Top: 0 },
            "A zero-position application restoration, including delayed and duplicate terminals, must preserve reading.");
        owner.SubmittedAccessibility(owner.BeginAccessibility(), 20);
        reader.ObserveViewport(0, 0, owner.AcceptAccessibilityCompletion(20));
        Check(reader.IsFollowing && reader.UnseenCount == 0, "Genuine accessibility navigation to zero resumes Live.");
        return Task.CompletedTask;
    }

    internal static Task WindowsScrollOrderingAndNavigation()
    {
        var calls = ReadingCards(Guid.NewGuid(), 20);
        var reader = new LiveReadingState(calls);
        reader.SetVisible(true);
        var owner = new WindowsLiveScrollIntent();
        var withheld = owner.Cancel();
        owner.SubmittedApplication(withheld, 2);
        owner.SubmittedApplication(owner.Cancel(), 3);
        owner.SubmittedAccessibility(owner.BeginAccessibility(), 4);
        reader.ObserveViewport(0, 0, owner.AcceptAccessibilityCompletion(4));
        owner.SubmittedAccessibility(owner.BeginAccessibility(), 5);
        reader.ObserveViewport(3, -24, owner.AcceptAccessibilityCompletion(5));
        foreach (var call in calls.Take(4))
            reader.RecordAdmission(call);
        foreach (var id in new[] { 3, 4, 2, 5, 2 })
            reader.ObserveViewport(0, 0, owner.AcceptAccessibilityCompletion(id));
        Check(!owner.IsCurrent(withheld) && !reader.IsFollowing && reader.UnseenCount == 4 &&
            ReferenceEquals(reader.ResolveAnchor()?.Card, calls[3]),
            "An old application terminal after genuine accessibility navigation to its destination erased four unread.");
        owner.Retire();
        var replacement = new WindowsLiveScrollIntent();
        replacement.SubmittedAccessibility(replacement.BeginAccessibility(), 2);
        Check(!owner.AcceptAccessibilityCompletion(2) && replacement.AcceptAccessibilityCompletion(2),
            "A correlation ID is scoped to the native view which issued it.");
        return Task.CompletedTask;
    }

    internal static Task WindowsGestureKeepsFinalPosition()
    {
        foreach (var insertAtHead in new[] { false, true })
        foreach (var detents in new[] { 1, 3 })
        {
            var calls = ReadingCards(Guid.NewGuid(), 20);
            var reader = new LiveReadingState(calls);
            var owner = new WindowsLiveScrollIntent();
            reader.SetVisible(true);
            var card = calls[0];
            var cut = -12;
            for (var detent = 0; detent < detents; detent++)
            {
                owner.BeginPhysical();
                reader.ObserveViewport(reader.IndexOf(card), cut, owner.IsPhysical);
                for (var frame = 0; frame < 3; frame++)
                {
                    var admitted = new CallItem { ServerId = card.ServerId };
                    calls.Insert(insertAtHead ? 0 : calls.Count, admitted);
                    reader.RecordAdmission(admitted);
                    cut -= 12;
                    reader.ObserveViewport(reader.IndexOf(card), cut, owner.IsPhysical);
                }
            }
            owner.EndPhysical();
            Check(reader.UnseenCount == 3 * detents && reader.ResolveAnchor()?.Top == cut &&
                ReferenceEquals(reader.ResolveAnchor()?.Card, card) && owner.PendingOperations == 0,
                "Continuing physical input across head/tail admissions lost the last observed reading position.");
        }
        return Task.CompletedTask;
    }

    internal static Task WindowsReaderBoundsSubmittedWork()
    {
        var queue = new WindowsLiveAnimationQueue();
        var firstPresenter = new WindowsLiveScrollIntent();
        for (var id = 1; id <= WindowsLiveAnimationQueue.Capacity; id++)
        {
            var generation = firstPresenter.Cancel();
            firstPresenter.SubmittedApplication(generation, id);
            queue.Add(id, firstPresenter);
        }
        Check(!queue.HasRoom && !queue.TryTake(5000, out _), "Foreign animation IDs must keep their native behavior.");
        firstPresenter.Retire();
        Check(queue.TryTake(1, out var current) && !current && queue.HasRoom,
            "An accepted, undispatched animation must remain identifiable after viewport retirement.");
        var secondPresenter = new WindowsLiveScrollIntent();
        secondPresenter.SubmittedApplication(secondPresenter.Cancel(), 5001);
        queue.Add(5001, secondPresenter);
        queue.Completed(2);
        Check(queue.TryTake(5001, out current) && current && !queue.TryTake(5001, out _) &&
            !queue.TryTake(2, out _), "Native dispatch and terminal receipts must be consumed exactly once.");
        Check(WindowsLiveScrollIntent.WheelDistance(-60, 3, 640) == 24 &&
            WindowsLiveScrollIntent.WheelDistance(30, 3, 640) == -12 &&
            WindowsLiveScrollIntent.WheelDistance(-120, 0, 640) == 0 &&
            WindowsLiveScrollIntent.WheelDistance(120, uint.MaxValue, 640) == -640,
            "Native wheel direction, fractional detents and system line/page preferences must be preserved.");

        var owner = new WindowsLiveScrollIntent();
        for (var n = 0; n < 4096; n++)
        {
            owner.SubmittedApplication(owner.Cancel(), n);
            Check(owner.PendingOperations == 1, "A missing acknowledgement accumulated operation history.");
        }
        var active = owner.Generation;
        owner.RetireApplication(active - 1, 4095);
        owner.RetireApplication(active, 4094);
        Check(owner.PendingOperations == 1, "An unrelated layout must not retire an outstanding native operation.");
        owner.RetireApplication(active, 4095);
        Check(owner.PendingOperations == 0, "Measured placement must retire an app operation whose terminal was withheld.");
        owner.SubmittedAccessibility(owner.BeginAccessibility(), 4095);
        owner.RetireApplication(owner.Generation, 4095);
        Check(owner.AcceptAccessibilityCompletion(4095), "Measured app placement consumed a reused accessibility ID.");
        var obsolete = owner.Generation;
        owner.BeginPhysical();
        Check(!owner.IsCurrent(obsolete) && owner.IsPhysical && owner.PendingOperations == 0,
            "An unacknowledged application move blocked a new physical gesture.");
        owner.EndPhysical();
        owner.SubmittedAccessibility(owner.BeginAccessibility(), 4100);
        owner.Cancel();
        owner.SubmittedAccessibility(obsolete, 4101);
        Check(!owner.AcceptAccessibilityCompletion(4100) && !owner.AcceptAccessibilityCompletion(4101),
            "Canceled work must not rearm an operation slot.");
        owner.Retire();
        owner.SubmittedApplication(owner.Cancel(), 1);
        Check(owner.PendingOperations == 0 && !owner.IsCurrent(owner.Generation), "A retired owner accepted a new operation.");
        return Task.CompletedTask;
    }

    internal static Task WindowsReconstructionMirrorsCurrentProjection()
    {
        var source = ReadingCards(Guid.NewGuid(), 500);
        var bookmark = source[390];
        var rows = new LiveReadingSource(source, 389);
        Check(ReferenceEquals(rows[1], bookmark), "The initial window must contain the saved canonical card.");
        var changes = (System.Collections.Specialized.INotifyCollectionChanged)source;
        changes.CollectionChanged += (_, change) => rows.Apply(change);
        for (var i = 0; i < 8; i++)
        {
            source.Insert(0, new CallItem { ServerId = bookmark.ServerId });
            source.RemoveAt(source.Count - 1);
        }
        Check(rows.Count == 111, "Before placement the reconstruction source must remain stable.");
        rows.Complete();
        Check(rows.SequenceEqual(source) && ReferenceEquals(rows[398], bookmark),
            "Publishing the projection must include every admission made during reconstruction.");
        source.Move(4, 9);
        source[12] = new CallItem { ServerId = bookmark.ServerId };
        source.Remove(bookmark);
        Check(rows.SequenceEqual(source), "Reorders, replacement and eviction must reach the completed projection.");
        source.Clear();
        source.Add(bookmark);
        Check(rows.SequenceEqual(source), "Canonical reset followed by admission must reach the replacement.");
        rows.Retire();
        source.Clear();
        rows.Complete();
        Check(rows.Count == 1 && ReferenceEquals(rows[0], bookmark), "A retired, detached source should remain inert.");
        var interrupted = new LiveReadingSource(source, 0);
        changes.CollectionChanged += (_, change) => interrupted.Apply(change);
        source.Add(bookmark);
        source.Clear();
        var replacement = new CallItem { ServerId = bookmark.ServerId };
        source.Add(replacement);
        interrupted.Complete();
        Check(interrupted.Count == 1 && ReferenceEquals(interrupted[0], replacement),
            "A whole-source reset before first publication must discard all obsolete identities.");
        return Task.CompletedTask;
    }

    internal static Task WindowsHiddenBookmarksSurviveEviction()
    {
        var server = Guid.NewGuid();
        var source = ReadingCards(server, 40);
        var display = new LiveCallDisplay(source, favorite: _ => true);
        display.SetServers([server]);
        display.Observe();
        var readers = new[] { display.CombinedReader, display.FavoritesReader, display.ReaderForServer(server) };
        var saved = source[20];
        var neighbor = source[21];
        foreach (var reader in readers)
        {
            reader.SetVisible(true);
            reader.ObserveViewport(20, -9, true);
            reader.SetVisible(false);
        }
        for (var i = 0; i < 12; i++)
            Admit(display, new() { ServerId = server });
        source.Remove(saved);
        foreach (var reader in readers)
            Check(ReferenceEquals(reader.ResolveAnchor()?.Card, neighbor) && reader.UnseenCount == 12,
                "Eviction while hidden must choose the surviving neighbor after all intervening prepends.");
        display.Detach();
        return Task.CompletedTask;
    }

    internal static Task WindowsSeedCancellationIncludesInFlightEdit()
    {
        Action<ObservableCollection<CallItem>>[] edits =
        [
            cards => cards.Insert(0, new CallItem()),
            cards => cards.RemoveAt(cards.Count - 1),
            cards => cards.Move(3, 0),
            cards => cards[3] = new CallItem(),
            cards => cards.Clear()
        ];
        foreach (var edit in edits)
        {
            var cards = ReadingCards(Guid.NewGuid(), 6);
            var mirror = new LiveReadingSource(cards, 0);
            mirror.Complete();
            mirror.SeedAt(3);
            // A viewport can subscribe before its handler. Canceling a partial restore
            // from its callback publishes the canonical edit before the mirror receives it.
            cards.CollectionChanged += (_, _) => mirror.Complete();
            cards.CollectionChanged += (_, change) => mirror.Apply(change);
            edit(cards);
            Check(mirror.IsComplete && mirror.SequenceEqual(cards),
                "A canonical change incorporated during publication was applied a second time.");
        }
        return Task.CompletedTask;
    }

    internal static Task WindowsDetachedReadersTrackBookmarkWhileVisible()
    {
        var north = Guid.NewGuid();
        var source = ReadingCards(north, 80);
        var excluded = new HashSet<CallItem>();
        var display = new LiveCallDisplay(source, favorite: call => !excluded.Contains(call));
        display.SetServers([north]);
        display.Observe();
        try
        {
            var recorder = display.ReaderForServer(north);
            var readers = new[] { display.CombinedReader, recorder, display.FavoritesReader };
            var saved = source[40];
            var firstSurvivor = source[41];
            var secondSurvivor = source[42];
            foreach (var reader in readers)
            {
                // Native unload does not toggle logical visibility. The catalog is still live.
                reader.SetVisible(true);
                reader.ObserveViewport(40, -9, true);
            }
            for (var i = 0; i < 19; i++)
                Admit(display, new() { ServerId = north });

            // Do not resolve the bookmarks here. Looking them up would itself repair a stale index.
            excluded.Add(saved);
            display.RefreshFavorites();
            source.Remove(saved);
            source.Remove(firstSurvivor);
            foreach (var reader in readers)
            {
                var survivor = reader.ResolveAnchor();
                Check(survivor is { Index: 59 } && ReferenceEquals(survivor.Value.Card, secondSurvivor) &&
                    reader.UnseenCount == 19 && !reader.IsFollowing && reader.IsVisible,
                    "Detached visible readers must retain the adjacent survivor and all eligible admissions.");
            }
        }
        finally { display.Detach(); }
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
