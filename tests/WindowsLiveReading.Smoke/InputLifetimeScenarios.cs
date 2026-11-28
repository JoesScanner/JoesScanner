using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using JoesScanner.Models;
using JoesScanner.ViewModels;
using JoesScanner.Views;
using JoesScanner.Views.Controls;
using Microsoft.UI.Xaml.Automation;
using Native = Microsoft.UI.Xaml.Controls;

namespace JoesScanner.WinUI;

internal sealed partial class LiveReadingSmokeApplication
{
    private async Task ExerciseInputLifetimeAsync(Window window)
    {
        var calls = new ObservableCollection<CallItem>();
        var display = new LiveCallDisplay(calls, favorite: card => card.ServerId == _north);
        display.SetServers(new[] { _north, _south });
        display.Observe();
        var readers = new[] { display.CombinedReader, display.FavoritesReader };
        var grid = new Grid();
        var page = new MainPage(_ => { }) { Content = grid };
        var lists = readers.Select(reader => new LiveCollectionView
        {
            ItemTemplate = page.LiveCardTemplate,
            ReadingSource = reader.Cards,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
            SelectionMode = SelectionMode.None,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never
        }).ToArray();
        var owners = lists.Select((list, i) => new LiveReadingViewport(list, new Button(), readers[i])).ToArray();
        var serial = 0;
        for (var i = 0; i < LiveCallDisplay.CardLimit; i++)
            Admit();
        foreach (var list in lists)
            grid.Add(list);
        window.Page = page;
        try
        {
            for (var turn = 0; turn <= 180; turn++)
            {
                var index = (turn + 1) % 2;
                owners[1 - index].Show(false);
                lists[1 - index].IsVisible = false;
                lists[index].IsVisible = true;
                owners[index].Show(true);
                await WaitForAsync(() => owners[index].SmokeReaderReady, "retained projection to attach");
                await Task.Yield();
            }
            var target = (Native.ItemsView)lists[1].Handler!.PlatformView!;
            Check(target.Focus(Microsoft.UI.Xaml.FocusState.Programmatic), "Could not focus the retained Favorites list.");
            var before = owners.Select(owner => owner.SmokePreviewKeys).ToArray();
            PostTestKey(window);
            await WaitForAsync(() => owners[1].SmokePreviewKeys > before[1], "targeted F24 to reach the active projection");
            Check(owners[1].SmokePreviewKeys == before[1] + 1 && owners[0].SmokePreviewKeys == before[0],
                $"One key delivered {owners[1].SmokePreviewKeys - before[1]} active and {owners[0].SmokePreviewKeys - before[0]} retired callbacks.");

            var reader = readers[1];
            var live = lists[1];
            var owner = owners[1];
            owner.Show(false);
            var retired = Enumerable.Range(0, 32).Select(_ => AttachAndRetire(live, reader)).ToArray();
            owner.Show(true);
            await WaitForAsync(() => owner.SmokeReaderReady, "retained presenter to reconnect");
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Check(retired.All(weak => !weak.IsAlive), "The surviving native list retained a retired viewport owner.");
            Console.WriteLine("PASS 180 projection switches, single active key callback and zero inactive callbacks; 32 retired owners collected on a surviving list");

            var viewer = Viewer(live)!;
            var provider = Provider(viewer);
            var distant = reader.Cards[100];
            owner.Reveal(distant);
            Check(!owner.SmokeReaderReady && !provider.HorizontallyScrollable, "The invalid-input test requires a pending reveal on a vertical list.");
            Rejected<InvalidOperationException>(() => provider.Scroll(ScrollAmount.SmallIncrement, ScrollAmount.NoAmount));
            Rejected<InvalidOperationException>(() => provider.SetScrollPercent(25, -1));
            Rejected<ArgumentOutOfRangeException>(() => provider.Scroll(ScrollAmount.NoAmount, (ScrollAmount)int.MaxValue));
            Check(!owner.SmokeReaderReady, "Rejected accessibility requests canceled the pending reveal.");
            await WaitForAsync(() => owner.SmokeReaderReady && ReferenceEquals(reader.ResolveAnchor()?.Card, distant) &&
                CardTop(live, viewer, reader.IndexOf(distant)) is { } y && y >= 0 && y < viewer.ViewportHeight,
                "the original distant reveal to finish after rejected UIA requests");
            Console.WriteLine("PASS rejected horizontal Scroll, SetScrollPercent and invalid ScrollAmount preserved the pending vertical reveal");

            for (var index = 0; index < 2; index++)
            {
                owner.FollowLatest();
                await WaitForAsync(() => reader.IsFollowing && viewer.VerticalOffset < 1 && owner.SmokeReaderReady,
                    "the native leading edge");
                var selected = reader.Cards[index];
                Check(selected.HasDetectedAddress, "The selected Favorite must have an address.");
                owner.Reveal(selected);
                await WaitForAsync(() => owner.SmokeReaderReady && reader.ResolveAnchor()?.Top is not null, "near-Live address reveal");
                var initial = CardTop(live, viewer, index)!.Value;
                var screen = PhysicalY(window, viewer, initial);
                var submissions = owner.SmokeAppSubmissions;
                for (var i = 0; i < 12; i++)
                {
                    var previous = viewer.VerticalOffset;
                    Admit();
                    await WaitForAsync(() => viewer.VerticalOffset > previous + 1 &&
                        CardTop(live, viewer, reader.IndexOf(selected)) is { } y && Math.Abs(y - initial) <= 1.5,
                        "native anchoring to retain the selected Favorite through each arrival");
                }
                var final = CardTop(live, viewer, reader.IndexOf(selected))!.Value;
                Check(ReferenceEquals(reader.ResolveAnchor()?.Card, selected) && !reader.IsFollowing && reader.UnseenCount == 12 &&
                    owner.SmokeAppSubmissions == submissions && Math.Abs(PhysicalY(window, viewer, final) - screen) <= 2,
                    "A near-Live address reveal lost its reading intent, geometry or native compensation ownership.");
                Console.WriteLine($"PASS address Favorite {index}, 12 admissions; relative {initial:F2}/{final:F2} DIPs, screen {screen}/{PhysicalY(window, viewer, final)} px, 12 unread, zero app compensations");
            }
            await ExerciseSupersededLiveCommandAsync(viewer, owner, reader, Admit);
            await ExerciseAcceptedLiveWithNewInputAsync(window, target, viewer, owner, reader, Admit);
            await ExerciseWheelOwnershipAsync(live, viewer, owner, reader, Admit);
            await ExerciseUnownedInertiaAsync(viewer, owner, reader, Admit);
        }
        finally
        {
            foreach (var owner in owners)
                owner.Show(false);
            display.Detach();
        }

        void Admit()
        {
            var id = ++serial;
            var text = string.Join(" ", Enumerable.Repeat($"Dispatch {id}.", id % 7 + 1));
            var card = new CallItem(text) { ServerId = _north, HasDetectedAddress = true };
            calls.Insert(0, card);
            display.RecordVisibleAdmission(card);
            if (calls.Count > LiveCallDisplay.CardLimit)
                calls.RemoveAt(LiveCallDisplay.CardLimit);
        }
    }

    private static async Task ExerciseSupersededLiveCommandAsync(Native.ScrollView viewer,
        LiveReadingViewport owner, LiveReadingState reader, Action admit)
    {
        for (var trial = 0; trial < 3; trial++)
        {
            Provider(viewer).SetScrollPercent(-1, 0);
            await WaitForAsync(() => reader.IsFollowing && viewer.VerticalOffset < 0.1 && owner.SmokePending == 0,
                "the initial user navigation to reach Live");
            var submitted = new List<int>();
            var completed = new HashSet<int>();
            double? offsetAtJump = null;
            viewer.ScrollCompleted += Completed;
            owner.SmokeApplicationSubmitted += Submitted;
            try
            {
                owner.Reveal(reader.Cards[8]);
                var expected = trial == 1 ? 1 : 2;
                await WaitForAsync(() => submitted.Count == expected && submitted.All(completed.Contains) &&
                    owner.SmokePending == 0 && (trial == 0
                        ? reader.IsFollowing && viewer.VerticalOffset < 0.1
                        : !reader.IsFollowing && reader.ResolveAnchor() is { Index: > 20, Top: not null }),
                    "superseded native operations to finish at the last intentional destination");
                Check(offsetAtJump is >= 0 and < 0.1 && submitted.Distinct().Count() == expected,
                    "The old reveal must be in flight at physical zero when the explicit Live command arrives.");
                if (trial != 0)
                {
                    var saved = reader.ResolveAnchor()!.Value;
                    var requests = owner.SmokeAppSubmissions;
                    for (var i = 0; i < 4; i++)
                        admit();
                    Check(reader.UnseenCount == 4 && ReferenceEquals(reader.ResolveAnchor()?.Card, saved.Card) &&
                        owner.SmokeAppSubmissions == requests, "User navigation lost its bookmark after superseding Live.");
                }
            }
            finally
            {
                owner.SmokeApplicationSubmitted -= Submitted;
                viewer.ScrollCompleted -= Completed;
            }

            void Completed(Native.ScrollView sender, Native.ScrollingScrollCompletedEventArgs e) =>
                completed.Add(e.CorrelationId);
            void Submitted(int id)
            {
                if (id < 0)
                    return;
                submitted.Add(id);
                if (submitted.Count == 1)
                {
                    // Exercise the ordering before WinUI has presented the first native movement.
                    offsetAtJump = viewer.VerticalOffset;
                    owner.FollowLatest();
                }
                if ((trial == 1 && submitted.Count == 1) || (trial == 2 && submitted.Count == 2))
                    Provider(viewer).SetScrollPercent(-1, 18);
            }
        }
        Console.WriteLine("PASS Live superseded an accepted reveal at native zero; genuine UIA navigation before or after Live submission kept four unread");
    }

    private static async Task ExerciseUnownedInertiaAsync(Native.ScrollView viewer, LiveReadingViewport owner,
        LiveReadingState reader, Action admit)
    {
        owner.FollowLatest();
        await WaitForAsync(() => reader.IsFollowing && viewer.VerticalOffset < 1 && owner.SmokePending == 0,
            "newest position before the inertia ownership control");
        Provider(viewer).SetScrollPercent(-1, 100 * 40 / viewer.ScrollableHeight);
        await WaitForAsync(() => !reader.IsFollowing && viewer.VerticalOffset > 1 && owner.SmokePending == 0,
            "partially clipped canonical reading position");
        var bookmark = reader.ResolveAnchor()!.Value.Card;
        for (var n = 0; n < 4; n++)
            admit();
        await WaitForAsync(() => reader.UnseenCount == 4 && ReferenceEquals(bookmark, reader.ResolveAnchor()?.Card) &&
            viewer.VerticalOffset > 100, "four arrivals under the native anchor");

        var states = new List<Native.ScrollingInteractionState>();
        var finished = false;
        var id = -1;
        void State(Native.ScrollView sender, object args) => states.Add(sender.State);
        void Completed(Native.ScrollView sender, Native.ScrollingScrollCompletedEventArgs args)
        {
            if (args.CorrelationId == id)
                finished = true;
        }
        viewer.StateChanged += State;
        viewer.ScrollCompleted += Completed;
        try
        {
            // No pointer, key, automation, or scrollbar command occurs here. WinUI can
            // also enter Inertia by itself when layout constrains the interaction tracker.
            id = viewer.AddScrollVelocity(new(0, -7500), new(.9995f, .9995f));
            await WaitForAsync(() => finished && viewer.State == Native.ScrollingInteractionState.Idle &&
                viewer.VerticalOffset <= LiveReadingState.TopTolerance, "unowned native inertia to reach the leading edge");
            Check(states.Contains(Native.ScrollingInteractionState.Inertia) &&
                !states.Contains(Native.ScrollingInteractionState.Interaction), "Control did not isolate unowned inertia.");
            Check(!reader.IsFollowing && reader.UnseenCount == 4 &&
                ReferenceEquals(reader.ResolveAnchor()?.Card, bookmark), $"Native inertia changed intent. {owner.SmokeStatus}");
            // An actual UIA command at exactly that same offset still owns navigation.
            Provider(viewer).SetScrollPercent(-1, 0);
            await WaitForAsync(() => reader.IsFollowing && reader.UnseenCount == 0, "genuine automation at the same leading edge");
            Console.WriteLine($"PASS unowned native inertia {id} reached zero and retained its bookmark/four unread; genuine UIA at zero cleared them");
        }
        finally
        {
            viewer.StateChanged -= State;
            viewer.ScrollCompleted -= Completed;
        }
    }

    private static async Task ExerciseAcceptedLiveWithNewInputAsync(Window window, Native.ItemsView list,
        Native.ScrollView viewer, LiveReadingViewport owner, LiveReadingState reader, Action admit)
    {
        foreach (bool keyboard in new[] { false, true })
        {
            Provider(viewer).SetScrollPercent(-1, 20);
            await WaitForAsync(() => !reader.IsFollowing && viewer.VerticalOffset > viewer.ScrollableHeight * .15 &&
                owner.SmokePending == 0, "reading origin of the nonzero Live request");
            Check(list.Focus(Microsoft.UI.Xaml.FocusState.Programmatic), "Could not focus the production list for navigation.");
            int? jumpId = null;
            bool completed = false;
            double issuedAt = 0;
            void Submitted(int id)
            {
                if (jumpId.HasValue || id < 0)
                    return;
                jumpId = id;
                issuedAt = viewer.VerticalOffset;
                if (keyboard)
                    PostTestKey(window, 0x23); // End
                else
                    Provider(viewer).SetScrollPercent(-1, 80);
            }
            void Completed(Native.ScrollView sender, Native.ScrollingScrollCompletedEventArgs e)
            {
                if (e.CorrelationId == jumpId)
                    completed = true;
            }
            viewer.ScrollCompleted += Completed;
            owner.SmokeApplicationSubmitted += Submitted;
            try
            {
                owner.FollowLatest();
                await WaitForAsync(() => completed && owner.SmokePending == 0 && !reader.IsFollowing &&
                    reader.ResolveAnchor() is { Top: not null } && viewer.VerticalOffset >
                    viewer.ScrollableHeight * (keyboard ? .95 : .7), "user navigation after an accepted nonzero Live request");
                Check(issuedAt > 100, "Live must have accepted a real native movement before the input.");
                var anchor = reader.ResolveAnchor()!.Value.Card;
                for (var i = 0; i < 4; i++)
                    admit();
                // End can select one of the four trailing cards evicted by these admissions.
                // The exact adjacent survivor is then the projection's new trailing card.
                var survivor = reader.IndexOf(anchor) >= 0 ? anchor : reader.Cards[^1];
                Check(!reader.IsFollowing && reader.UnseenCount == 4 && ReferenceEquals(reader.ResolveAnchor()?.Card, survivor),
                    $"After four admissions, expected bookmark {reader.IndexOf(survivor)}. Actual {owner.SmokeStatus}.");
                Console.WriteLine($"PASS {(keyboard ? "End key" : "UIA")} superseded accepted Live id={jumpId} from {issuedAt:F2} DIPs, four unread");
            }
            finally
            {
                viewer.ScrollCompleted -= Completed;
                owner.SmokeApplicationSubmitted -= Submitted;
            }
        }
    }

    private static async Task ExerciseWheelOwnershipAsync(LiveCollectionView list, Native.ScrollView viewer,
        LiveReadingViewport owner, LiveReadingState reader, Action admit)
    {
        foreach (var step in new[] { "earlier reveal", "accepted Live", "dispatching Live", "moving Live", "superseded held row", "held missing terminal" })
        {
            var heldRow = step is "superseded held row" or "held missing terminal";
            var withheld = new List<int>();
            Action? resume = null;
            int? withheldBring = null;
            var replaced = false;
            var extended = false;
            var fromZero = step == "earlier reveal";
            // A deliberately elongated animation must exercise a real interruption within
            // measured cards. WinUI's stock distant animation can cycle layout on this
            // variable-height template before any input arrives. Other phases start at 20%.
            var moving = heldRow || step == "moving Live";
            var originPercent = moving ? 80 * viewer.ViewportHeight / viewer.ScrollableHeight : 20;
            Provider(viewer).SetScrollPercent(-1, fromZero ? 0 : originPercent);
            await WaitForAsync(() => owner.SmokePending == 0 && (fromZero ? reader.IsFollowing :
                !reader.IsFollowing && viewer.State == Native.ScrollingInteractionState.Idle &&
                viewer.VerticalOffset > (moving ? viewer.ViewportHeight * .6 : viewer.ScrollableHeight * .16) &&
                viewer.VerticalOffset < (moving ? viewer.ViewportHeight : viewer.ScrollableHeight * .24) &&
                reader.ResolveAnchor() is { Top: not null } origin && CardTop(list, viewer, origin.Index) is { } measured &&
                Math.Abs(measured - origin.Top.Value) <= 1.5), "wheel starting bookmark");
            var saved = reader.ResolveAnchor()?.Card ?? reader.Cards[0];
            var cut = CardTop(list, viewer, reader.IndexOf(saved))!.Value;
            var submitted = false;
            var initialOffset = viewer.VerticalOffset;
            CallItem? bringTarget = null;
            void TurnWheel()
            {
                submitted = true;
                owner.SmokeWheel(-120);
                for (var i = 0; i < 3; i++)
                    admit();
                Check(!reader.IsFollowing && reader.UnseenCount == 3, "An admission before ViewChanged was lost.");
            }
            void Accepted(int id)
            {
                if (submitted || id < 0)
                    return;
                if (fromZero)
                    owner.FollowLatest();
                if (fromZero || step == "accepted Live")
                    TurnWheel();
            }
            void Dispatching(Native.ScrollView sender, Native.ScrollingScrollAnimationStartingEventArgs args)
            {
                if (step == "dispatching Live" && !submitted)
                    TurnWheel();
            }
            void ExtendMotion(Native.Primitives.ScrollPresenter sender, Native.ScrollingScrollAnimationStartingEventArgs args)
            {
                if (!extended && (step == "moving Live" || heldRow) && args.Animation is Microsoft.UI.Composition.Vector3KeyFrameAnimation animation)
                {
                    extended = true;
                    animation.Duration = TimeSpan.FromSeconds(1.8);
                }
            }
            void Moving(Native.ScrollView sender, object args)
            {
                if (!moving || submitted || sender.VerticalOffset >= initialOffset - 100 || sender.VerticalOffset <= 20)
                    return;
                for (var index = 0; index < reader.Cards.Count; index++)
                {
                    if (CardTop(list, viewer, index) is not { } y || y <= -65 || y > 0)
                        continue;
                    var nextRow = FindCard(list, index + 4);
                    if (heldRow && nextRow == null)
                        continue;
                    saved = reader.Cards[index];
                    cut = y;
                    if (heldRow)
                    {
                        bringTarget = reader.Cards[index + 4];
                        submitted = true;
                        // Emulate an automation client on its own dispatcher turn while
                        // the controlled native animation is moving, then overlap admissions.
                        sender.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
                        {
                            TurnWheel();
                            nextRow!.StartBringIntoView(new Microsoft.UI.Xaml.BringIntoViewOptions
                            {
                                AnimationDesired = false,
                                VerticalAlignmentRatio = 0
                            });
                        });
                    }
                    else
                        TurnWheel();
                    return;
                }
            }
            void Supersede(Native.ScrollView sender, Native.ScrollingBringingIntoViewEventArgs args)
            {
                if (step == "held missing terminal" && bringTarget != null && resume == null)
                {
                    Check(list.ReadingRows?.IsComplete == false, "The native Bring must operate on the held mirror.");
                    withheldBring = args.CorrelationId;
                    resume = owner.SuppressSmokeTerminals(withheld.Add);
                }
                if (step == "superseded held row" && !replaced && bringTarget != null && !args.Cancel)
                {
                    // The preceding handler has just accepted the canonical target on the held mirror.
                    replaced = true;
                    Provider(viewer).SetScrollPercent(-1, 70);
                }
            }
            owner.SmokeApplicationSubmitted += Accepted;
            viewer.BringingIntoView += Supersede;
            viewer.ScrollAnimationStarting += Dispatching;
            viewer.ScrollPresenter.ScrollAnimationStarting += ExtendMotion;
            viewer.ViewChanged += Moving;
            try
            {
                if (fromZero)
                    owner.Reveal(reader.Cards[8]);
                else
                    owner.FollowLatest();
                await WaitForAsync(() => submitted && owner.SmokePending == 0 && !owner.SmokeHasWheelAnchor &&
                    !reader.IsFollowing && reader.UnseenCount == 3 &&
                    (step == "superseded held row"
                        ? replaced && viewer.VerticalOffset > viewer.ScrollableHeight * .65
                        : bringTarget is { } target ? CardTop(list, viewer, reader.IndexOf(target)) is { } aligned && Math.Abs(aligned) < 1.5
                        : CardTop(list, viewer, reader.IndexOf(saved)) is { } after && Math.Abs(after - cut + 48) < 1.5),
                    $"the complete wheel displacement during {step}, with three pre-presentation admissions");
                if (step == "held missing terminal")
                {
                    await WaitForAsync(() => withheldBring.HasValue && withheld.Contains(withheldBring.Value),
                        "the withheld viewport terminal for the real correlated Bring");
                    Console.WriteLine($"PASS original held Bring {withheldBring} placed the canonical row at {CardTop(list, viewer, reader.IndexOf(bringTarget!)):F2} DIPs with the viewport terminal withheld, three unread");
                }
                else if (step == "superseded held row")
                    Console.WriteLine($"PASS newer UIA superseded held row navigation at {viewer.VerticalOffset:F2} DIPs, three unread");
                else
                    Console.WriteLine($"PASS wheel during {step}, cut {cut:F2}/{CardTop(list, viewer, reader.IndexOf(saved)):F2}, three unread");
            }
            finally
            {
                resume?.Invoke();
                foreach (var id in withheld)
                    owner.SmokeComplete(id);
                owner.SmokeApplicationSubmitted -= Accepted;
                viewer.BringingIntoView -= Supersede;
                viewer.ScrollAnimationStarting -= Dispatching;
                viewer.ScrollPresenter.ScrollAnimationStarting -= ExtendMotion;
                viewer.ViewChanged -= Moving;
            }
        }

        var first = reader.ResolveAnchor()!.Value;
        var initial = CardTop(list, viewer, reader.IndexOf(first.Card))!.Value;
        var held = new List<int>();
        Action? release = owner.SuppressSmokeTerminals(held.Add);
        try
        {
            owner.SmokeWheel(-60);
            owner.SmokeWheel(30);
            await WaitForAsync(() => held.Count > 0 && CardTop(list, viewer, reader.IndexOf(first.Card))
                is { } after && Math.Abs(after - initial + 12) < 1.5, "opposite fine-resolution wheel deltas");
            Check(owner.SmokePending <= 1 && owner.SmokeHasWheelAnchor, "Missing terminals retained a wheel queue.");
            owner.SmokeWheel(-120);
            // A previous impulse can report its terminal between the new routed input and dispatch.
            owner.SmokeComplete(held[0]);
            viewer.UpdateLayout();
            await WaitForAsync(() => held.Count >= 2 && CardTop(list, viewer, reader.IndexOf(first.Card))
                is { } continued && Math.Abs(continued - initial + 60) < 1.5,
                "newer wheel input after the preceding terminal");
            Provider(viewer).SetScrollPercent(-1, 77);
            await WaitForAsync(() => held.Count >= 3, "subsequent genuine provider request");
            release();
            release = null;
            foreach (var id in held)
                owner.SmokeComplete(id);
            await WaitForAsync(() => !owner.SmokeHasWheelAnchor && owner.SmokePending == 0 &&
                viewer.VerticalOffset > .7 * viewer.ScrollableHeight && !reader.IsFollowing, "newer navigation ownership");
            Check(reader.UnseenCount == 3, "Obsolete wheel terminals cleared unread calls.");
        }
        finally
        {
            release?.Invoke();
        }

        // Actual WinUI requests can await dispatch while only one reader intent remains active.
        owner.CancelPendingScroll();
        for (var n = 0; n < WindowsLiveAnimationQueue.Capacity; n++)
            Check(owner.SmokeAppRequest(n % 2 == 0 ? 100 : 200) >= 0, "Native dispatch capacity was not available.");
        Check(!owner.SmokeAcceptsMoreAnimations && owner.SmokeAppRequest(300) < 0 && owner.SmokePending == 1,
            "Native queued application IDs exceeded their bound.");
        Provider(viewer).SetScrollPercent(-1, 77);
        await WaitForAsync(() => owner.SmokeAcceptsMoreAnimations && owner.SmokePending == 0 &&
            !reader.IsFollowing && viewer.VerticalOffset > viewer.ScrollableHeight * .7, "user navigation under backpressure");

        var origin = viewer.VerticalOffset;
        var minimum = origin;
        int? old = null;
        var completed = false;
        void Retire(int id)
        {
            if (old.HasValue || id < 0)
                return;
            old = id;
            owner.Show(false);
            owner.Show(true);
            Provider(viewer).SetScrollPercent(-1, 85);
        }
        void Arrived(Native.ScrollView sender, Native.ScrollingScrollCompletedEventArgs args)
        {
            if (args.CorrelationId == old)
                completed = true;
        }
        void ObserveFrame(object? sender, object args) => minimum = Math.Min(minimum, viewer.VerticalOffset);
        owner.SmokeApplicationSubmitted += Retire;
        viewer.ScrollCompleted += Arrived;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += ObserveFrame;
        try
        {
            owner.FollowLatest();
            await WaitForAsync(() => completed && !reader.IsFollowing && viewer.VerticalOffset > viewer.ScrollableHeight * .8 &&
                owner.SmokePending == 0, "retired application work to yield to the reconnected presenter");
            Check(minimum >= origin - 2, $"The retired native operation moved the reattached presenter to {minimum:F2}.");
            Console.WriteLine($"PASS retired accepted Live {old}, minimum {minimum:F2}, final {viewer.VerticalOffset:F2}");
        }
        finally
        {
            owner.SmokeApplicationSubmitted -= Retire;
            viewer.ScrollCompleted -= Arrived;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= ObserveFrame;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndRetire(LiveCollectionView list, LiveReadingState reader)
    {
        var owner = new LiveReadingViewport(list, new Button(), reader);
        owner.Show(true);
        owner.Show(false);
        return new WeakReference(owner);
    }

    private static void Rejected<TException>(Action request) where TException : Exception
    {
        try { request(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected the Scroll provider to reject the request with {typeof(TException).Name}.");
    }

    private delegate bool EnumerateChild(IntPtr handle, IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumerateChild callback, IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr flags);

    private static void PostTestKey(Window window, int key = 0x87)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.Handler!.PlatformView);
        var input = IntPtr.Zero;
        EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var pid);
            var type = new StringBuilder(128);
            GetClassName(child, type, type.Capacity);
            if (pid != Environment.ProcessId || type.ToString() != "InputSiteWindowClass")
                return true;
            input = child;
            return false;
        }, IntPtr.Zero);
        // Route the key only to this test window's input site, including on a noninteractive desktop.
        Check(input != IntPtr.Zero && PostMessage(input, 0x0100, (IntPtr)key, (IntPtr)1) &&
            PostMessage(input, 0x0101, (IntPtr)key, new IntPtr(unchecked((int)0xC0000001))), "Could not send a key to the test window.");
    }
}

