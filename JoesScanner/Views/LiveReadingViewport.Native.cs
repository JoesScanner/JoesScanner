using System.Diagnostics;
using JoesScanner.ViewModels;
#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FrameworkElement = Microsoft.UI.Xaml.FrameworkElement;
using NativeElement = Microsoft.UI.Xaml.UIElement;
using NativeViewer = Microsoft.UI.Xaml.Controls.ScrollViewer;
#elif ANDROID
using AndroidX.RecyclerView.Widget;
using Android.Views;
using DroidView = Android.Views.View;
#elif IOS || MACCATALYST
using CoreGraphics;
using Foundation;
using UIKit;
#endif

namespace JoesScanner.Views;

internal sealed partial class LiveReadingViewport
{
#if WINDOWS || ANDROID
    private static readonly TimeSpan InputInterval = TimeSpan.FromMilliseconds(220);
    private long? _nativeInput;
    private bool RecentNativeInput => _nativeInput is { } time && Stopwatch.GetElapsedTime(time) < InputInterval;

    private void StartNativeInput()
    {
        _nativeInput = Stopwatch.GetTimestamp();
        _programmaticScroll = null;
        CancelPendingScroll();
    }
#endif

#if WINDOWS
    private NativeElement? _nativeRoot;
    private NativeViewer? _nativeViewer;
    private ItemsRepeater? _repeater;
    private PointerEventHandler? _wheelHandler;
    private KeyEventHandler? _scrollKeyHandler;
    private bool _directManipulation;

    private void ConnectNativeInput()
    {
        if (_list.Handler?.PlatformView is not NativeElement root)
            return;
        if (!ReferenceEquals(_nativeRoot, root))
        {
            DisconnectNativeInput();
            _nativeRoot = root;
            _wheelHandler = OnWheel;
            _scrollKeyHandler = OnScrollKey;
            root.AddHandler(NativeElement.PointerWheelChangedEvent, _wheelHandler, true);
            root.AddHandler(NativeElement.KeyDownEvent, _scrollKeyHandler, true);
        }
        if (_nativeViewer == null && FindDescendant<NativeViewer>(root) is { } viewer)
        {
            _nativeViewer = viewer;
            _repeater = FindDescendant<ItemsRepeater>(viewer);
            viewer.DirectManipulationStarted += OnManipulationStarted;
            viewer.DirectManipulationCompleted += OnManipulationCompleted;
        }
    }

    private void DisconnectNativeInput()
    {
        if (_nativeRoot is { } root && _wheelHandler is { } wheel && _scrollKeyHandler is { } scrollKey)
        {
            root.RemoveHandler(NativeElement.PointerWheelChangedEvent, wheel);
            root.RemoveHandler(NativeElement.KeyDownEvent, scrollKey);
        }
        if (_nativeViewer is { } viewer)
        {
            viewer.DirectManipulationStarted -= OnManipulationStarted;
            viewer.DirectManipulationCompleted -= OnManipulationCompleted;
        }
        _nativeRoot = null;
        _nativeViewer = null;
        _repeater = null;
        _wheelHandler = null;
        _scrollKeyHandler = null;
        _directManipulation = false;
        _nativeInput = null;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e) => StartNativeInput();

    private void OnScrollKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is global::Windows.System.VirtualKey.Up or global::Windows.System.VirtualKey.Down or
            global::Windows.System.VirtualKey.PageUp or global::Windows.System.VirtualKey.PageDown or
            global::Windows.System.VirtualKey.Home or global::Windows.System.VirtualKey.End)
            StartNativeInput();
    }

    private void OnManipulationStarted(object? sender, object e)
    {
        _directManipulation = true;
        StartNativeInput();
    }

    private void OnManipulationCompleted(object? sender, object e) => _directManipulation = false;

    private bool NativeUserScrolling() => _directManipulation || RecentNativeInput;

    private bool NativeAtTop()
    {
        ConnectNativeInput();
        return _nativeViewer?.VerticalOffset <= LiveReadingState.TopTolerance;
    }

    private CardPosition? MeasureCard(int index)
    {
        ConnectNativeInput();
        if (index < 0 || _nativeViewer is not { } viewer)
            return null;
        var element = _nativeRoot is ListViewBase legacy ? legacy.ContainerFromIndex(index) : _repeater?.TryGetElement(index);
        if (element is not FrameworkElement { ActualHeight: > 0 } card)
            return null;
        var point = card.TransformToVisual(viewer).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        return new(point.Y, card.ActualHeight, viewer.ViewportHeight);
    }

    private void ShiftViewport(double delta)
    {
        if (_nativeViewer is { } viewer)
            viewer.ChangeView(null, Math.Clamp(viewer.VerticalOffset + delta, 0, viewer.ScrollableHeight), null, true);
    }

    private static T? FindDescendant<T>(NativeElement element) where T : NativeElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (VisualTreeHelper.GetChild(element, index) is NativeElement child &&
                (child as T ?? FindDescendant<T>(child)) is { } match)
                return match;
        return null;
    }
#elif ANDROID
    private RecyclerView? _nativeList;

    private void ConnectNativeInput()
    {
        if (_list.Handler?.PlatformView is not RecyclerView recycler || ReferenceEquals(recycler, _nativeList))
            return;
        DisconnectNativeInput();
        _nativeList = recycler;
        recycler.GenericMotion += OnGenericMotion;
        recycler.KeyPress += OnScrollKey;
    }

    private void DisconnectNativeInput()
    {
        if (_nativeList is { } recycler)
        {
            recycler.GenericMotion -= OnGenericMotion;
            recycler.KeyPress -= OnScrollKey;
        }
        _nativeList = null;
        _nativeInput = null;
    }

    private void OnGenericMotion(object? sender, DroidView.GenericMotionEventArgs e)
    {
        if (e.Event?.ActionMasked == MotionEventActions.Scroll)
            StartNativeInput();
    }

    private void OnScrollKey(object? sender, DroidView.KeyEventArgs e)
    {
        if (e.Event?.Action == KeyEventActions.Down && e.KeyCode is Keycode.DpadUp or Keycode.DpadDown or
            Keycode.PageUp or Keycode.PageDown or Keycode.MoveHome or Keycode.MoveEnd)
            StartNativeInput();
    }

    private bool NativeUserScrolling() => RecentNativeInput ||
        _nativeList?.ScrollState is RecyclerView.ScrollStateDragging or RecyclerView.ScrollStateSettling;

    private bool NativeAtTop() => _nativeList?.CanScrollVertically(-1) == false;

    private CardPosition? MeasureCard(int index)
    {
        if (index < 0 || _nativeList is not { } recycler || recycler.GetLayoutManager() is not { } layout ||
            layout.FindViewByPosition(index) is not { } item)
            return null;
        var top = layout.GetDecoratedTop(item);
        return new(top - recycler.PaddingTop, layout.GetDecoratedBottom(item) - top,
            recycler.Height - recycler.PaddingTop - recycler.PaddingBottom);
    }

    private void ShiftViewport(double delta) => _nativeList?.ScrollBy(0, (int)Math.Round(delta));
#elif IOS || MACCATALYST
    private UICollectionView? _nativeList;

    private void ConnectNativeInput()
    {
        if (_nativeList == null && _list.Handler?.PlatformView is UIView root)
            _nativeList = FindCollection(root);
    }

    private void DisconnectNativeInput() => _nativeList = null;

    private bool NativeUserScrolling() => _nativeList is { } native && (native.Dragging || native.Decelerating ||
        native.PanGestureRecognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed);

    private bool NativeAtTop() => _nativeList is { } native &&
        native.ContentOffset.Y + native.AdjustedContentInset.Top <= LiveReadingState.TopTolerance;

    private CardPosition? MeasureCard(int index)
    {
        if (index < 0 || _nativeList is not { } native)
            return null;
        using var path = NSIndexPath.FromItemSection(index, 0);
        if (native.CellForItem(path) is not { } cell)
            return null;
        var inset = native.AdjustedContentInset;
        return new(cell.Frame.Y - native.ContentOffset.Y - inset.Top, cell.Frame.Height,
            native.Bounds.Height - inset.Top - inset.Bottom);
    }

    private void ShiftViewport(double delta)
    {
        if (_nativeList is not { } native)
            return;
        var inset = native.AdjustedContentInset;
        var top = -(double)inset.Top;
        var bottom = Math.Max(top, (double)(native.ContentSize.Height - native.Bounds.Height + inset.Bottom));
        var y = Math.Clamp((double)native.ContentOffset.Y + delta, top, bottom);
        native.SetContentOffset(new CGPoint(native.ContentOffset.X, y), false);
    }

    private static UICollectionView? FindCollection(UIView root)
    {
        if (root is UICollectionView collection)
            return collection;
        foreach (var child in root.Subviews)
            if (FindCollection(child) is { } found)
                return found;
        return null;
    }
#endif
}
