#if WINDOWS || WINDOWS_LIVE_READING_TESTS
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using JoesScanner.Models;

namespace JoesScanner.Views;

// A replacement host first lays out the saved card and its predecessor. Publishing
// the missing prefix lets WinUI keep that realized element as its scroll anchor.
internal sealed class LiveReadingSource(IReadOnlyList<CallItem> source, int start)
    : ObservableCollection<CallItem>(source.Skip(start))
{
    private bool _retired;
    internal bool IsComplete { get; private set; }

    // An existing repeater keeps its MAUI item factory and recycling pool when
    // only the rows change. Replacing ItemsSource would replace that factory.
    internal void SeedAt(int start)
    {
        IsComplete = false;
        Items.Clear();
        for (var i = start; i < source.Count; i++)
            Items.Add(source[i]);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }

    // A canceled native animation cannot anchor layout until the presenter leaves its
    // Animation state. Keep the published identities stable across that handoff.
    internal void DeferPublication() => IsComplete = false;

    internal void CatchUp()
    {
        if (IsComplete)
            Synchronize();
        else
            Complete();
    }

    internal void Complete()
    {
        if (_retired || IsComplete)
            return;
        Synchronize();
        IsComplete = true;
    }

    internal void Apply(NotifyCollectionChangedEventArgs change)
    {
        if (_retired || !IsComplete)
            return;
        // An earlier listener can cancel a reconstruction from the same catalog
        // notification. Complete() will already have published that change.
        switch (change.Action)
        {
            case NotifyCollectionChangedAction.Add when change.NewItems?.Count == 1 && change.NewStartingIndex >= 0:
                if (Count < source.Count)
                    Insert(change.NewStartingIndex, (CallItem)change.NewItems[0]!);
                break;
            case NotifyCollectionChangedAction.Remove when change.OldItems?.Count == 1 && change.OldStartingIndex >= 0:
                if (Count > source.Count)
                    RemoveAt(change.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Move when change.NewItems?.Count == 1 && change.NewStartingIndex >= 0 && change.OldStartingIndex >= 0:
                if (!ReferenceEquals(this[change.NewStartingIndex], change.NewItems[0]))
                    Move(change.OldStartingIndex, change.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace when change.NewItems?.Count == 1 && change.NewStartingIndex >= 0:
                if (!ReferenceEquals(this[change.NewStartingIndex], change.NewItems[0]))
                    this[change.NewStartingIndex] = (CallItem)change.NewItems[0]!;
                break;
            default:
                Synchronize();
                break;
        }
    }

    internal bool ExtendSeed()
    {
        if (_retired || IsComplete || Count == 0)
            return false;
        for (var i = 1; i < source.Count; i++)
            if (ReferenceEquals(source[i], this[0]))
            {
                Insert(0, source[i - 1]);
                return true;
            }
        return false;
    }

    internal void Retire() => _retired = true;

    internal void SynchronizeTail(int count)
    {
        if (!_retired && !IsComplete)
            Synchronize(Math.Max(0, source.Count - count));
    }

    private void Synchronize(int start = 0)
    {
        var retained = new HashSet<CallItem>(start == 0 ? source : source.Skip(start), ReferenceEqualityComparer.Instance);
        for (var i = Count - 1; i >= 0; i--)
            if (!retained.Contains(this[i]))
                RemoveAt(i);

        // Walk backwards so an arriving prefix is inserted before the same
        // realized containers. A Reset would discard all of those containers.
        var before = Count;
        for (var i = source.Count - 1; i >= start; i--)
        {
            var card = source[i];
            if (before > 0 && ReferenceEquals(this[before - 1], card))
            {
                before--;
                continue;
            }
            var previous = IndexOf(card);
            if (previous < 0)
                Insert(before, card);
            else
            {
                if (previous < before)
                    before--;
                Move(previous, before);
            }
        }
    }
}
#endif
