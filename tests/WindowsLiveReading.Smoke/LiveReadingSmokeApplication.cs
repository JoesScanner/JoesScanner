using System.Collections.ObjectModel;
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
using NativeList = Microsoft.UI.Xaml.Controls.ListViewBase;
using NativeViewer = Microsoft.UI.Xaml.Controls.ScrollViewer;

namespace JoesScanner.WinUI;

internal sealed class LiveReadingSmokeApplication : Application
{
    private readonly ObservableCollection<CallItem> _cards = new(Enumerable.Range(1, 20)
        .Select(index => new CallItem($"Synthetic dispatch recording {index}.")));

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var reader = new LiveReadingState(_cards);
        var newCalls = new Button { IsVisible = false };
        var pin = new LivePlaybackPinView();
        var realized = new HashSet<LiveTranscriptLabel>();
        var list = new CollectionView
        {
            ItemsSource = _cards,
            SelectionMode = SelectionMode.None,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems
        };
        var viewport = new LiveReadingViewport(list, newCalls, reader);
        list.ItemTemplate = new DataTemplate(() =>
        {
            var transcript = new LiveTranscriptLabel { LineBreakMode = LineBreakMode.WordWrap };
            transcript.SetBinding(Label.TextProperty, nameof(CallItem.Transcription));
            transcript.Loaded += (_, _) =>
            {
                realized.Add(transcript);
                transcript.ShowCorrectionFeedback(true);
            };
            transcript.Unloaded += (_, _) => realized.Remove(transcript);
            var card = new Border { Padding = 10, MinimumHeightRequest = 72, Content = transcript };
            card.SizeChanged += (_, _) =>
            {
                if (card.BindingContext is CallItem recording)
                    viewport.CardSizeChanged(recording);
            };
            return card;
        });
        var grid = new Grid { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star),
            new RowDefinition(GridLength.Auto)] };
        grid.Add(pin);
        grid.Add(list, 0, 1);
        grid.Add(newCalls, 0, 2);
        var window = new Window(new ContentPage { Content = grid }) { Width = 480, Height = 640 };
        window.Created += (_, _) => window.Dispatcher.Dispatch(() => _ = RunAsync());
        return window;

        async Task RunAsync()
        {
            try
            {
                viewport.Show(true);
                await WaitForAsync(() => list.IsLoaded && Viewer(list)?.ScrollableHeight > 0 && !reader.HasQueuedUpdate,
                    "the native virtualized list to load");
                var native = Viewer(list)!;
                Admit("First native admission");
                await WaitForAsync(() => !reader.HasQueuedUpdate && FindCard(list, 0) != null, "the first arrival");
                Check(reader.IsFollowing && native.VerticalOffset < 1 && !newCalls.IsVisible, "The live list did not follow at the top.");

                native.ChangeView(null, 12, null, true);
                await WaitForAsync(() => !reader.IsFollowing && reader.ResolveAnchor() is { Index: 0, Top: < -3 },
                    "a partially visible first card to enter reading mode");
                native.ChangeView(null, 103, null, true);
                await WaitForAsync(() => reader.ResolveAnchor() is { Index: > 0 }, "a reader anchor beyond the first card");
                var anchor = reader.ResolveAnchor()!.Value;
                var originalTop = CardTop(list, native, anchor.Index)!.Value;

                for (var index = 0; index < 5; index++)
                    Admit($"Burst recording {index + 1}");
                await WaitForAsync(() => reader.UnseenCount == 5 && AtAnchor() && !reader.HasQueuedUpdate, "the burst to retain its reader anchor");
                Check(newCalls.Text == "New Calls (5)" && newCalls.IsVisible, "The native New Calls button did not show five admissions.");

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

                var button = (Microsoft.UI.Xaml.Controls.Button)newCalls.Handler!.PlatformView!;
                var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
                invoke.Invoke();
                await WaitForAsync(() => native.VerticalOffset < 1 && reader.IsFollowing && !newCalls.IsVisible,
                    "the native New Calls button to return to the top");

                Console.WriteLine($"PASS Native Windows CollectionView retained the card at {originalTop:0.0} to {finalTop:0.0} DIPs and New Calls returned to the top");
                Environment.ExitCode = 0;

                bool AtAnchor() => CardTop(list, native, reader.IndexOf(anchor.Card)) is { } top &&
                    Math.Abs(top - originalTop) <= 1.5;
                bool SameViewport() => Math.Abs(WindowTop(native) - origin) < 0.5 &&
                    Math.Abs(native.ViewportHeight - viewportHeight) < 0.5;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL {error.Message}");
                Environment.ExitCode = 1;
            }
            finally
            {
                viewport.Show(false);
                CloseWindow(window);
            }
        }

        void Admit(string text)
        {
            var call = new CallItem(text);
            _cards.Insert(0, call);
            reader.RecordAdmission(call);
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
        FindDescendant<NativeViewer>((NativeElement?)list.Handler?.PlatformView);

    private static NativeFrameworkElement? FindCard(CollectionView list, int index) =>
        (list.Handler?.PlatformView is NativeList legacy ? legacy.ContainerFromIndex(index) :
            FindDescendant<NativeRepeater>(Viewer(list))?.TryGetElement(index)) as NativeFrameworkElement;

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
