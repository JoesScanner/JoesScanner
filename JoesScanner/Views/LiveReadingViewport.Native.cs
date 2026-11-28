using System.Diagnostics;
using JoesScanner.ViewModels;
#if ANDROID
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
#if ANDROID
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

#if ANDROID
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
