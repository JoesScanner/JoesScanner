using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Maui.Controls.Xaml;
using JoesScanner.Models;
using JoesScanner.ViewModels;
using JoesScanner.Views;
using JoesScanner.Views.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using NativeElement = Microsoft.UI.Xaml.UIElement;
using NativeFrameworkElement = Microsoft.UI.Xaml.FrameworkElement;
using NativeRepeater = Microsoft.UI.Xaml.Controls.ItemsRepeater;
using NativeItems = Microsoft.UI.Xaml.Controls.ItemsView;
using NativeViewer = Microsoft.UI.Xaml.Controls.ScrollView;

namespace JoesScanner.WinUI;

internal sealed partial class LiveReadingSmokeApplication : Application
{
    private int _failures;
    private readonly Guid _north = Guid.NewGuid();
    private readonly Guid _south = Guid.NewGuid();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Console.WriteLine("Starting production Windows Live Reading smoke");
        using var stream = typeof(LiveReadingSmokeApplication).Assembly.GetManifestResourceStream("ToolbarStyles")!;
        var dictionary = XElement.Load(stream);
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        dictionary.Elements().Where(el => (string?)el.Attribute(x + "Key") is not
            ("PillButtonStyle" or "PillButtonBorder" or "PillButtonBackground")).Remove();
        Resources.MergedDictionaries.Add(new ResourceDictionary().LoadFromXaml(dictionary.ToString()));
        var window = new Window(new ContentPage()) { Width = 480, Height = 640 };
        window.Created += (_, _) => window.Dispatcher.Dispatch(async () =>
        {
            try
            {
                await RunCaseAsync("Combined", () => ExerciseProjectionAsync(window, favorites: false));
                await RunCaseAsync("Favorites", () => ExerciseProjectionAsync(window, favorites: true));
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    UserAppTheme = theme;
                    await RunCaseAsync($"Switching {theme}", () => SwitchReadersAsync(window));
                }
                await RunCaseAsync("Detached Separate", () => ExerciseDetachedEvictionAsync(window, favorites: false));
                await RunCaseAsync("Detached Favorites", () => ExerciseDetachedEvictionAsync(window, favorites: true));
                await RunCaseAsync("Tail eviction", () => ExerciseTailEvictionAsync(window));
                await RunCaseAsync("Interrupted tail seed", () => ExerciseTailEvictionAsync(window, TailNavigation.Current));
                await RunCaseAsync("Retired tail seed", () => ExerciseTailEvictionAsync(window, TailNavigation.Reattached));
                await RunCaseAsync("Input lifetime", () => ExerciseInputLifetimeAsync(window));
                await RunCaseAsync("Reading lifetime", () => ExerciseReadingLifetimeAsync(window));
                Console.WriteLine($"Native smoke complete, failures={_failures}");
                Environment.ExitCode = _failures == 0 ? 0 : 1;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL {error}");
                Environment.ExitCode = 1;
            }
            finally { CloseWindow(window); }
        });
        return window;
    }

    private async Task RunCaseAsync(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL {name}: {ex}");
        }
    }

    private async Task ExerciseProjectionAsync(Window window, bool favorites)
    {
        Console.WriteLine($"Testing {(favorites ? "Favorites" : "Combined")} projection");
        var display = new LiveCallDisplay(new ObservableCollection<CallItem>(), favorite: call => call.ServerId == _north);
        display.SetServers(new[] { _north, _south });
        display.Observe();
        var reader = favorites ? display.FavoritesReader : display.CombinedReader;
        var header = new LiveRecorderHeader(_north, new Label { Text = "North" }, new Label { Text = "Online" });
        var newCalls = header.NewCalls;
        var pin = new LivePlaybackPinView();

        var list = new LiveCollectionView
        {
            ReadingSource = reader.Cards,
            AutomationId = favorites ? "SmokeFavorites" : "SmokeCombined",
            EmptyView = new Label { Text = "No Favorites" },
            IsVisible = !favorites,
            SelectionMode = SelectionMode.None,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems
        };
        var viewport = new LiveReadingViewport(list, newCalls, reader);
        var page = new MainPage(viewport.CardSizeChanged);
        var realized = page.LoadedTranscripts;
        list.ItemTemplate = page.LiveCardTemplate;
        var grid = new Grid { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
            new RowDefinition(GridLength.Star)] };
        grid.Add(header);
        grid.Add(pin, 0, 1);
        grid.Add(list, 0, 2);
        page.Content = grid;
        window.Page = page;
        try
        {
            // Favorites starts hidden and empty on the Live page.
            await WaitForAsync(() => grid.IsLoaded, "the page to load");
            viewport.Show(true);
            list.IsVisible = true;
            for (var i = 0; i < LiveCallDisplay.CardLimit; i++)
                Admit($"North dispatch {i} " + string.Join(" ", Enumerable.Repeat("Engine response.", 1 + i % 8)));
            await WaitForAsync(() => list.IsLoaded && Viewer(list)?.ScrollableHeight > 0 && !reader.HasQueuedUpdate,
                "the native virtualized list to load");
            var native = Viewer(list)!;
            Admit("First native admission");
            await WaitForAsync(() => !reader.HasQueuedUpdate && FindCard(list, 0) != null, "the first arrival");
            Check(list.Handler?.PlatformView is LiveItemsView && native.ScrollPresenter is LiveScrollPresenter,
                "Live cards did not use the production Windows collection handler and presenter.");
            Check(reader.IsFollowing && native.VerticalOffset < 1 && !newCalls.IsVisible, "The live list did not follow at the top.");

            NavigateTo(native, 12);
            await WaitForAsync(() => !reader.IsFollowing && reader.ResolveAnchor() is { Index: 0, Top: < -3 },
                "a partially visible first card to enter reading mode");
            NavigateTo(native, 207);
            await WaitForAsync(() => reader.ResolveAnchor() is { Index: > 0 }, "a reader anchor beyond the first card");
            var anchor = reader.ResolveAnchor()!.Value;
            var originalTop = CardTop(list, native, anchor.Index)!.Value;
            var readingOrigin = WindowTop(native);
            var screenOrigin = PhysicalY(window, native, 0);
            var screenCard = PhysicalY(window, native, originalTop);

            if (favorites)
            {
                Admit("South nonfavorite", _south);
                Check(AtAnchor() && reader.UnseenCount == 0, "A nonfavorite changed the Favorites viewport.");
            }
            for (var index = 0; index < 5; index++)
                Admit($"Burst recording {index + 1}");
            await WaitForAsync(() => reader.UnseenCount == 5 && AtAnchor() && !reader.HasQueuedUpdate, "the burst to retain its reader anchor");
            Check(newCalls.Text == "New Calls (5)" && newCalls.IsVisible, "The native New Calls button did not show five admissions.");

            Check(newCalls.Style == Resources["PillButtonStyle"] && newCalls.HeightRequest == 22 &&
                Math.Abs(WindowTop(native) - readingOrigin) < 0.5 && PhysicalY(window, native, 0) == screenOrigin &&
                Math.Abs(PhysicalY(window, native, CardTop(list, native, reader.IndexOf(anchor.Card))!.Value) - screenCard) <= 2,
                "Showing the recorder New Calls pill moved the absolute reading surface.");
            Console.WriteLine($"PASS Recorder indicator uses the production pill and kept list Y at {readingOrigin:0.0} DIPs, " +
                $"screen list/card={screenOrigin}/{screenCard} physical px");

            var origin = WindowTop(native);
            var viewportHeight = native.ViewportHeight;
            var active = new CallItem("Active recording outside the display window.");
            pin.Present(active);
            await WaitForAsync(() => pin.Content is { IsVisible: true, Height: > 0 }, "the native pin to be displayed");
            Check(ReferenceEquals(pin.Card, active) && SameViewport() && AtAnchor() && reader.UnseenCount == 5,
                "Showing the active pin changed the reader's viewport or New Calls.");
            pin.Present(null);
            await Task.Delay(50);
            Check(pin.Card == null && pin.Content is { IsVisible: false } && SameViewport() && AtAnchor() && reader.UnseenCount == 5,
                "Clearing the active pin changed the reader's viewport or New Calls.");
            Console.WriteLine($"PASS Native Windows pin appearance and removal kept the viewport at {origin:0.0} DIPs and New Calls (5)");

            var transcript = realized.Single(label => ReferenceEquals(label.BindingContext, anchor.Card));
            Check(transcript.IsInVisibleList() && transcript.BackgroundColor?.Alpha is null or 0,
                "A realized card must start with correction feedback clear.");
            var originalHeight = FindCard(list, reader.IndexOf(anchor.Card))!.ActualHeight;
            anchor.Card.Correct(string.Join(" ", Enumerable.Repeat("A longer corrected synthetic dispatch transcript.", 24)));
            Check(transcript.BackgroundColor?.Alpha > 0, "A genuine correction must tint the native label.");
            await WaitForAsync(() => FindCard(list, reader.IndexOf(anchor.Card))?.ActualHeight > originalHeight + 20 &&
                AtAnchor() && !reader.HasQueuedUpdate, "the multiline transcript to grow without moving its card");
            anchor.Card.Correct("Short correction.");
            Check(transcript.BackgroundColor?.Alpha > 0, "A second correction must restart feedback.");
            await WaitForAsync(() => FindCard(list, reader.IndexOf(anchor.Card))?.ActualHeight < originalHeight + 2 &&
                AtAnchor() && !reader.HasQueuedUpdate, "the transcript correction to retain its card");
            Check(reader.UnseenCount == 5, "A transcript correction changed the unseen count.");
            await WaitForAsync(() => transcript.BackgroundColor?.Alpha is null or 0, "the second correction tint to clear");
            anchor.Card.Correct("A third correction before the view retires.");
            Check(transcript.BackgroundColor?.Alpha > 0, "The current transcript should have feedback before it is hidden.");
            transcript.ShowCorrectionFeedback(false);
            Check(transcript.BackgroundColor?.Alpha is null or 0, "Retiring the transcript must immediately remove its tint.");
            anchor.Card.Correct("Updated while the view is retired.");
            Check(transcript.BackgroundColor?.Alpha is null or 0, "A retired label must stop observing the recording.");
            Console.WriteLine("PASS Native Windows transcript corrections tinted, retained the reader anchor, and cleared when retired");
            var finalTop = CardTop(list, native, reader.IndexOf(anchor.Card))!.Value;

            if (favorites)
            {
                var index = reader.IndexOf(anchor.Card);
                var survivor = reader.Cards[index + 1];
                display.Combined.Remove(anchor.Card);
                await WaitForAsync(() => !reader.HasQueuedUpdate && reader.ResolveAnchor() is { } fallback &&
                    ReferenceEquals(fallback.Card, survivor) && FindCard(list, fallback.Index) is { } element &&
                    CardTop(list, native, fallback.Index) is { } top && top + element.ActualHeight > 0 &&
                    top < native.ViewportHeight, "a surviving Favorite to be visible after anchor eviction");
                Check(!reader.IsFollowing && reader.UnseenCount == 5, "Eviction changed the reader's intent or count.");
                Console.WriteLine("PASS Native Favorites eviction selected the surviving neighbor and retained New Calls (5)");
            }

            var button = (Microsoft.UI.Xaml.Controls.Button)newCalls.Handler!.PlatformView!;
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
            invoke.Invoke();
            await WaitForAsync(() => native.VerticalOffset < 1 && reader.IsFollowing && !newCalls.IsVisible,
                "the native New Calls button to return to the top");

            Console.WriteLine($"PASS Native Windows CollectionView retained the card at {originalTop:0.0} to {finalTop:0.0} DIPs and New Calls returned to the top");
            Console.WriteLine($"PASS {(favorites ? "Favorites" : "Combined")} projection followed Live after its deliberate return");

            if (favorites)
            {
                var provider = Provider(native);
                provider.SetScrollPercent(-1, 10);
                await WaitForAsync(() => !reader.IsFollowing, "UI Automation to navigate away");
                Admit("Accessibility arrival");
                await WaitForAsync(() => reader.UnseenCount == 1 && !reader.HasQueuedUpdate, "accessibility anchor restoration");
                provider.SetScrollPercent(-1, 0);
                await WaitForAsync(() => reader.IsFollowing && reader.UnseenCount == 0 && native.VerticalOffset < 1,
                    "UI Automation to return to the top after restoration");
                Console.WriteLine("PASS WinUI ScrollPattern navigated away and returned to Live after an arrival");
                await RunCaseAsync("external item navigation", async () =>
                {
                    await ExternalScrollAsync(list.AutomationId, "item", 0);
                    await WaitForAsync(() => !reader.IsFollowing && native.VerticalOffset > 0 &&
                        reader.ResolveAnchor()?.Index > 0, "an external ScrollItem.ScrollIntoView to navigate away");
                    Console.WriteLine($"PASS external ScrollItem navigated to {native.VerticalOffset:F2} DIPs");
                });
                viewport.FollowLatest();
                await WaitForAsync(() => native.VerticalOffset < 1, "return after external item navigation");
                await RunCaseAsync("withheld accessibility collision", () =>
                    ExerciseOwnershipAsync(viewport, list, native, reader, () => Admit("Accessibility collision")));
                viewport.FollowLatest();
                await WaitForAsync(() => reader.IsFollowing && native.VerticalOffset < 1, "return after accessibility collision");
                await RunCaseAsync("continuous admissions", () =>
                    ExerciseContinuousArrivalsAsync(list, native, display, _north, () => Admit("Continuous admission")));
                viewport.FollowLatest();
                await WaitForAsync(() => reader.IsFollowing && native.VerticalOffset < 1, "return after continuous arrivals");
                await RunCaseAsync("coalesced explicit commands", async () =>
                {
                    NavigateTo(native, 320);
                    await WaitForAsync(() => reader.ResolveAnchor()?.Index > 0, "explicit command churn origin");
                    var realizedCard = reader.ResolveAnchor()!.Value.Card;
                    var before = viewport.SmokeAppSubmissions;
                    for (var i = 0; i < 4096; i++)
                    {
                        if ((i & 1) == 0)
                            viewport.Reveal(realizedCard);
                        else
                            viewport.FollowLatest();
                    }
                    var immediate = viewport.SmokeAppSubmissions - before;
                    await WaitForAsync(() => native.VerticalOffset < 1 && reader.IsFollowing && reader.UnseenCount == 0,
                        "the final explicit command to reach Live");
                    var settled = viewport.SmokeAppSubmissions - before;
                    Console.WriteLine($"Explicit reveal/Live churn commands=4096, immediate native requests={immediate}, settled={settled}");
                    Check(immediate == 0 && settled == 1, "Superseded explicit commands submitted unnecessary native work.");
                });
            }

            if (favorites && Environment.GetEnvironmentVariable("JOESSCANNER_SMOKE_WHEEL") == "1")
            {
                await RunCaseAsync("interactive card template", () => ExerciseCardActionsAsync(window, page, list, reader));
                for (var run = 0; run < 4; run++)
                {
                    viewport.FollowLatest();
                    await WaitForAsync(() => reader.IsFollowing && native.VerticalOffset < 1, "wheel comparison origin");
                    await RunCaseAsync($"wheel comparison {run + 1}", () =>
                        ExerciseWheelBurstAsync(window, list, native, reader, viewport, () => Admit("Wheel arrival"), run % 2 == 0 ? 1 : 3));
                }
                viewport.FollowLatest();
                await WaitForAsync(() => reader.IsFollowing && native.VerticalOffset < 1, "scrollbar origin");
                await RunCaseAsync("native scrollbar", () => ExerciseScrollbarAsync(window, list, native, reader, viewport));
                viewport.FollowLatest();
                await WaitForAsync(() => reader.IsFollowing && native.VerticalOffset < 1, "Jump to Live after the wheel burst");
            }

            if (favorites)
            {
                await RunCaseAsync("reattach", async () =>
                {
                    NavigateTo(native, 290);
                    await WaitForAsync(() => reader.ResolveAnchor()?.Index > 0, "reader before hiding");
                    var saved = reader.ResolveAnchor()!.Value;
                    for (var i = 0; i < 5; i++)
                        Admit("Before hiding");
                    var savedY = saved.Top!.Value;
                    viewport.CardSizeChanged(saved.Card);
                    viewport.Show(false);
                    NavigateTo(native, native.ScrollableHeight);
                    await WaitForAsync(() => FindCard(list, reader.IndexOf(saved.Card)) == null, "retired card recycling");
                    Check(ReferenceEquals(reader.ResolveAnchor()?.Card, saved.Card) && reader.UnseenCount == 5,
                        "A native move after detaching Favorites changed its reader.");
                    viewport.Show(true);
                    try
                    {
                        await WaitForAsync(() => CardTop(list, native, reader.IndexOf(saved.Card)) is { } y &&
                            Math.Abs(y - savedY) <= 1.5, "the reattached Favorites card and cut");
                    }
                    finally { Console.WriteLine($"Reattach expected={savedY}, index={reader.IndexOf(saved.Card)}, {viewport.SmokeStatus}"); }
                    Check(newCalls.Text == "New Calls (5)", "Restoring Favorites changed its unread count.");
                    Console.WriteLine("PASS Favorites restored its recycled anchor after movement on the retired viewport");
                });
                viewport.FollowLatest();
                await WaitForAsync(() => reader.IsFollowing && native.VerticalOffset < 1, "return before distant recreation");
                await ExerciseRecreationAsync(window, grid, list, viewport, display, () => Admit("During reconstruction"));
            }

            bool AtAnchor() => CardTop(list, native, reader.IndexOf(anchor.Card)) is { } top &&
                Math.Abs(top - originalTop) <= 1.5;
            bool SameViewport() => Math.Abs(WindowTop(native) - origin) < 0.5 &&
                Math.Abs(native.ViewportHeight - viewportHeight) < 0.5;
        }
        finally
        {
            viewport.Show(false);
            display.Detach();
        }

        void Admit(string text, Guid? recorder = null)
        {
            var call = new CallItem(text) { ServerId = recorder ?? _north, HasDetectedAddress = display.Combined.Count % 9 == 0 };
            display.Combined.Insert(0, call);
            display.RecordVisibleAdmission(call);
            if (display.Combined.Count > LiveCallDisplay.CardLimit)
                display.Combined.RemoveAt(LiveCallDisplay.CardLimit);
        }
    }

    // The isolated interactive desktop enables real mouse input. Admit the first
    // Favorite on the same UI turn as the native movement, before wheel idle.
    private static async Task ExerciseWheelBurstAsync(Window window, CollectionView list, NativeViewer viewer,
        LiveReadingState reader, LiveReadingViewport viewport, Action admit, int detents)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.Handler!.PlatformView);
        var origin = viewer.TransformToVisual(null).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        var scale = viewer.XamlRoot.RasterizationScale;
        var point = new ScreenPoint
        {
            X = (int)((origin.X + viewer.ViewportWidth / 2) * scale),
            Y = (int)((origin.Y + viewer.ViewportHeight / 2) * scale)
        };
        Check(ClientToScreen(hwnd, ref point), "Could not locate the smoke viewport on the desktop.");
        Check(SetCursorPos(point.X, point.Y), "Could not position the isolated desktop pointer.");
        var clock = Stopwatch.StartNew();
        double lastFrame = 0;
        var frames = 0;
        double firstFrame = 0;
        var inject = false;
        void OnMovement(NativeViewer sender, object args)
        {
            if (viewer.VerticalOffset <= 0 || reader.IsFollowing)
                return;
            lastFrame = clock.Elapsed.TotalMilliseconds;
            if (++frames == 1)
            {
                firstFrame = lastFrame;
                if (inject)
                    admit();
            }
        }

        async Task WaitForIdle() => await WaitForAsync(() => frames > 0 && !reader.HasQueuedUpdate &&
            viewer.State == Microsoft.UI.Xaml.Controls.ScrollingInteractionState.Idle &&
            clock.Elapsed.TotalMilliseconds - lastFrame > 350, "the entire physical wheel gesture through idle");

        async Task WheelAsync()
        {
            for (var i = 0; i < detents; i++)
            {
                mouse_event(0x0800, 0, 0, -120, UIntPtr.Zero);
                if (i + 1 < detents)
                    await Task.Delay(54);
            }
        }

        viewer.ViewChanged += OnMovement;
        try
        {
            await WheelAsync();
            await WaitForIdle();
            var control = reader.ResolveAnchor()!.Value;
            var expected = CardTop(list, viewer, control.Index)!.Value;
            int ScreenY(double y) => PhysicalY(window, viewer, y);
            var expectedScreen = ScreenY(expected);
            var controlFrames = frames;
            var provider = Provider(viewer);
            provider.SetScrollPercent(-1, 0);
            await WaitForAsync(() => viewer.VerticalOffset < 1 && reader.IsFollowing, "return to calibration origin");
            await Task.Delay(350);
            frames = 0;
            inject = true;
            clock.Restart();
            var beforeWheel = viewport.SmokeAppSubmissions;
            var wheels = WheelAsync();
            await WaitForAsync(() => frames > 0, "the first physical wheel frame and concurrent Favorite");
            await Task.Delay(35);
            admit();
            await Task.Delay(45);
            admit();
            await wheels;
            await WaitForIdle();
            var top = CardTop(list, viewer, reader.IndexOf(control.Card));
            Console.WriteLine($"Wheel detents={detents}, control frames={controlFrames}, burst frames={frames}, first admission={firstFrame:0.0} ms, " +
                $"control={control.Card.Transcription} at {expected:0.0}, burst={reader.ResolveAnchor()?.Card.Transcription} at {top:0.0}");
            Check(top.HasValue && Math.Abs(expected - top.Value) <= 1.5 && Math.Abs(expectedScreen - ScreenY(top.Value)) <= 2 &&
                ReferenceEquals(reader.ResolveAnchor()?.Card, control.Card) && firstFrame < 220 && reader.UnseenCount == 3 &&
                viewport.SmokeAppSubmissions == beforeWheel,
                "The complete gesture with arrivals diverged from the uninterrupted control.");
            Console.WriteLine($"PASS Physical wheel through idle with three Favorites; relative {expected:0.0}/{top:0.0} DIPs; " +
                $"screen {expectedScreen}/{ScreenY(top!.Value)} px; application submissions={viewport.SmokeAppSubmissions - beforeWheel}");
            // Wheel routing uses the pointer location. Keyboard routing also
            // requires the smoke window to own the foreground and XAML focus.
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            await WaitForAsync(() => GetForegroundWindow() == hwnd, "the isolated smoke window to own keyboard focus");
            Check(viewer.Focus(Microsoft.UI.Xaml.FocusState.Keyboard), "Could not focus the native scroll viewer.");
            var keys = 0;
            Microsoft.UI.Xaml.Input.KeyEventHandler key = (_, args) =>
            {
                if (args.Key == global::Windows.System.VirtualKey.PageUp)
                    keys++;
            };
            viewer.AddHandler(NativeElement.KeyDownEvent, key, true);
            try
            {
                for (var i = 0; i < 4 && !reader.IsFollowing; i++)
                {
                    keybd_event(0x21, 0x49, 1, UIntPtr.Zero);
                    keybd_event(0x21, 0x49, 3, UIntPtr.Zero);
                    await Task.Delay(300);
                }
                Check(keys > 0 && viewer.VerticalOffset < 1 && reader.IsFollowing && reader.UnseenCount == 0,
                    $"Keyboard Page Up did not return to Live (keys={keys}, offset={viewer.VerticalOffset}, unread={reader.UnseenCount}).");
            }
            finally { viewer.RemoveHandler(NativeElement.KeyDownEvent, key); }
            Console.WriteLine($"PASS Focused native Page Up reached the Live edge and cleared three unread Favorites ({keys} keys)");
        }
        finally { viewer.ViewChanged -= OnMovement; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { internal int X; internal int Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);

    private static int PhysicalY(Window window, NativeViewer viewer, double relativeY)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.Handler!.PlatformView);
        var point = new ScreenPoint { Y = (int)Math.Round((WindowTop(viewer) + relativeY) * viewer.XamlRoot.RasterizationScale) };
        Check(ClientToScreen(hwnd, ref point), "Could not measure the physical screen position.");
        return point.Y;
    }

    private async Task SwitchReadersAsync(Window window)
    {
        var display = new LiveCallDisplay(new ObservableCollection<CallItem>(), favorite: card => card.ServerId == _north);
        display.SetServers(new[] { _north, _south });
        display.Observe();
        var grid = new Grid { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)] };
        var separateHeader = new LiveRecorderHeader(_south, new Label { Text = "South" }, new Label { Text = "Online" });
        grid.Add(separateHeader);
        var readers = new[] { display.CombinedReader, display.FavoritesReader, display.ReaderForServer(_south) };
        var views = readers.Select(reader =>
        {
            var list = new LiveCollectionView
            {
                ReadingSource = reader.Cards, IsVisible = false,
                ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset
            };
            grid.Add(list, 0, 1);
            var indicator = ReferenceEquals(reader, readers[2]) ? separateHeader.NewCalls : new Button();
            return (List: list, Viewport: new LiveReadingViewport(list, indicator, reader));
        }).ToArray();
        var page = new MainPage(call =>
        {
            foreach (var view in views)
                view.Viewport.CardSizeChanged(call);
        });
        foreach (var view in views)
            view.List.ItemTemplate = page.LiveCardTemplate;
        try
        {
            for (var i = 0; i < 80; i++)
                Admit(i % 2 == 0 ? _north : _south);
            page.Content = grid;
            window.Page = page;
            var anchors = new LiveReadingAnchor[views.Length];
            for (var i = 0; i < views.Length; i++)
            {
                Show(i);
                var view = views[i];
                await WaitForAsync(() => view.List.IsLoaded && Viewer(view.List) is
                    { ScrollableHeight: > 0, ScrollPresenter.IsLoaded: true } && !readers[i].HasQueuedUpdate,
                    "the selected projection to be measured");
                NavigateTo(Viewer(view.List)!, 105 + 113 * i);
                await WaitForAsync(() => !readers[i].IsFollowing && readers[i].ResolveAnchor()?.Top < -1,
                    "an independent partially visible anchor");
                anchors[i] = readers[i].ResolveAnchor()!.Value;
            }
            var separate = views[2].List;
            var separateViewer = Viewer(separate)!;
            var saved = anchors[2];
            var screenOrigin = PhysicalY(window, separateViewer, 0);
            var screenCard = PhysicalY(window, separateViewer, CardTop(separate, separateViewer, saved.Index)!.Value);
            Admit(_north);
            Admit(_south);
            Admit(_north);
            Check(readers.Select(reader => reader.UnseenCount).SequenceEqual(new[] { 3, 2, 1 }),
                "The three projections must count their own arrivals while another list is visible.");
            await WaitForAsync(() => separateHeader.NewCalls.IsVisible && separateHeader.NewCalls.Width > 0 &&
                separateHeader.NewCalls.Text == "New Calls (1)" &&
                CardTop(separate, separateViewer, readers[2].IndexOf(saved.Card)) is { } cut &&
                PhysicalY(window, separateViewer, 0) == screenOrigin && Math.Abs(PhysicalY(window, separateViewer, cut) - screenCard) <= 2,
                "the real Separate recorder header to preserve the absolute reading surface and card");
            var expectedText = Color.FromArgb(UserAppTheme == AppTheme.Dark ? "#e5e7eb" : "#374151");
            await WaitForAsync(() => page.LoadedTranscripts.Any(label => label.IsInVisibleList() && Equals(label.TextColor, expectedText)),
                "the production transcript template to resolve the selected theme");
            Console.WriteLine($"PASS {UserAppTheme} Separate New Calls (1), absolute reading surface/card {screenOrigin}/{screenCard} px");
            for (var i = 0; i < views.Length; i++)
            {
                Show(i);
                var list = views[i].List;
                var anchor = anchors[i];
                await WaitForAsync(() => !readers[i].HasQueuedUpdate && Viewer(list) is { } viewer &&
                    CardTop(list, viewer, readers[i].IndexOf(anchor.Card)) is { } top &&
                    Math.Abs(top - anchor.Top!.Value) <= 1.5, "the projection to restore its own native pixel anchor");
                views[i].Viewport.FollowLatest();
                await WaitForAsync(() => Viewer(list)?.VerticalOffset < 1 && readers[i].UnseenCount == 0,
                    "the selected projection to return to Live");
            }
            Console.WriteLine("PASS Combined, Favorites and Separate restored independent native anchors and counts 3/2/1 across switches");
        }
        finally
        {
            foreach (var view in views)
                view.Viewport.Show(false);
            display.Detach();
        }

        void Show(int selected)
        {
            separateHeader.IsVisible = selected == 2;
            for (var i = 0; i < views.Length; i++)
            {
                views[i].Viewport.Show(i == selected);
                views[i].List.IsVisible = i == selected;
            }
        }

        void Admit(Guid server)
        {
            var card = new CallItem($"Call {display.Combined.Count}") { ServerId = server };
            display.Combined.Insert(0, card);
            display.RecordVisibleAdmission(card);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
                await Task.Delay(25, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Timed out waiting for {description}.");
        }
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition)
            throw new InvalidOperationException(failure);
    }

    private static NativeViewer? Viewer(CollectionView list) =>
        (list.Handler?.PlatformView as NativeItems)?.ScrollView;

    private static IScrollProvider Provider(NativeViewer viewer) =>
        (IScrollProvider)FrameworkElementAutomationPeer.CreatePeerForElement(viewer.ScrollPresenter)!
            .GetPattern(PatternInterface.Scroll)!;

    private static void NavigateTo(NativeViewer viewer, double y) =>
        Provider(viewer).SetScrollPercent(-1, Math.Clamp(100 * y / viewer.ScrollableHeight, 0, 100));

    private static NativeFrameworkElement? FindCard(CollectionView list, int index) =>
        index >= 0 ? FindDescendant<NativeRepeater>(Viewer(list))?.TryGetElement(index) as NativeFrameworkElement : null;

    private static double? CardTop(CollectionView list, NativeViewer viewer, int index) =>
        FindCard(list, index)?.TransformToVisual(viewer).TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;

    private static double WindowTop(NativeViewer viewer) =>
        viewer.TransformToVisual(null).TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;

    private static T? FindDescendant<T>(NativeElement? root) where T : NativeElement
    {
        if (root != null)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (VisualTreeHelper.GetChild(root, i) is NativeElement child &&
                    (child as T ?? FindDescendant<T>(child)) is { } found)
                    return found;
        return null;
    }
}
