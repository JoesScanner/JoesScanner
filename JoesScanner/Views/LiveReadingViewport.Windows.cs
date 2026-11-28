#if WINDOWS
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using JoesScanner.Views.Controls;
using JoesScanner.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Native = Microsoft.UI.Xaml.Controls;
using NativeScroll = Microsoft.UI.Xaml.Controls.Primitives;
using Key = Windows.System.VirtualKey;

namespace JoesScanner.Views;

internal sealed partial class LiveReadingViewport
{
    private const int MaximumAlignmentRequests = 8;
    private const uint GetWheelScrollLines = 0x0068;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool ReadMouseWheelSettings(uint action, uint parameter, out uint lines, uint update);

    private sealed class WindowsRestore(CallItem card, double? top, bool center, long generation)
    {
        internal CallItem Card = card;
        internal bool SeekingTail;
        internal int TailWindow;
        internal readonly double? Top = top;
        internal readonly bool Center = center;
        internal readonly long Generation = generation;
        internal bool Staging;
        internal bool RequiresFreshSeed;
        internal bool AtSeed;
        internal int? WaitingFor;
        internal bool PositionSubmitted;
        internal double RequestedOffset;
        internal bool SeedNeedsFrame;
        internal int AlignmentRequests;
        internal bool VerifyNextFrame = true;
    }

    // WinRT routed event removal requires the exact delegate instance registered with AddHandler.
    private sealed class RoutedInput(LiveReadingViewport owner)
    {
        internal readonly KeyEventHandler PreviewKey = owner.OnWindowsPreviewKey;
        internal readonly PointerEventHandler Wheel = owner.OnWindowsWheel;
        internal readonly PointerEventHandler Press = owner.OnWindowsPress;
        internal readonly KeyEventHandler Key = owner.OnWindowsKey;
        internal readonly KeyEventHandler KeyUp = owner.OnWindowsKeyUp;
        internal readonly PointerEventHandler Release = owner.OnWindowsRelease;
    }

    private readonly RoutedInput _routedInput;
    private LiveItemsView? _windowsList;
    private Native.ScrollView? _windowsView;
    private LiveScrollPresenter? _windowsPresenter;
    private Native.ItemsRepeater? _windowsRepeater;
    private NativeScroll.IScrollController? _windowsBar;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _restoreWatchdog;
    private WindowsLiveScrollIntent? _windowsIntent;
    private WindowsRestore? _windowsRestore;
    private CallItem? _revealWhenConnected;
    private LiveReadingSource? _windowsSource;
    private bool _windowsLayoutSubscribed;
    private bool _processingWindowsLayout;
    private bool _followWhenReady;
    private bool _explicitWindowsFollow;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private CallItem? _wheelAnchor;
    private double? _wheelOriginCut;
    // Only an explicit gesture interrupting application movement needs verified placement.
    private double? _requestedUserCut;
    private long? _verifyingUserCut;
    private WindowsLiveScrollIntent? _userCommitOwner;
    private int _userAlignmentRequests;
    private bool _holdingWheelRows;
    private Native.ScrollingBringingIntoViewEventArgs? _heldBring;
    private double _wheelDelta;
    private bool _wholeWheelDetents;
    private bool _wheelPosted;
    private long _wheelEpoch;
    private int _impulseDirection;
    private int? _wheelRequest;
    private bool _releaseWheelAnchor;
    private bool _anchoringEvictedTail;
    private bool _observeTailAfterTimeout;
    private long _scrollViewToken;
    private long _presenterToken;
    private long? _observeAfterLayout;
    private CallItem? _resizedWindowsAnchor;
    private long? _accessibilityGeneration;
    private bool _watchingRestoreFrames;
    private bool _physicalMoved;
    private bool _idleInputPosted;
    private long _inputBoundaryGeneration;
    private double? _savedRepeaterOpacity;

    private void ConnectNativeInput(bool replacedTemplate = false)
    {
        if (!_reader.IsVisible || _list.Handler?.PlatformView is not LiveItemsView root ||
            ReferenceEquals(_windowsList, root))
            return;
        DisconnectNativeInput();
        _windowsList = root;
        _windowsSource = ((LiveCollectionView)_list).ReadingRows;
        _windowsIntent = new();
        root.Loaded += OnWindowsLoaded;
        _scrollViewToken = root.RegisterPropertyChangedCallback(Native.ItemsView.ScrollViewProperty, OnWindowsSurfaceChanged);
        root.AddHandler(UIElement.PreviewKeyDownEvent, _routedInput.PreviewKey, true);
        RequestWindowsLayout();
        if (_reader.ResolveAnchor() is { } anchor)
            _windowsRestore = new(anchor.Card, anchor.Top, ReferenceEquals(_revealWhenConnected, anchor.Card), _windowsIntent.Generation)
                { RequiresFreshSeed = replacedTemplate };
        else
            _followWhenReady = _reader.IsFollowing;
        _revealWhenConnected = null;
        // A projection can reconnect inside its previous native submission. Its live
        // presenter already accepts input even while the outer layout is unwinding.
        if (_processingWindowsLayout)
            AttachWindowsSurface();
        else
            ProcessWindowsLayout();
    }

    private void DisconnectNativeInput()
    {
        InvalidateWindowsAnimation();
        _windowsIntent?.Retire();
        _windowsIntent = null;
        if (_windowsList is { } root)
        {
            root.UnregisterPropertyChangedCallback(Native.ItemsView.ScrollViewProperty, _scrollViewToken);
            root.Loaded -= OnWindowsLoaded;
            root.RemoveHandler(UIElement.PreviewKeyDownEvent, _routedInput.PreviewKey);
            root.LayoutUpdated -= OnWindowsLayout;
        }
        if (_windowsPresenter is { } presenter)
        {
            presenter.ScrollCompleted -= OnWindowsPresenterScrollCompleted;
            presenter.AccessibilityStarting -= OnWindowsAccessibilityStarting;
            presenter.AccessibilitySubmitted -= OnWindowsAccessibilitySubmitted;
        }
        if (_windowsBar is { } bar)
        {
            bar.ScrollByRequested -= OnBarScrollBy;
            bar.ScrollToRequested -= OnBarScrollTo;
            bar.AddScrollVelocityRequested -= OnBarScrollVelocity;
            _windowsBar = null;
        }
        if (_windowsView is { } view)
        {
            view.UnregisterPropertyChangedCallback(Native.ScrollView.ScrollPresenterProperty, _presenterToken);
            view.ScrollAnimationStarting -= OnAppAnimationStarting;
            view.AnchorRequested -= OnWindowsAnchorRequested;
            view.ViewChanged -= OnWindowsViewChanged;
            view.StateChanged -= OnWindowsStateChanged;
            view.BringingIntoView -= OnWindowsBringingIntoView;
            view.RemoveHandler(UIElement.PointerWheelChangedEvent, _routedInput.Wheel);
            view.RemoveHandler(UIElement.PointerPressedEvent, _routedInput.Press);
            view.RemoveHandler(UIElement.KeyDownEvent, _routedInput.Key);
            view.RemoveHandler(UIElement.KeyUpEvent, _routedInput.KeyUp);
            view.RemoveHandler(UIElement.PointerReleasedEvent, _routedInput.Release);
            view.RemoveHandler(UIElement.PointerCaptureLostEvent, _routedInput.Release);
        }
        if (_anchoringEvictedTail && _windowsView is { } retiredView)
            retiredView.VerticalAnchorRatio = 0;
        _anchoringEvictedTail = false;
        _windowsSource?.Complete();
        _windowsSource = null;
        EndWindowsRestoreRendering();
        _windowsList = null;
        _windowsView = null;
        ClearWindowsWheelAnchor();
        _windowsPresenter = null;
        _windowsRepeater = null;
        _windowsRestore = null;
        _physicalMoved = _idleInputPosted = false;
        _observeAfterLayout = _accessibilityGeneration = null;
        _resizedWindowsAnchor = null;
        _followWhenReady = _explicitWindowsFollow = _windowsLayoutSubscribed = false;
    }

    private void OnWindowsLoaded(object sender, RoutedEventArgs e) => WindowsLayoutChanged();

    private void OnWindowsSurfaceChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_windowsList is not { } root || _windowsView is not { } view || _windowsIntent is not { } intent ||
            (ReferenceEquals(root.ScrollView, view) && ReferenceEquals(view.ScrollPresenter, _windowsPresenter)))
            return;
        // WinUI can replace either template part without changing the MAUI handler. Reject old
        // callbacks immediately and attach once the replacement template has finished applying.
        intent.Retire();
        _windowsIntent = null;
        if (!root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            if (!ReferenceEquals(_windowsList, root) || _windowsIntent != null)
                return;
            DisconnectNativeInput();
            ConnectNativeInput(replacedTemplate: true);
        }))
            DisconnectNativeInput();
    }

    private bool AttachWindowsSurface()
    {
        if (_windowsView != null)
            return true;
        if (_windowsList is not { IsLoaded: true } root)
            return false;
        root.ApplyTemplate();
        if (root.ScrollView is not { } view)
            return false;
        view.ApplyTemplate();
        if (view.ScrollPresenter is not LiveScrollPresenter presenter ||
            FindWindowsDescendant<Native.ItemsRepeater>(presenter) is not { } repeater)
            return false;
        _windowsView = view;
        _windowsPresenter = presenter;
        _windowsRepeater = repeater;
        _presenterToken = view.RegisterPropertyChangedCallback(Native.ScrollView.ScrollPresenterProperty, OnWindowsSurfaceChanged);
        UpdateWindowsAnchorPolicy();
        _windowsBar = presenter.VerticalScrollController;
        if (_windowsBar is { } bar)
        {
            bar.ScrollByRequested += OnBarScrollBy;
            bar.ScrollToRequested += OnBarScrollTo;
            bar.AddScrollVelocityRequested += OnBarScrollVelocity;
        }
        presenter.ScrollCompleted += OnWindowsPresenterScrollCompleted;
        presenter.AccessibilityStarting += OnWindowsAccessibilityStarting;
        presenter.AccessibilitySubmitted += OnWindowsAccessibilitySubmitted;
        view.ScrollAnimationStarting += OnAppAnimationStarting;
        view.AnchorRequested += OnWindowsAnchorRequested;
        view.ViewChanged += OnWindowsViewChanged;
        view.StateChanged += OnWindowsStateChanged;
        view.BringingIntoView += OnWindowsBringingIntoView;
        view.AddHandler(UIElement.PointerWheelChangedEvent, _routedInput.Wheel, true);
        view.AddHandler(UIElement.PointerPressedEvent, _routedInput.Press, true);
        view.AddHandler(UIElement.KeyDownEvent, _routedInput.Key, true);
        view.AddHandler(UIElement.KeyUpEvent, _routedInput.KeyUp, true);
        view.AddHandler(UIElement.PointerReleasedEvent, _routedInput.Release, true);
        view.AddHandler(UIElement.PointerCaptureLostEvent, _routedInput.Release, true);
        return true;
    }

    private static T? FindWindowsDescendant<T>(DependencyObject parent) where T : class
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindWindowsDescendant<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }

    private void CancelWindowsWork()
    {
        _revealWhenConnected = null;
        _windowsIntent?.Cancel();
        AbandonWindowsRestore();
    }

    private void AbandonWindowsRestore(bool continuingWheel = false)
    {
        InvalidateWindowsAnimation();
        if (!continuingWheel)
            ClearWindowsWheelAnchor();
        _anchoringEvictedTail = false;
        _observeTailAfterTimeout = false;
        UpdateWindowsAnchorPolicy();
        _windowsRestore = null;
        _followWhenReady = _explicitWindowsFollow = false;
        _physicalMoved = false;
        _observeAfterLayout = _accessibilityGeneration = null;
        _resizedWindowsAnchor = null;
        if (_reader.IsVisible && !_holdingWheelRows && _windowsSource is { IsComplete: false } source)
        {
            source.Complete();
            _windowsView?.UpdateLayout();
        }
        EndWindowsRestoreRendering();
    }

    private void UpdateWindowsAnchorPolicy()
    {
        if (_windowsView is not { } view)
            return;
        // At offset zero WinUI pins the leading edge. An explicit reader there needs
        // row anchoring instead. Away from the edge, retain the normal leading cut
        // so changing a clipped transcript's height keeps its top in place.
        var ratio = 0d;
        if (!_reader.IsFollowing)
        {
            if (_anchoringEvictedTail)
                ratio = 1;
            else if (_wheelAnchor != null || view.VerticalOffset <= LiveReadingState.TopTolerance)
                ratio = 1 / Math.Max(2, view.ViewportHeight);
        }
        if (view.VerticalAnchorRatio != ratio)
            view.VerticalAnchorRatio = ratio;
    }

    private void ObserveWindowsTailCut()
    {
        if (!_anchoringEvictedTail || _windowsRestore is { SeekingTail: false } || _windowsIntent is not { } intent ||
            _windowsView is not { } view || _windowsSource is not { IsComplete: true, Count: > 0 } rows ||
            rows.Count != _reader.Cards.Count || !ReferenceEquals(rows[^1], _reader.Cards[^1]))
            return;
        var index = rows.Count - 1;
        var card = rows[index];
        if (IsBoundWindowsCard(index, card) && MeasureWindowsCard(index) is { } bounds &&
            bounds.Top < view.ViewportHeight && bounds.Top + bounds.Height > 0)
        {
            // Eviction supplied an identity without a cut. Keep its measured cut current
            // as WinUI refines the extent of variable-height rows at the trailing edge.
            if (_windowsRestore is { } restoring)
            {
                if (restoring.WaitingFor.HasValue)
                {
                    intent.Cancel();
                    InvalidateWindowsAnimation();
                }
                _windowsRestore = null;
                EndWindowsRestoreRendering();
            }
            _observeTailAfterTimeout = false;
            _reader.RememberPosition(card, bounds.Top);
        }
        else if (!_observeTailAfterTimeout && _windowsRestore == null &&
            _reader.ResolveAnchor() is { } anchor && ReferenceEquals(anchor.Card, card))
        {
            _windowsRestore = new(card, null, false, intent.Generation) { SeekingTail = true };
            WatchWindowsRestoreFrames(hideSeed: false);
        }
    }

    private void FollowWindowsLive()
    {
        if (!_reader.IsVisible)
            return;
        UpdateWindowsAnchorPolicy();
        _followWhenReady = true;
        _explicitWindowsFollow = true;
        RequestWindowsLayout();
    }

    private void RevealWindowsCard(CallItem card)
    {
        if (!_reader.IsVisible || _windowsIntent is not { } intent)
        {
            _revealWhenConnected = card;
            return;
        }
        UpdateWindowsAnchorPolicy();
        _windowsRestore = new(card, null, true, intent.Generation);
        RequestWindowsLayout();
    }

    private void WindowsCardsChanged(NotifyCollectionChangedEventArgs change)
    {
        if (_windowsIntent is not { } intent)
            return;
        if (_wheelAnchor is { } wheelCard && _reader.IndexOf(wheelCard) < 0)
        {
            ClearWindowsWheelAnchor();
            UpdateWindowsAnchorPolicy();
        }
        if (_anchoringEvictedTail && _reader.ResolveAnchor()?.Index != _reader.Cards.Count - 1)
        {
            _anchoringEvictedTail = false;
            UpdateWindowsAnchorPolicy();
        }
        if (change.Action == NotifyCollectionChangedAction.Reset || _reader.Cards.Count == 0)
        {
            CancelWindowsWork();
            _windowsSource?.Complete();
            if (_reader.Cards.Count > 0)
            {
                if (_reader.ResolveAnchor() is { } anchor)
                    _windowsRestore = new(anchor.Card, anchor.Top, false, intent.Generation);
                else
                    _followWhenReady = _reader.IsFollowing;
            }
            RequestWindowsLayout();
            return;
        }
        if (_windowsRestore is { } restoring && _reader.IndexOf(restoring.Card) < 0)
        {
            if (restoring.SeekingTail && _reader.ResolveAnchor() is { } tail && tail.Index == _reader.Cards.Count - 1)
            {
                // The identity at the end changed while a native end request was in flight.
                // The request still has the same destination and navigation owner.
                restoring.Card = tail.Card;
                RequestWindowsLayout();
                return;
            }
            if (_windowsSource is { IsComplete: false } suffix &&
                _reader.ResolveAnchor() is { } adjacent && adjacent.Index == _reader.Cards.Count - 1)
            {
                intent.Cancel();
                InvalidateWindowsAnimation();
                _anchoringEvictedTail = true;
                UpdateWindowsAnchorPolicy();
                _windowsRestore = new(adjacent.Card, null, false, intent.Generation)
                {
                    SeekingTail = true, Staging = true, TailWindow = Math.Max(1, suffix.Count),
                    SeedNeedsFrame = true
                };
                WatchWindowsRestoreFrames(hideSeed: true);
                return;
            }
            CancelWindowsWork();
            if (_reader.ResolveAnchor() is { } replacement)
                _windowsRestore = new(replacement.Card, replacement.Top, false, intent.Generation);
            RequestWindowsLayout();
        }
        else if (_anchoringEvictedTail && _windowsRestore == null)
        {
            RequestWindowsLayout();
        }
        else if (!intent.IsPhysical && intent.PendingOperations == 0 && !_observeAfterLayout.HasValue && _windowsRestore == null &&
            _reader.ResolveAnchor() is { Top: null } survivor)
        {
            // The reader chooses the surviving identity after retention or a membership edit.
            // WinUI first gets its layout pass to keep that element on screen.
            _windowsRestore = new(survivor.Card, null, false, intent.Generation);
            RequestWindowsLayout();
        }
    }

    private void WindowsCardSizeChanged(CallItem card)
    {
        if (_windowsIntent is not { IsPhysical: false, PendingOperations: 0 } ||
            _windowsRestore != null || _observeAfterLayout.HasValue ||
            _reader.ResolveAnchor() is not { Top: < 0 } anchor || !ReferenceEquals(anchor.Card, card))
            return;
        _resizedWindowsAnchor = card;
        RequestWindowsLayout();
    }

    private void WindowsLayoutChanged()
    {
        if (_windowsList == null)
            ConnectNativeInput();
        if (_windowsView == null || _windowsRestore != null || _followWhenReady || _observeAfterLayout.HasValue)
            RequestWindowsLayout();
    }

    private void RequestWindowsLayout()
    {
        if (_windowsList is not { } root || _windowsLayoutSubscribed)
            return;
        _windowsLayoutSubscribed = true;
        root.LayoutUpdated += OnWindowsLayout;
        root.InvalidateArrange();
    }

    private void OnWindowsLayout(object? sender, object e) => ProcessWindowsLayout();

    private void ProcessWindowsLayout()
    {
        if (_processingWindowsLayout || !_reader.IsVisible || _windowsIntent is not { } intent)
            return;
        _processingWindowsLayout = true;
        try
        {
            if (!AttachWindowsSurface() || _windowsView is not { ViewportHeight: > 0, ViewportWidth: > 0 } view)
                return;
            // Retain the latest logical request when the native dispatch queue is full.
            // A dispatch/terminal makes room; unrelated presenter input stays available.
            var canSubmit = _windowsPresenter?.AppAnimations.Queue.HasRoom == true;
            if (_followWhenReady && canSubmit)
            {
                var submit = _explicitWindowsFollow || view.VerticalOffset > LiveReadingState.TopTolerance;
                _followWhenReady = _explicitWindowsFollow = false;
                // An older accepted WinUI operation can still move a presenter whose
                // current offset is zero. A deliberate Live command supersedes it natively.
                if (_reader.IsFollowing && submit)
                    ScrollWindowsTo(0, intent.Generation);
            }
            if (_resizedWindowsAnchor is { } resized)
            {
                _resizedWindowsAnchor = null;
                if (_reader.ResolveAnchor() is { Top: { } cut } saved && ReferenceEquals(saved.Card, resized) &&
                    MeasureWindowsCard(saved.Index) is { } bounds && cut + bounds.Height <= LiveReadingState.PixelTolerance)
                {
                    if (bounds.Top + bounds.Height > 0 && bounds.Top < view.ViewportHeight)
                        _reader.RememberPosition(resized, bounds.Top);
                    else
                        _windowsRestore = new(resized, 0, false, intent.Generation);
                }
            }
            if (canSubmit && _windowsRestore is { WaitingFor: null } restore && intent.IsCurrent(restore.Generation))
                AdvanceWindowsRestore(restore);
            if (_anchoringEvictedTail)
                ObserveWindowsTailCut();
            if (_observeAfterLayout is { } generation && intent.IsCurrent(generation) && SampleWindowsReader())
                _observeAfterLayout = null;
            if (_releaseWheelAnchor)
            {
                ClearWindowsWheelAnchor();
                UpdateWindowsAnchorPolicy();
            }
            if (!_anchoringEvictedTail && _windowsRestore is not { WaitingFor: null } && !_followWhenReady && !_observeAfterLayout.HasValue &&
                _windowsList is { } root && _windowsLayoutSubscribed)
            {
                root.LayoutUpdated -= OnWindowsLayout;
                _windowsLayoutSubscribed = false;
            }
        }
        finally
        {
            _processingWindowsLayout = false;
        }
    }

    // Eviction replaced a bookmark before its short suffix could be published. Keep
    // surviving containers, place the new trailing card, then publish its missing prefix.
    private void AdvanceWindowsTailWindow(WindowsRestore restore, LiveReadingSource rows, Native.ScrollView view)
    {
        if (rows.Count == 0 || !ReferenceEquals(rows[^1], restore.Card))
        {
            rows.SynchronizeTail(restore.TailWindow);
            restore.SeedNeedsFrame = true;
            WatchWindowsRestoreFrames(hideSeed: true);
            return;
        }
        var index = rows.Count - 1;
        if (view.ScrollableHeight <= LiveReadingState.TopTolerance && rows.ExtendSeed())
        {
            restore.TailWindow = rows.Count;
            restore.SeedNeedsFrame = true;
            WatchWindowsRestoreFrames(hideSeed: true);
            return;
        }
        var remaining = view.ScrollableHeight - view.VerticalOffset;
        if (remaining > LiveReadingState.PixelTolerance && restore.AlignmentRequests < MaximumAlignmentRequests)
        {
            restore.AlignmentRequests++;
            restore.RequestedOffset = view.ScrollableHeight;
            var id = ScrollWindowsTo(restore.RequestedOffset, restore.Generation);
            if (id >= 0)
                restore.WaitingFor = id;
            WatchWindowsRestoreFrames(hideSeed: true);
            return;
        }
        if (IsBoundWindowsCard(index, restore.Card) && MeasureWindowsCard(index) is { } bounds &&
            bounds.Top < view.ViewportHeight && bounds.Top + bounds.Height > 0 &&
            Math.Abs(remaining) <= LiveReadingState.PixelTolerance)
        {
            restore.TailWindow = 0;
            restore.Staging = restore.RequiresFreshSeed = false;
            restore.SeedNeedsFrame = restore.VerifyNextFrame = true;
            restore.AlignmentRequests = 0;
            rows.Complete();
            _windowsList?.InvalidateArrange();
            WatchWindowsRestoreFrames(hideSeed: false);
            return;
        }
        restore.SeedNeedsFrame = true;
        WatchWindowsRestoreFrames(hideSeed: true);
    }

    private void AdvanceWindowsRestore(WindowsRestore restore)
    {
        var view = _windowsView!;
        if (view.State == Native.ScrollingInteractionState.Animation)
            return; // WinUI suspends layout anchoring during an active application move.
        var canonicalIndex = _reader.IndexOf(restore.Card);
        if (canonicalIndex < 0)
        {
            _windowsRestore = null;
            _windowsSource?.Complete();
            EndWindowsRestoreRendering();
            return;
        }
        if (restore.SeedNeedsFrame || (_windowsSource is { IsComplete: false } source &&
            source.Count != _windowsRepeater?.ItemsSourceView?.Count))
            return;
        if (restore.TailWindow > 0 && _windowsSource is { IsComplete: false } suffix)
        {
            AdvanceWindowsTailWindow(restore, suffix, view);
            return;
        }
        var localIndex = _windowsSource?.IndexOf(restore.Card) ?? canonicalIndex;
        // Containers reparented into a new presenter briefly retain transforms from the old one.
        // Rebuild the seed even when those old containers still look realized.
        var bounds = restore.RequiresFreshSeed && !restore.Staging ? null : MeasureWindowsCard(localIndex);
        if (_windowsSource is not { } rows || localIndex < 0 || localIndex >= rows.Count ||
            !ReferenceEquals(rows[localIndex], restore.Card))
            bounds = null;
        // An evicted oldest card has no saved cut. A complete projection can realize
        // its last survivor directly at the native end, without replacing the viewport
        // with a suffix whose short, temporary extent can resemble a completed layout.
        if (!restore.Center && restore.Top == null && canonicalIndex == _reader.Cards.Count - 1 &&
            _windowsSource is { IsComplete: true })
        {
            _anchoringEvictedTail = true;
            restore.SeekingTail = true;
            UpdateWindowsAnchorPolicy();
            if (bounds is not { } tail || tail.Top >= view.ViewportHeight || tail.Top + tail.Height <= 0)
            {
                if (view.ScrollableHeight - view.VerticalOffset > LiveReadingState.PixelTolerance)
                {
                    if (++restore.AlignmentRequests > MaximumAlignmentRequests)
                    {
                        ReleaseUnplacedWindowsAnchor();
                        return;
                    }
                    restore.VerifyNextFrame = true;
                    restore.RequestedOffset = view.ScrollableHeight;
                    var id = ScrollWindowsTo(restore.RequestedOffset, restore.Generation);
                    if (id >= 0)
                        restore.WaitingFor = id;
                }
                WatchWindowsRestoreFrames(hideSeed: false);
                return;
            }
        }
        if (bounds == null)
        {
            if (!restore.Staging)
            {
                WatchWindowsRestoreFrames(hideSeed: true);
                // Centered navigation parks the retained handler before replacing its source.
                // For a clipped bookmark, first shorten the mirror so high-rate tail evictions
                // cannot race a full-length jump back to the old origin.
                if (restore.Center && !restore.AtSeed && view.VerticalOffset > LiveReadingState.TopTolerance)
                {
                    restore.AtSeed = true;
                    restore.RequestedOffset = 0;
                    var origin = ScrollWindowsTo(0, restore.Generation);
                    if (origin >= 0)
                    {
                        restore.WaitingFor = origin;
                        return;
                    }
                }
                restore.Staging = true;
                restore.SeedNeedsFrame = true;
                _windowsSource!.SeedAt(Math.Max(0, canonicalIndex - 1));
            }
            else if (!restore.AtSeed && view.VerticalOffset > LiveReadingState.TopTolerance)
            {
                restore.AtSeed = true;
                restore.RequestedOffset = 0;
                var id = ScrollWindowsTo(0, restore.Generation);
                if (id >= 0)
                    restore.WaitingFor = id;
            }
            return;
        }

        var (top, height) = bounds.Value;
        var staged = _windowsSource is { IsComplete: false };
        // A native offset only establishes that a request ran. Variable-height
        // containers can move in the same layout. Re-measure the canonical card.
        var desired = staged ? Math.Min(0, restore.Top ?? 0) : restore.Top ?? 0;
        if (restore.Center)
            desired = (view.ViewportHeight - height) / 2;
        else if (!staged && !restore.Center && !restore.Top.HasValue && top < view.ViewportHeight && top + height > 0)
            desired = top;
        else if (height + desired <= LiveReadingState.PixelTolerance || desired >= view.ViewportHeight)
            desired = 0;
        var offset = Math.Clamp(view.VerticalOffset + top - desired, 0, view.ScrollableHeight);
        if ((!staged || !restore.PositionSubmitted) && Math.Abs(offset - view.VerticalOffset) > LiveReadingState.PixelTolerance)
        {
            // Bounds under ordinary insertions belong to WinUI. This budget only
            // applies to an explicit navigation or a saved-bookmark reconstruction.
            if (++restore.AlignmentRequests > MaximumAlignmentRequests)
            {
                ReleaseUnplacedWindowsAnchor();
                return;
            }
            restore.VerifyNextFrame = true;
            restore.PositionSubmitted = true;
            restore.RequestedOffset = offset;
            WatchWindowsRestoreFrames(hideSeed: staged);
            var id = ScrollWindowsTo(offset, restore.Generation);
            if (id >= 0)
            {
                restore.WaitingFor = id;
                return;
            }
        }
        if (staged)
        {
            // A short suffix near the oldest call may not fill a viewport yet. Preserve the
            // target container and give it room above until native scrolling is possible.
            if (view.VerticalOffset <= LiveReadingState.TopTolerance && _windowsSource!.ExtendSeed())
            {
                restore.PositionSubmitted = false;
                restore.SeedNeedsFrame = true;
                WatchWindowsRestoreFrames(hideSeed: true);
                return;
            }
            // Give WinUI a centered, realized anchor before publishing the missing prefix.
            if (restore.Center)
                WindowsPublishingCenteredCard(top, height, view.ViewportHeight);
            _windowsSource!.Complete();
            view.UpdateLayout();
            // Centering uses all preceding items when a short seed could not fill half a viewport.
            restore.PositionSubmitted = false;
            restore.VerifyNextFrame = true;
            _windowsList?.InvalidateArrange();
            WatchWindowsRestoreFrames(hideSeed: false);
            return;
        }
        if (restore.VerifyNextFrame)
        {
            restore.VerifyNextFrame = false;
            restore.SeedNeedsFrame = true;
            WatchWindowsRestoreFrames(hideSeed: false);
            return;
        }
        if (top >= view.ViewportHeight || top + height <= 0)
        {
            // A clamped offset cannot prove that stale container geometry is usable.
            ReleaseUnplacedWindowsAnchor();
            return;
        }
        // The ItemsView exposes MAUI's item wrapper. Verify the bound card in this
        // exact recycled row before committing its geometry to the bookmark.
        if (!IsBoundWindowsCard(localIndex, restore.Card))
        {
            restore.SeedNeedsFrame = true;
            WatchWindowsRestoreFrames(hideSeed: false);
            return;
        }
        _reader.RememberPosition(restore.Card, top);
        _windowsRestore = null;
        EndWindowsRestoreRendering();
    }

    private void WatchWindowsRestoreFrames(bool hideSeed)
    {
        if (hideSeed && _savedRepeaterOpacity == null && _windowsRepeater is { } repeater)
        {
            // The presenter stays available for real wheel, keyboard and accessibility input.
            // Only the temporary suffix is withheld from composition until its cut is ready.
            _windowsList?.SetReconstructing(true);
            _savedRepeaterOpacity = repeater.Opacity;
            repeater.Opacity = 0;
        }
        if (_restoreWatchdog == null && _windowsView is { } view)
        {
            // This deadline only releases an unresponsive presentation transaction.
            // It never acknowledges scrolls, grants input ownership or reissues a move.
            _restoreWatchdog = view.DispatcherQueue.CreateTimer();
            _restoreWatchdog.IsRepeating = false;
            _restoreWatchdog.Interval = TimeSpan.FromSeconds(2);
            _restoreWatchdog.Tick += OnWindowsRestoreUnresponsive;
            _restoreWatchdog.Start();
        }
        if (_watchingRestoreFrames)
            return;
        _watchingRestoreFrames = true;
        CompositionTarget.Rendering += OnWindowsRestoreFrame;
    }

    private void EndWindowsRestoreRendering()
    {
        if (_watchingRestoreFrames)
        {
            CompositionTarget.Rendering -= OnWindowsRestoreFrame;
            _watchingRestoreFrames = false;
        }
        if (_restoreWatchdog is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnWindowsRestoreUnresponsive;
            _restoreWatchdog = null;
        }
        if (_savedRepeaterOpacity is { } opacity && _windowsRepeater is { } repeater)
            repeater.Opacity = opacity;
        if (_savedRepeaterOpacity.HasValue)
            _windowsList?.SetReconstructing(false);
        _savedRepeaterOpacity = null;
    }

    private void OnWindowsRestoreFrame(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnWindowsRestoreFrame;
        _watchingRestoreFrames = false;
        if (_windowsRestore is not { } restore || _windowsIntent?.IsCurrent(restore.Generation) != true)
            return;
        restore.SeedNeedsFrame = false;
        VerifyWindowsRestorePosition();
        ProcessWindowsLayout();
    }

    private void VerifyWindowsRestorePosition()
    {
        // A still-current operation can reach its measured layout before its terminal.
        // Only native input and request IDs can change reader intent.
        if (_windowsRestore is { WaitingFor: { } appId } restore && _windowsIntent is { } intent &&
            intent.IsCurrent(restore.Generation) && _windowsView is { } view &&
            view.State != Native.ScrollingInteractionState.Animation &&
            Math.Abs(view.VerticalOffset - restore.RequestedOffset) <= LiveReadingState.PixelTolerance)
        {
            // Consume the known application ID as well as its presentation continuation.
            // A duplicate terminal can no longer block subsequent layout work or observe input.
            intent.RetireApplication(restore.Generation, appId);
            restore.WaitingFor = null;
            RequestWindowsLayout();
        }
    }

    private void OnWindowsRestoreUnresponsive(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
    {
        if (!ReferenceEquals(timer, _restoreWatchdog) || _windowsRestore is not { } restore ||
            _windowsIntent is not { } intent || !intent.IsCurrent(restore.Generation))
            return;
        ReleaseUnplacedWindowsAnchor();
    }

    // Preserve the logical target and requested cut for the next real attachment;
    // the complete list remains available for the reader to navigate immediately.
    private void ReleaseUnplacedWindowsAnchor()
    {
        AppLog.DebugWriteLine(() => "Live reading released the full projection before WinUI supplied the requested layout.");
        var parked = _windowsRestore is { SeekingTail: true } && _windowsSource is { IsComplete: true };
        _windowsIntent?.Cancel();
        AbandonWindowsRestore();
        if (parked)
        {
            // A native end request may finish after the safety release. Observe a
            // subsequently realized survivor without restarting a seek from the deadline.
            _anchoringEvictedTail = true;
            _observeTailAfterTimeout = true;
            UpdateWindowsAnchorPolicy();
            RequestWindowsLayout();
        }
    }

    private int ScrollWindowsTo(double offset, long generation)
    {
        if (_windowsIntent is not { } intent || !intent.IsCurrent(generation) ||
            _windowsPresenter is not { } presenter || !presenter.AppAnimations.Queue.HasRoom || _windowsView is not { } view)
            return -1;
        var id = view.ScrollTo(view.HorizontalOffset, offset, new Native.ScrollingScrollOptions(
            Native.ScrollingAnimationMode.Enabled, Native.ScrollingSnapPointsMode.Ignore));
        intent.SubmittedApplication(generation, id);
        presenter.AppAnimations.Queue.Add(id, intent);
        WindowsAppScrollSubmitted(id);
        return id;
    }

    private void InvalidateWindowsAnimation() => _windowsPresenter?.AppAnimations.Invalidate();

    private void OnAppAnimationStarting(Native.ScrollView sender, Native.ScrollingScrollAnimationStartingEventArgs args)
    {
        if (ReferenceEquals(sender, _windowsView) && (_followWhenReady || _windowsRestore is { WaitingFor: null }))
            RequestWindowsLayout();
    }

    partial void WindowsAppScrollSubmitted(int id);
    partial void WindowsPublishingCenteredCard(double top, double height, double viewportHeight);

    private void OnWindowsBringingIntoView(Native.ScrollView sender, Native.ScrollingBringingIntoViewEventArgs args)
    {
        if (!ReferenceEquals(sender, _windowsView) || _windowsRepeater is not { } repeater || args.Cancel)
            return;
        // ScrollItem.ScrollIntoView and native focus navigation target a concrete row.
        // Our own operations use ScrollTo; native layout anchoring does not request a row.
        for (var row = args.RequestEventArgs.TargetElement as UIElement; row != null;
            row = VisualTreeHelper.GetParent(row) as UIElement)
        {
            if (!ReferenceEquals(VisualTreeHelper.GetParent(row), repeater))
                continue;
            var index = repeater.GetElementIndex(row);
            // A held full mirror still contains visible canonical rows. A genuine
            // ScrollItem or focus request for one of them supersedes the wheel handoff.
            if (_windowsSource is not { } rows || index < 0 || index >= rows.Count ||
                !ReferenceEquals(repeater.TryGetElement(index), row) || (!rows.IsComplete && !_holdingWheelRows))
            {
                args.Cancel = true;
                return;
            }
            if (_holdingWheelRows)
            {
                var card = rows[index];
                if (_windowsIntent is not { } intent || _reader.IndexOf(card) < 0 ||
                    !IsBoundWindowsCard(index, card) || MeasureWindowsCard(index) is not { } bounds)
                {
                    args.Cancel = true;
                    return;
                }
                // Let the original Bring operate on the same published mirror and preserve
                // its native ID. A same-turn newer input can still cancel these event args
                // before WinUI queues the physical operation.
                CancelHeldBring();
                _accessibilityGeneration = intent.BeginAccessibility();
                _wheelEpoch++;
                _wheelPosted = false;
                _wheelDelta = 0;
                _wheelRequest = null;
                _releaseWheelAnchor = false;
                _wheelAnchor = card;
                _wheelOriginCut = bounds.Top + sender.VerticalOffset - args.TargetVerticalOffset;
                _requestedUserCut = _wheelOriginCut;
                _userAlignmentRequests = 0;
                if (Math.Abs(args.TargetVerticalOffset - sender.VerticalOffset) <= LiveReadingState.PixelTolerance ||
                    args.CorrelationId < 0)
                {
                    args.Cancel = true;
                    QueueWindowsWheel(sender);
                }
                else
                {
                    _heldBring = args;
                    _windowsPresenter!.AppAnimations.AwaitUserTerminal(args.CorrelationId, FinishHeldBring);
                    OnWindowsAccessibilitySubmitted(args.CorrelationId);
                }
                return;
            }
            OnWindowsAccessibilityStarting();
            OnWindowsAccessibilitySubmitted(args.CorrelationId);
            return;
        }
    }

    private void OnWindowsPresenterScrollCompleted(NativeScroll.ScrollPresenter sender, Native.ScrollingScrollCompletedEventArgs args)
        => CompleteWindowsScroll(sender, args.CorrelationId);

    private void CompleteWindowsScroll(NativeScroll.ScrollPresenter sender, int id)
    {
        // Correlation IDs belong to a presenter. A retemplate can keep the outer ScrollView.
        if (!ReferenceEquals(sender, _windowsPresenter) || _windowsIntent is not { } intent)
            return;
        if (_wheelRequest == id)
        {
            _wheelRequest = null;
            // A later detent may have arrived before its high-priority native submission.
            // The terminal identifies only the previous impulse.
            if (!_wheelPosted && !_holdingWheelRows && _wheelDelta == 0)
            {
                if (_requestedUserCut.HasValue && _windowsView is { } view)
                {
                    intent.AcceptAccessibilityCompletion(id);
                    _ = VerifyWindowsUserPlacementAsync(view, intent, intent.Generation);
                    return;
                }
                _releaseWheelAnchor = true;
                RequestWindowsLayout();
            }
        }
        if (intent.AcceptAccessibilityCompletion(id) && !_holdingWheelRows)
            ObserveWindowsAfterLayout(intent.Generation);
        else if (_followWhenReady)
            RequestWindowsLayout();
        else if (_windowsRestore is { } restore && restore.WaitingFor == id && intent.IsCurrent(restore.Generation))
        {
            restore.WaitingFor = null;
            RequestWindowsLayout();
        }
    }

    private void OnWindowsAccessibilityStarting()
    {
        if (_windowsIntent is not { } intent)
            return;
        var generation = intent.BeginAccessibility();
        AbandonWindowsRestore();
        _accessibilityGeneration = generation;
    }

    private void OnWindowsAccessibilitySubmitted(int id)
    {
        if (_windowsIntent is not { } intent || _accessibilityGeneration is not { } generation ||
            !intent.IsCurrent(generation))
            return;
        intent.SubmittedAccessibility(generation, id);
        if (id < 0)
            ObserveWindowsAfterLayout(generation);
    }

    private void BeginWindowsPhysical()
    {
        if (_windowsIntent is not { } intent || intent.IsPhysical)
            return;
        intent.BeginPhysical();
        AbandonWindowsRestore();
        _physicalMoved = false;
    }

    private void OnWindowsWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _windowsView) || _windowsView is not { ScrollableHeight: > 0 } view ||
            _windowsIntent is not { } intent || e.Handled || e.KeyModifiers != Windows.System.VirtualKeyModifiers.None)
            return;
        var point = e.GetCurrentPoint(view).Properties;
        if (point.IsHorizontalMouseWheel || point.MouseWheelDelta == 0 ||
            !ReadMouseWheelSettings(GetWheelScrollLines, 0, out var lines, 0) || lines == 0)
            return;
        var delta = WindowsLiveScrollIntent.WheelDistance(point.MouseWheelDelta, lines, view.ViewportHeight);
        MoveWindowsWheel(view, intent, delta, point.MouseWheelDelta % 120 == 0);
        e.Handled = true;
    }

    private void MoveWindowsWheel(Native.ScrollView view, WindowsLiveScrollIntent intent, double delta, bool wholeDetent)
    {
        var interruptingApplication = _windowsPresenter is { } native &&
            (native.AppAnimations.IsDispatched || native.AppAnimations.Queue.HasPending || _explicitWindowsFollow);
        CancelHeldBring();
        var start = view.VerticalOffset;
        var stopAnimation = _windowsPresenter?.AppAnimations.IsDispatched == true && view.State == Native.ScrollingInteractionState.Animation;
        var generation = intent.BeginAccessibility();
        AbandonWindowsRestore(continuingWheel: true);
        _accessibilityGeneration = generation;
        _releaseWheelAnchor = false;
        if (_reader.IsFollowing)
        {
            if (start > LiveReadingState.TopTolerance)
                SampleWindowsReader();
            else if (delta > 0 && _reader.Cards.Count > 0)
            {
                // A genuine downward turn can precede both the first pixel and an admission.
                _reader.ReadFrom(_reader.Cards[0]);
                _reader.RememberPosition(_reader.Cards[0], 0);
            }
        }
        _wheelAnchor ??= _reader.ResolveAnchor()?.Card;
        UpdateWindowsAnchorPolicy();
        if (_userAlignmentRequests > 0 || _verifyingUserCut.HasValue)
            _requestedUserCut = _wheelAnchor is { } continuing && _windowsSource is { } currentRows
                ? MeasureWindowsCard(currentRows.IndexOf(continuing))?.Top : null;
        if (interruptingApplication && _requestedUserCut == null && _wheelAnchor is { } origin && _windowsSource is { } source)
            _requestedUserCut = MeasureWindowsCard(source.IndexOf(origin))?.Top;
        if (_requestedUserCut.HasValue)
            _requestedUserCut -= delta;
        _userAlignmentRequests = 0;
        if (stopAnimation && !_holdingWheelRows && _windowsSource is { IsComplete: true } rows)
        {
            // WinUI disables element anchoring during Animation. Keep this exact user
            // origin until the interrupted tracker returns to an anchorable state.
            _wheelOriginCut = _wheelAnchor is { } card ? MeasureWindowsCard(rows.IndexOf(card))?.Top : null;
            _holdingWheelRows = true;
            rows.DeferPublication();
            InterruptWindowsAnimation(view, start);
        }
        _wholeWheelDetents = _wheelDelta == 0 ? wholeDetent : _wholeWheelDetents && wholeDetent;
        _wheelDelta += delta;
        QueueWindowsWheel(view);
    }

    private void InterruptWindowsAnimation(Native.ScrollView view, double offset)
    {
        // The stock peer implements an immediate tracker update. Its public Scroll
        // pattern wrapper remains reserved for genuine accessibility navigation.
        if (_windowsPresenter is { IsLoaded: true } presenter && view.ScrollableHeight > 0 &&
            FrameworkElementAutomationPeer.CreatePeerForElement(presenter) is IScrollProvider native)
            native.SetScrollPercent(-1, Math.Clamp(offset / view.ScrollableHeight * 100, 0, 100));
    }

    private void QueueWindowsWheel(Native.ScrollView view)
    {
        if (_heldBring != null || _wheelPosted || (_holdingWheelRows && view.State == Native.ScrollingInteractionState.Animation))
            return;
        var epoch = _wheelEpoch;
        _wheelPosted = true;
        if (!view.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            if (epoch != _wheelEpoch || !ReferenceEquals(view, _windowsView))
                return;
            _wheelPosted = false;
            if (_holdingWheelRows && !PublishWindowsWheelRows(view))
                return;
            var delta = _wheelDelta;
            _wheelDelta = 0;
            if (delta != 0)
                SubmitWindowsWheel(view, delta);
            else if (!_wheelRequest.HasValue && _requestedUserCut.HasValue && _windowsIntent is { } owner)
                _ = VerifyWindowsUserPlacementAsync(view, owner, owner.Generation);
            else if (!_wheelRequest.HasValue)
            {
                ClearWindowsWheelAnchor();
                if (_windowsIntent is { } current)
                    ObserveWindowsAfterLayout(current.Generation);
            }
        }))
            ClearWindowsWheelAnchor();
    }

    private bool PublishWindowsWheelRows(Native.ScrollView view)
    {
        if (view.State == Native.ScrollingInteractionState.Animation)
            return false;
        _holdingWheelRows = false;
        var source = _windowsSource;
        view.InvalidateArrange();
        view.UpdateLayout();
        _ = view.CurrentAnchor;
        source?.Complete();
        view.UpdateLayout();
        // Stopping an in-flight virtualizing layout may change this row's measured cut
        // even on the former source. Reestablish the genuine input origin once, before
        // applying the signed detents. Ordinary insertions only use native anchoring.
        if (_wheelAnchor is { } card && _wheelOriginCut is { } expected && source != null &&
            MeasureWindowsCard(source.IndexOf(card)) is { } bounds &&
            Math.Abs(bounds.Top - expected) > LiveReadingState.PixelTolerance)
            InterruptWindowsAnimation(view, view.VerticalOffset + bounds.Top - expected);
        _wheelOriginCut = null;
        return true;
    }

    private async Task VerifyWindowsUserPlacementAsync(Native.ScrollView view, WindowsLiveScrollIntent intent,
        long generation)
    {
        _verifyingUserCut = generation;
        if (ReferenceEquals(_userCommitOwner, intent))
            return;
        _userCommitOwner = intent;
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(view).Compositor;
        try
        {
            await compositor.RequestCommitAsync();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException &&
            (!ReferenceEquals(view, _windowsView) || !intent.IsCurrent(generation)))
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_userCommitOwner, intent))
                _userCommitOwner = null;
        }
        if (!ReferenceEquals(intent, _windowsIntent) || !ReferenceEquals(view, _windowsView) ||
            _verifyingUserCut is not { } latest || !intent.IsCurrent(latest))
            return;
        if (generation != latest)
        {
            // Several newer commands can share one outstanding native commit. Observe
            // the latest after its own commit before measuring its canonical position.
            _ = VerifyWindowsUserPlacementAsync(view, intent, latest);
            return;
        }
        _verifyingUserCut = null;
        if (_requestedUserCut is { } cut && _wheelAnchor is { } card && _windowsSource is { IsComplete: true } rows)
        {
            var index = rows.IndexOf(card);
            if (index >= 0 && IsBoundWindowsCard(index, card) && MeasureWindowsCard(index) is { } bounds)
            {
                // Virtualized rows can be remeasured during a native move. Finish this
                // specific user request on its canonical element after the compositor commit.
                var target = Math.Clamp(view.VerticalOffset + bounds.Top - cut, 0, view.ScrollableHeight);
                var delta = target - view.VerticalOffset;
                if (Math.Abs(delta) > LiveReadingState.PixelTolerance && _userAlignmentRequests < MaximumAlignmentRequests)
                {
                    _userAlignmentRequests++;
                    var id = view.ScrollBy(0, delta, new Native.ScrollingScrollOptions(
                        Native.ScrollingAnimationMode.Disabled, Native.ScrollingSnapPointsMode.Ignore));
                    if (id >= 0)
                    {
                        _wheelRequest = id;
                        _impulseDirection = 0;
                        if (_windowsPresenter is { } presenter)
                            presenter.AppAnimations.AwaitUserTerminal(id, receipt => CompleteWindowsScroll(presenter, receipt));
                        OnWindowsAccessibilitySubmitted(id);
                        return;
                    }
                }
            }
        }
        ClearWindowsWheelAnchor();
        ObserveWindowsAfterLayout(generation);
    }

    private void SubmitWindowsWheel(Native.ScrollView view, double delta)
    {
        int id;
        if (_wholeWheelDetents && _uiSettings.AnimationsEnabled)
        {
            // WinUI ScrollView's native line-to-impulse conversion and inertia decay.
            var direction = Math.Sign(delta);
            var velocity = (direction - (_wheelRequest.HasValue ? _impulseDirection : 0)) * 30d +
                delta * 7.600855902349023;
            id = view.AddScrollVelocity(new System.Numerics.Vector2(0, (float)velocity),
                new System.Numerics.Vector2(.9995f, .9995f));
            _impulseDirection = direction;
        }
        else
            id = view.ScrollBy(0, delta, new Native.ScrollingScrollOptions(
                Native.ScrollingAnimationMode.Disabled, Native.ScrollingSnapPointsMode.Ignore));
        _wheelRequest = id >= 0 ? id : null;
        _releaseWheelAnchor = id < 0;
        OnWindowsAccessibilitySubmitted(id);
    }

    private void OnWindowsAnchorRequested(Native.ScrollView sender, Native.ScrollingAnchorRequestedEventArgs args)
    {
        if (!ReferenceEquals(sender, _windowsView) || _wheelAnchor is not { } card ||
            _windowsSource is not { } rows || _windowsRepeater is not { } repeater)
            return;
        var index = rows.IndexOf(card);
        if (index >= 0 && IsBoundWindowsCard(index, card) && repeater.TryGetElement(index) is { } row)
            args.AnchorElement = row;
    }

    private void FinishHeldBring(int id)
    {
        if (_heldBring?.CorrelationId != id || _windowsView is not { } view || _windowsIntent is not { } intent)
            return;
        _heldBring = null;
        _wheelOriginCut = null; // Verify this row after publication using correlated relative movement.
        intent.AcceptAccessibilityCompletion(id);
        QueueWindowsWheel(view);
    }

    private void CancelHeldBring()
    {
        if (_heldBring is { } bring)
        {
            bring.Cancel = true;
            _windowsPresenter?.AppAnimations.ForgetUserTerminal(bring.CorrelationId);
            _heldBring = null;
        }
    }

    private void ClearWindowsWheelAnchor()
    {
        CancelHeldBring();
        _requestedUserCut = null;
        _verifyingUserCut = null;
        _userAlignmentRequests = 0;
        _impulseDirection = 0;
        _wheelEpoch++;
        _wheelPosted = false;
        _wheelDelta = 0;
        _wheelAnchor = null;
        if (_wheelRequest is { } pending)
            _windowsPresenter?.AppAnimations.ForgetUserTerminal(pending);
        _wheelRequest = null;
        _wheelOriginCut = null;
        _releaseWheelAnchor = false;
        if (_holdingWheelRows)
        {
            _holdingWheelRows = false;
            _windowsSource?.Complete();
        }
    }

    partial void WindowsPreviewKeyReceived();

    private void OnWindowsPreviewKey(object sender, KeyRoutedEventArgs e)
    {
        WindowsPreviewKeyReceived();
        if (_windowsIntent == null || _windowsView is not { } view || e.Handled || !ReferenceEquals(sender, _windowsList))
            return;
        if (!ReferenceEquals(e.OriginalSource, view) && !ReferenceEquals(e.OriginalSource, _windowsList) &&
            !ReferenceEquals(e.OriginalSource, _windowsPresenter))
            return; // Preserve the keyboard behavior of focusable controls within a card.
        if (e.Key is not (Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down))
            return;
        e.Handled = true;
        OnWindowsAccessibilityStarting();
        var target = e.Key switch
        {
            Key.Home => 0,
            Key.End => view.ScrollableHeight,
            Key.PageUp => view.VerticalOffset - view.ViewportHeight,
            Key.PageDown => view.VerticalOffset + view.ViewportHeight,
            Key.Up => view.VerticalOffset - 16,
            _ => view.VerticalOffset + 16
        };
        var id = view.ScrollTo(view.HorizontalOffset, Math.Clamp(target, 0, view.ScrollableHeight),
            new Native.ScrollingScrollOptions(Native.ScrollingAnimationMode.Disabled, Native.ScrollingSnapPointsMode.Ignore));
        OnWindowsAccessibilitySubmitted(id);
    }

    private void OnBarScrollBy(NativeScroll.IScrollController sender, NativeScroll.ScrollControllerScrollByRequestedEventArgs e)
        => RegisterWindowsBarRequest(sender, e.CorrelationId);

    private void OnBarScrollTo(NativeScroll.IScrollController sender, NativeScroll.ScrollControllerScrollToRequestedEventArgs e)
        => RegisterWindowsBarRequest(sender, e.CorrelationId);

    private void OnBarScrollVelocity(NativeScroll.IScrollController sender, NativeScroll.ScrollControllerAddScrollVelocityRequestedEventArgs e)
        => RegisterWindowsBarRequest(sender, e.CorrelationId);

    private void RegisterWindowsBarRequest(NativeScroll.IScrollController sender, int id)
    {
        // ScrollPresenter subscribes to the stock controller first and fills in its request ID.
        if (!ReferenceEquals(sender, _windowsBar) || id < 0)
            return;
        OnWindowsAccessibilityStarting();
        OnWindowsAccessibilitySubmitted(id);
    }

    private void OnWindowsKey(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled && ReferenceEquals(sender, _windowsView) &&
            e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            BeginWindowsPhysical();
            QueueWindowsInputBoundary();
        }
    }

    private void OnWindowsKeyUp(object sender, KeyRoutedEventArgs e) => QueueWindowsInputBoundary();
    private void OnWindowsRelease(object sender, PointerRoutedEventArgs e) => QueueWindowsInputBoundary();

    private void QueueWindowsInputBoundary()
    {
        if (_windowsView is not { } view || _windowsIntent is not { IsPhysical: true } intent)
            return;
        _inputBoundaryGeneration = intent.Generation;
        if (_idleInputPosted)
            return;
        _idleInputPosted = true;
        if (!view.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ReferenceEquals(_windowsView, view))
                return;
            _idleInputPosted = false;
            if (intent.IsCurrent(_inputBoundaryGeneration) && intent.IsPhysical && view.State == Native.ScrollingInteractionState.Idle)
                EndWindowsPhysical(intent);
        }))
            _idleInputPosted = false;
    }

    private void EndWindowsPhysical(WindowsLiveScrollIntent intent)
    {
        intent.EndPhysical();
        if (_physicalMoved)
            ObserveWindowsAfterLayout(intent.Generation);
        _physicalMoved = false;
    }

    private void OnWindowsPress(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _windowsView))
            return;
        for (var node = e.OriginalSource as DependencyObject; node != null && node != _windowsView;
            node = VisualTreeHelper.GetParent(node))
            if (node is Native.Primitives.ScrollBar { Orientation: Native.Orientation.Vertical })
            {
                // The stock scroll controller owns track, line and thumb requests. Its ID can
                // be assigned before PointerPressed bubbles back through the ScrollView.
                if (_windowsRestore != null)
                    CancelWindowsWork();
                return;
            }
    }

    private void OnWindowsStateChanged(Native.ScrollView sender, object args)
    {
        if (!ReferenceEquals(sender, _windowsView) || _windowsIntent is not { } intent)
            return;
        if (_windowsRestore != null && sender.State == Native.ScrollingInteractionState.Idle)
            RequestWindowsLayout();
        if (_holdingWheelRows && sender.State != Native.ScrollingInteractionState.Animation)
            QueueWindowsWheel(sender);
        if (sender.State == Native.ScrollingInteractionState.Inertia && _wheelRequest.HasValue)
            return; // This is the correlated wheel impulse already owned by the current user.
        if (sender.State == Native.ScrollingInteractionState.Interaction)
            BeginWindowsPhysical();
        else if (sender.State == Native.ScrollingInteractionState.Idle && intent.IsPhysical)
            EndWindowsPhysical(intent);
    }

    private void OnWindowsViewChanged(Native.ScrollView sender, object args)
    {
        if (!ReferenceEquals(sender, _windowsView))
            return;
        VerifyWindowsRestorePosition();
        if (!_holdingWheelRows && _wheelRequest.HasValue && sender.VerticalOffset > LiveReadingState.TopTolerance)
            SampleWindowsReader();
        if (_windowsIntent?.IsPhysical == true)
        {
            _physicalMoved = true;
            SampleWindowsReader();
        }
        UpdateWindowsAnchorPolicy();
        if (_anchoringEvictedTail)
            ObserveWindowsTailCut();
    }

    private void ObserveWindowsAfterLayout(long generation)
    {
        _observeAfterLayout = generation;
        RequestWindowsLayout();
        _windowsView?.UpdateLayout();
        ProcessWindowsLayout();
    }

    private bool SampleWindowsReader()
    {
        if (_windowsView is not { } view || _windowsRepeater is not { } repeater || _reader.Cards.Count == 0)
            return false;
        if (view.VerticalOffset <= LiveReadingState.TopTolerance)
        {
            _reader.ObserveViewport(0, 0, true);
            UpdateWindowsAnchorPolicy();
            return true;
        }
        var first = int.MaxValue;
        var cut = 0d;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(repeater); i++)
        {
            var child = VisualTreeHelper.GetChild(repeater, i);
            if (child is not FrameworkElement row)
                continue;
            var index = repeater.GetElementIndex(row);
            if (index < 0 || index >= first || index >= _reader.Cards.Count || row.ActualHeight <= 0)
                continue;
            var y = WindowsCardTop(row, view);
            if (y + row.ActualHeight > LiveReadingState.TopTolerance && y < view.ViewportHeight)
            {
                first = index;
                cut = y;
            }
        }
        if (first == int.MaxValue)
            return false;
        // A distant jump may realize a lower row before the clipped row at the top.
        // A visible gap is meaningful only once the preceding container confirms it.
        if (first > 0 && cut > LiveReadingState.PixelTolerance &&
            (MeasureWindowsCard(first - 1) is not { } previous || previous.Top + previous.Height > LiveReadingState.TopTolerance))
            return false;
        _reader.ObserveViewport(first, cut, true);
        UpdateWindowsAnchorPolicy();
        return true;
    }

    private bool IsBoundWindowsCard(int index, CallItem card) =>
        _windowsRepeater?.TryGetElement(index) is Native.ItemContainer row &&
        FindWindowsDescendant<ICrossPlatformLayoutBacking>(row)?.CrossPlatformLayout is VisualElement view &&
        ReferenceEquals(view.Parent, _list) && ReferenceEquals(view.BindingContext, card);

    private (double Top, double Height)? MeasureWindowsCard(int index)
    {
        if (index < 0 || _windowsView is not { } view || _windowsRepeater is not { } repeater ||
            repeater.ItemsSourceView is not { } items || index >= items.Count ||
            repeater.TryGetElement(index) is not FrameworkElement { ActualHeight: > 0 } row ||
            repeater.GetElementIndex(row) != index ||
            !ReferenceEquals(row.DataContext, items.GetAt(index)))
            return null;
        return (WindowsCardTop(row, view), row.ActualHeight);
    }

    private static double WindowsCardTop(FrameworkElement row, Native.ScrollView view) =>
        row.TransformToVisual(view).TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;
}
#endif

