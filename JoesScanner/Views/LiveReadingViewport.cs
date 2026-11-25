using System.Collections.Specialized;
using System.Diagnostics;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;

namespace JoesScanner.Views;

internal sealed partial class LiveReadingViewport
{
    private static readonly TimeSpan LayoutDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ScrollAcknowledgement = TimeSpan.FromMilliseconds(180);
    private readonly CollectionView _list;
    private readonly Button _indicator;
    private readonly LiveReadingState _reader;
    private readonly INotifyCollectionChanged _catalog;
    private long? _programmaticScroll;
    private CallItem? _reveal;
    private CallItem? _centeredCard;
    private bool _goToTop;
    private bool _applyingNativeScroll;

    private readonly record struct CardPosition(double Top, double Height, double ViewportHeight);

    internal LiveReadingViewport(CollectionView list, Button indicator, LiveReadingState reader)
    {
        _list = list;
        _indicator = indicator;
        _reader = reader;
        _catalog = (INotifyCollectionChanged)reader.Cards;
    }

    internal void Show(bool visible)
    {
        if (!_reader.SetVisible(visible))
            return;
        if (visible)
        {
            _catalog.CollectionChanged += OnCardsChanged;
            _reader.UnseenChanged += UpdateIndicator;
            _indicator.Clicked += OnNewCalls;
            _list.Scrolled += OnScrolled;
            _list.SizeChanged += OnViewportSizeChanged;
            _list.Loaded += OnViewportLoaded;
            _list.Unloaded += OnViewportUnloaded;
            _list.HandlerChanging += OnHandlerChanging;
            _list.HandlerChanged += OnViewportLoaded;
            ConnectNativeInput();
            QueueUpdate();
        }
        else
        {
            CancelPendingScroll();
            DisconnectNativeInput();
            _catalog.CollectionChanged -= OnCardsChanged;
            _reader.UnseenChanged -= UpdateIndicator;
            _indicator.Clicked -= OnNewCalls;
            _list.Scrolled -= OnScrolled;
            _list.SizeChanged -= OnViewportSizeChanged;
            _list.Loaded -= OnViewportLoaded;
            _list.Unloaded -= OnViewportUnloaded;
            _list.HandlerChanging -= OnHandlerChanging;
            _list.HandlerChanged -= OnViewportLoaded;
        }
        UpdateIndicator();
    }

    internal void CancelPendingScroll()
    {
        _reader.InvalidateUpdate();
        _reveal = _centeredCard = null;
        _goToTop = false;
    }

    internal void FollowLatest()
    {
        CancelPendingScroll();
#if WINDOWS || ANDROID
        _nativeInput = null;
#endif
        _reader.FollowLatest();
        _goToTop = true;
        QueueUpdate();
    }

    internal void Reveal(CallItem card)
    {
        CancelPendingScroll();
#if WINDOWS || ANDROID
        _nativeInput = null;
#endif
        if (!_reader.ReadFrom(card))
            return;
        _reveal = card;
        QueueUpdate();
    }

    internal void CardSizeChanged(CallItem card)
    {
        if (_reader.IsVisible && !_reader.IsFollowing && _reader.IndexOf(card) >= 0)
            QueueUpdate();
    }

    private void OnNewCalls(object? sender, EventArgs e) => FollowLatest();

    private void UpdateIndicator()
    {
        var count = _reader.UnseenCount;
        _indicator.IsVisible = _reader.IsVisible && count > 0;
        if (count > 0)
            _indicator.Text = $"New Calls ({count})";
    }

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && _reader.Cards.Count == 0)
            CancelPendingScroll();
        if (!_reader.IsFollowing || e is { Action: NotifyCollectionChangedAction.Add, NewStartingIndex: 0 } ||
            e is { Action: NotifyCollectionChangedAction.Remove, OldStartingIndex: 0 })
            QueueUpdate();
    }

    private void OnViewportSizeChanged(object? sender, EventArgs e)
    {
        if (!_reader.IsFollowing)
            QueueUpdate();
    }

    private void OnViewportLoaded(object? sender, EventArgs e)
    {
        ConnectNativeInput();
        QueueUpdate();
    }

    private void OnHandlerChanging(object? sender, HandlerChangingEventArgs e)
        => OnViewportUnloaded(sender, EventArgs.Empty);

    private void OnViewportUnloaded(object? sender, EventArgs e)
    {
        DisconnectNativeInput();
        CancelPendingScroll();
    }

    private void OnScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        var index = e.FirstVisibleItemIndex;
        if (!_reader.IsVisible || !_list.IsLoaded || _applyingNativeScroll || index < 0 || index >= _reader.Cards.Count ||
            !double.IsFinite(e.VerticalDelta) || e.VerticalDelta == 0)
            return;

        var nativeInput = NativeUserScrolling();
        var acknowledgingScroll = _programmaticScroll is { } at && Stopwatch.GetElapsedTime(at) < ScrollAcknowledgement;
        // A native gesture or wheel takes precedence. Without that signal a
        // layout or our own scroll acknowledgement cannot change reader intent.
        if (!nativeInput && (acknowledgingScroll || _reader.HasQueuedUpdate))
            return;
        try
        {
            var position = MeasureCard(index);
            var top = position?.Top ?? (index == 0 && double.IsFinite(e.VerticalOffset)
                ? -Math.Max(0, e.VerticalOffset) : (double?)null);
            CancelPendingScroll();
            _reader.ObserveViewport(index, top, userScroll: true);
        }
        catch (Exception ex)
        {
            AppLog.DebugWriteLine(() => $"Live reading position could not be measured ({ex.GetType().Name}).");
        }
    }

    private void QueueUpdate(bool realize = true)
    {
        if (_reader.QueueUpdate() is not { } revision)
            return;
        var viewport = new WeakReference<LiveReadingViewport>(this);
        if (!_list.Dispatcher.DispatchDelayed(LayoutDelay, () =>
        {
            if (viewport.TryGetTarget(out var target))
                target.ApplyUpdate(revision, realize);
        }))
            _reader.InvalidateUpdate();
    }

    private void ApplyUpdate(int revision, bool realize)
    {
        if (!_reader.TakeUpdate(revision) || _list.Handler == null || !_list.IsLoaded || _reader.Cards.Count == 0)
            return;
        try
        {
            if (_reveal is { } reveal)
            {
                _reveal = null;
                if (_reader.IndexOf(reveal) < 0)
                    return;
                _centeredCard = reveal;
                Scroll(() => _list.ScrollTo(reveal, position: ScrollToPosition.Center, animate: false));
                QueueUpdate(realize: false);
                return;
            }
            if (_centeredCard is { } centered)
            {
                _centeredCard = null;
                if (MeasureCard(_reader.IndexOf(centered)) is { } position)
                    _reader.RememberPosition(centered, position.Top);
                return;
            }

            var explicitTop = _goToTop;
            _goToTop = false;
            if (!explicitTop && NativeUserScrolling())
                return;
            if (_reader.IsFollowing)
            {
                if (!NativeAtTop())
                    Scroll(() => _list.ScrollTo(_reader.Cards[0], position: ScrollToPosition.Start, animate: false));
                return;
            }

            if (_reader.ResolveAnchor() is not { } anchor)
                return;
            if (MeasureCard(anchor.Index) is { } actual)
            {
                if (anchor.Top is { } desired)
                {
                    // A shorter transcript can put the old cut point beyond
                    // the card. Keep that card on screen in that case.
                    if (actual.Height + desired <= LiveReadingState.PixelTolerance || desired >= actual.ViewportHeight)
                    {
                        desired = 0;
                        _reader.RememberPosition(anchor.Card, desired);
                    }
                    var delta = actual.Top - desired;
                    if (Math.Abs(delta) > LiveReadingState.PixelTolerance)
                        Scroll(() => ShiftViewport(delta));
                    return;
                }
                if (actual.Top + actual.Height > 0 && actual.Top < actual.ViewportHeight)
                {
                    _reader.RememberPosition(anchor.Card, actual.Top);
                    return;
                }
            }
            if (realize)
            {
                // A recycled container may need one layout pass to become
                // measurable. Restore its pixel offset on the following pass.
                Scroll(() => _list.ScrollTo(anchor.Card, position: ScrollToPosition.Start, animate: false));
                QueueUpdate(realize: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.DebugWriteLine(() => $"Live reading position could not be restored ({ex.GetType().Name}).");
        }
    }

    private void Scroll(Action move)
    {
        _programmaticScroll = Stopwatch.GetTimestamp();
        _applyingNativeScroll = true;
        try { move(); }
        finally { _applyingNativeScroll = false; }
    }
}
