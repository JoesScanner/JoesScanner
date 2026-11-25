using JoesScanner.Models;

internal sealed class ReadingTestDispatcher
{
    private readonly Queue<Action> _work = new();
    internal int PendingCount => _work.Count;

    internal bool DispatchDelayed(TimeSpan delay, Action action)
    {
        _work.Enqueue(action);
        return true;
    }

    internal void Drain()
    {
        for (var pass = 0; _work.TryDequeue(out var action); pass++)
        {
            if (pass == 20)
                throw new InvalidOperationException("Viewport did not settle.");
            action();
        }
    }
}

internal sealed class Button
{
    internal event EventHandler? Clicked;
    internal bool IsVisible { get; set; }
    internal string? Text { get; set; }

    internal void Click() => Clicked?.Invoke(this, EventArgs.Empty);
}

internal enum ScrollToPosition { Start, Center }
internal sealed class HandlerChangingEventArgs : EventArgs;
internal sealed class ItemsViewScrolledEventArgs(int first, double offset, double delta) : EventArgs
{
    internal int FirstVisibleItemIndex { get; } = first;
    internal double VerticalOffset { get; } = offset;
    internal double VerticalDelta { get; } = delta;
}

// Deterministic geometry and delayed dispatch for the production controller.
// This fixture does not stand in for native virtualization or gesture tests.
internal sealed class CollectionView(IReadOnlyList<CallItem> cards)
{
    private readonly Dictionary<CallItem, double> _heights = new(ReferenceEqualityComparer.Instance);
    internal event EventHandler<ItemsViewScrolledEventArgs>? Scrolled;
    internal event EventHandler? Loaded;
    internal event EventHandler? Unloaded;
    internal event EventHandler? SizeChanged;
    internal event EventHandler<HandlerChangingEventArgs>? HandlerChanging;
    internal event EventHandler? HandlerChanged;

    internal ReadingTestDispatcher Dispatcher { get; } = new();
    internal object? Handler { get; private set; } = new();
    internal bool IsLoaded { get; private set; } = true;
    internal bool UserScrolling { get; private set; }
    internal double ViewportHeight { get; private set; } = 180;
    internal double Offset { get; private set; }
    internal int TopMoves { get; private set; }
    internal int OffsetCorrections { get; private set; }
    internal int ScrollSubscribers => Scrolled?.GetInvocationList().Length ?? 0;

    internal void SetHeight(CallItem call, double height) => _heights[call] = height;
    internal double HeightOf(CallItem call) => _heights.GetValueOrDefault(call, 100);
    internal double TopOf(int index) => cards.Take(index).Sum(HeightOf) - Offset;

    internal void Move(double offset, bool user = false)
    {
        var previous = Offset;
        Offset = Math.Clamp(offset, 0, Math.Max(0, cards.Sum(HeightOf) - ViewportHeight));
        var first = 0;
        while (first < cards.Count && TopOf(first) + HeightOf(cards[first]) <= 0)
            first++;
        UserScrolling = user;
        try { Scrolled?.Invoke(this, new(first, Offset, Offset - previous)); }
        finally { UserScrolling = false; }
    }

    internal void ScrollTo(CallItem card, ScrollToPosition position, bool animate)
    {
        var index = cards.ToList().IndexOf(card);
        if (position == ScrollToPosition.Start)
            TopMoves++;
        var center = position == ScrollToPosition.Center ? (ViewportHeight - HeightOf(card)) / 2 : 0;
        Move(Offset + TopOf(index) - center);
    }

    internal void Shift(double delta)
    {
        OffsetCorrections++;
        Move(Offset + delta);
    }

    internal void SetViewportHeight(double height)
    {
        ViewportHeight = height;
        SizeChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Unmount()
    {
        Unloaded?.Invoke(this, EventArgs.Empty);
        HandlerChanging?.Invoke(this, new());
        Handler = null;
        IsLoaded = false;
    }

    internal void Mount()
    {
        Handler = new();
        IsLoaded = true;
        HandlerChanged?.Invoke(this, EventArgs.Empty);
        Loaded?.Invoke(this, EventArgs.Empty);
    }
}

namespace JoesScanner.Views
{
    internal sealed partial class LiveReadingViewport
    {
        private void ConnectNativeInput() { }
        private void DisconnectNativeInput() { }
        private bool NativeUserScrolling() => _list.UserScrolling;
        private bool NativeAtTop() => _list.Offset <= JoesScanner.ViewModels.LiveReadingState.TopTolerance;

        private CardPosition? MeasureCard(int index)
        {
            if (index < 0 || index >= _reader.Cards.Count)
                return null;
            var top = _list.TopOf(index);
            var height = _list.HeightOf(_reader.Cards[index]);
            return top + height < -20 || top > _list.ViewportHeight + 20
                ? null : new(top, height, _list.ViewportHeight);
        }

        private void ShiftViewport(double delta) => _list.Shift(delta);
    }
}
