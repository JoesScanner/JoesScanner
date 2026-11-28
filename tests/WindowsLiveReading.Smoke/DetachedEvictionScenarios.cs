using System.Collections.ObjectModel;
using JoesScanner.Models;
using JoesScanner.ViewModels;
using JoesScanner.Views;
using JoesScanner.Views.Controls;

namespace JoesScanner.WinUI;

internal sealed partial class LiveReadingSmokeApplication
{
    private enum TailNavigation { None, Current, Reattached }

    private async Task ExerciseTailEvictionAsync(Window window, TailNavigation navigation = TailNavigation.None)
    {
        var calls = new ObservableCollection<CallItem>();
        var display = new LiveCallDisplay(calls, favorite: card => card.ServerId == _north);
        display.SetServers(new[] { _north, _south });
        display.Observe();
        var reader = display.ReaderForServer(_north);
        var serial = 0;
        for (var i = 0; i < LiveCallDisplay.CardLimit; i++)
            calls.Add(Recording(i % 7 != 0));
        var header = new LiveRecorderHeader(_north, new Label { Text = "North" }, new Label { Text = "Online" });
        var list = new LiveCollectionView
        {
            ReadingSource = reader.Cards, ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems, SelectionMode = SelectionMode.None,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never, VerticalScrollBarVisibility = ScrollBarVisibility.Never
        };
        var owner = new LiveReadingViewport(list, header.NewCalls, reader);
        var page = new MainPage(owner.CardSizeChanged);
        list.ItemTemplate = page.LiveCardTemplate;
        var panel = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)] };
        panel.Add(header);
        panel.Add(new LivePlaybackPinView(), 0, 1);
        panel.Add(list, 0, 2);
        var grid = new Grid();
        grid.Add(panel);
        page.Content = grid;
        window.Page = page;
        owner.Show(true);
        try
        {
            await WaitForAsync(() => owner.SmokeReaderReady && Viewer(list)?.ScrollableHeight > 1000, "initial recorder list");
            if (navigation != TailNavigation.None)
            {
                await ExerciseTailSeedNavigationAsync();
                return;
            }
            Provider(Viewer(list)!).SetScrollPercent(-1, 96);
            await WaitForAsync(() => reader.ResolveAnchor() is { Index: > 350, Top: not null } && owner.SmokePending == 0,
                "initial clipped reader near the end");
            owner.Show(false);
            grid.Remove(panel);
            await WaitForAsync(() => !list.IsLoaded, "recorder pane to unload");
            for (var i = 0; i < 19; i++)
                Admit();
            grid.Add(panel);
            owner.Show(true);
            for (var batch = 0; batch < 104; batch++)
            {
                for (var i = 0; i < 6; i++)
                    Admit();
                await Task.Delay(12);
            }
            var viewer = Viewer(list)!;
            var end = reader.Cards[^1];
            bool AtEnd()
            {
                var index = reader.Cards.Count - 1;
                return owner.SmokeReaderReady && owner.SmokePending == 0 && !reader.IsFollowing &&
                    reader.ResolveAnchor() is { Top: { } cut } saved && ReferenceEquals(saved.Card, end) &&
                    page.LoadedTranscripts.Any(label => ReferenceEquals(label.BindingContext, end)) &&
                    FindCard(list, index) is { } row && CardTop(list, viewer, index) is { } top &&
                    top < viewer.ViewportHeight && top + row.ActualHeight > 0 && Math.Abs(top - cut) <= 1.5;
            }
            await WaitForAsync(AtEnd, "the final surviving recorder card to be realized at its saved cut");
            await Task.Delay(2500);
            Check(AtEnd() && reader.UnseenCount == LiveCallDisplay.CardLimit &&
                list.ItemsSource.Cast<CallItem>().SequenceEqual(reader.Cards),
                $"Tail recovery moved after the source settled: {owner.SmokeStatus}");
            Console.WriteLine($"PASS 624 overlapping arrivals, realized final recorder row {reader.Cards.Count - 1}, " +
                $"relative {reader.ResolveAnchor()!.Value.Top:F2} DIPs, screen {PhysicalY(window, viewer, reader.ResolveAnchor()!.Value.Top!.Value)} px, " +
                $"{reader.UnseenCount} unread, zero pending operations");

            Provider(viewer).SetScrollPercent(-1, 10);
            await WaitForAsync(() => reader.ResolveAnchor() is { Index: > 0 and < 100, Top: not null } && owner.SmokePending == 0,
                "intentional UI Automation navigation away from the eviction survivor");
            var intentional = reader.ResolveAnchor()!.Value;
            var requests = owner.SmokeAppSubmissions;
            for (var i = 0; i < 24; i++)
            {
                Admit();
                await Task.Delay(16);
            }
            await WaitForAsync(() => ReferenceEquals(reader.ResolveAnchor()?.Card, intentional.Card) &&
                CardTop(list, viewer, reader.IndexOf(intentional.Card)) is { } top &&
                Math.Abs(top - intentional.Top!.Value) <= 1.5, "native retention of the superseding user bookmark");
            Check(owner.SmokeAppSubmissions == requests, "Further arrivals invoked application compensation for a surviving card.");
            Console.WriteLine("PASS genuine UIA navigation superseded tail recovery; 24 further arrivals retained the selected card without application scrolls");
        }
        finally
        {
            owner.Show(false);
            display.Detach();
        }

        async Task ExerciseTailSeedNavigationAsync()
        {
            var viewer = Viewer(list)!;
            Provider(viewer).SetScrollPercent(-1, 16);
            await WaitForAsync(() => !reader.IsFollowing && viewer.VerticalOffset > 1000 && owner.SmokePending == 0,
                "the nonzero starting position for the requested trailing card");
            var original = reader.Cards[^1];
            var rows = list.ReadingRows!;
            var deliver = owner.CaptureSmokeTerminalReceiver();
            var accepted = -1;
            Task? navigationTask = null;
            var seeded = false;
            rows.CollectionChanged += Seeded;
            owner.SmokeApplicationSubmitted += Submitted;
            try
            {
                owner.Reveal(original);
                await WaitForAsync(() => navigationTask != null, "eviction on the actual unpublished suffix");
                await navigationTask!;
            }
            finally
            {
                rows.CollectionChanged -= Seeded;
                owner.SmokeApplicationSubmitted -= Submitted;
            }

            void Submitted(int id) => accepted = id;

            void Seeded(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs change)
            {
                if (seeded || rows.IsComplete || change.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                    return;
                seeded = true;
                // Admit on the same publication turn as the real suffix. Its evicted target
                // must be replaced without mutating or releasing the published mirror here.
                for (var i = 0; i < 3; i++)
                    Admit();
                Check(reader.IndexOf(original) < 0 && owner.SmokeHasEvictedTailWindow && reader.UnseenCount == 3,
                    "The evicted seed lost its canonical admissions.");
                Check(viewer.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                    () => navigationTask = InterruptAsync(accepted)), "Could not queue genuine UIA on the held suffix.");
            }

            async Task InterruptAsync(int oldId)
            {
                Check(owner.SmokeHasEvictedTailWindow && oldId >= 0, "The actual suffix and its previous receipt are required.");
                var before = reader.UnseenCount;
                if (navigation == TailNavigation.Reattached)
                {
                    owner.Show(false);
                    grid.Remove(panel);
                    await WaitForAsync(() => !list.IsLoaded, "the interrupted pane to unload");
                    Admit();
                    before++;
                    grid.Add(panel);
                    owner.Show(true);
                }
                await WaitForAsync(() => Viewer(list) is { ScrollPresenter.IsLoaded: true } attached &&
                    owner.SmokeOwnsViewer(attached), "the viewport to own its current native presenter");
                var current = Viewer(list)!;
                Provider(current).SetScrollPercent(-1, 16);
                await WaitForAsync(() => list.ReadingRows?.IsComplete == true && owner.SmokePending == 0 &&
                    reader.ResolveAnchor() is { Index: > 10 and < 140, Top: not null } &&
                    current.VerticalOffset > current.ScrollableHeight * .12 && current.VerticalOffset < current.ScrollableHeight * .2,
                    "the new user destination on the complete projection");
                var bookmark = reader.ResolveAnchor()!.Value;
                deliver(oldId);
                for (var i = 0; i < 4; i++)
                    Admit();
                await WaitForAsync(() => reader.UnseenCount == before + 4 && !reader.IsFollowing &&
                    ReferenceEquals(reader.ResolveAnchor()?.Card, bookmark.Card) && list.ItemsSource.Cast<CallItem>().SequenceEqual(reader.Cards) &&
                    CardTop(list, current, reader.IndexOf(bookmark.Card)) is { } top && Math.Abs(top - bookmark.Top!.Value) <= 1.5,
                    "the superseding bookmark, its offset and all canonical arrivals");
                Console.WriteLine($"PASS {navigation} tail navigation superseded receipt {oldId}, retained the measured card at " +
                    $"{bookmark.Top:F2} DIPs with {reader.UnseenCount} unread and released the complete source");
            }
        }

        CallItem Recording(bool eligible)
        {
            var id = ++serial;
            return new CallItem($"Call {id} " + string.Join(" ", Enumerable.Repeat("Dispatch at Main.", id % 5 == 0 ? 18 : id % 6 + 1)))
                { ServerId = eligible ? _north : _south };
        }
        void Admit()
        {
            var card = Recording(true);
            LiveCallDisplay.InsertCard(calls, card, 0, null);
            display.RecordVisibleAdmission(card);
        }
    }

    private async Task ExerciseDetachedEvictionAsync(Window window, bool favorites)
    {
        var calls = new ObservableCollection<CallItem>(Enumerable.Range(0, 160)
            .Select(i => new CallItem($"Recorder call {i}") { ServerId = _north }));
        var excluded = new HashSet<CallItem>();
        var display = new LiveCallDisplay(calls, favorite: call => !excluded.Contains(call));
        display.SetServers(new[] { _north });
        display.Observe();
        var reader = favorites ? display.FavoritesReader : display.ReaderForServer(_north);
        var list = new LiveCollectionView
        {
            ReadingSource = reader.Cards,
            SelectionMode = SelectionMode.None,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never
        };
        var header = new LiveRecorderHeader(_north, new Label { Text = "North" }, new Label { Text = "Online" });
        var panel = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)] };
        panel.Add(header);
        panel.Add(new LivePlaybackPinView(), 0, 1);
        panel.Add(list, 0, 2);
        var viewport = new LiveReadingViewport(list, header.NewCalls, reader);
        var page = new MainPage(viewport.CardSizeChanged);
        list.ItemTemplate = page.LiveCardTemplate;
        var pageGrid = new Grid();
        pageGrid.Add(panel);
        page.Content = pageGrid;
        window.Page = page;
        viewport.Show(true);
        try
        {
            await WaitForAsync(() => viewport.SmokeReaderReady && Viewer(list)?.ScrollableHeight > 1000,
                "the recorder collection to become scrollable");
            Provider(Viewer(list)!).SetScrollPercent(-1, 50);
            await WaitForAsync(() => reader.ResolveAnchor() is { Index: > 5, Top: not null } && viewport.SmokePending == 0,
                "the initial reading bookmark");
            var bookmark = reader.ResolveAnchor()!.Value;
            var survivor = reader.Cards[bookmark.Index + 1];
            pageGrid.Remove(panel);
            await WaitForAsync(() => !list.IsLoaded && !viewport.SmokeReaderReady, "the native viewport to unload");
            for (var i = 0; i < 19; i++)
            {
                var card = new CallItem($"Detached admission {i}") { ServerId = _north };
                calls.Insert(0, card);
                display.RecordVisibleAdmission(card);
            }
            if (favorites)
            {
                excluded.Add(bookmark.Card);
                display.RefreshFavorites();
            }
            else
                calls.Remove(bookmark.Card);
            pageGrid.Add(panel);
            await WaitForAsync(() => viewport.SmokeReaderReady && reader.ResolveAnchor() is { } anchor &&
                ReferenceEquals(anchor.Card, survivor) && anchor.Index == bookmark.Index + 19 &&
                Viewer(list) is { } viewer && FindCard(list, anchor.Index) is { } row &&
                CardTop(list, viewer, anchor.Index) is { } cut && cut < viewer.ViewportHeight && cut + row.ActualHeight > 0,
                "the adjacent surviving card to be visible after reattachment");
            Check(reader.IsVisible && !reader.IsFollowing && reader.UnseenCount == 19 && header.NewCalls.IsVisible,
                "Detached eviction changed the reader's intent or unread count.");
            Console.WriteLine($"PASS {(favorites ? "Favorites" : "Separate")} native unload, 19 prepends and eviction " +
                $"restored {survivor.Transcription} at index {reader.IndexOf(survivor)} with New Calls (19).");
        }
        finally
        {
            viewport.Show(false);
            display.Detach();
        }
    }
}
