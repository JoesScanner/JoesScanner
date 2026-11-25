using JoesScanner.Models;

namespace JoesScanner.ViewModels;

internal readonly record struct LiveReadingAnchor(CallItem Card, int Index, double? Top);

// Each projection owns its visual position. None of this state is used by playback.
internal sealed class LiveReadingState(IReadOnlyList<CallItem> cards)
{
    private readonly HashSet<CallItem> _unseen = new(ReferenceEqualityComparer.Instance);
    private int _scrollRevision;
    private bool _updateQueued;
    private LiveReadingAnchor? _anchor;

    internal const double TopTolerance = 0.1;
    internal const double PixelTolerance = 0.75;
    internal event Action? UnseenChanged;
    internal IReadOnlyList<CallItem> Cards => cards;
    internal bool IsVisible { get; private set; }
    internal bool IsFollowing { get; private set; } = true;
    internal bool HasQueuedUpdate => _updateQueued;
    internal int UnseenCount => _unseen.Count;

    internal bool SetVisible(bool visible)
    {
        if (IsVisible == visible)
            return false;
        IsVisible = visible;
        InvalidateUpdate();
        return true;
    }

    internal int? QueueUpdate()
    {
        if (!IsVisible || _updateQueued)
            return null;
        _updateQueued = true;
        return ++_scrollRevision;
    }

    internal bool TakeUpdate(int revision)
    {
        if (!IsVisible || !_updateQueued || _scrollRevision != revision)
            return false;
        _updateQueued = false;
        return true;
    }

    internal void InvalidateUpdate()
    {
        _updateQueued = false;
        ++_scrollRevision;
    }

    internal static bool IsAtTop(int firstIndex, double? top) =>
        firstIndex == 0 && top is { } y && double.IsFinite(y) && y >= -TopTolerance;

    internal void ObserveViewport(int firstIndex, double? top, bool userScroll)
    {
        if (!userScroll || !IsVisible || firstIndex < 0 || firstIndex >= cards.Count)
            return;
        if (IsAtTop(firstIndex, top))
        {
            FollowLatest();
            return;
        }
        InvalidateUpdate();
        IsFollowing = false;
        _anchor = new(cards[firstIndex], firstIndex, top);
    }

    internal bool ReadFrom(CallItem card)
    {
        var index = IndexOf(card);
        if (index < 0)
            return false;
        InvalidateUpdate();
        IsFollowing = false;
        _anchor = new(card, index, null);
        return true;
    }

    internal void RememberPosition(CallItem card, double top)
    {
        if (!IsFollowing && _anchor is { } anchor && ReferenceEquals(anchor.Card, card))
            _anchor = anchor with { Top = top };
    }

    internal LiveReadingAnchor? ResolveAnchor()
    {
        if (_anchor is not { } anchor || cards.Count == 0)
            return null;
        var index = IndexOf(anchor.Card);
        // If retention or a projection edit removed the anchor, keep the reader
        // in the same part of the list using a surviving card.
        if (index < 0)
        {
            index = Math.Min(anchor.Index, cards.Count - 1);
            _anchor = new(cards[index], index, null);
        }
        else
        {
            _anchor = anchor with { Index = index };
        }
        return _anchor;
    }

    internal void FollowLatest()
    {
        InvalidateUpdate();
        IsFollowing = true;
        _anchor = null;
        if (_unseen.Count == 0)
            return;
        _unseen.Clear();
        UnseenChanged?.Invoke();
    }

    internal void RecordAdmission(CallItem card)
    {
        if (!IsFollowing && _unseen.Add(card))
            UnseenChanged?.Invoke();
    }

    internal void ForgetCard(CallItem card)
    {
        if (_unseen.Remove(card))
            UnseenChanged?.Invoke();
    }

    internal void RetainVisibleAdmissions()
    {
        if (_unseen.Count != 0 && _unseen.RemoveWhere(card => IndexOf(card) < 0) != 0)
            UnseenChanged?.Invoke();
    }

    internal int IndexOf(CallItem card)
    {
        for (var index = 0; index < cards.Count; index++)
            if (ReferenceEquals(cards[index], card))
                return index;
        return -1;
    }
}
