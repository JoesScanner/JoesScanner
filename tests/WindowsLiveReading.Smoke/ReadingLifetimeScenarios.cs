using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using JoesScanner.Models;
using JoesScanner.ViewModels;
using JoesScanner.Views;
using JoesScanner.Views.Controls;
using Microsoft.UI.Xaml.Media;
using Native = Microsoft.UI.Xaml.Controls;

namespace JoesScanner.WinUI;

internal sealed partial class LiveReadingSmokeApplication
{
    private sealed class CountingCatalog(IEnumerable<CallItem> cards) : ObservableCollection<CallItem>(cards)
    {
        private readonly List<NotifyCollectionChangedEventHandler> _listeners = [];
        internal int ListenerCount => _listeners.Count;

        public override event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add
            {
                base.CollectionChanged += value;
                if (value != null)
                    _listeners.Add(value);
            }
            remove
            {
                base.CollectionChanged -= value;
                if (value != null)
                    _listeners.Remove(value);
            }
        }
    }

    // The catalog stays alive beyond the retired page. A surviving catalog subscription
    // must not keep its native host, MAUI card roots or bindings alive.
    private async Task ExerciseReadingLifetimeAsync(Window window)
    {
        int[] addresses = [395, 98, 404, 134, 386, 80, 278, 116];
        var calls = new CountingCatalog(Enumerable.Range(0, LiveCallDisplay.CardLimit)
            .Select(i => new CallItem($"Call {i:0000} " + string.Join(" ", Enumerable.Repeat("Dispatch at Main.", i % 5 == 0 ? 18 : i % 6 + 1)))
            { ServerId = _north, HasDetectedAddress = addresses.Contains(i) }));
        var templates = new List<WeakReference>();
        var page = new MainPage(_ => { });
        var panels = new Grid();
        page.Content = panels;
        window.Page = page;
        // Production Combined, Separate and Favorites share MainPage's DataTemplate.
        // Create its instrumentation outside the lifetime of every individual list.
        var template = CountCardCreations(page.LiveCardTemplate, templates);
        for (var i = 0; i < 12; i++)
        {
            var idlePanel = new Grid();
            var idleList = new LiveCollectionView { ReadingSource = calls, ItemTemplate = template };
            idlePanel.Add(idleList);
            panels.Add(idlePanel);
            await WaitForAsync(() => calls.ListenerCount == 1 && Viewer(idleList)?.ViewportHeight > 0,
                "one active subscription to the idle catalog");
            panels.Remove(idlePanel);
            await WaitForAsync(() => calls.ListenerCount == 0,
                "retired catalog subscription to detach without any further admission");
        }
        Console.WriteLine("PASS 12 recorder replacements with no catalog changes left zero retired source subscriptions");
        templates.Clear();
        var retired = await ExerciseRetainedCardsAsync(panels, page, template, calls, addresses, templates);
        var retiredCards = templates.ToArray();
        var next = new LiveCollectionView
        {
            ReadingSource = calls, ItemTemplate = template, SelectionMode = SelectionMode.None,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset
        };
        // Removing the selected recorder makes another projection visible. Keep the
        // catalog, host page and shared factory alive while that next list is on screen.
        panels.Add(next);
        await WaitForAsync(() => Viewer(next)?.ViewportHeight > 0 && page.LoadedTranscripts.Count > 0,
            "the replacement projection to render");
        try
        {
            // Drain dispatcher and finalizer work before measuring WinRT/MAUI reachability.
            for (var i = 0; i < 16 && (retired.Any(reference => reference.IsAlive) || retiredCards.Any(reference => reference.IsAlive)); i++)
            {
                await Task.Delay(100);
                CollectRetiredReadingObjects();
            }
            var liveHosts = retired.Count(reference => reference.IsAlive);
            var liveTemplates = retiredCards.Count(reference => reference.IsAlive);
            Check(liveHosts == 0 && liveTemplates == 0,
                $"Retired owner/list/handler/presenter/repeater or card roots survived with a live catalog: hosts {liveHosts}/{retired.Length}, cards {liveTemplates}/{retiredCards.Length}.");
            GC.KeepAlive(template);
            GC.KeepAlive(calls);
            Console.WriteLine($"PASS retired owner/list/handler/presenter/repeater and {retiredCards.Length} production templates collected with next projection active");
        }
        finally { panels.Remove(next); }
    }

    private static DataTemplate CountCardCreations(DataTemplate template, List<WeakReference> roots) => new(() =>
    {
        var card = (Border)template.CreateContent();
        roots.Add(new(card));
        return card;
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectRetiredReadingObjects()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ExerciseRetainedCardsAsync(Grid panels, MainPage page, DataTemplate template, ObservableCollection<CallItem> calls,
        int[] addresses, List<WeakReference> templates)
    {
        var reader = new LiveReadingState(calls);
        var list = new LiveCollectionView
        {
            ReadingSource = calls, ItemTemplate = template, ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset, SelectionMode = SelectionMode.None,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never, VerticalScrollBarVisibility = ScrollBarVisibility.Never
        };
        var owner = new LiveReadingViewport(list, new Button(), reader);
        var grid = new Grid();
        grid.Add(list);
        panels.Add(grid);
        owner.Show(true);
        await WaitForAsync(() => list.Handler?.PlatformView is LiveItemsView { ScrollView.ScrollableHeight: > 1000 },
            "the lifetime test's populated production list");
        var host = (LiveItemsView)list.Handler!.PlatformView!;
        var native = host.ScrollView!;
        var repeater = FindDescendant<Native.ItemsRepeater>(native)!;
        var source = list.ItemsSource;
        var factory = host.ItemTemplate;
        var warmCount = 0;
        try
        {
            for (var i = 0; i < 72; i++)
            {
                var index = addresses[i % addresses.Length];
                var target = calls[index];
                owner.Reveal(target);
                await WaitForAsync(() => owner.SmokeReaderReady && TargetIsCentered(index, target),
                    $"distant address reveal {i + 1} to canonical card {index}");
                Check(ReferenceEquals(source, list.ItemsSource) && ReferenceEquals(factory, host.ItemTemplate) &&
                      owner.SmokePending == 0 && !reader.IsFollowing, "A reveal replaced its source or item factory or changed reading intent.");
                if ((i + 1) % addresses.Length == 0)
                {
                    CollectRetiredReadingObjects();
                    var retained = templates.Count(reference => reference.IsAlive);
                    var children = VisualTreeHelper.GetChildrenCount(repeater);
                    using var process = Process.GetCurrentProcess();
                    Console.WriteLine($"Reveal {i + 1}, templates {retained}/{templates.Count}, native children {children}, " +
                        $"loaded {page.LoadedTranscripts.Count}, managed {GC.GetTotalMemory(false) / 1048576d:F2} MiB, private {process.PrivateMemorySize64 / 1048576d:F1} MiB");
                    if (i == 31)
                        warmCount = retained;
                    // Every subsequent batch repeats all eight cards on the same unchanged host.
                    if (i > 31)
                        Check(retained <= warmCount + 2, $"The reachable fixed-workload template pool grew from {warmCount} to {retained}.");
                }
            }
            Check(owner.SmokeCenteredPublications > 0 && owner.SmokeMaximumCenterError <= 1.5,
                $"Distant card publication lost its measured center: {owner.SmokeCenteredPublications} observations, maximum error {owner.SmokeMaximumCenterError:F2} DIPs.");
            Console.WriteLine($"PASS {owner.SmokeCenteredPublications} centered card publications before restoring the complete list, maximum error {owner.SmokeMaximumCenterError:F2} DIPs");
            var last = addresses[^1];
            for (var i = 0; i < 4; i++)
            {
                owner.Show(false);
                list.IsVisible = false;
                await Task.Delay(30);
                var clock = Stopwatch.StartNew();
                var frames = 0;
                var blank = 0;
                void ObserveFrame(object? sender, object args)
                {
                    frames++;
                    if (repeater.Opacity == 0)
                        blank++;
                }
                CompositionTarget.Rendering += ObserveFrame;
                try
                {
                    list.IsVisible = true;
                    owner.Show(true);
                    await WaitForAsync(() => owner.SmokeReaderReady && TargetIsCentered(last, calls[last]) && frames > 0,
                        "return to unchanged distant projection");
                    Console.WriteLine($"Retained return {i + 1}, measured-ready {clock.Elapsed.TotalMilliseconds:F1} ms, frames={frames}, blank={blank}");
                    Check(blank == 0 && ReferenceEquals(factory, host.ItemTemplate) && ReferenceEquals(source, list.ItemsSource),
                        "An unchanged retained pane blanked or replaced its template factory.");
                }
                finally { CompositionTarget.Rendering -= ObserveFrame; }
            }
            return [new(owner), new(list), new(list.Handler!), new(native.ScrollPresenter), new(repeater)];
        }
        finally
        {
            owner.Show(false);
            // Recorder removal uses this exact unparenting boundary while MainPage,
            // the canonical catalog and the shared production template remain alive.
            panels.Remove(grid);
        }

        bool TargetIsCentered(int index, CallItem target)
        {
            if (reader.ResolveAnchor() is not { } saved || !ReferenceEquals(saved.Card, target) ||
                repeater.TryGetElement(index) is not Microsoft.UI.Xaml.FrameworkElement row || repeater.GetElementIndex(row) != index ||
                !ReferenceEquals(row.DataContext, repeater.ItemsSourceView.GetAt(index)) ||
                CardTop(list, native, index) is not { } top || saved.Top is not { } cut)
                return false;
            return Math.Abs(top - cut) <= 1.5 && Math.Abs(top - (native.ViewportHeight - row.ActualHeight) / 2) <= 1.5;
        }
    }
}
