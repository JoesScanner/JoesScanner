using System.Diagnostics;
using System.Globalization;
using System.Text;
using JoesScanner.ViewModels;
using JoesScanner.Views;
using JoesScanner.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Native = Microsoft.UI.Xaml.Controls;
using Window = Microsoft.Maui.Controls.Window;
using Grid = Microsoft.Maui.Controls.Grid;

namespace JoesScanner.WinUI;

internal sealed partial class LiveReadingSmokeApplication
{
    private static async Task ExerciseCardActionsAsync(Window window, MainPage page, LiveCollectionView list,
        LiveReadingState reader)
    {
        const int index = 1;
        var card = reader.Cards[index];
        var row = FindCard(list, index) ?? throw new InvalidOperationException("The card to click was not realized.");
        var transcript = page.LoadedTranscripts.Single(label => ReferenceEquals(label.BindingContext, card));
        var favorite = FindPart<Native.Button>(row,
            button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "ToggleCallFavorite")
            ?? throw new InvalidOperationException("The realized card was missing its native Favorite button.");
        var before = page.TappedCard.Count;
        await ClickAsync((FrameworkElement)transcript.Handler!.PlatformView!, "transcript");
        await WaitForAsync(() => page.TappedCard.Count == before + 1 && ReferenceEquals(page.TappedCard.Card, card),
            "a real pointer tap to reach the bound production card gesture");
        var previousFavorite = page.FavoriteClick.Count;
        await ClickAsync(favorite, "nested Favorite");
        await WaitForAsync(() => page.FavoriteClick.Count == previousFavorite + 1 && ReferenceEquals(page.FavoriteClick.Card, card),
            "the nested Favorite button to receive its own card");
        Check(page.TappedCard.Count == before + 1, "The nested Favorite button also triggered the card gesture.");
        Console.WriteLine("PASS physical transcript tap and nested Favorite click routed once to their respective card actions");

        async Task ClickAsync(FrameworkElement target, string action)
        {
            var root = target.XamlRoot.Content;
            var inside = target.TransformToVisual(null).TransformPoint(
                new global::Windows.Foundation.Point(target.ActualWidth / 2, target.ActualHeight / 2));
            var scale = target.XamlRoot.RasterizationScale;
            var point = new ScreenPoint { X = (int)Math.Round(inside.X * scale), Y = (int)Math.Round(inside.Y * scale) };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.Handler!.PlatformView);
            Check(ClientToScreen(hwnd, ref point), $"Could not locate the {action}.");
            var description = $"{action} at screen {point.X},{point.Y}";
            Check(IsWithin(VisualTreeHelper.FindElementsInHostCoordinates(inside, root).FirstOrDefault(), target),
                $"The center of the {description} did not hit its bound native element.");
            DependencyObject? pressed = null;
            DependencyObject? released = null;
            Microsoft.UI.Xaml.Input.PointerEventHandler down = (_, args) => pressed = args.OriginalSource as DependencyObject;
            Microsoft.UI.Xaml.Input.PointerEventHandler up = (_, args) => released = args.OriginalSource as DependencyObject;
            root.AddHandler(UIElement.PointerPressedEvent, down, true);
            root.AddHandler(UIElement.PointerReleasedEvent, up, true);
            try
            {
                MoveScreenPointer(point);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                try { await WaitForAsync(() => pressed != null, $"native pointer-down delivery to the {description}"); }
                finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
                await WaitForAsync(() => released != null, $"native pointer-up delivery to the {description}");
                Check(IsWithin(pressed, target) && IsWithin(released, target),
                    $"The {description} received an unexpected pointer route: {pressed?.GetType().Name}/{released?.GetType().Name}.");
            }
            finally
            {
                root.RemoveHandler(UIElement.PointerPressedEvent, down);
                root.RemoveHandler(UIElement.PointerReleasedEvent, up);
            }
        }
    }

    private static async Task ExerciseContinuousArrivalsAsync(LiveCollectionView list, Native.ScrollView viewer,
        LiveCallDisplay display, Guid favoriteRecorder, Action admit)
    {
        var reader = display.FavoritesReader;
        NavigateTo(viewer, 378);
        await WaitForAsync(() => reader.ResolveAnchor()?.Index > 0, "the continuous admission anchor");
        var anchor = reader.ResolveAnchor()!.Value;
        var relative = CardTop(list, viewer, anchor.Index)!.Value;
        var absolute = WindowTop(viewer) + relative;
        var peakRealized = 0;
        var repeater = FindDescendant<Native.ItemsRepeater>(viewer)!;
        var drift = 0d;
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 65; i++)
        {
            admit();
            await Task.Delay(12);
            peakRealized = Math.Max(peakRealized, VisualTreeHelper.GetChildrenCount(repeater));
            if (CardTop(list, viewer, reader.IndexOf(anchor.Card)) is { } top)
                drift = Math.Max(drift, Math.Abs(relative - top));
        }
        var after = CardTop(list, viewer, reader.IndexOf(anchor.Card));
        Console.WriteLine($"Continuous relative={relative:F2}/{after:F2}, window={absolute:F2}/{WindowTop(viewer) + after:F2}, " +
            $"unread={reader.UnseenCount}, eligible/canonical={reader.Cards.Count}/{display.Combined.Count}, peak realized={peakRealized}");
        Check(after.HasValue && Math.Abs(after.Value - relative) <= 1.5 &&
            Math.Abs(WindowTop(viewer) + after.Value - absolute) <= 1.5 &&
            reader.UnseenCount == 65 && ReferenceEquals(anchor.Card, reader.ResolveAnchor()?.Card) &&
            peakRealized is > 0 and < 100 && display.Combined.Count == LiveCallDisplay.CardLimit &&
            reader.Cards.SequenceEqual(display.Combined.Where(card => card.ServerId == favoriteRecorder)),
            "Continuous admissions changed the card, its settled geometry, eligible count, or bounded realization.");
        Console.WriteLine($"PASS 65 continuous arrivals over {timer.ElapsedMilliseconds} ms, {reader.Cards.Count} cards, " +
            $"relative {relative:F2}/{after:F2}, window {absolute:F2}/{WindowTop(viewer) + after:F2}, " +
            $"maximum sampled drift {drift:F2} DIPs, peak realized {peakRealized}");
    }

    private async Task ExerciseRecreationAsync(Window window, Grid grid, LiveCollectionView list, LiveReadingViewport retired,
        LiveCallDisplay display, Action admit)
    {
        var reader = display.FavoritesReader;
        var before = Viewer(list)!;
        Provider(before).SetScrollPercent(-1, 80);
        await WaitForAsync(() => reader.ResolveAnchor() is { Index: > 300 } saved &&
            CardTop(list, before, saved.Index) is { } actual && Math.Abs(actual - saved.Top!.Value) <= 1.5,
            "a measured distant virtualized anchor before retirement");
        var anchor = reader.ResolveAnchor()!.Value;
        var absolute = WindowTop(before) + anchor.Top!.Value;
        var screenY = PhysicalY(window, before, anchor.Top.Value);
        using var old = new LiveReadingViewport.HeldNativeScroll(retired, 0);
        await WaitForAsync(() => old.Arrived, "a terminal from the retiring native host");
        retired.Show(false);
        grid.Remove(list);
        var replacement = new LiveCollectionView
        {
            ReadingSource = reader.Cards, ItemTemplate = list.ItemTemplate,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset, SelectionMode = SelectionMode.None
        };
        var active = new LiveReadingViewport(replacement, new Button(), reader);
        var during = 0;
        var reconstructionTime = Stopwatch.StartNew();
        var withheld = 0;
        Action? restoreEvents = null;
        var withheldFrames = 0;
        CompositionTarget.Rendering += OnSourceStaged;
        active.Show(true);
        grid.Add(replacement, 0, 2);
        try
        {
            await RunCaseAsync("distant handler reconstruction", async () =>
            {
                await WaitForAsync(() => active.SmokeReaderReady && Viewer(replacement) is { } native && !ReferenceEquals(before, native) &&
                    replacement.ItemsSource.Cast<object>().Count() == reader.Cards.Count &&
                    Provider(native).VerticallyScrollable &&
                    CardTop(replacement, native, reader.IndexOf(anchor.Card)) is { } top &&
                    Math.Abs(top - anchor.Top.Value) <= 1.5, "the replacement to reconstruct the complete distant anchor");
                reconstructionTime.Stop();
                await WaitForAsync(() => withheld > 0 && active.SmokePending == 0,
                    "a geometrically completed reconstruction to retire its owned ID when every native terminal is withheld");
                old.Deliver();
                old.Deliver();
                var viewer = Viewer(replacement)!;
                var actual = CardTop(replacement, viewer, reader.IndexOf(anchor.Card))!.Value;
                Check(during == 4 && reader.UnseenCount == 4 && !reader.IsFollowing && ReferenceEquals(reader.ResolveAnchor()?.Card, anchor.Card)
                    && Math.Abs(WindowTop(viewer) + actual - absolute) <= 1.5 &&
                    Math.Abs(PhysicalY(window, viewer, actual) - screenY) <= 2 && old.SubmitObsolete(0) < 0,
                    "A retired host, changed data, or replacement geometry invalidated the saved anchor.");
                Console.WriteLine($"PASS distant handler replacement row {anchor.Index}, relative {anchor.Top:F2}/{actual:F2} DIPs, " +
                    $"screen {screenY}/{PhysicalY(window, viewer, actual)} px, during reconstruction {during} arrivals; " +
                    $"new handler to visible complete scrollable source={reconstructionTime.Elapsed.TotalMilliseconds:F1} ms " +
                    $"(25 ms probe interval), blank composition frames={withheldFrames}, " +
                    $"suppressed terminals={withheld}, retained={active.SmokePending}");
            });
            restoreEvents?.Invoke();
            restoreEvents = null;
            await RunCaseAsync("accessibility preempts reconstruction", async () =>
            {
                // A new presenter must accept intentional navigation even while its distant
                // seed is unpublished. Use its actual provider during that precise phase.
                active.Show(false);
                grid.Remove(replacement);
                CompositionTarget.Rendering -= OnSourceStaged;
                replacement = new LiveCollectionView
                {
                    ReadingSource = reader.Cards, ItemTemplate = list.ItemTemplate,
                    ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
                    ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset, SelectionMode = SelectionMode.None
                };
                active = new LiveReadingViewport(replacement, new Button(), reader);
                var interrupted = false;
                CompositionTarget.Rendering += Preempt;
                try
                {
                    active.Show(true);
                    grid.Add(replacement, 0, 2);
                    await WaitForAsync(() => interrupted && active.SmokeReaderReady && reader.IsFollowing &&
                        reader.UnseenCount == 0 && Viewer(replacement)?.VerticalOffset < 1 &&
                        replacement.ItemsSource.Cast<object>().SequenceEqual(reader.Cards),
                        "a tagged user return during reconstruction to supersede the old cut on the complete source");
                    var native = Viewer(replacement)!;
                    NavigateTo(native, 348);
                    await WaitForAsync(() => !reader.IsFollowing && reader.ResolveAnchor()?.Index > 0, "intentional subsequent navigation");
                    var saved = reader.ResolveAnchor()!.Value;
                    for (var i = 0; i < 4; i++)
                        admit();
                    old.Deliver();
                    await WaitForAsync(() => reader.UnseenCount == 4 && ReferenceEquals(saved.Card, reader.ResolveAnchor()?.Card) &&
                        CardTop(replacement, native, reader.IndexOf(saved.Card)) is { } cut && Math.Abs(cut - saved.Top!.Value) <= 1.5,
                        "the reader's post-interruption card and four unread calls");
                    Console.WriteLine("PASS user UIA scroll preempted an unpublished seed, subsequent navigation retained four unread");
                }
                finally { CompositionTarget.Rendering -= Preempt; }

                void Preempt(object? sender, object args)
                {
                    if (!interrupted && replacement.ItemsSource is LiveReadingSource { IsComplete: false } && Viewer(replacement) is { } native)
                    {
                        interrupted = true;
                        Provider(native).SetScrollPercent(-1, 0);
                    }
                }
            });
            display.Combined.Clear();
            await WaitForAsync(() => !reader.Cards.Any() && reader.UnseenCount == 0, "source reset retirement");
            old.Deliver();
            active.FollowLatest();
            admit();
            await WaitForAsync(() => reader.Cards.Count == 1 && reader.IsFollowing && reader.UnseenCount == 0 &&
                FindCard(replacement, 0) != null, "new canonical data after reset");
            Console.WriteLine("PASS reset, repopulation and late retired callback");
        }
        finally
        {
            restoreEvents?.Invoke();
            CompositionTarget.Rendering -= OnSourceStaged;
            active.Show(false);
        }

        void OnSourceStaged(object? sender, object args)
        {
            if (Viewer(replacement) is { } viewer && FindDescendant<Native.ItemsRepeater>(viewer)?.Opacity == 0)
                withheldFrames++;
            if (during != 0 ||
                replacement.ItemsSource is not LiveReadingSource { IsComplete: false })
                return;
            restoreEvents = active.SuppressSmokeTerminals(_ => withheld++);
            // The native source has exposed its suffix. Admit into the canonical projection
            // on this composition tick, before the full source has been published.
            while (during < 4)
            {
                during++;
                admit();
            }
        }
    }

    private static async Task ExerciseOwnershipAsync(LiveReadingViewport viewport, LiveCollectionView list,
        Native.ScrollView viewer, LiveReadingState reader, Action admit)
    {
        NavigateTo(viewer, 320);
        await WaitForAsync(() => !reader.IsFollowing && reader.ResolveAnchor()?.Index > 0, "the collision start position");
        using var old = new LiveReadingViewport.HeldNativeScroll(viewport, 0);
        Check(old.Retained == 1, "The withheld ID must belong to the production application scope.");
        await WaitForAsync(() => old.Arrived && viewer.VerticalOffset == 0, "the application's withheld terminal at zero");
        await ExternalScrollAsync(list.AutomationId, "root", 0);
        await WaitForAsync(() => reader.IsFollowing, "an external root scroll to the same destination");
        await ExternalScrollAsync(list.AutomationId, "presenter", 6);
        await WaitForAsync(() => !reader.IsFollowing && reader.ResolveAnchor()?.Index > 0,
            "external presenter navigation away from the application's destination");
        for (var i = 0; i < 4; i++)
            admit();
        await WaitForAsync(() => reader.UnseenCount == 4 && !reader.HasQueuedUpdate, "four unread Favorites");
        await Task.Delay(250);
        var card = reader.ResolveAnchor()!.Value.Card;
        var top = CardTop(list, viewer, reader.IndexOf(card));
        var offset = viewer.VerticalOffset;
        Check(top.HasValue && old.SubmitObsolete(0) < 0, "A superseded app operation could still submit.");
        old.Deliver();
        old.Deliver();
        await Task.Delay(250);
        var finalTop = CardTop(list, viewer, reader.IndexOf(card));
        Check(!reader.IsFollowing && reader.UnseenCount == 4 && ReferenceEquals(card, reader.ResolveAnchor()?.Card)
            && finalTop.HasValue && Math.Abs(finalTop.Value - top!.Value) <= 1.5 &&
            Math.Abs(viewer.VerticalOffset - offset) <= 1 && old.Retained <= 1,
            "A repeated delayed application terminal changed the active reader.");
        Console.WriteLine($"PASS external root and presenter UIA app collision, withheld ID={old.Id}, unread=4, " +
            $"relative={top:F2}/{finalTop:F2} DIPs, retained={old.Retained}");
        var ids = new HashSet<int>();
        var peak = 0;
        viewport.CancelPendingScroll();
        for (var i = 0; i < 128; i++)
        {
            var id = viewport.SmokeAppRequest(i % 4 < 2 ? 0 : 260);
            Check(id >= 0 && ids.Add(id), "Consecutive accepted WinUI requests did not have distinct IDs.");
            peak = Math.Max(peak, viewport.SmokePending);
        }
        viewport.CancelPendingScroll();
        old.Deliver();
        await Task.Delay(250);
        Console.WriteLine($"Native churn issued={ids.Count}, maximum retained={peak}, after cancel={viewport.SmokePending}, " +
            $"following={reader.IsFollowing}, unread={reader.UnseenCount}");
        Check(peak == 1 && viewport.SmokePending == 0 && reader.UnseenCount == 4 && !reader.IsFollowing,
            "Canceled back-to-back application requests consumed navigation or accumulated pending state.");
        Console.WriteLine($"PASS 128 consecutive/same-destination native requests, unique IDs={ids.Count}, maximum retained={peak}; " +
            $"after cancellation retained={viewport.SmokePending}, unread={reader.UnseenCount}");
        viewport.FollowLatest();
        await WaitForAsync(() => reader.IsFollowing && viewer.VerticalOffset < 1, "explicit recovery after collision");
    }

    private static async Task ExternalScrollAsync(string automationId, string surface, double percent)
    {
        const string script = """
            $ErrorActionPreference='Stop'
            Add-Type -AssemblyName UIAutomationClient
            Add-Type -AssemblyName UIAutomationTypes
            $a=[Windows.Automation.AutomationElement]
            $p=[Windows.Automation.PropertyCondition]
            $scope=[Windows.Automation.TreeScope]
            $w=$a::RootElement.FindFirst($scope::Children,$p::new($a::ProcessIdProperty,[int]$env:SMOKE_PID))
            $root=$w.FindFirst($scope::Descendants,$p::new($a::AutomationIdProperty,$env:SMOKE_ID))
            $target=$root
            if($env:SMOKE_SURFACE -eq 'item'){
              $rows=$root.FindAll($scope::Descendants,$p::new($a::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
              if($rows.Count -lt 2){throw 'Expected multiple virtualized list items'}
              $row=$rows.Item($rows.Count-1)
              $row.GetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
              Write-Output ("UIA process {0} scrolled the last of {1} realized list items into view" -f $PID,$rows.Count)
              return
            }
            if($env:SMOKE_SURFACE -eq 'presenter'){
              $target=$root.FindFirst($scope::Descendants,$p::new($a::AutomationIdProperty,'PART_ScrollPresenter'))
            }
            $provider=$target.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
            $provider.SetScrollPercent(-1,[double]::Parse($env:SMOKE_PERCENT,[Globalization.CultureInfo]::InvariantCulture))
            Write-Output ("UIA process {0} targeted {1} ({2}) at {3}%" -f $PID,$target.Current.AutomationId,$target.Current.ControlType.ProgrammaticName,$env:SMOKE_PERCENT)
            """;
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        start.Environment["SMOKE_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        start.Environment["SMOKE_ID"] = automationId;
        start.Environment["SMOKE_SURFACE"] = surface;
        start.Environment["SMOKE_PERCENT"] = percent.ToString(CultureInfo.InvariantCulture);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(); throw; }
        Check(process.ExitCode == 0, $"External {surface} UIA failed: {await errors}");
        Console.Write(await output);
    }

    private static async Task ExerciseScrollbarAsync(Window window, LiveCollectionView list, Native.ScrollView viewer,
        LiveReadingState reader, LiveReadingViewport viewport)
    {
        list.VerticalScrollBarVisibility = ScrollBarVisibility.Always;
        Native.Primitives.ScrollBar? bar = null;
        await WaitForAsync(() => viewer.VerticalScrollBarVisibility == Native.ScrollingScrollBarVisibility.Visible &&
            (bar = FindPart<Native.Primitives.ScrollBar>(viewer, candidate => candidate.Orientation == Native.Orientation.Vertical))
                is { Visibility: Microsoft.UI.Xaml.Visibility.Visible, ActualHeight: > 40 },
            "the visible mapped native scrollbar");
        Native.Primitives.RepeatButton? part = null;
        await WaitForAsync(() => (part = FindPart<Native.Primitives.RepeatButton>(bar!,
            candidate => candidate.Name == "VerticalLargeIncrease")) is { ActualHeight: > 2 }, "the arranged page-increment track");
        var track = part!;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.Handler!.PlatformView);
        Check(GetForegroundWindow() == hwnd, "The test window lost foreground before the scrollbar click.");
        var point = new ScreenPoint();
        var moveCount = 0;
        var hoveredTrack = false;
        string? hoverHit = null;
        Microsoft.UI.Xaml.Input.PointerEventHandler moved = (_, args) =>
        {
            moveCount++;
            var hitElement = args.OriginalSource as FrameworkElement;
            hoverHit = $"{hitElement?.GetType().Name}/{hitElement?.Name}";
            hoveredTrack = IsWithin(hitElement, track);
        };
        viewer.AddHandler(UIElement.PointerMovedEvent, moved, true);
        try
        {
            // Auto-hiding WinUI scrollbars need actual pointer movement after keyboard navigation.
            // The automation peer's bounds are client-relative in this WinUI host.
            MoveMouse(viewer, 0.45, 0.55);
            await WaitForAsync(() => moveCount > 0, "actual mouse input inside the viewport after keyboard navigation");
            var probes = 0;
            await WaitForAsync(() =>
            {
                if (hoveredTrack)
                    return true;
                if (probes++ == 0)
                    MoveMouse(bar!, 0.88, 0.62);
                else
                    MoveMouse(track, probes % 2 == 0 ? 0.5 : 0.8, probes % 2 == 0 ? 0.55 : 0.56);
                return false;
            }, "a native pointer move routed through the page-increment track");
        }
        finally
        {
            viewer.RemoveHandler(UIElement.PointerMovedEvent, moved);
            Console.WriteLine($"Scrollbar hover {track.Name}, screen={point.X}/{point.Y}, mouse moves={moveCount}, " +
                $"hit={hoverHit}, reachedTrack={hoveredTrack}");
        }
        var presses = 0;
        var pressedTrack = false;
        string? hit = null;
        var presenter = (LiveScrollPresenter)viewer.ScrollPresenter;
        var barController = presenter.VerticalScrollController;
        var sequence = new List<string>();
        void By(Native.Primitives.IScrollController sender, Native.Primitives.ScrollControllerScrollByRequestedEventArgs e) =>
            sequence.Add($"by {e.CorrelationId} / {viewport.SmokeStatus}");
        void To(Native.Primitives.IScrollController sender, Native.Primitives.ScrollControllerScrollToRequestedEventArgs e) =>
            sequence.Add($"to {e.CorrelationId} / {viewport.SmokeStatus}");
        void Velocity(Native.Primitives.IScrollController sender, Native.Primitives.ScrollControllerAddScrollVelocityRequestedEventArgs e) =>
            sequence.Add($"velocity {e.CorrelationId} / {viewport.SmokeStatus}");
        void Completed(Native.Primitives.ScrollPresenter sender, Native.ScrollingScrollCompletedEventArgs e) =>
            sequence.Add($"terminal {e.CorrelationId} / {viewport.SmokeStatus}");
        void BarChanged(object sender, Native.Primitives.ScrollEventArgs e) =>
            sequence.Add($"stock {e.ScrollEventType} new={e.NewValue} / {viewport.SmokeStatus}");
        if (barController != null)
        {
            barController.ScrollByRequested += By;
            barController.ScrollToRequested += To;
            barController.AddScrollVelocityRequested += Velocity;
        }
        presenter.ScrollCompleted += Completed;
        bar!.Scroll += BarChanged;
        Microsoft.UI.Xaml.Input.PointerEventHandler press = (_, args) =>
        {
            presses++;
            var node = args.OriginalSource as FrameworkElement;
            hit = $"{node?.GetType().Name}/{node?.Name}";
            pressedTrack = IsWithin(node, track);
            sequence.Add($"press / {viewport.SmokeStatus}");
        };
        viewer.AddHandler(UIElement.PointerPressedEvent, press, true);
        try
        {
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            try { await Task.Delay(100); }
            finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
            await WaitForAsync(() => pressedTrack && presses > 0 && viewer.VerticalOffset > 1 && !reader.IsFollowing &&
                reader.ResolveAnchor()?.Index > 0, "a genuine native scrollbar track click to navigate away");
        }
        finally
        {
            viewer.RemoveHandler(UIElement.PointerPressedEvent, press);
            if (barController != null)
            {
                barController.ScrollByRequested -= By;
                barController.ScrollToRequested -= To;
                barController.AddScrollVelocityRequested -= Velocity;
            }
            presenter.ScrollCompleted -= Completed;
            bar.Scroll -= BarChanged;
            foreach (var entry in sequence)
                Console.WriteLine($"Scrollbar event {entry}");
            Console.WriteLine($"Scrollbar {bar.Name} {bar.ActualWidth}x{bar.ActualHeight}, pointer={point.X}/{point.Y}, " +
                $"routed={presses}, hit={hit}, offset={viewer.VerticalOffset:F2}, reader={reader.ResolveAnchor()?.Index}");
        }
        Console.WriteLine($"PASS scrollbar track click reached offset {viewer.VerticalOffset:F2}");
        list.VerticalScrollBarVisibility = ScrollBarVisibility.Never;

        void MoveMouse(FrameworkElement target, double x, double y)
        {
            var location = target.TransformToVisual(null).TransformPoint(
                new global::Windows.Foundation.Point(target.ActualWidth * x, target.ActualHeight * y));
            var scale = target.XamlRoot.RasterizationScale;
            point.X = (int)Math.Round(location.X * scale);
            point.Y = (int)Math.Round(location.Y * scale);
            Check(ClientToScreen(hwnd, ref point), "Could not locate the current native element on screen.");
            MoveScreenPointer(point);
        }
    }

    private static void MoveScreenPointer(ScreenPoint point)
    {
        var dx = (int)Math.Round((point.X - GetSystemMetrics(76)) * 65535d / (GetSystemMetrics(78) - 1));
        var dy = (int)Math.Round((point.Y - GetSystemMetrics(77)) * 65535d / (GetSystemMetrics(79) - 1));
        mouse_event(0xC001, dx, dy, 0, UIntPtr.Zero); // MOVE | VIRTUALDESK | ABSOLUTE
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject target)
    {
        for (; node != null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, target))
                return true;
        return false;
    }

    private static T? FindPart<T>(DependencyObject root, Predicate<T> matches) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T target && matches(target))
                return target;
            if (FindPart(child, matches) is { } found)
                return found;
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
